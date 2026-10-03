using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services;

// <summary>
/// 【功能说明】：MoA（Mixture of Agents）生成式聚合服务 — 并行 N 路调用后，由裁判模型综合择优
/// 【服务对象】：ChatCompletionProxyService，在 mode=moa 时调用
/// 【调用方式】：依赖注入 IMoaAggregateService
/// 【禁止重复】：项目内唯一 MoA 聚合实现
/// </summary>
public interface IMoaAggregateService
{
    /// <summary>
    /// 并行发出 fanOut 个请求，收集响应后由裁判模型综合择优返回
    /// </summary>
    Task<ChatProxyResult> AggregateAsync(
        ChatProxyRequest request,
        List<ProviderEndpointConfig> candidates,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// MoA 生成式裁判聚合实现
/// </summary>
public class MoaAggregateService(
    ILogger<MoaAggregateService> logger,
    IHttpClientFactory httpClientFactory,
    ProviderHealthService healthImpl,
    IOptions<McpServerConfig> config) : IMoaAggregateService
{
    private readonly RoutingConfig _routing = config.Value.Routing;

    public async Task<ChatProxyResult> AggregateAsync(
        ChatProxyRequest request,
        List<ProviderEndpointConfig> candidates,
        CancellationToken cancellationToken = default)
    {
        var judgeModel = _routing.Aggregation.JudgeModel;
        var judgeProviderId = _routing.Aggregation.JudgeProviderId;
        if (string.IsNullOrWhiteSpace(judgeModel))
            return new ChatProxyResult(false, null, request.Model, null, 0, 0, [],
                "MoA 模式缺少 JudgeModel 配置，请在 Routing:Aggregation:JudgeModel 中指定");

        var fanOut = Math.Max(2, Math.Min(_routing.Aggregation.FanOut, candidates.Count));
        var topCandidates = candidates.Take(fanOut).ToList();
        if (topCandidates.Count < 2)
            return await SendSingleAsync(topCandidates[0], request,
                topCandidates[0].DefaultModel ?? topCandidates[0].Models!.FirstOrDefault()!, cancellationToken);

        logger.LogInformation("MoA aggregation: fanOut={FanOut} judge={Judge}", fanOut, judgeModel);

        // 并行发送所有候选请求
        var sendTasks = topCandidates.Select(async ep =>
        {
            var alias = request.Model == "SuperModel" || request.Model == "auto";
            var actualModel = alias
                ? (ep.DefaultModel ?? ep.Models?.FirstOrDefault()!)
                : StripPrefix(request.Model, ep);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await SendSingleAsync(ep, request, actualModel, cancellationToken);
                sw.Stop();
                return (Endpoint: ep, Result: result, LatencyMs: sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                logger.LogWarning(ex, "MoA send failed: {Provider}", ep.ProviderId);
                return (Endpoint: ep, Result: new ChatProxyResult(false, ep.ProviderId, actualModel, null, sw.ElapsedMilliseconds, 1, [ep.ProviderId], ex.Message), LatencyMs: sw.ElapsedMilliseconds);
            }
        });
        var sends = await Task.WhenAll(sendTasks);

        var successes = sends.Where(s => s.Result.Success).ToList();
        if (successes.Count == 0)
        {
            var errors = sends.Select(s => $"{s.Endpoint.ProviderId}({s.Result.Error ?? "unknown"})").ToArray();
            return new ChatProxyResult(false, null, request.Model, null, 0, fanOut, errors, "所有并行候选均失败");
        }

        // 构造裁判 prompt：汇总各候选回答，让裁判模型选出最佳或综合
        var judgeMessages = BuildJudgeMessages(request, successes);

        // 裁判请求：复用与原始请求相同参数，但模型换为 judgeModel
        var judgeRequest = new ChatProxyRequest(
            Model: judgeModel,
            Messages: judgeMessages,
            ProviderId: string.IsNullOrWhiteSpace(judgeProviderId) ? null : judgeProviderId,
            Temperature: request.Temperature,
            MaxTokens: request.MaxTokens,
            GroupId: request.GroupId,
            Strategy: request.Strategy);

        logger.LogInformation("MoA judge call: model={JudgeModel} candidates={Count}", judgeModel, successes.Count);

        // 调用裁判（复用现有串行逻辑）
        var judgeResult = await CallWithFallbackAsync(judgeRequest, cancellationToken);

        // 记录各候选健康状态
        foreach (var s in successes)
            healthImpl.ReportSuccess(s.Endpoint.ProviderId, s.LatencyMs);

        return judgeResult;
    }

    // <summary>
    /// 构造裁判 prompt：将各候选响应按 provider 标注，要求裁判择优
    /// </summary>
    private static ChatMessage[] BuildJudgeMessages(ChatProxyRequest original, List<(ProviderEndpointConfig, ChatProxyResult, long)> successes)
    {
        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine("以下是多个模型对同一问题的回答，请综合分析后给出最终答案：");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine($"用户问题：{original.Messages.LastOrDefault()?.Content ?? ""}");
        promptBuilder.AppendLine();

        foreach (var (ep, result, latency) in successes)
        {
            var content = ExtractContent(result.ResponseJson);
            promptBuilder.AppendLine($"【{ep.DisplayName ?? ep.ProviderId}】（延迟 {latency}ms）：{content}");
            promptBuilder.AppendLine();
        }
        promptBuilder.AppendLine("请综合以上各模型的回答，给出最优的最终答案。");

        return [
            new ChatMessage("system", "你是一个智能裁判，负责综合多个 AI 模型的回答，选出最优答案或生成综合回答。"),
            new ChatMessage("user", promptBuilder.ToString())
        ];
    }

    private static string? ExtractContent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var msg = choices[0].TryGetProperty("message", out var m) ? m : default(JsonElement);
                return msg.TryGetProperty("content", out var c) ? c.GetString() : null;
            }
        }
        catch { }
        return null;
    }

    private static string StripPrefix(string model, ProviderEndpointConfig ep) =>
        model.StartsWith(ep.ProviderId + "/", StringComparison.OrdinalIgnoreCase)
            ? model[(ep.ProviderId.Length + 1)..]
            : model;

    private async Task<ChatProxyResult> SendSingleAsync(
        ProviderEndpointConfig endpoint,
        ChatProxyRequest request,
        string actualModel,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("openai-compat");
        var timeout = _routing.FirstTokenTimeoutMs > 0
            ? Math.Clamp(_routing.FirstTokenTimeoutMs / 1000.0, 1, 600)
            : Math.Clamp(endpoint.TimeoutSeconds, 10, 600);
        var httpRequest = BuildRequest(endpoint, request, actualModel, stream: false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        try
        {
            var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
            return new ChatProxyResult(true, endpoint.ProviderId, actualModel, body, 0, 1, [], null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MoA send failed: {Provider}", endpoint.ProviderId);
            return new ChatProxyResult(false, endpoint.ProviderId, actualModel, null, 0, 1, [endpoint.ProviderId], ex.Message);
        }
    }

    private async Task<ChatProxyResult> CallWithFallbackAsync(ChatProxyRequest request, CancellationToken cancellationToken)
    {
        // 解析 provider/model 格式 → 精确匹配提供商
        var slashIdx = request.Model.IndexOf('/');
        string? parsedProviderId = null;
        string parsedModel = request.Model;
        if (slashIdx > 0 && !string.IsNullOrWhiteSpace(request.ProviderId))
        {
            parsedProviderId = request.ProviderId;
            parsedModel = request.Model;
        }
        else if (slashIdx > 0)
        {
            parsedProviderId = request.Model[..slashIdx];
            parsedModel = request.Model[(slashIdx + 1)..];
        }
        if (!string.IsNullOrWhiteSpace(request.ProviderId))
            parsedProviderId = request.ProviderId;

        var alias = request.Model == "SuperModel" || request.Model == "auto";
        var all = healthImpl.GetConfiguredProviders()
            .GroupBy(p => p.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(p => p.Enabled && !healthImpl.IsUnavailableForRouting(p.ProviderId))
            .ToDictionary(p => p.ProviderId, StringComparer.OrdinalIgnoreCase);

        // provider/model 格式：精确匹配
        if (!string.IsNullOrWhiteSpace(parsedProviderId))
        {
            if (!all.TryGetValue(parsedProviderId, out var ep))
                return new ChatProxyResult(false, null, parsedModel, null, 0, 1, [], $"未找到提供商 {parsedProviderId}");
            return await SendSingleAsync(ep, request, parsedModel, cancellationToken);
        }

        var ranked = healthImpl.GetRankedProviders();
        var candidates = ranked
            .Where(h => all.TryGetValue(h.ProviderId, out var ep) && (alias ? ResolveAliasModel(ep!) != null : true))
            .Select(h => all[h.ProviderId])
            .ToList();

        if (candidates.Count == 0)
            return new ChatProxyResult(false, null, request.Model, null, 0, 0, [], "无可用裁判提供商");

        foreach (var ep in candidates)
        {
            try
            {
                return await SendSingleAsync(ep, request,
                    alias ? (ep.DefaultModel ?? ep.Models!.FirstOrDefault()!) : StripPrefix(request.Model, ep),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MoA judge fallback failed: {Provider}", ep.ProviderId);
                _ = Task.Run(() => healthImpl.ReportFailure(ep.ProviderId, ex.Message));
            }
        }
        return new ChatProxyResult(false, null, request.Model, null, 0, candidates.Count,
            candidates.Select(c => c.ProviderId).ToArray(), "所有裁判候选均失败");
    }

    private static string? ResolveAliasModel(ProviderEndpointConfig ep) => ep.DefaultModel ?? ep.Models?.FirstOrDefault();

    private HttpRequestMessage BuildRequest(ProviderEndpointConfig endpoint, ChatProxyRequest request, string actualModel, bool stream)
    {
        var payload = new Dictionary<string, object> {
            ["model"] = actualModel,
            ["messages"] = request.Messages.Select(m => {
                var msg = new Dictionary<string, object> { ["role"] = m.Role, ["content"] = m.Content ?? "" };
                if (!string.IsNullOrWhiteSpace(m.ToolCallId)) msg["tool_call_id"] = m.ToolCallId;
                if (!string.IsNullOrWhiteSpace(m.ToolCallsJson))
                    msg["tool_calls"] = JsonSerializer.Deserialize<object[]>(m.ToolCallsJson)!;
                return msg;
            }).ToArray(),
            ["stream"] = stream
        };
        if (request.Temperature.HasValue) payload["temperature"] = request.Temperature.Value;
        if (request.MaxTokens.HasValue) payload["max_tokens"] = request.MaxTokens.Value;
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        healthImpl.ApplyAuthHeaders(httpRequest, endpoint);
        return httpRequest;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}
