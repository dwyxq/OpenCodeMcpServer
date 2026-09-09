// <summary>
/// 【功能说明】：模型代理与优化 MCP 工具 - 提供商目录查看、健康检查、OpenAI 兼容聊天转发（自动择优+故障转移）
/// 【服务对象】：OpenCode 客户端经 MCP 协议调用；依赖 IChatCompletionProxyService / IProviderHealthService
/// 【调用方式】：MCP 工具（WithToolsFromAssembly 自动发现）
/// 【禁止重复】：项目内唯一代理工具入口，转发逻辑全部委托服务层，禁止在此写业务
/// </summary>
using System.ComponentModel;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using OpenCodeMcpServer.Models;
using OpenCodeMcpServer.Services;

namespace OpenCodeMcpServer.Tools;

/// <summary>
/// 模型代理与自动优化工具（freellmapi 代理能力的 MCP 形态）
/// 5 工具（list_provider_models/check_provider_health/chat_completion/get_provider_status/get_routing_config
/// </summary>
[McpServerToolType]
public sealed class ProviderProxyTools {
    private readonly IChatCompletionProxyService _proxy;
    private readonly IProviderHealthService _health;
    private readonly RoutingConfig _routing;

    public ProviderProxyTools(IChatCompletionProxyService proxy, IProviderHealthService health, IOptions<McpServerConfig> options) {
        _proxy = proxy;
        _health = health;
        _routing = options.Value.Routing ?? new RoutingConfig();
    }

    [McpServerTool, Description(@"列出已配置的全部模型提供商（agnes/stepfun/sensenova/longcat/nvidia/opencode/openrouter/tokenrhythm 等）及其模型目录与健康状态")]
    public async Task<string> ListProviderModels(CancellationToken cancellationToken = default) {
        var catalog = await _proxy.GetProviderCatalogAsync(cancellationToken);
        if (catalog.Length == 0) return "未配置任何提供商，请在 appsettings.json 的 ProviderEndpoints:CustomProviders 中添加。";

        var output = new StringBuilder();
        output.AppendLine($"共 {catalog.Length} 个提供商（可用 {catalog.Count(c => c.Available)}）:");
        output.AppendLine();
        foreach (var p in catalog) {
            output.AppendLine($"## {p.DisplayName} ({p.ProviderId}) {(p.Available ? "[可用]" : "[不可用]")}");
            output.AppendLine($"**端点**: {p.BaseUrl}");
            output.AppendLine($"**密钥**: {(p.Keyless ? "免密钥" : p.HasApiKey ? "已配置" : "未配置（请设置环境变量）")}" + (p.Enabled ? "" : " **已禁用**"));
            output.AppendLine($"**健康评分**: {p.Score:0.#}" + (p.LastError != null ? $" | 最近错误: {p.LastError}" : ""));
            output.AppendLine($"**模型** ({p.Models.Length}): {string.Join(", ", p.Models)}");
            output.AppendLine();
        }
        return output.ToString();
    }

    [McpServerTool, Description("立即探测所有提供商健康状态（延迟/成功率），用于自动优化的选路依据；连续失败 3 次的提供商进入 15 分钟冷却")]
    public async Task<string> CheckProviderHealth(CancellationToken cancellationToken = default) {
        var report = await _health.ProbeAllAsync(cancellationToken);
        var output = new StringBuilder();
        output.AppendLine($"健康检查完成：{report.AvailableCount}/{report.TotalProbed} 可用");
        output.AppendLine();
        foreach (var p in report.Providers) {
            output.AppendLine($"- {(p.Available ? "✓" : "✗")} {p.DisplayName}: 评分 {p.Score:0.#} | 成功率 {p.SuccessRate:P0} ({p.SuccessCount}成/{p.FailCount}败) | " +
                (p.LastLatencyMs.HasValue ? $"延迟 {p.LastLatencyMs}ms" : "延迟未知") +
                (p.CooldownUntil.HasValue ? $" | 冷却至 {p.CooldownUntil:HH:mm} UTC" : "") +
                (p.LastError != null ? $" | {p.LastError}" : ""));
        }
        return output.ToString();
    }

    [McpServerTool, Description("代理转发 OpenAI 兼容 chat/completions 请求到免费提供商；未指定提供商时按健康评分自动择优，失败自动切换下一优提供商")]
    public async Task<string> ChatCompletion(
        [Description("模型 ID，如 kimi-k3、big-pickle、LongCat-2.0（也可写 provider/model 形式）")] string model,
        [Description("用户消息内容（与 systemPrompt 二选一；复杂对话用 messagesJson）")] string? message = null,
        [Description("系统提示词（可选）")] string? systemPrompt = null,
        [Description("完整消息列表 JSON，格式 [{\"role\":\"user\",\"content\":\"...\"}]（提供时忽略 message/systemPrompt）")] string? messagesJson = null,
[Description("指定提供商 ID（可选，空则自动择优）")] string? providerId = null,
        [Description("采样温度")] double? temperature = null,
        [Description("最大输出 tokens")] int? maxTokens = null,
        [Description("路由策略（auto=健康择优 / sticky=会话粘滞 / balanced=轮询均衡，空用 Routing 默认）")] string? strategy = null,
        [Description("会话 ID（sticky 粘滞策略下绑定到固定提供商）")] string? sessionId = null,
        [Description("必需能力标签列表（如 vision,function_calling，仅选满足该能力的模型提供商）")] string[]? capabilities = null,
        CancellationToken cancellationToken = default) {
        var messages = ParseMessages(message, systemPrompt, messagesJson);
        if (messages == null)
            return "参数错误：请提供 message 或 messagesJson";

var request = new ChatProxyRequest(
            Model: model,
            Messages: messages,
            ProviderId: providerId,
            Temperature: temperature,
            MaxTokens: maxTokens,
            Strategy: strategy,
            SessionId: sessionId,
            Capabilities: capabilities);

        var result = await _proxy.ChatAsync(request, cancellationToken);

        if (!result.Success)
            return $"❌ 转发失败（尝试 {result.Attempts} 次，已试: {(result.TriedProviders.Length > 0 ? string.Join(" → ", result.TriedProviders) : "无")}）：{result.Error}";

        var output = new StringBuilder();
        output.AppendLine($"✅ 经 {result.ProviderId} 调用 {model} 成功（延迟 {result.LatencyMs}ms，尝试 {result.Attempts} 次）");
        output.AppendLine();
        output.AppendLine("上游响应:");
        output.AppendLine(ExtractAssistantContent(result.ResponseJson));
        output.AppendLine();
        output.AppendLine("原始 JSON:");
        output.AppendLine(result.ResponseJson);
        return output.ToString();
    }

    [McpServerTool, Description("获取当前提供商健康排序报告（不发起新探测），查看各提供商评分、成功率、冷却状态")]
    public string GetProviderStatus() {
        var report = _health.GetReport();
        var ranked = _health.GetRankedProviders();
        var output = new StringBuilder();
        output.AppendLine($"提供商健康排序（最近全量探测: {(report.CheckedAt.HasValue ? report.CheckedAt.Value.ToString("yyyy-MM-dd HH:mm") + " UTC" : "从未，可调用 check_provider_health")}）:");
        output.AppendLine();
foreach (var p in ranked) {
            output.AppendLine($"{(p.Available ? "✓" : "✗")} [{p.Score:0.#}] {p.DisplayName} ({p.ProviderId}) 成功率={p.SuccessRate:P0} 连败={p.ConsecutiveFails}" +
                (p.CooldownUntil.HasValue ? $" 冷却中(至 {p.CooldownUntil:HH:mm} UTC)" : ""));
        }
        return output.ToString();
    }

    [McpServerTool, Description("获取当前生效的智能路由配置（策略/重试/预算/退避），来自 appsettings.json 的 Routing 节点")]
    public string GetRoutingConfig() {
        var output = new StringBuilder();
        output.AppendLine("智能路由配置（Routing 节点）:");
        output.AppendLine($"**策略**: {_routing.Strategy}（auto=健康择优 / sticky=会话粘滞 / balanced=轮询均衡）");
        output.AppendLine($"**最大重试/切换**: {_routing.MaxRetry} 次（最高尝试 {_routing.MaxRetry + 1} 次）");
        output.AppendLine($"**单次首 token 超时**: {(_routing.FirstTokenTimeoutMs > 0 ? _routing.FirstTokenTimeoutMs + "ms" : "用各提供商端点超时")}");
        output.AppendLine($"**整次请求总预算**: {(_routing.TotalBudgetMs > 0 ? _routing.TotalBudgetMs + "ms" : "不限制")}");
        output.AppendLine($"**失败切换退避**: {_routing.RetryBackoffMs}ms");
        output.AppendLine($"**热池阈值**: {_routing.HotPoolThreshold}（评分≥此值的可用提供商优先选用）");
        output.AppendLine($"**探索通道**: {(_routing.ExplorationEnabled ? "启用（热池空时降级探索补数据）" : "禁用")}");
        return output.ToString();
    }

    private static ChatMessage[]? ParseMessages(string? message, string? systemPrompt, string? messagesJson) {
        if (!string.IsNullOrWhiteSpace(messagesJson)) {
            try {
                var parsed = JsonSerializer.Deserialize<ChatMessage[]>(messagesJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return parsed is { Length: > 0 } ? parsed : null;
            } catch (JsonException) {
                return null;
            }
        }
        if (string.IsNullOrWhiteSpace(message)) return null;

        var list = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt)) list.Add(new ChatMessage("system", systemPrompt));
        list.Add(new ChatMessage("user", message));
        return list.ToArray();
    }

    /// <summary>从 OpenAI 标准响应提取助手回复文本（解析失败时返回原文）</summary>
    private static string ExtractAssistantContent(string? responseJson) {
        if (string.IsNullOrWhiteSpace(responseJson)) return "(空响应)";
        try {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0) {
                var choice = choices[0];
                if (choice.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content)) {
                    return content.GetString() ?? "(空内容)";
                }
            }
        } catch (JsonException) { }
        return responseJson;
    }
}
