// <summary>
/// 【功能说明】：OpenRouter 模型提供者 - 通过 OpenRouter API 检索聚合的免费和付费模型
/// 【服务对象】：模型发现服务，支持可选 API Key（免费模型无需 Key）
/// 【调用方式】：通过 IModelSourceProvider 接口注入，OPENROUTER_API_KEY 环境变量优先
/// 【禁止重复】：项目内唯一 OpenRouter Provider，禁止在其他地方重复实现
/// </summary>
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services.Providers;

/// <summary>
/// OpenRouter 模型提供者
/// </summary>
public class OpenRouterModelProvider : IModelSourceProvider
{
    private readonly ILogger<OpenRouterModelProvider> _logger;
    private readonly HttpClient _httpClient;
    private readonly OpenRouterProviderConfig _config;
    private readonly string? _apiKey;

    public ModelSource Source => ModelSource.OpenRouter;

    public OpenRouterModelProvider(
        ILogger<OpenRouterModelProvider> logger,
        HttpClient httpClient,
        IOptions<ProviderDiscoveryConfig> options,
        IOptions<ProviderApiKeyConfig> apiKeys)
    {
        _logger = logger;
        _httpClient = httpClient;
        _config = options.Value.OpenRouter;
        _apiKey = apiKeys.Value.OpenRouterApiKey;

        var baseUrl = _config.BaseUrl.TrimEnd('/');
        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "OpenCodeMcpServer/1.0");

        // 如有 API Key，添加到请求头
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }
    }

    public async Task<FreeModel[]> FetchModelsAsync(ModelSearchQuery query, CancellationToken cancellationToken = default)
    {
        var models = new List<FreeModel>();

        try
        {
            var response = await _httpClient.GetStringAsync("models", cancellationToken);
            var json = JsonSerializer.Deserialize<JsonElement>(response);
            var data = json.GetProperty("data");

            foreach (var model in data.EnumerateArray())
            {
                try
                {
                    var freeModel = ParseOpenRouterModel(model);
                    if (freeModel != null && (query.FreeOnly != true || freeModel.Pricing.IsFree))
                    {
                        if (string.IsNullOrWhiteSpace(query.Keyword) ||
                            freeModel.Name.Contains(query.Keyword, StringComparison.OrdinalIgnoreCase) ||
                            freeModel.Description.Contains(query.Keyword, StringComparison.OrdinalIgnoreCase))
                        {
                            models.Add(freeModel);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse OpenRouter model");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch models from OpenRouter");
        }

        return models.Take(query.MaxResults).ToArray();
    }

    public async Task<FreeModel?> GetModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        try
        {
            var id = modelId.StartsWith("openrouter:") ? modelId[11..] : modelId;
            var response = await _httpClient.GetStringAsync($"models/{id}", cancellationToken);
            var model = JsonSerializer.Deserialize<JsonElement>(response);
            return ParseOpenRouterModel(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get model {ModelId} from OpenRouter", modelId);
            return null;
        }
    }

    private FreeModel? ParseOpenRouterModel(JsonElement model)
    {
        try
        {
            var id = model.GetProperty("id").GetString() ?? "";
            var name = model.GetProperty("name").GetString() ?? id;
            var description = model.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";
            var contextLength = model.TryGetProperty("context_length", out var ctx) ? ctx.GetInt32() : 4096;
            var pricing = model.TryGetProperty("pricing", out var pricingProp) ? pricingProp : default;

            // 检查是否为免费（prompt 和 completion 价格都为 0）
            var isFree = pricing.ValueKind == JsonValueKind.Object &&
                         pricing.TryGetProperty("prompt", out var promptCost) &&
                         pricing.TryGetProperty("completion", out var completionCost) &&
                         promptCost.GetDecimal() == 0 && completionCost.GetDecimal() == 0;

            var architecture = model.TryGetProperty("architecture", out var arch) ? arch : default;
            var modality = architecture.ValueKind == JsonValueKind.Object && architecture.TryGetProperty("modality", out var mod) ? mod : default;
            var modalityStr = modality.ValueKind == JsonValueKind.Array
                ? modality.EnumerateArray().Select(m => m.GetString() ?? "").ToArray()
                : Array.Empty<string>();

            var capabilities = new ModelCapabilities(
                SupportsChat: true,
                SupportsCompletion: true,
                SupportsEmbedding: modalityStr.Contains("embedding"),
                SupportsFunctionCalling: modalityStr.Contains("tools") || modalityStr.Contains("function_calling"),
                SupportsVision: modalityStr.Contains("image") || modalityStr.Contains("vision"),
                SupportsAudio: modalityStr.Contains("audio"),
                MaxContextTokens: contextLength,
                MaxOutputTokens: Math.Min(contextLength / 4, 8192),
                SupportedLanguages: new[] { "en", "zh", "multi" }
            );

            var pricingInfo = new ModelPricing(
                IsFree: isFree,
                FreeTierDetails: isFree ? "Free tier available on OpenRouter" : null,
                InputCostPer1kTokens: pricing.ValueKind == JsonValueKind.Object && pricing.TryGetProperty("prompt", out var promptPrice) ? (decimal?)promptPrice.GetDecimal() : null,
                OutputCostPer1kTokens: pricing.ValueKind == JsonValueKind.Object && pricing.TryGetProperty("completion", out var completionPrice) ? (decimal?)completionPrice.GetDecimal() : null,
                Currency: "USD"
            );

            var metadata = new Dictionary<string, object>
            {
                ["model_id"] = id,
                ["created"] = model.TryGetProperty("created", out var created) ? created.GetInt64() : 0,
                ["top_provider"] = model.TryGetProperty("top_provider", out var tp) ? tp.GetString() ?? "" : ""
            };

            return new FreeModel(
                Id: $"openrouter:{id}",
                Name: name,
                Provider: "OpenRouter",
                Description: description,
                ModelUrl: $"https://openrouter.ai/models/{id}",
                ApiEndpoint: $"{_config.BaseUrl}/chat/completions",
                License: "Varies by model",
                Capabilities: capabilities,
                Pricing: pricingInfo,
                DiscoveredAt: DateTime.UtcNow,
                LastUpdated: DateTime.UtcNow,
                Metadata: metadata
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse OpenRouter model");
            return null;
        }
    }
}