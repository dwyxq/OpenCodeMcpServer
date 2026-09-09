// <summary>
/// 【功能说明】：Hugging Face 模型提供者 - 通过 HF REST API 检索公开和私有模型
/// 【服务对象】：模型发现服务，支持带/不带 API Key 访问
/// 【调用方式】：通过 IModelSourceProvider 接口注入，HF_TOKEN 环境变量优先
/// 【禁止重复】：项目内唯一 HF Provider，禁止在其他地方重复实现
/// </summary>
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services.Providers;

/// <summary>
/// HuggingFace 模型提供者
/// </summary>
public class HuggingFaceModelProvider : IModelSourceProvider
{
    private readonly ILogger<HuggingFaceModelProvider> _logger;
    private readonly HttpClient _httpClient;
    private readonly HuggingFaceProviderConfig _config;
    private readonly string? _apiKey;

    public ModelSource Source => ModelSource.HuggingFace;

    public HuggingFaceModelProvider(
        ILogger<HuggingFaceModelProvider> logger,
        HttpClient httpClient,
        IOptions<ProviderDiscoveryConfig> options,
        IOptions<ProviderApiKeyConfig> apiKeys)
    {
        _logger = logger;
        _httpClient = httpClient;
        _config = options.Value.HuggingFace;
        _apiKey = apiKeys.Value.HuggingFaceToken;

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
            // HuggingFace API: GET /api/models
            var queryParams = new List<string>
            {
                $"filter={Uri.EscapeDataString("text-generation")}",
                "sort=downloads",
                "direction=-1",
                $"limit={query.MaxResults}"
            };

            if (!string.IsNullOrWhiteSpace(query.Keyword))
            {
                queryParams.Add($"search={Uri.EscapeDataString(query.Keyword)}");
            }

            var url = $"models?{string.Join("&", queryParams)}";
            var response = await _httpClient.GetStringAsync(url, cancellationToken);
            var hfModels = JsonSerializer.Deserialize<JsonElement[]>(response);

            if (hfModels != null)
            {
                foreach (var model in hfModels)
                {
                    try
                    {
                        var freeModel = ParseHuggingFaceModel(model);
                        if (freeModel != null && (query.FreeOnly != true || freeModel.Pricing.IsFree))
                        {
                            models.Add(freeModel);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse Hugging Face model");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch models from Hugging Face");
        }

        return models.ToArray();
    }

    public async Task<FreeModel?> GetModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        try
        {
            var id = modelId.StartsWith("hf:") ? modelId[3..] : modelId;
            var response = await _httpClient.GetStringAsync($"models/{id}", cancellationToken);
            var model = JsonSerializer.Deserialize<JsonElement>(response);
            return ParseHuggingFaceModel(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get model {ModelId} from Hugging Face", modelId);
            return null;
        }
    }

    private FreeModel? ParseHuggingFaceModel(JsonElement model)
    {
        try
        {
            var id = model.GetProperty("modelId").GetString() ?? "";
            var name = model.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? id : id;
            var description = model.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";
            var tags = model.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array
                ? tagsProp.EnumerateArray().Select(t => t.GetString() ?? "").ToArray()
                : Array.Empty<string>();
            var downloads = model.TryGetProperty("downloads", out var dlProp) ? dlProp.GetInt32() : 0;
            var likes = model.TryGetProperty("likes", out var likesProp) ? likesProp.GetInt32() : 0;
            var createdAt = model.TryGetProperty("createdAt", out var createdProp) && DateTime.TryParse(createdProp.GetString(), out var dt) ? dt : DateTime.UtcNow;
            var updatedAt = model.TryGetProperty("lastModified", out var updatedProp) && DateTime.TryParse(updatedProp.GetString(), out var udt) ? udt : DateTime.UtcNow;

            // 判断是否免费（开源许可证模型免费）
            var licenseTag = tags.FirstOrDefault(t => t.StartsWith("license:"));
            var isFreeLicense = string.IsNullOrEmpty(licenseTag) ||
                                licenseTag.Contains("apache", StringComparison.OrdinalIgnoreCase) ||
                                licenseTag.Contains("mit", StringComparison.OrdinalIgnoreCase) ||
                                licenseTag.Contains("bsd", StringComparison.OrdinalIgnoreCase) ||
                                licenseTag.Contains("open", StringComparison.OrdinalIgnoreCase) ||
                                licenseTag.Contains("coppa", StringComparison.OrdinalIgnoreCase);
            var isPaidLicense = licenseTag != null &&
                                (licenseTag.Contains("commercial", StringComparison.OrdinalIgnoreCase) ||
                                 licenseTag.Contains("proprietary", StringComparison.OrdinalIgnoreCase));
            var isFree = isFreeLicense && !isPaidLicense;

            var capabilities = new ModelCapabilities(
                SupportsChat: tags.Contains("conversational") || tags.Contains("chat"),
                SupportsCompletion: true,
                SupportsEmbedding: tags.Contains("embedding"),
                SupportsFunctionCalling: tags.Contains("function-calling") || tags.Contains("tool-use"),
                SupportsVision: tags.Contains("vision") || tags.Contains("multimodal"),
                SupportsAudio: tags.Contains("audio") || tags.Contains("speech"),
                MaxContextTokens: GetMaxContextFromTags(tags),
                MaxOutputTokens: 4096,
                SupportedLanguages: tags.Where(t => t.StartsWith("lang:")).Select(t => t.Substring(5)).ToArray()
            );

            var metadata = new Dictionary<string, object>
            {
                ["downloads"] = downloads,
                ["likes"] = likes,
                ["tags"] = tags,
                ["pipeline_tag"] = model.TryGetProperty("pipeline_tag", out var pt) ? pt.GetString() ?? "" : ""
            };

            return new FreeModel(
                Id: $"hf:{id}",
                Name: name,
                Provider: "Hugging Face",
                Description: description,
                ModelUrl: $"https://huggingface.co/{id}",
                ApiEndpoint: $"https://api-inference.huggingface.co/models/{id}",
                License: licenseTag?[8..],
                Capabilities: capabilities,
                Pricing: new ModelPricing(
                    IsFree: isFree,
                    FreeTierDetails: isFree ? "Open source model, free to use locally or via HF Inference API" : null,
                    InputCostPer1kTokens: isFree ? 0 : null,
                    OutputCostPer1kTokens: isFree ? 0 : null,
                    Currency: "USD"
                ),
                DiscoveredAt: createdAt,
                LastUpdated: updatedAt,
                Metadata: metadata
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Hugging Face model");
            return null;
        }
    }

    private int GetMaxContextFromTags(string[] tags)
    {
        foreach (var tag in tags)
        {
            if (tag.StartsWith("context:") && int.TryParse(tag.Substring(8), out var ctx))
                return ctx;
            if (tag.EndsWith("k") && int.TryParse(tag[..^1], out var k))
                return k * 1024;
        }
        return 4096;
    }
}