// <summary>
/// 【功能说明】：模型提供商 API 密钥配置 - 支持 HuggingFace、OpenRouter、TogetherAI 等需要认证的平台
/// 【服务对象】：全局通用，所有模型源提供者使用
/// 【调用方式】：通过 IOptions&lt;ProviderApiKeyConfig&gt; 依赖注入
/// 【禁止重复】：项目内唯一配置，禁止在 Provider 中硬编码 Key
/// </summary>
using System.Text.Json.Serialization;

namespace OpenCodeMcpServer.Models;

/// <summary>
/// 模型提供商 API 密钥配置
/// </summary>
public record ProviderApiKeyConfig(
    /// <summary>HuggingFace Token - 用于访问私有模型和更高速率限制</summary>
    [property: JsonPropertyName("HF_TOKEN")]
    string? HuggingFaceToken,

    /// <summary>OpenRouter API Key - 用于访问 OpenRouter 聚合平台</summary>
    [property: JsonPropertyName("OPENROUTER_API_KEY")]
    string? OpenRouterApiKey,

    /// <summary>TogetherAI API Key - 用于访问 Together AI 模型</summary>
    [property: JsonPropertyName("TOGETHER_API_KEY")]
    string? TogetherAiApiKey,

    /// <summary>Groq API Key - 用于访问 Groq 高速推理</summary>
    [property: JsonPropertyName("GROQ_API_KEY")]
    string? GroqApiKey,

    /// <summary>Anthropic API Key - 用于访问 Claude 模型</summary>
    [property: JsonPropertyName("ANTHROPIC_API_KEY")]
    string? AnthropicApiKey,

    /// <summary>自定义提供商映射列表</summary>
    [property: JsonPropertyName("CustomProviders")]
    Dictionary<string, string> CustomProviders
);

/// <summary>
/// 单个提供商的完整配置（端点 + 密钥 + OpenAI 兼容扩展）
/// </summary>
public record ProviderEndpointConfig(
    string ProviderId = "",
    string BaseUrl = "",
    string? ApiKey = null,
    string? ApiKeyEnvVar = null,
    int TimeoutSeconds = 60,
    bool Enabled = true,

    /// <summary>显示名称（缺省时使用配置节键名）</summary>
    string? DisplayName = null,

    /// <summary>静态模型目录（提供商不支持 /models 端点时的回退列表）</summary>
    string[]? Models = null,

    /// <summary>额外请求头（如 OpenRouter 的 HTTP-Referer / X-Title）</summary>
    Dictionary<string, string>? ExtraHeaders = null,

    /// <summary>是否免密钥访问（true 时完全省略 Authorization 头）</summary>
    bool Keyless = false,

    /// <summary>模型能力标签映射（模型名 → 能力标签列表，如 vision/function_calling；未配置则该模型视为全能力，不参与过滤）</summary>
    Dictionary<string, string[]>? Capabilities = null,

    /// <summary>别名路由（model=SuperModel/auto）时该提供商实际发送的模型；缺省回退 Models 列表首个</summary>
    string? DefaultModel = null
);

/// <summary>
/// 模型发现扩展配置（API 端点、认证、超时）
/// </summary>
public record ProviderDiscoveryConfig(
    /// <summary>HuggingFace 配置</summary>
    HuggingFaceProviderConfig HuggingFace,

    /// <summary>Ollama 本地配置</summary>
    OllamaProviderConfig Ollama,

    /// <summary>OpenRouter 配置</summary>
    OpenRouterProviderConfig OpenRouter,

    /// <summary>TogetherAI 配置</summary>
    TogetherAiProviderConfig TogetherAi,

    /// <summary>Groq 配置</summary>
    GroqProviderConfig Groq,

    /// <summary>自定义提供商</summary>
    Dictionary<string, ProviderEndpointConfig> CustomProviders
);

/// <summary>
/// HuggingFace 提供商配置
/// </summary>
public record HuggingFaceProviderConfig(
    string BaseUrl = "https://huggingface.co/api",
    string? ApiKeyEnvVar = "HF_TOKEN",
    int TimeoutSeconds = 30,
    bool Enabled = true
);

/// <summary>
/// Ollama 本地实例配置（支持远程 Ollama 服务器）
/// </summary>
public record OllamaProviderConfig(
    /// <summary>Ollama 服务器地址，默认本地</summary>
    string BaseUrl = "http://localhost:11434",
    /// <summary>Ollama Web 界面地址（用于元数据）</summary>
    string LibraryBaseUrl = "https://ollama.com/library",
    int TimeoutSeconds = 30,
    bool Enabled = true
);

/// <summary>
/// OpenRouter 提供商配置
/// </summary>
public record OpenRouterProviderConfig(
    string BaseUrl = "https://openrouter.ai/api/v1",
    string? ApiKeyEnvVar = "OPENROUTER_API_KEY",
    int TimeoutSeconds = 30,
    bool Enabled = true
);

/// <summary>
/// TogetherAI 提供商配置
/// </summary>
public record TogetherAiProviderConfig(
    string BaseUrl = "https://api.together.xyz",
    string? ApiKeyEnvVar = "TOGETHER_API_KEY",
    int TimeoutSeconds = 30,
    bool Enabled = true
);

/// <summary>
/// Groq 提供商配置
/// </summary>
public record GroqProviderConfig(
    string BaseUrl = "https://api.groq.com/openai/v1",
    string? ApiKeyEnvVar = "GROQ_API_KEY",
    int TimeoutSeconds = 30,
    bool Enabled = true
);
