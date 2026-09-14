// <summary>
/// 【功能说明】：提供商健康服务 - OpenAI 兼容提供商健康探测、成功率/延迟评分、连续失败冷却、状态持久化
/// 【服务对象】：ChatCompletionProxyService（故障转移排序）、OpenAiCompatibleCatalogProvider（目录拉取优先级）
/// 【调用方式】：依赖注入 IProviderHealthService（单例）
/// 【禁止重复】：项目内唯一健康评分实现，参考 freellmapi 失败计数+冷却机制的 C# 移植
/// </summary>
using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services;

/// <summary>
/// 提供商健康服务实现 ResolveApiKey、ToEntry 评分（成功率×70+延迟分-冷却100）、连续失败≥3→冷却15分钟、持久化
/// </summary>
public class ProviderHealthService : IProviderHealthService {
    private const int ConsecutiveFailThreshold = 3; // 连续失败次数阈值
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(15);
    private static readonly string StateFilePath = Path.Combine(AppContext.BaseDirectory, "data", "provider-health.json");

    private readonly ILogger<ProviderHealthService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProviderDiscoveryConfig _endpoints;
    private readonly ProviderApiKeyConfig _apiKeys;
    private readonly ConcurrentDictionary<string, MutableState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProviderRateLimitEntry> _rateLimits = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private readonly RoutingConfig _routing;
    private DateTime _lastFullProbe = DateTime.MinValue;

    public ProviderHealthService(
        ILogger<ProviderHealthService> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<ProviderDiscoveryConfig> endpoints,
        IOptions<ProviderApiKeyConfig> apiKeys,
        IOptions<McpServerConfig> options) {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _endpoints = endpoints.Value;
        _apiKeys = apiKeys.Value;
        _routing = options.Value.Routing ?? new RoutingConfig();
        LoadState();
    }

    /// <summary>
    /// 解析提供商的 API Key，优先级：自定义字典 > 配置文件 > 环境变量；若 Keyless 则返回 null
    /// </summary>
    /// <param name="endpoint"></param>
    /// <returns></returns>
    public string? ResolveApiKey(ProviderEndpointConfig endpoint) {
        if (endpoint.Keyless) return null;
        if (_apiKeys.CustomProviders.TryGetValue(endpoint.ProviderId, out var dictKey) && !string.IsNullOrWhiteSpace(dictKey))
            return dictKey;
        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey)) return endpoint.ApiKey;
        if (!string.IsNullOrWhiteSpace(endpoint.ApiKeyEnvVar)) {
            var env = Environment.GetEnvironmentVariable(endpoint.ApiKeyEnvVar);
            if (!string.IsNullOrWhiteSpace(env)) return env;
        }
        return null;
    }

    /// <summary>
    /// 判断提供商是否符合热池条件（可用且评分≥热池阈值），用于故障转移排序
    /// </summary>
    /// <param name="entry"></param>
    /// <returns></returns>
    public bool IsQualifiedForHotPool(ProviderHealthEntry entry) =>
        entry.Available && entry.Score >= _routing.HotPoolThreshold;

    /// <summary>
    /// 从候选提供商中挑选一个用于探索的提供商（可用但未达热池阈值，或未探测/延迟未知），以积累健康数据
    /// </summary>
    /// <param name="candidates"></param>
    /// <returns></returns>
    public ProviderHealthEntry? PickExplorationModel(IReadOnlyList<ProviderHealthEntry> candidates) {
        // 探索通道：从可用但未达热池阈值、或探测数据不足（未探测/延迟未知）的候选中挑选
        // 用于积累健康数据，避免新提供商或状态不佳提供商长期无人使用
        if (candidates.Count == 0) return null;
        var exploration = candidates
            .Where(e => e.Available && !IsQualifiedForHotPool(e))
            .OrderBy(e => e.LastCheckedAt.HasValue ? 1 : 0) // 未探测过的优先探索
            .ThenBy(e => e.Score)                            // 评分较低的优先补数据
            .ToList();
        return exploration.Count > 0 ? exploration[0] : null;
    }

    /// <summary>
    /// 全量探测所有启用的 OpenAI 兼容自定义提供商，更新健康状态并持久化
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<ProviderHealthReport> ProbeAllAsync(CancellationToken cancellationToken = default) {
        if (!await _probeLock.WaitAsync(0, cancellationToken))
            return GetReport(); // 已有探测在进行，直接返回当前快照

        try {
            var targets = GetConfiguredProviders();
            var tasks = targets.Select(p => ProbeOneAsync(p, cancellationToken));
            await Task.WhenAll(tasks);
            _lastFullProbe = DateTime.UtcNow;
            SaveState();
            var now = DateTime.UtcNow;
            _logger.LogInformation("Provider health probe completed: {Available}/{Total} available",
                _states.Count(s => s.Value.IsAvailable(now)), targets.Count);
            return GetReport();
        } finally {
            _probeLock.Release();
        }
    }

    /// <summary>
    /// 获取当前提供商健康报告快照（不触发探测），包含总探测数、可用数、各提供商状态
    /// </summary>
    /// <returns></returns>
    public ProviderHealthReport GetReport() {
        var entries = GetRankedProviders();
        return new ProviderHealthReport(
            CheckedAt: _lastFullProbe == DateTime.MinValue ? null : _lastFullProbe,
            TotalProbed: entries.Count(e => e.LastCheckedAt.HasValue),
            AvailableCount: entries.Count(e => e.Available),
            Providers: entries.ToArray());
    }

    /// <inheritdoc/>
    public IReadOnlyList<ProviderHealthEntry> GetRankedProviders() {
        var now = DateTime.UtcNow;
        return GetConfiguredProviders()
            .Select(p => ToEntry(p, GetState(p.ProviderId), now))
            .OrderByDescending(e => e.Available)
            .ThenByDescending(e => e.Score)
            .ToArray();
    }

    /// <inheritdoc/>
    public void ReportSuccess(string providerId, long latencyMs) {
        var state = GetState(providerId);
        lock (state) {
            state.SuccessCount++;
            state.ConsecutiveFails = 0;
            state.LastLatencyMs = latencyMs;
            state.LastCheckedAt = DateTime.UtcNow;
            state.LastError = null;
            state.CooldownUntil = null;
        }
        SaveState();
    }

    /// <inheritdoc/>
    public void ReportFailure(string providerId, string reason) {
        var state = GetState(providerId);
        lock (state) {
            state.FailCount++;
            state.ConsecutiveFails++;
            state.LastCheckedAt = DateTime.UtcNow;
            state.LastError = reason;
            if (state.ConsecutiveFails >= ConsecutiveFailThreshold) {
                state.CooldownUntil = DateTime.UtcNow.Add(CooldownDuration);
                _logger.LogWarning("Provider {ProviderId} entered cooldown until {Until} ({Reason})",
                    providerId, state.CooldownUntil, reason);
            }
        }
        SaveState();
    }

    /// <inheritdoc/>
    public void ReportRateLimited(string providerId, int retryAfterMs) {
        if (retryAfterMs <= 0) retryAfterMs = 60_000; // 默认 60 秒避让
        var entry = new ProviderRateLimitEntry(
            ProviderId: providerId,
            RateLimitedAt: DateTime.UtcNow,
            RetryAfterAt: DateTime.UtcNow.AddMilliseconds(retryAfterMs),
            RetryAfterMs: retryAfterMs);
        _rateLimits[providerId] = entry;
        _logger.LogWarning("Provider {ProviderId} rate limited until {Until} ({Ms}ms)",
            providerId, entry.RetryAfterAt, retryAfterMs);
    }

    /// <inheritdoc/>
    public bool IsRateLimited(string providerId) {
        if (!_rateLimits.TryGetValue(providerId, out var entry)) return false;
        if (entry.RetryAfterAt <= DateTime.UtcNow) {
            _rateLimits.TryRemove(providerId, out _);
            return false;
        }
        return true;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ProviderRateLimitEntry> GetRateLimitReport() {
        var now = DateTime.UtcNow;
        return _rateLimits.Values
            .Where(e => e.RetryAfterAt > now)
            .OrderBy(e => e.RetryAfterAt)
            .ToList();
    }

    /// <inheritdoc/>
    public void ClearRateLimits() {
        _rateLimits.Clear();
        _logger.LogInformation("All rate limits cleared");
    }

    /// <inheritdoc/>
    public bool IsUnavailableForRouting(string providerId) {
        if (IsRateLimited(providerId)) return true;
        var state = GetState(providerId);
        lock (state) {
            return state.CooldownUntil.HasValue && state.CooldownUntil > DateTime.UtcNow;
        }
    }

    /// <summary>获取所有启用的 OpenAI 兼容自定义提供商配置</summary>
    internal List<ProviderEndpointConfig> GetConfiguredProviders() {
        return _endpoints.CustomProviders
            .Where(kv => kv.Value.Enabled && !string.IsNullOrWhiteSpace(kv.Value.BaseUrl))
            .Select(kv => string.IsNullOrWhiteSpace(kv.Value.ProviderId)
                ? kv.Value with { ProviderId = kv.Key, DisplayName = kv.Value.DisplayName ?? kv.Key }
                : kv.Value with { DisplayName = kv.Value.DisplayName ?? kv.Key })
            .ToList();
    }

    private async Task ProbeOneAsync(ProviderEndpointConfig endpoint, CancellationToken cancellationToken) {
        var client = _httpClientFactory.CreateClient("openai-compat");
        var start = DateTime.UtcNow;
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.BaseUrl.TrimEnd('/')}/models");
            ApplyAuthHeaders(request, endpoint);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var latency = (long)(DateTime.UtcNow - start).TotalMilliseconds;
            if (response.IsSuccessStatusCode) {
                ReportSuccess(endpoint.ProviderId, latency);
            } else {
                // 401/403 视为可达但缺密钥（提供商在线），不计入失败冷却
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) {
                    var state = GetState(endpoint.ProviderId);
                    lock (state) {
                        state.LastCheckedAt = DateTime.UtcNow;
                        state.LastError = $"HTTP {(int)response.StatusCode} (reachable, key missing/invalid)";
                        state.Reachable = true;
                    }
                } else {
                    ReportFailure(endpoint.ProviderId, $"HTTP {(int)response.StatusCode}");
                }
            }
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
            ReportFailure(endpoint.ProviderId, ex.Message);
        }
    }

    /// <summary>为请求应用鉴权与扩展头（供代理服务复用同一规则）</summary>
    internal void ApplyAuthHeaders(HttpRequestMessage request, ProviderEndpointConfig endpoint) {
        var key = ResolveApiKey(endpoint);
        if (!string.IsNullOrWhiteSpace(key))
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        if (endpoint.ExtraHeaders != null) {
            foreach (var header in endpoint.ExtraHeaders)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private MutableState GetState(string providerId) =>
        _states.GetOrAdd(providerId, _ => new MutableState());

    private ProviderHealthEntry ToEntry(ProviderEndpointConfig endpoint, MutableState state, DateTime now) {
        lock (state) {
            var inCooldown = state.CooldownUntil.HasValue && state.CooldownUntil > now;
            var total = state.SuccessCount + state.FailCount;
            var successRate = total == 0 ? 0.5 : (double)state.SuccessCount / total;
            // 评分：成功率为主(0-70) + 延迟得分(0-30，未探测中性 15)；冷却中直接 -100 沉底
            var latencyScore = state.LastLatencyMs.HasValue
                ? Math.Max(0, 30 - state.LastLatencyMs.Value / 400.0)
                : 15;
            var score = successRate * 70 + latencyScore - (inCooldown ? 100 : 0);
            var available = (state.Reachable || state.SuccessCount > 0) && !inCooldown;
            return new ProviderHealthEntry(
                ProviderId: endpoint.ProviderId,
                DisplayName: endpoint.DisplayName ?? endpoint.ProviderId,
                Enabled: endpoint.Enabled,
                Available: available,
                SuccessCount: state.SuccessCount,
                FailCount: state.FailCount,
                ConsecutiveFails: state.ConsecutiveFails,
                LastLatencyMs: state.LastLatencyMs,
                SuccessRate: Math.Round(successRate, 3),
                Score: Math.Round(score, 1),
                LastCheckedAt: state.LastCheckedAt,
                CooldownUntil: inCooldown ? state.CooldownUntil : null,
                LastError: state.LastError);
        }
    }

    private void LoadState() {
        try {
            if (!File.Exists(StateFilePath)) return;
            var json = File.ReadAllText(StateFilePath);
            var saved = JsonSerializer.Deserialize<Dictionary<string, PersistedState>>(json);
            if (saved == null) return;
            foreach (var kv in saved) {
                _states[kv.Key] = new MutableState {
                    SuccessCount = kv.Value.SuccessCount,
                    FailCount = kv.Value.FailCount,
                    ConsecutiveFails = kv.Value.ConsecutiveFails,
                    LastLatencyMs = kv.Value.LastLatencyMs,
                    LastCheckedAt = kv.Value.LastCheckedAt,
                    CooldownUntil = kv.Value.CooldownUntil,
                    LastError = kv.Value.LastError,
                    Reachable = kv.Value.Reachable
                };
            }
            _logger.LogDebug("Loaded provider health state for {Count} providers", saved.Count);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to load provider health state");
        }
    }

    private void SaveState() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
            var snapshot = new Dictionary<string, PersistedState>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _states) {
                lock (kv.Value) {
                    snapshot[kv.Key] = new PersistedState {
                        SuccessCount = kv.Value.SuccessCount,
                        FailCount = kv.Value.FailCount,
                        ConsecutiveFails = kv.Value.ConsecutiveFails,
                        LastLatencyMs = kv.Value.LastLatencyMs,
                        LastCheckedAt = kv.Value.LastCheckedAt,
                        CooldownUntil = kv.Value.CooldownUntil,
                        LastError = kv.Value.LastError,
                        Reachable = kv.Value.Reachable
                    };
                }
            }
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to save provider health state");
        }
    }

    private sealed class MutableState {
        public int SuccessCount;
        public int FailCount;
        public int ConsecutiveFails;
        public long? LastLatencyMs;
        public DateTime? LastCheckedAt;
        public DateTime? CooldownUntil;
        public string? LastError;
        public bool Reachable;

        public bool IsAvailable(DateTime now) =>
            (Reachable || SuccessCount > 0) && (!CooldownUntil.HasValue || CooldownUntil <= now);
    }

    private sealed class PersistedState {
        public int SuccessCount { get; set; }
        public int FailCount { get; set; }
        public int ConsecutiveFails { get; set; }
        public long? LastLatencyMs { get; set; }
        public DateTime? LastCheckedAt { get; set; }
        public DateTime? CooldownUntil { get; set; }
        public string? LastError { get; set; }
        public bool Reachable { get; set; }
    }
}
