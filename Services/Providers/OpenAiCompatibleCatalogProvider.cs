// <summary>
/// 【功能说明】：OpenAI 兼容模型目录提供者 - 统一从配置的 CustomProviders（
/// agnes/stepfun/sensenova/longcat/nvidia/opencode/openrouter/tokenrhythm 等）拉取模型列表
/// 【服务对象】：ModelDiscoveryService（经 IModelSourceProvider 自动聚合，Source=Custom）
/// 【调用方式】：依赖注入注册为 IModelSourceProvider 单例；优先 GET {baseUrl}/models，失败回退静态 Models 配置
/// 【禁止重复】：项目内唯一 OpenAI 兼容目录实现，新增同类提供商只需改 appsettings.json 配置，禁止再写新 Provider 类
/// </summary>
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services.Providers;

/// <summary>
/// OpenAI 兼容模型目录提供者（配置驱动，覆盖所有 OpenAI 兼容免费提供商）
/// </summary>
public class OpenAiCompatibleCatalogProvider : IModelSourceProvider
{
    private static readonly TimeSpan CatalogFetchTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<OpenAiCompatibleCatalogProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProviderHealthService _health;
    private readonly ProviderHealthService _healthImpl;

    public ModelSource Source => ModelSource.Custom;

    public OpenAiCompatibleCatalogProvider(
        ILogger<OpenAiCompatibleCatalogProvider> logger,
        IHttpClientFactory httpClientFactory,
        IProviderHealthService health)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _health = health;
        _healthImpl = (ProviderHealthService)health;
    }

    /// <inheritdoc/>
    public async Task<FreeModel[]> FetchModelsAsync(ModelSearchQuery query, CancellationToken cancellationToken = default)
    {
        var providers = _healthImpl.GetConfiguredProviders();
        var tasks = providers.Select(p => FetchFromProviderAsync(p, cancellationToken));
        var results = await Task.WhenAll(tasks);
        var models = results.SelectMany(x => x).ToList();

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            models = models.Where(m =>
                m.Name.Contains(query.Keyword, StringComparison.OrdinalIgnoreCase) ||
                m.Id.Contains(query.Keyword, StringComparison.OrdinalIgnoreCase) ||
                m.Provider.Contains(query.Keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (query.FreeOnly == true)
        {
            models = models.Where(m => m.Pricing.IsFree).ToList();
        }

        return models.Take(query.MaxResults).ToArray();
    }

    /// <inheritdoc/>
    public async Task<FreeModel?> GetModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var all = await FetchModelsAsync(new ModelSearchQuery(null, null, null, null, null, 500, 1, "name", false), cancellationToken);
        return all.FirstOrDefault(m =>
            string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Id, $"custom:{modelId}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>从单个提供商拉取目录，失败时回退到静态配置列表</summary>
    private async Task<FreeModel[]> FetchFromProviderAsync(ProviderEndpointConfig endpoint, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("openai-compat");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.BaseUrl.TrimEnd('/')}/models");
            _healthImpl.ApplyAuthHeaders(request, endpoint);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(CatalogFetchTimeout);
            using var response = await client.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cts.Token);
                var models = ParseModelsJson(json, endpoint);
                if (models.Count > 0) return models.ToArray();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "Catalog fetch failed for {ProviderId}, falling back to static models", endpoint.ProviderId);
        }

        // 回退：静态配置的模型目录
        return (endpoint.Models ?? Array.Empty<string>())
            .Select(m => BuildStaticModel(endpoint, m))
            .ToArray();
    }

    /// <summary>解析 OpenAI 标准 GET /models 响应 { "data": [ { "id": ... } ] }</summary>
    private List<FreeModel> ParseModelsJson(string json, ProviderEndpointConfig endpoint)
    {
        var models = new List<FreeModel>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return models;

        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            models.Add(BuildStaticModel(endpoint, id));
        }
        return models;
    }

    private static FreeModel BuildStaticModel(ProviderEndpointConfig endpoint, string modelId) => new(
        Id: $"custom:{endpoint.ProviderId}:{modelId}",
        Name: modelId,
        Provider: endpoint.DisplayName ?? endpoint.ProviderId,
        Description: $"{endpoint.DisplayName ?? endpoint.ProviderId} 提供的 {modelId}",
        ModelUrl: endpoint.BaseUrl,
        ApiEndpoint: $"{endpoint.BaseUrl.TrimEnd('/')}/chat/completions",
        License: "Free tier (provider terms apply)",
        Capabilities: new ModelCapabilities(
            SupportsChat: true,
            SupportsCompletion: true,
            SupportsEmbedding: false,
            SupportsFunctionCalling: true,
            SupportsVision: false,
            SupportsAudio: false,
            MaxContextTokens: 128000,
            MaxOutputTokens: 8192,
            SupportedLanguages: new[] { "zh", "en" }),
        Pricing: new ModelPricing(true, "免费提供商（以提供商条款为准）", 0, 0, "USD"),
        DiscoveredAt: DateTime.UtcNow,
        LastUpdated: DateTime.UtcNow,
        Metadata: new Dictionary<string, object> { ["provider_id"] = endpoint.ProviderId });
}
