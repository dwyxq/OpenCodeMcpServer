using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services;

// <summary>
/// 【功能说明】：共识投票聚合服务 — 并行发 N 个请求，取内容相似度最高的响应（多数投票）
/// 【服务对象】：ChatCompletionProxyService，在 mode=vote 时调用
/// 【调用方式】：依赖注入 IVoteAggregateService
/// 【禁止重复】：项目内唯一共识投票实现
/// </summary>
public interface IVoteAggregateService
{
    /// <summary>
    /// 并行发出 fanOut 个请求，收集响应后投票选出共识结果
    /// </summary>
    Task<ChatProxyResult> AggregateAsync(
        ChatProxyRequest request,
        List<ProviderEndpointConfig> candidates,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 共识投票聚合实现
/// </summary>
public class ParallelVoteService(
    ILogger<ParallelVoteService> logger,
    IHttpClientFactory httpClientFactory,
    IProviderHealthService health,
    ProviderHealthService healthImpl,
    IOptions<McpServerConfig> config) : IVoteAggregateService
{
    private readonly RoutingConfig _routing = config.Value.Routing;

    public async Task<ChatProxyResult> AggregateAsync(
        ChatProxyRequest request,
        List<ProviderEndpointConfig> candidates,
        CancellationToken cancellationToken = default)
    {
        var fanOut = Math.Max(1, Math.Min(_routing.Aggregation.FanOut, candidates.Count));
        var topCandidates = candidates.Take(fanOut).ToList();
        if (topCandidates.Count == 1)
            return await SendSingleAsync(topCandidates[0], request, cancellationToken);

        logger.LogInformation("Vote aggregation: fanOut={FanOut} providers={Providers}",
            fanOut, string.Join(", ", topCandidates.Select(p => p.ProviderId)));

        var tasks = topCandidates.Select(async ep =>
        {
            var alias = IsAlias(request.Model);
            var actualModel = alias ? ResolveAliasModel(ep) ?? ep.Models!.FirstOrDefault()! : StripPrefix(request.Model, ep);
            var result = await SendSingleAsync(ep, request, actualModel, cancellationToken);
            return (Endpoint: ep, Result: result);
        });

        var results = await Task.WhenAll(tasks);
        var successful = results.Where(r => r.Result.Success).ToList();

        if (successful.Count == 0)
        {
            var allErrors = results.Select(r => $"{r.Endpoint.ProviderId}({r.Result.Error ?? "unknown"})")
                .ToArray();
            return new ChatProxyResult(
                false, null, request.Model, null, 0, fanOut, allErrors,
                $"所有 {fanOut} 路并行请求均失败");
        }

        // 投票：提取各响应的 content，计算相似度矩阵，找共识
        var voteResult = PerformVote(successful, request);

        // 记录健康状态
        foreach (var (ep, result) in successful)
            health.ReportSuccess(ep.ProviderId, result.LatencyMs);

        return voteResult;
    }

    // <summary>
    /// 对成功响应进行共识投票：比较 content 相似度，返回得票最高的响应
    /// </summary>
    private ChatProxyResult PerformVote(List<(ProviderEndpointConfig Endpoint, ChatProxyResult Result)> successful, ChatProxyRequest request)
    {
        if (successful.Count == 1)
            return successful[0].Result;

        var threshold = _routing.Aggregation.ConsensusThreshold;

        // 解析各响应的 content
        var contents = successful.Select(pair =>
        {
            var content = ExtractContent(pair.Result.ResponseJson);
            return (ProviderId: pair.Endpoint.ProviderId, Content: content, Result: pair.Result);
        }).ToList();

        // 两两比较相似度，计算每路得分
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < contents.Count; i++)
        {
            scores.TryAdd(contents[i].ProviderId, 0);
            for (int j = i + 1; j < contents.Count; j++)
            {
                var sim = ComputeSimilarity(contents[i].Content ?? "", contents[j].Content ?? "");
                scores[contents[i].ProviderId] = scores.GetValueOrDefault(contents[i].ProviderId, 0) + sim;
                scores[contents[j].ProviderId] = scores.GetValueOrDefault(contents[j].ProviderId, 0) + sim;
            }
        }

        // 选取得分最高者
        var winner = scores.OrderByDescending(s => s.Value).First();
        var winnerEntry = contents.First(c => c.ProviderId.Equals(winner.Key, StringComparison.OrdinalIgnoreCase));

        // 检查是否达到共识阈值
        var maxScore = winner.Value;
        var avgScore = scores.Values.Average();
        var consensus = avgScore > 0 && maxScore / avgScore >= threshold;

        if (!consensus)
        {
            logger.LogWarning("Vote no consensus: maxScore={Max} avgScore={Avg} threshold={Threshold}",
                maxScore, avgScore, threshold);
        }

        logger.LogInformation("Vote winner: {ProviderId} score={Score} consensus={Consensus}",
            winner.Key, winner.Value, consensus);

        return winnerEntry.Result;
    }

    // <summary>
    /// 计算两段文本的余弦相似度（基于 token 共现）
    /// </summary>
    private static double ComputeSimilarity(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
        var tokensA = Tokenize(a);
        var tokensB = Tokenize(b);
        if (tokensA.Count == 0 || tokensB.Count == 0) return 0;

        var freqA = tokensA.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        var freqB = tokensB.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        var allTerms = freqA.Keys.Union(freqB.Keys).ToHashSet();

        double dot = 0, magA = 0, magB = 0;
        foreach (var term in allTerms)
        {
            double va = freqA.TryGetValue(term, out var av) ? av : 0;
            double vb = freqB.TryGetValue(term, out var bv) ? bv : 0;
            dot += va * vb;
            magA += va * va;
            magB += vb * vb;
        }
        return (magA > 0 && magB > 0) ? dot / (Math.Sqrt(magA) * Math.Sqrt(magB)) : 0;
    }

    // <summary>
    /// 简单 tokenization：按空白和标点分割，转小写
    /// </summary>
    private static List<string> Tokenize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return sb.ToString().ToLowerInvariant()
            .Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .ToList();
    }

    // <summary>
    /// 从 JSON 响应中提取 content 字符串
    /// </summary>
    private static string? ExtractContent(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var msg = choices[0].TryGetProperty("message", out var m) ? m : default(JsonElement);
                return msg.TryGetProperty("content", out var c) ? c.GetString() : null;
            }
        }
        catch { /* ignore parse errors */ }
        return null;
    }

    private static bool IsAlias(string model) =>
        string.Equals(model, "SuperModel", StringComparison.OrdinalIgnoreCase)
        || string.Equals(model, "auto", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveAliasModel(ProviderEndpointConfig endpoint) =>
        endpoint.DefaultModel ?? endpoint.Models?.FirstOrDefault();

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
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();
            if (!response.IsSuccessStatusCode)
                throw new Exception($"HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
            return new ChatProxyResult(true, endpoint.ProviderId, actualModel, body, sw.ElapsedMilliseconds, 1, [], null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogWarning(ex, "Vote single failed: {Provider}", endpoint.ProviderId);
            return new ChatProxyResult(false, endpoint.ProviderId, actualModel, null, sw.ElapsedMilliseconds, 1, [endpoint.ProviderId], ex.Message);
        }
    }

    private async Task<ChatProxyResult> SendSingleAsync(
        ProviderEndpointConfig endpoint,
        ChatProxyRequest request,
        CancellationToken cancellationToken) =>
        await SendSingleAsync(endpoint, request,
            IsAlias(request.Model) ? (ResolveAliasModel(endpoint) ?? endpoint.Models!.FirstOrDefault()!)
                : StripPrefix(request.Model, endpoint),
            cancellationToken);

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
