// <summary>
/// 【功能说明】：OpenAI 兼容聊天代理接口 - 将 chat/completions 请求转发到配置的免费提供商，按健康评分自动选择与故障转移
/// 【服务对象】：ProviderProxyTools（MCP 工具层）
/// 【调用方式】：依赖注入 IChatCompletionProxyService
/// 【禁止重复】：项目内唯一代理转发实现（freellmapi 代理功能的 MCP 工具形态移植）
/// </summary>
namespace OpenCodeMcpServer.Services;

/// <summary>
/// OpenAI 兼容聊天代理接口
/// </summary>
public interface IChatCompletionProxyService
{
    /// <summary>转发聊天补全请求；未指定提供商时按健康评分自动选择并自动故障转移</summary>
    Task<ChatProxyResult> ChatAsync(ChatProxyRequest request, CancellationToken cancellationToken = default);

    /// <summary>列出所有配置提供商及其静态模型目录（含健康状态）</summary>
    Task<ProviderCatalogEntry[]> GetProviderCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>流式转发聊天补全请求：force stream=false 发上游，收到完整响应后包装成 SSE 流返回</summary>
    IAsyncEnumerable<string> ChatStreamAsync(ChatProxyRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 代理聊天请求
/// </summary>
public record ChatProxyRequest(
    /// <summary>模型 ID（必填，如 kimi-k3、big-pickle）</summary>
    string Model,
    /// <summary>对话消息列表（role/content）</summary>
    ChatMessage[] Messages,
    /// <summary>指定提供商 ID（可空，空则自动择优）</summary>
    string? ProviderId = null,
    /// <summary>采样温度（可空，使用提供商默认）</summary>
    double? Temperature = null,
    /// <summary>最大输出 token（可空）</summary>
    int? MaxTokens = null,
    /// <summary>路由策略：auto 按健康评分择优 / sticky 会话粘滞 / balanced 轮询均衡（空则用 Routing 配置默认）</summary>
    string? Strategy = null,
    /// <summary>会话 ID（sticky 粘滞策略下将请求绑定到固定提供商）</summary>
    string? SessionId = null,
    /// <summary>必需能力标签（如 vision/function_calling），仅选择提供满足能力模型的提供商（空则不过滤）</summary>
    string[]? Capabilities = null,
    /// <summary>分组 ID（如"高能力组"/"推理组"/"代码组"，空则不过滤）</summary>
    string? GroupId = null
);

/// <summary>
/// 聊天消息
/// </summary>
public record ChatMessage(
    string Role,
    string Content,
    /// <summary>工具调用 ID（tool 角色消息必填，配合 assistant 的 tool_calls）</summary>
    string? ToolCallId = null,
    /// <summary>assistant 消息的工具调用数组原始 JSON（如 [{"id":"call_x","type":"function","function":{...}}]）</summary>
    string? ToolCallsJson = null);

/// <summary>
/// 代理聊天结果
/// </summary>
public record ChatProxyResult(
    bool Success,
    string? ProviderId,
    string Model,
    string? ResponseJson,
    long LatencyMs,
    int Attempts,
    string[] TriedProviders,
    string? Error
);

/// <summary>
/// 提供商目录条目（配置 + 模型 + 健康）
/// </summary>
public record ProviderCatalogEntry(
    string ProviderId,
    string DisplayName,
    string BaseUrl,
    bool Enabled,
    bool HasApiKey,
    bool Keyless,
    string[] Models,
    string[]? Groups,
    bool Available,
    double Score,
    string? LastError
);
