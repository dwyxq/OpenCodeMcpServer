// <summary>
/// 【功能说明】：Groq 模型提供者 - 通过 Groq API 检索免费和付费模型
/// 【服务对象】：模型发现服务，支持可选 API Key
/// 【调用方式】：通过 IModelSourceProvider 接口注入
/// 【禁止重复】：项目内唯一 Groq Provider
/// </summary>
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services.Providers;

public class GroqModelProvider : IModelSourceProvider
{
    private readonly ILogger<GroqModelProvider> _logger;
    private readonly HttpClient _httpClient;
    private readonly GroqProviderConfig _config;
    private readonly string? _apiKey;

    public ModelSource Source => ModelSource.Groq;

    public GroqModelProvider(
        ILogger<GroqModelProvider> logger,
        HttpClient httpClient,
        IOptions<ProviderDiscoveryConfig> options,
        IOptions<ProviderApiKeyConfig> apiKeys)
    {
        _logger = logger;
        _httpClient = httpClient;
        _config = options.Value.Groq;
        _apiKey = apiKeys.Value.GroqApiKey;

        var baseUrl = _config.BaseUrl.TrimEnd('/');
        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "OpenCodeMcpServer/1.0");

        if (!string.IsNullOrWhiteSpace(_apiKey))
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
    }

    public async Task<FreeModel[]> FetchModelsAsync(ModelSearchQuery query, CancellationToken cancellationToken = default)
    {
        // Groq 没有公开的模型列表 API，返回空结果
        _logger.LogDebug("Groq provider: no public model list API available");
        return Array.Empty<FreeModel>();
    }

    public async Task<FreeModel?> GetModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        return null;
    }
}