using System.Text.Json.Serialization;

namespace OpenCodeMcpServer.Models;

/// <summary>
/// 免费模型信息
/// </summary>
public record FreeModel(
    string Id,
    string Name,
    string Provider,
    string Description,
    string ModelUrl,
    string? ApiEndpoint,
    string? License,
    ModelCapabilities Capabilities,
    ModelPricing Pricing,
    DateTime DiscoveredAt,
    DateTime? LastUpdated,
    Dictionary<string, object> Metadata
);

/// <summary>
/// 模型能力
/// </summary>
public record ModelCapabilities(
    bool SupportsChat,
    bool SupportsCompletion,
    bool SupportsEmbedding,
    bool SupportsFunctionCalling,
    bool SupportsVision,
    bool SupportsAudio,
    int MaxContextTokens,
    int MaxOutputTokens,
    string[] SupportedLanguages
);

/// <summary>
/// 模型定价信息
/// </summary>
public record ModelPricing(
    bool IsFree,
    string? FreeTierDetails,
    decimal? InputCostPer1kTokens,
    decimal? OutputCostPer1kTokens,
    string Currency
);

/// <summary>
/// 模型发现源
/// </summary>
public enum ModelSource
{
    HuggingFace,
    Ollama,
    GitHub,
    OpenRouter,
    TogetherAI,
    Groq,
    Nvidia,
    Custom,
    AgnesAiCn,
    Stepfun,
    AgnesAi,
    SenseNova,
    Longcat,
    NvidiaNim,
    OpencodeZen,
    Tokenrhythm
}

/// <summary>
/// 提示词模板
/// </summary>
public record PromptTemplate(
    string Id,
    string Name,
    string Description,
    string Category,
    string Template,
    string[] Variables,
    string[] Tags,
    string Author,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsBuiltIn,
    Dictionary<string, object> Metadata
);

/// <summary>
/// 技能信息
/// </summary>
public record SkillInfo(
    string Id,
    string Name,
    string Description,
    string Category,
    string Version,
    string Author,
    string RepositoryUrl,
    string[] Tags,
    string[] Dependencies,
    SkillInstallInfo InstallInfo,
    DateTime DiscoveredAt,
    bool IsInstalled,
    Dictionary<string, object> Metadata
);

/// <summary>
/// 技能安装信息
/// </summary>
public record SkillInstallInfo(
    string InstallCommand,
    string[] RequiredTools,
    string? PostInstallScript,
    Dictionary<string, string> EnvironmentVariables
);

/// <summary>
/// 搜索查询参数
/// </summary>
public record ModelSearchQuery(
    string? Keyword,
    ModelSource? Source,
    bool? FreeOnly,
    string[]? Capabilities,
    int? MinContextTokens,
    int MaxResults,
    int Page,
    string SortBy,
    bool SortDescending
);

/// <summary>
/// 搜索结果
/// </summary>
public record SearchResult<T>(
    T[] Items,
    int TotalCount,
    int Page,
    int PageSize,
    bool HasMore
);

/// <summary>
/// 提示词切换请求
/// </summary>
public record PromptSwitchRequest(
    string CurrentPromptId,
    string TargetPromptId,
    Dictionary<string, string>? VariableValues,
    bool PreserveContext
);

/// <summary>
/// 配置设置
/// </summary>
public record McpServerConfig(
    ModelDiscoveryConfig ModelDiscovery,
    ProviderDiscoveryConfig ProviderEndpoints,
    ProviderApiKeyConfig ProviderApiKeys,
    PromptManagementConfig PromptManagement,
    SkillManagementConfig SkillManagement,
    CacheConfig Cache,
    LoggingConfig Logging,
    RoutingConfig Routing
);

/// <summary>
/// 智能路由配置（预算控制 + 路由策略默认值）
/// </summary>
public record RoutingConfig(
    /// <summary>默认路由策略：auto 自动按健康评分择优 / sticky 会话粘滞 / balanced 轮询均衡</summary>
    string Strategy = "auto",
    /// <summary>单请求最大重试/切换次数（预算内）</summary>
    int MaxRetry = 3,
    /// <summary>单次请求首 token 超时毫秒（0 时用提供商端点 TimeoutSeconds）</summary>
    int FirstTokenTimeoutMs = 60000,
    /// <summary>整次 chat 请求总预算毫秒，含所有重试（0 表示不限制）</summary>
    int TotalBudgetMs = 60000,
    /// <summary>失败切换下一候选前的退避毫秒</summary>
    int RetryBackoffMs = 500,
    /// <summary>热池阈值：健康评分 ≥ 此值的可用提供商进入热池优先选用（0-100）</summary>
    int HotPoolThreshold = 80,
    /// <summary>热池为空时是否降级探索通道（挑选未达阈值/探测数据不足的候选以积累健康数据）</summary>
    bool ExplorationEnabled = true,
    /// <summary>提供商被限流（HTTP 429）后自动避让的退避毫秒（响应未带 Retry-After 时的默认值；带则用头值）</summary>
    int RateLimitRetryAfterMs = 30000
);

/// <summary>
/// 模型发现配置
/// </summary>
public record ModelDiscoveryConfig(
    bool Enabled,
    string[] EnabledSources,
    int RefreshIntervalHours,
    int MaxModelsPerSource,
    bool AutoUpdate,
    bool RefreshOnStartup,
    int WarmupDelaySeconds,
    string CacheDirectory,
    int HealthProbeIntervalHours
);

/// <summary>
/// 提示词管理配置
/// </summary>
public record PromptManagementConfig(
    bool Enabled,
    string PromptsDirectory,
    string[] BuiltInCategories,
    int MaxCustomPrompts,
    bool AllowVariableSubstitution
);

/// <summary>
/// 技能管理配置
/// </summary>
public record SkillManagementConfig(
    bool Enabled,
    string SkillsDirectory,
    string[] SkillRegistries,
    bool AutoDiscover,
    int MaxSkills
);

/// <summary>
/// 缓存配置
/// </summary>
public record CacheConfig(
    int DefaultTtlMinutes,
    int MaxCacheSizeMb,
    bool EnableCompression
);

/// <summary>
/// 日志配置
/// </summary>
public record LoggingConfig(
    string Level,
    bool EnableFileLogging,
    string LogDirectory,
    int MaxLogFiles,
    int MaxLogFileSizeMb
);