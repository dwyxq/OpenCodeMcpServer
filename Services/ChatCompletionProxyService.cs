// <summary>
/// 【功能说明】：OpenAI 兼容聊天代理服务 - 转发 chat/completions，按健康评分自动选路 + 失败自动切换下一优提供商
/// 【服务对象】：ProviderProxyTools，依赖 IProviderHealthService 评分与反馈
/// 【调用方式】：依赖注入 IChatCompletionProxyService（单例）
/// 【禁止重复】：项目内唯一转发实现；鉴权头规则统一走 ProviderHealthService.ApplyAuthHeaders
/// </summary>
using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services;

/// <summary>
/// OpenAI 兼容聊天代理实现 按健康排序+失败切下一候选最多4次
/// </summary>
public class ChatCompletionProxyService : IChatCompletionProxyService {
    private readonly ILogger<ChatCompletionProxyService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProviderHealthService _health;
    private readonly ProviderHealthService _healthImpl;
    private readonly RoutingConfig _routing;
    /// <summary>
    /// sticky 会话粘滞：sessionId → 绑定 ProviderId
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sessionMap = new(StringComparer.Ordinal);
    /// <summary>
    /// balanced 轮询计数器（原子递增）
    /// </summary>
    private int _roundRobin;

    public ChatCompletionProxyService(
        ILogger<ChatCompletionProxyService> logger,
        IHttpClientFactory httpClientFactory,
        IProviderHealthService health,
        IOptions<McpServerConfig> options) {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _health = health;
        _healthImpl = (ProviderHealthService)health;
        _routing = options.Value.Routing ?? new RoutingConfig();
    }

    /// <inheritdoc/>
    public async Task<ChatProxyResult> ChatAsync(ChatProxyRequest request, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(request.Model))
            return Failure(request.Model, 0, Array.Empty<string>(), "model 参数不能为空");
        if (request.Messages.Length == 0)
            return Failure(request.Model, 0, Array.Empty<string>(), "messages 不能为空");

        var candidates = ResolveCandidates(request);
        if (candidates.Count == 0)
            return Failure(request.Model, 0, Array.Empty<string>(),
                $"未找到提供模型 '{request.Model}' 的已配置提供商（可用 provider_models 工具查看目录）");

        var tried = new List<string>();
        var attempts = 0;
        string? lastError = null;
        var maxAttempts = Math.Min(candidates.Count, Math.Max(1, _routing.MaxRetry + 1));
        var totalStopwatch = Stopwatch.StartNew();

        foreach (var endpoint in candidates.Take(maxAttempts)) {
            cancellationToken.ThrowIfCancellationRequested();

            // 总预算控制：超过 totalBudgetMs 直接终止（含重试的整次请求总耗时上限）
            if (_routing.TotalBudgetMs > 0 && totalStopwatch.ElapsedMilliseconds >= _routing.TotalBudgetMs)
                return Failure(request.Model, attempts, tried.ToArray(),
                    $"{lastError ?? "尚未开始"}（整次请求总预算 {_routing.TotalBudgetMs}ms 已耗尽）");

            // 失败切换下家前退避（非首次尝试）
            if (attempts > 0 && _routing.RetryBackoffMs > 0)
                await Task.Delay(_routing.RetryBackoffMs, cancellationToken);

            tried.Add(endpoint.ProviderId);
            attempts++;
            var start = Stopwatch.GetTimestamp();
            try {
                var json = await SendOnceAsync(endpoint, request, cancellationToken);
                var latency = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                _health.ReportSuccess(endpoint.ProviderId, latency);
                _logger.LogInformation("Chat proxy ok: provider={Provider} model={Model} latency={Latency}ms attempts={Attempts}",
                    endpoint.ProviderId, request.Model, latency, attempts);

                // sticky 粘滞：成功后记录 sessionId → 绑定 ProviderId
                if (string.Equals(ResolveStrategy(request), "sticky", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(request.SessionId)) {
                    _sessionMap[request.SessionId] = endpoint.ProviderId;
                }
                return new ChatProxyResult(true, endpoint.ProviderId, request.Model, json, latency, attempts, tried.ToArray(), null);
            } catch (ProxyUpstreamException ex) {
                lastError = $"{endpoint.ProviderId}: {ex.Message}";
                _health.ReportFailure(endpoint.ProviderId, ex.Message);
                _logger.LogWarning("Chat proxy upstream failure ({Provider}): {Error}", endpoint.ProviderId, ex.Message);
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) {
                if (cancellationToken.IsCancellationRequested) throw;
                lastError = $"{endpoint.ProviderId}: {ex.Message}";
                _health.ReportFailure(endpoint.ProviderId, ex.Message);
                _logger.LogWarning("Chat proxy network failure ({Provider}): {Error}", endpoint.ProviderId, ex.Message);
            }
        }

        return Failure(request.Model, attempts, tried.ToArray(), lastError ?? "所有候选提供商均失败");
    }

    /// <summary>
    /// 获取当前已配置提供商的健康评分、可用性、是否启用、是否有 API Key 等信息，按可用性+评分降序排列
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<ProviderCatalogEntry[]> GetProviderCatalogAsync(CancellationToken cancellationToken = default) {
        var healthMap = _health.GetRankedProviders().ToDictionary(h => h.ProviderId, StringComparer.OrdinalIgnoreCase);
        var catalog = _healthImpl.GetConfiguredProviders()
            .GroupBy(p => p.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(p => {
                healthMap.TryGetValue(p.ProviderId, out var h);
                return new ProviderCatalogEntry(
                    ProviderId: p.ProviderId,
                    DisplayName: p.DisplayName ?? p.ProviderId,
                    BaseUrl: p.BaseUrl,
                    Enabled: p.Enabled,
                    HasApiKey: !p.Keyless && _health.ResolveApiKey(p) != null,
                    Keyless: p.Keyless,
                    Models: p.Models ?? Array.Empty<string>(),
                    Available: h?.Available ?? false,
                    Score: h?.Score ?? 0,
                    LastError: h?.LastError);
            })
            .OrderByDescending(e => e.Available)
            .ThenByDescending(e => e.Score)
            .ToArray();
        return Task.FromResult(catalog);
    }

    /// <summary>
    /// 解析候选提供商：指定则精确匹配，未指定按模型归属 + 策略（auto/sticky/balanced）重排
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    private List<ProviderEndpointConfig> ResolveCandidates(ChatProxyRequest request) {
        var all = _healthImpl.GetConfiguredProviders()
            .GroupBy(p => p.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(p => p.Enabled)
            .ToDictionary(p => p.ProviderId, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(request.ProviderId)) {
            if (!all.TryGetValue(request.ProviderId, out var exact))
                return new List<ProviderEndpointConfig>();
            return new List<ProviderEndpointConfig> { exact };
        }

        // 候选：按健康评分排序的、能提供该模型且满足能力过滤的提供商
        var ranked = _health.GetRankedProviders();
        var baseCandidates = ranked
            .Where(h => all.TryGetValue(h.ProviderId, out var ep)
                && ServesModel(ep!, request.Model)
                && SupportsCapabilities(ep!, request.Model, request.Capabilities))
            .Select(h => all[h.ProviderId])
            .ToList();

        switch (ResolveStrategy(request)) {
            case "sticky":      // 粘滞：会话已有绑定且绑定提供商仍可用则置顶；否则按健康排序并记录新绑定
                return ResolveSticky(request, baseCandidates);
            case "balanced":    // 均衡：在可用候选间轮询
                return ResolveBalanced(baseCandidates);
            default:            // auto 热池优先，热池空降级探索
                return ResolveAuto(baseCandidates);
        }
    }

    /// <summary>auto 路由：热池（评分≥阈值且可用）候选置顶优先；热池为空且启用探索时降级探索通道补数据</summary>
    private List<ProviderEndpointConfig> ResolveAuto(List<ProviderEndpointConfig> baseCandidates) {
        if (baseCandidates.Count <= 1)
            return baseCandidates;

        var healthMap = _health.GetRankedProviders().ToDictionary(h => h.ProviderId, StringComparer.OrdinalIgnoreCase);
        var hotPool = baseCandidates
            .Where(c => healthMap.TryGetValue(c.ProviderId, out var h) && _health.IsQualifiedForHotPool(h))
            .ToList();
        // 热池有候选：热池优先置顶，其余按原健康顺序跟在后面（保底）
        if (hotPool.Count > 0)
            return hotPool.Concat(baseCandidates.Except(hotPool)).ToList();

        // 热池空：启用探索时降级探索通道挑选一个补数据，否则保持原顺序
        if (_routing.ExplorationEnabled) {
            var ranked = _health.GetRankedProviders();
            var explorer = _health.PickExplorationModel(ranked
                .Where(h => baseCandidates.Any(c => c.ProviderId.Equals(h.ProviderId, StringComparison.OrdinalIgnoreCase)))
                .ToList());
            if (explorer != null) {
                var ep = baseCandidates.First(c => c.ProviderId.Equals(explorer.ProviderId, StringComparison.OrdinalIgnoreCase));
                return new List<ProviderEndpointConfig> { ep }.Concat(baseCandidates.Except(new[] { ep })).ToList();
            }
        }
        return baseCandidates;
    }

    /// <summary>
    /// 确定生效路由策略：请求参数优先，缺省回退 Routing 配置默认 策略
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    private string ResolveStrategy(ChatProxyRequest request) => string.IsNullOrWhiteSpace(request.Strategy) ? _routing.Strategy : request.Strategy;

    /// <summary>
    /// sticky 粘滞：会话已有绑定且绑定提供商仍可用则置顶；否则按健康排序并记录新绑定
    /// </summary>
    /// <param name="request"></param>
    /// <param name="candidates"></param>
    /// <returns></returns>
    private List<ProviderEndpointConfig> ResolveSticky(ChatProxyRequest request, List<ProviderEndpointConfig> candidates) {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            return candidates;
        if (_sessionMap.TryGetValue(request.SessionId, out var bound)
            && candidates.Any(c => c.ProviderId.Equals(bound, StringComparison.OrdinalIgnoreCase))) {
            // 绑定的提供商仍可用，置顶优先
            return candidates.OrderByDescending(c => c.ProviderId.Equals(bound, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(c => _health.GetRankedProviders().FirstOrDefault(h => h.ProviderId.Equals(c.ProviderId, StringComparison.OrdinalIgnoreCase))?.Score ?? 0)
                .ToList();
        }
        // 无绑定或绑定失效：记预绑定（成功后再固化），按健康排序
        _sessionMap[request.SessionId] = candidates.FirstOrDefault()?.ProviderId ?? string.Empty;
        return candidates;
    }

    /// <summary>balanced 均衡：在可用候选间轮询（原子计数器）</summary>
    private List<ProviderEndpointConfig> ResolveBalanced(List<ProviderEndpointConfig> candidates) {
        if (candidates.Count <= 1) return candidates;
        var idx = Interlocked.Increment(ref _roundRobin) % candidates.Count;
        return candidates.Skip(idx).Concat(candidates.Take(idx)).ToList();
    }

    /// <summary>判断提供商是否支持该模型：静态目录 + 前缀写法 provider/model 兼容</summary>
    private static bool ServesModel(ProviderEndpointConfig endpoint, string model) {
        if (model.StartsWith(endpoint.ProviderId + "/", StringComparison.OrdinalIgnoreCase))
            model = model[(endpoint.ProviderId.Length + 1)..];
        return endpoint.Models?.Any(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase)) ?? false;
    }

    /// <summary>能力过滤：请求指定 capabilities 时，校验该模型声明的能力标签是否覆盖（未配置能力映射的模型视为全能力，不拦截）</summary>
    private static bool SupportsCapabilities(ProviderEndpointConfig endpoint, string model, string[]? required) {
        if (required is null || required.Length == 0) return true;
        if (model.StartsWith(endpoint.ProviderId + "/", StringComparison.OrdinalIgnoreCase))
            model = model[(endpoint.ProviderId.Length + 1)..];
        if (endpoint.Capabilities is null || endpoint.Capabilities.Count == 0) return true;
        var cap = endpoint.Capabilities
            .FirstOrDefault(kv => string.Equals(kv.Key, model, StringComparison.OrdinalIgnoreCase));
        // 模型未在能力映射中声明 → 视为全能力，不拦截
        if (string.IsNullOrEmpty(cap.Key)) return true;
        return required.All(r => cap.Value.Contains(r, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>向单个提供商发送一次非流式 chat/completions 请求</summary>
    private async Task<string> SendOnceAsync(ProviderEndpointConfig endpoint, ChatProxyRequest request, CancellationToken cancellationToken) {
        var client = _httpClientFactory.CreateClient("openai-compat");
        var timeout = _routing.FirstTokenTimeoutMs > 0
            ? Math.Clamp(_routing.FirstTokenTimeoutMs / 1000.0, 1, 600)
            : Math.Clamp(endpoint.TimeoutSeconds, 10, 600);

        var payload = new Dictionary<string, object> {
            ["model"] = StripProviderPrefix(request.Model, endpoint),
            ["messages"] = request.Messages.Select(m => new Dictionary<string, object> { ["role"] = m.Role, ["content"] = m.Content }).ToArray(),
            ["stream"] = false
        };
        if (request.Temperature.HasValue) payload["temperature"] = request.Temperature.Value;
        if (request.MaxTokens.HasValue) payload["max_tokens"] = request.MaxTokens.Value;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.BaseUrl.TrimEnd('/')}/chat/completions") {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        _healthImpl.ApplyAuthHeaders(httpRequest, endpoint);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);

        if (!response.IsSuccessStatusCode)
            throw new ProxyUpstreamException($"HTTP {(int)response.StatusCode}: {Truncate(body, 300)}");
        return body;
    }

    private static string StripProviderPrefix(string model, ProviderEndpointConfig endpoint) =>
        model.StartsWith(endpoint.ProviderId + "/", StringComparison.OrdinalIgnoreCase)
            ? model[(endpoint.ProviderId.Length + 1)..]
            : model;

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    private static ChatProxyResult Failure(string model, int attempts, string[] tried, string error) =>
        new(false, null, model, null, 0, attempts, tried, error);

    /// <summary>上游返回非 2xx（计入失败评分并触发故障转移）</summary>
    private sealed class ProxyUpstreamException : Exception {
        public ProxyUpstreamException(string message) : base(message) { }
    }
}
