// <summary>
/// 【功能说明】：MCP 服务器配置绑定 - 统一加载所有配置到强类型对象
/// 【服务对象】：全局配置管理，通过 IOptions&lt;McpServerConfig&gt; 依赖注入
/// 【调用方式】：Program.cs 中注册，McpServerConfigBinder.Load() 静态调用
/// 【禁止重复】：项目内唯一配置入口，禁止在代码中硬编码配置值
/// </summary>
using Microsoft.Extensions.Configuration;
using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Configuration;

/// <summary>
/// MCP 服务器配置绑定
/// </summary>
public static class McpServerConfigBinder
{
    public static McpServerConfig Load(IConfiguration configuration)
    {
        var config = new McpServerConfig(
            ModelDiscovery: BindModelDiscovery(configuration),
            ProviderEndpoints: BindProviderEndpoints(configuration),
            ProviderApiKeys: BindProviderApiKeys(configuration),
            PromptManagement: configuration.GetSection("PromptManagement").Get<PromptManagementConfig>() ?? DefaultPromptManagementConfig(),
            SkillManagement: configuration.GetSection("SkillManagement").Get<SkillManagementConfig>() ?? DefaultSkillManagementConfig(),
            Cache: configuration.GetSection("Cache").Get<CacheConfig>() ?? DefaultCacheConfig(),
            Logging: configuration.GetSection("Logging").Get<LoggingConfig>() ?? DefaultLoggingConfig(),
            Routing: BindRouting(configuration),
            HttpServer: configuration.GetSection("HttpServer").Get<HttpServerConfig>() ?? new HttpServerConfig()
        );

        return config;
    }

    /// <summary>
    /// 手工绑定路由配置：int/string 字段需逐个读取并回退默认值（record 位置参数缺键时 Get&lt;&gt; 会得 0/空，导致默认值失效）
    /// </summary>
    private static RoutingConfig BindRouting(IConfiguration configuration)
    {
        var section = configuration.GetSection("Routing");
        var def = DefaultRoutingConfig();
        return new RoutingConfig(
            Strategy: string.IsNullOrWhiteSpace(section["Strategy"]) ? def.Strategy : section["Strategy"]!,
            MaxRetry: section["MaxRetry"] == null ? def.MaxRetry : int.Parse(section["MaxRetry"]!),
            FirstTokenTimeoutMs: section["FirstTokenTimeoutMs"] == null ? def.FirstTokenTimeoutMs : int.Parse(section["FirstTokenTimeoutMs"]!),
            TotalBudgetMs: section["TotalBudgetMs"] == null ? def.TotalBudgetMs : int.Parse(section["TotalBudgetMs"]!),
            RetryBackoffMs: section["RetryBackoffMs"] == null ? def.RetryBackoffMs : int.Parse(section["RetryBackoffMs"]!),
            HotPoolThreshold: section["HotPoolThreshold"] == null ? def.HotPoolThreshold : int.Parse(section["HotPoolThreshold"]!),
            ExplorationEnabled: section["ExplorationEnabled"] == null ? def.ExplorationEnabled : bool.Parse(section["ExplorationEnabled"]!),
            RateLimitRetryAfterMs: section["RateLimitRetryAfterMs"] == null ? def.RateLimitRetryAfterMs : int.Parse(section["RateLimitRetryAfterMs"]!),
            ModelAlias: string.IsNullOrWhiteSpace(section["ModelAlias"]) ? def.ModelAlias : section["ModelAlias"]!
        );
    }

    private static RoutingConfig DefaultRoutingConfig() => new(
        Strategy: "auto",
        MaxRetry: 3,
        FirstTokenTimeoutMs: 60000,
        TotalBudgetMs: 60000,
        RetryBackoffMs: 500,
        HotPoolThreshold: 80,
        ExplorationEnabled: true,
        RateLimitRetryAfterMs: 30000,
        ModelAlias: "SuperModel"
    );

    private static ProviderDiscoveryConfig BindProviderEndpoints(IConfiguration configuration)
    {
        return new ProviderDiscoveryConfig(
            HuggingFace: configuration.GetSection("ProviderEndpoints:HuggingFace").Get<HuggingFaceProviderConfig>() ?? DefaultHuggingFaceProviderConfig(),
            Ollama: configuration.GetSection("ProviderEndpoints:Ollama").Get<OllamaProviderConfig>() ?? DefaultOllamaProviderConfig(),
            OpenRouter: configuration.GetSection("ProviderEndpoints:OpenRouter").Get<OpenRouterProviderConfig>() ?? DefaultOpenRouterProviderConfig(),
            TogetherAi: configuration.GetSection("ProviderEndpoints:TogetherAi").Get<TogetherAiProviderConfig>() ?? DefaultTogetherAiProviderConfig(),
            Groq: configuration.GetSection("ProviderEndpoints:Groq").Get<GroqProviderConfig>() ?? DefaultGroqProviderConfig(),
            CustomProviders: configuration.GetSection("ProviderEndpoints:CustomProviders").Get<Dictionary<string, ProviderEndpointConfig>>() ?? new Dictionary<string, ProviderEndpointConfig>()
        );
    }

    private static ProviderApiKeyConfig BindProviderApiKeys(IConfiguration configuration)
    {
        return new ProviderApiKeyConfig(
            HuggingFaceToken: ReadKey(configuration, "HF_TOKEN"),
            OpenRouterApiKey: ReadKey(configuration, "OPENROUTER_API_KEY"),
            TogetherAiApiKey: ReadKey(configuration, "TOGETHER_API_KEY"),
            GroqApiKey: ReadKey(configuration, "GROQ_API_KEY"),
            AnthropicApiKey: ReadKey(configuration, "ANTHROPIC_API_KEY"),
            CustomProviders: configuration.GetSection("ApiKeys:CustomProviders").Get<Dictionary<string, string>>() ?? new Dictionary<string, string>()
        );
    }

    /// <summary>
    /// 读取 API Key：ApiKeys 节 → 根节 → 环境变量；空串视为未配置（继续回退，避免占位符短路环境变量注入）
    /// </summary>
    private static string? ReadKey(IConfiguration configuration, string name)
    {
        var value = configuration[$"ApiKeys:{name}"]
            ?? configuration[name]
            ?? Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static ModelDiscoveryConfig DefaultModelDiscoveryConfig() => new(
        Enabled: true,
        EnabledSources: new[] { "HuggingFace", "Ollama", "OpenRouter" },
        RefreshIntervalHours: 24,
        MaxModelsPerSource: 100,
        AutoUpdate: true,
        RefreshOnStartup: true,
        WarmupDelaySeconds: 5,
        CacheDirectory: Path.Combine(AppContext.BaseDirectory, "cache", "models"),
        HealthProbeIntervalHours: 6
    );

    /// <summary>
    /// 手工绑定模型发现配置：int 字段需逐个读取并回退默认值（record 位置参数缺键时 Get&lt;&gt; 会得 0，导致默认值失效）
    /// </summary>
    private static ModelDiscoveryConfig BindModelDiscovery(IConfiguration configuration)
    {
        var section = configuration.GetSection("ModelDiscovery");
        var def = DefaultModelDiscoveryConfig();
        return new ModelDiscoveryConfig(
            Enabled: section["Enabled"] == null ? def.Enabled : bool.Parse(section["Enabled"]!),
            EnabledSources: section.GetSection("EnabledSources").Get<string[]>() ?? def.EnabledSources,
            RefreshIntervalHours: section["RefreshIntervalHours"] == null ? def.RefreshIntervalHours : int.Parse(section["RefreshIntervalHours"]!),
            MaxModelsPerSource: section["MaxModelsPerSource"] == null ? def.MaxModelsPerSource : int.Parse(section["MaxModelsPerSource"]!),
            AutoUpdate: section["AutoUpdate"] == null ? def.AutoUpdate : bool.Parse(section["AutoUpdate"]!),
            RefreshOnStartup: section["RefreshOnStartup"] == null ? def.RefreshOnStartup : bool.Parse(section["RefreshOnStartup"]!),
            WarmupDelaySeconds: section["WarmupDelaySeconds"] == null ? def.WarmupDelaySeconds : int.Parse(section["WarmupDelaySeconds"]!),
            CacheDirectory: string.IsNullOrWhiteSpace(section["CacheDirectory"]) ? def.CacheDirectory : section["CacheDirectory"]!,
            HealthProbeIntervalHours: section["HealthProbeIntervalHours"] == null ? def.HealthProbeIntervalHours : int.Parse(section["HealthProbeIntervalHours"]!)
        );
    }

    private static PromptManagementConfig DefaultPromptManagementConfig() => new(
        Enabled: true,
        PromptsDirectory: Path.Combine(AppContext.BaseDirectory, "prompts"),
        BuiltInCategories: new[] { "开发", "架构", "AI", "学习", "运维" },
        MaxCustomPrompts: 1000,
        AllowVariableSubstitution: true
    );

    private static SkillManagementConfig DefaultSkillManagementConfig() => new(
        Enabled: true,
        SkillsDirectory: Path.Combine(AppContext.BaseDirectory, "skills"),
        SkillRegistries: new[] { "https://raw.githubusercontent.com/opencode/skill-registry/main/skills.json" },
        AutoDiscover: true,
        MaxSkills: 500
    );

    private static CacheConfig DefaultCacheConfig() => new(
        DefaultTtlMinutes: 30,
        MaxCacheSizeMb: 100,
        EnableCompression: true
    );

    private static LoggingConfig DefaultLoggingConfig() => new(
        Level: "Information",
        EnableFileLogging: true,
        LogDirectory: Path.Combine(AppContext.BaseDirectory, "logs"),
        MaxLogFiles: 10,
        MaxLogFileSizeMb: 10
    );

    private static HuggingFaceProviderConfig DefaultHuggingFaceProviderConfig() => new();
    private static OllamaProviderConfig DefaultOllamaProviderConfig() => new();
    private static OpenRouterProviderConfig DefaultOpenRouterProviderConfig() => new();
    private static TogetherAiProviderConfig DefaultTogetherAiProviderConfig() => new();
    private static GroqProviderConfig DefaultGroqProviderConfig() => new();
}
