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

        var alias = IsModelAlias(request.Model);
        var candidates = ResolveCandidates(request, alias);
        if (candidates.Count == 0)
            return Failure(request.Model, 0, Array.Empty<string>(),
                alias ? $"别名 '{request.Model}' 无可用提供商（所有已启用提供商均无可用模型）"
                      : $"未找到提供模型 '{request.Model}' 的已配置提供商（可用 provider_models 工具查看目录）");

        var tried = new List<(string ProviderId, string Reason)>();
        var attempts = 0;
        string? lastError = null;
        var maxAttempts = Math.Min(candidates.Count, Math.Max(1, _routing.MaxRetry + 1));
        var totalStopwatch = Stopwatch.StartNew();

        foreach (var endpoint in candidates.Take(maxAttempts)) {
            cancellationToken.ThrowIfCancellationRequested();

            // 总预算控制：超过 totalBudgetMs 直接终止（含重试的整次请求总耗时上限）
            if (_routing.TotalBudgetMs > 0 && totalStopwatch.ElapsedMilliseconds >= _routing.TotalBudgetMs) {
                var budgetReason = $"总预算{_routing.TotalBudgetMs}ms已耗尽";
                tried.Add((endpoint.ProviderId, budgetReason));
                return Failure(request.Model, attempts, FormatTried(tried),
                    $"{lastError ?? "尚未开始"}（整次请求总预算 {_routing.TotalBudgetMs}ms 已耗尽）");
            }

            // 失败切换下家前退避（非首次尝试）
            if (attempts > 0 && _routing.RetryBackoffMs > 0)
                await Task.Delay(_routing.RetryBackoffMs, cancellationToken);

            attempts++;
            var actualModel = alias ? ResolveAliasModel(endpoint)! : StripProviderPrefix(request.Model, endpoint);
            var start = Stopwatch.GetTimestamp();
            try {
                var json = await SendOnceAsync(endpoint, request, actualModel, cancellationToken);
                var latency = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                _health.ReportSuccess(endpoint.ProviderId, latency);
                _logger.LogInformation("Chat proxy ok: provider={Provider} model={Model} latency={Latency}ms attempts={Attempts}",
                    endpoint.ProviderId, actualModel, latency, attempts);

                // sticky 粘滞：成功后记录 sessionId → 绑定 ProviderId
                if (string.Equals(ResolveStrategy(request), "sticky", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(request.SessionId)) {
                    _sessionMap[request.SessionId] = endpoint.ProviderId;
                }
                return new ChatProxyResult(true, endpoint.ProviderId, actualModel, json, latency, attempts, FormatTried(tried), null);
            } catch (ProxyUpstreamException ex) {
                var resolvedKey = _health.ResolveApiKey(endpoint);
                var envVarName = endpoint.ApiKeyEnvVar;
                var reason = ExtractShortReason(ex.Message, resolvedKey, envVarName);
                tried.Add((endpoint.ProviderId, reason));
                lastError = $"{endpoint.ProviderId}({reason}): {ex.Message}";
                _ = Task.Run(() => _health.ReportFailure(endpoint.ProviderId, ex.Message));
                _logger.LogWarning("Chat proxy upstream failure ({Provider}): {Error}", endpoint.ProviderId, ex.Message);
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) {
                if (cancellationToken.IsCancellationRequested) throw;
                var resolvedKey = _health.ResolveApiKey(endpoint);
                var envVarName = endpoint.ApiKeyEnvVar;
                var reason = ExtractShortReason(ex.Message, resolvedKey, envVarName);
                tried.Add((endpoint.ProviderId, reason));
                lastError = $"{endpoint.ProviderId}({reason}): {ex.Message}";
                _ = Task.Run(() => _health.ReportFailure(endpoint.ProviderId, ex.Message));
                _logger.LogWarning("Chat proxy network failure ({Provider}): {Error}", endpoint.ProviderId, ex.Message);
            }
        }

        return Failure(request.Model, attempts, FormatTried(tried), lastError ?? "所有候选提供商均失败");
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
    private List<ProviderEndpointConfig> ResolveCandidates(ChatProxyRequest request, bool alias) {
        var all = _healthImpl.GetConfiguredProviders()
            .GroupBy(p => p.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(p => p.Enabled && !_health.IsUnavailableForRouting(p.ProviderId))
            .ToDictionary(p => p.ProviderId, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(request.ProviderId)) {
            if (!all.TryGetValue(request.ProviderId, out var exact))
                return new List<ProviderEndpointConfig>();
            if (alias && ResolveAliasModel(exact) == null)
                return new List<ProviderEndpointConfig>();
            return new List<ProviderEndpointConfig> { exact };
        }

        // 候选：按健康评分排序；别名模式取所有有可用模型的提供商，普通模式按模型归属 + 能力过滤
        // 限流中的提供商（HTTP 429 避让期内）一律排除，健康但受限同样自动切换下一候选
        var ranked = _health.GetRankedProviders();
        var baseCandidates = (alias
            ? ranked.Where(h => all.TryGetValue(h.ProviderId, out var ep) && ResolveAliasModel(ep!) != null)
            : ranked.Where(h => all.TryGetValue(h.ProviderId, out var ep)
                && ServesModel(ep!, request.Model)
                && SupportsCapabilities(ep!, request.Model, request.Capabilities)))
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

    /// <summary>判断 model 是否为固定别名（Routing:ModelAlias 配置值，或通用别名 auto）</summary>
    private bool IsModelAlias(string model) =>
        (!string.IsNullOrWhiteSpace(_routing.ModelAlias)
            && string.Equals(model, _routing.ModelAlias, StringComparison.OrdinalIgnoreCase))
        || string.Equals(model, "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>别名路由时该提供商实际发送的模型：DefaultModel 优先，缺省回退静态 Models 目录首个</summary>
    private static string? ResolveAliasModel(ProviderEndpointConfig endpoint) =>
        endpoint.DefaultModel ?? endpoint.Models?.FirstOrDefault();

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

    /// <summary>向单个提供商发送一次非流式 chat/completions 请求（actualModel 为最终上游模型名：普通请求去前缀，别名请求为该提供商默认模型）</summary>
    private async Task<string> SendOnceAsync(ProviderEndpointConfig endpoint, ChatProxyRequest request, string actualModel, CancellationToken cancellationToken) {
        var client = _httpClientFactory.CreateClient("openai-compat");
        var timeout = _routing.FirstTokenTimeoutMs > 0
            ? Math.Clamp(_routing.FirstTokenTimeoutMs / 1000.0, 1, 600)
            : Math.Clamp(endpoint.TimeoutSeconds, 10, 600);

        var httpRequest = BuildHttpRequest(endpoint, request, actualModel, stream: false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);

        if (!response.IsSuccessStatusCode) {
            // HTTP 429 限流：记录避让（ReportRateLimited）并抛 ProxyUpstreamException 触发故障转移，
            // 使"健康但受限"的提供商自动切换下一候选而非反复重试
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) {
                var retryAfterMs = ParseRetryAfterMs(response);
                _health.ReportRateLimited(endpoint.ProviderId, retryAfterMs);
                throw new ProxyUpstreamException($"HTTP 429 rate limited, retry after {retryAfterMs}ms");
            }
            throw new ProxyUpstreamException($"HTTP {(int)response.StatusCode}: {Truncate(body, 300)}");
        }
        return body;
    }

    private static string StripProviderPrefix(string model, ProviderEndpointConfig endpoint) =>
        model.StartsWith(endpoint.ProviderId + "/", StringComparison.OrdinalIgnoreCase)
            ? model[(endpoint.ProviderId.Length + 1)..]
            : model;

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    /// <summary>解析 HTTP 429 响应的 Retry-After 头为避让毫秒；缺省回退 Routing:RateLimitRetryAfterMs</summary>
    private int ParseRetryAfterMs(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter != null)
        {
            if (response.Headers.RetryAfter.Delta.HasValue)
                return (int)Math.Clamp(response.Headers.RetryAfter.Delta.Value.TotalMilliseconds, 1, 600_000);
            if (response.Headers.RetryAfter.Date.HasValue)
            {
                var diff = (long)(response.Headers.RetryAfter.Date.Value.UtcDateTime - DateTime.UtcNow).TotalMilliseconds;
                return (int)Math.Clamp(diff, 1, 600_000);
            }
        }
        return _routing.RateLimitRetryAfterMs;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> ChatStreamAsync(ChatProxyRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(request.Model)) {
            yield return SerializeSseError("model 参数不能为空");
            yield break;
        }
        if (request.Messages.Length == 0) {
            yield return SerializeSseError("messages 不能为空");
            yield break;
        }

        var alias = IsModelAlias(request.Model);
        var candidates = ResolveCandidates(request, alias);
        if (candidates.Count == 0) {
            var msg = alias ? $"别名 '{request.Model}' 无可用提供商" : $"未找到提供模型 '{request.Model}' 的已配置提供商";
            yield return SerializeSseError(msg);
            yield break;
        }

        var tried = new List<(string ProviderId, string Reason)>();
        var attempts = 0;
        string? lastError = null;
        var maxAttempts = Math.Min(candidates.Count, Math.Max(1, _routing.MaxRetry + 1));
        var totalStopwatch = Stopwatch.StartNew();

        foreach (var endpoint in candidates.Take(maxAttempts)) {
            cancellationToken.ThrowIfCancellationRequested();
            if (_routing.TotalBudgetMs > 0 && totalStopwatch.ElapsedMilliseconds >= _routing.TotalBudgetMs) {
                var budgetReason = $"总预算{_routing.TotalBudgetMs}ms已耗尽";
                tried.Add((endpoint.ProviderId, budgetReason));
                yield return SerializeSseError($"{lastError ?? "整次请求总预算已耗尽"}（已尝试: {string.Join(" -> ", FormatTried(tried))}）");
                yield break;
            }
            if (attempts > 0 && _routing.RetryBackoffMs > 0)
                await Task.Delay(_routing.RetryBackoffMs, cancellationToken);

            attempts++;
            var actualModel = alias ? ResolveAliasModel(endpoint)! : StripProviderPrefix(request.Model, endpoint);
            var start = Stopwatch.GetTimestamp();
            HttpResponseMessage? upstreamResponse = null;
            try {
                upstreamResponse = await SendOnceStreamAsync(endpoint, request, actualModel, cancellationToken);
                var latency = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                _health.ReportSuccess(endpoint.ProviderId, latency);
                _logger.LogInformation("Chat stream ok: provider={Provider} model={Model} latency={Latency}ms attempts={Attempts}",
                    endpoint.ProviderId, actualModel, latency, attempts);

                if (string.Equals(ResolveStrategy(request), "sticky", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(request.SessionId))
                    _sessionMap[request.SessionId] = endpoint.ProviderId;
            } catch (ProxyUpstreamException ex) {
                var resolvedKey = _health.ResolveApiKey(endpoint);
                var envVarName = endpoint.ApiKeyEnvVar;
                var reason = ExtractShortReason(ex.Message, resolvedKey, envVarName);
                tried.Add((endpoint.ProviderId, reason));
                lastError = $"{endpoint.ProviderId}({reason}): {ex.Message}";
                _ = Task.Run(() => _health.ReportFailure(endpoint.ProviderId, ex.Message));
                _logger.LogWarning("Chat stream upstream failure ({Provider}): {Error}", endpoint.ProviderId, ex.Message);
                upstreamResponse?.Dispose();
                continue;
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) {
                if (cancellationToken.IsCancellationRequested) throw;
                var resolvedKey = _health.ResolveApiKey(endpoint);
                var envVarName = endpoint.ApiKeyEnvVar;
                var reason = ExtractShortReason(ex.Message, resolvedKey, envVarName);
                tried.Add((endpoint.ProviderId, reason));
                lastError = $"{endpoint.ProviderId}({reason}): {ex.Message}";
                _ = Task.Run(() => _health.ReportFailure(endpoint.ProviderId, ex.Message));
                _logger.LogWarning("Chat stream network failure ({Provider}): {Error}", endpoint.ProviderId, ex.Message);
                upstreamResponse?.Dispose();
                continue;
            }

            // 成功：读取上游 SSE 流并转发（无 try-catch，OperationCanceledException 自然终止迭代）
            using (upstreamResponse) {
                using var reader = new System.IO.StreamReader(upstreamResponse.Content.ReadAsStream(cancellationToken));
                while (!cancellationToken.IsCancellationRequested) {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null) break;
                    if (line.StartsWith("data: ")) {
                        yield return $"data: {line[6..]}\n\n";
                        if (line[6..] == "[DONE]") yield break;
                    } else if (!string.IsNullOrWhiteSpace(line)) {
                        yield return $"{line}\n\n";
                    }
                }
            }
            yield break;
        }
        yield return SerializeSseError($"{lastError ?? "所有候选提供商均失败"}（已尝试: {string.Join(" -> ", FormatTried(tried))}）");
    }

    private static string SerializeSseError(string message) =>
        $"data: {JsonSerializer.Serialize(new { error = new { message, type = "upstream_error" } })}\n\n";

    private async Task<HttpResponseMessage> SendOnceStreamAsync(ProviderEndpointConfig endpoint, ChatProxyRequest request, string actualModel, CancellationToken cancellationToken) {
        var client = _httpClientFactory.CreateClient("openai-compat");
        var timeout = _routing.FirstTokenTimeoutMs > 0
            ? Math.Clamp(_routing.FirstTokenTimeoutMs / 1000.0, 1, 600)
            : Math.Clamp(endpoint.TimeoutSeconds, 10, 600);

        var httpRequest = BuildHttpRequest(endpoint, request, actualModel, stream: true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        if (!response.IsSuccessStatusCode) {
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) {
                var retryAfterMs = ParseRetryAfterMs(response);
                _health.ReportRateLimited(endpoint.ProviderId, retryAfterMs);
                throw new ProxyUpstreamException($"HTTP 429 rate limited, retry after {retryAfterMs}ms");
            }
            _health.ReportFailure(endpoint.ProviderId, $"HTTP {(int)response.StatusCode}");
            throw new ProxyUpstreamException($"HTTP {(int)response.StatusCode}: {Truncate(body, 300)}");
        }
        return response;
    }

    private HttpRequestMessage BuildHttpRequest(ProviderEndpointConfig endpoint, ChatProxyRequest request, string actualModel, bool stream) {
        var payload = new Dictionary<string, object> {
            ["model"] = actualModel,
            ["messages"] = request.Messages.Select(m => {
                var msg = new Dictionary<string, object> { ["role"] = m.Role, ["content"] = m.Content ?? "" };
                // 透传 tool_call_id（tool 角色消息必填）
                if (!string.IsNullOrWhiteSpace(m.ToolCallId))
                    msg["tool_call_id"] = m.ToolCallId;
                // 透传 assistant 的 tool_calls 数组（函数调用场景）
                if (!string.IsNullOrWhiteSpace(m.ToolCallsJson))
                    msg["tool_calls"] = System.Text.Json.JsonSerializer.Deserialize<object[]>(m.ToolCallsJson)!;
                return msg;
            }).ToArray(),
            ["stream"] = stream
        };
        if (request.Temperature.HasValue) payload["temperature"] = request.Temperature.Value;
        if (request.MaxTokens.HasValue) payload["max_tokens"] = request.MaxTokens.Value;

        var json = JsonSerializer.Serialize(payload);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.BaseUrl.TrimEnd('/')}/chat/completions") {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        _healthImpl.ApplyAuthHeaders(httpRequest, endpoint);
        return httpRequest;
    }

    private static ChatProxyResult Failure(string model, int attempts, string[] tried, string error) =>
        new(false, null, model, null, 0, attempts, tried, error);

    /// <summary>格式化尝试列表为 "providerId(原因)" 格式</summary>
    private static string[] FormatTried(List<(string ProviderId, string Reason)> tried) =>
        tried.Select(t => string.IsNullOrWhiteSpace(t.Reason) ? t.ProviderId : $"{t.ProviderId}({t.Reason})").ToArray();

    /// <summary>脱敏 API Key：显示前4位+****+后4位；空串显示"为空"</summary>
    private static string MaskKey(string? key) {
        if (string.IsNullOrWhiteSpace(key)) return "为空";
        if (key.Length <= 8) return "****";
        return $"{key[..4]}****{key[^4..]}";
    }

    /// <summary>从 ProxyUpstreamException 消息中提取短原因，并附带 Key 信息</summary>
    private static string ExtractShortReason(string message, string? resolvedKey, string? envVarName) {
        string reason;
        if (message.Contains("403")) reason = "余额不足";
        else if (message.Contains("401")) reason = "密钥无效";
        else if (message.Contains("429")) reason = "限流";
        else if (message.Contains("402")) reason = "付费问题";
        else if (message.Contains("400")) reason = "请求无效";
        else if (message.Contains("500") || message.Contains("502") || message.Contains("503")) reason = "服务异常";
        else if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) || message.Contains("超时")) reason = "超时";
        else if (message.Contains("connection", StringComparison.OrdinalIgnoreCase)) reason = "连接失败";
        else {
            var colonIdx = message.IndexOf(':');
            reason = colonIdx > 0 && colonIdx < message.Length - 1
                ? message[(colonIdx + 1)..].Trim()
                : message;
            reason = reason.Length > 20 ? reason[..20] + "..." : reason;
        }

        // 附带 Key 信息
        if (!string.IsNullOrWhiteSpace(envVarName))
            return $"{reason}：\"{envVarName}\" \"{MaskKey(resolvedKey)}\"";
        return $"{reason}：key 未配置";
    }

    /// <summary>上游返回非 2xx（计入失败评分并触发故障转移）</summary>
    private sealed class ProxyUpstreamException : Exception {
        public ProxyUpstreamException(string message) : base(message) { }
    }
}
