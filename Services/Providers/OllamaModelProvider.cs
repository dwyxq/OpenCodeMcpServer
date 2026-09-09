// <summary>
/// 【功能说明】：Ollama 模型提供者 - 通过官方 REST API 获取本地/远程 Ollama 实例的模型列表
/// 【服务对象】：模型发现服务，自动检索运行中 Ollama 实例中的所有模型
/// 【调用方式】：通过 IModelSourceProvider 接口注入，由 ModelDiscoveryService 调用
/// 【禁止重复】：项目内唯一 Ollama Provider，禁止在其他地方重复实现
/// </summary>
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services.Providers;

/// <summary>
/// Ollama 模型提供者 - 支持本地和远程 Ollama 实例
/// </summary>
public class OllamaModelProvider : IModelSourceProvider
{
    private readonly ILogger<OllamaModelProvider> _logger;
    private readonly HttpClient _httpClient;
    private readonly OllamaProviderConfig _config;
    private const string DefaultBaseUrl = "http://localhost:11434";

    public ModelSource Source => ModelSource.Ollama;

    public OllamaModelProvider(
        ILogger<OllamaModelProvider> logger,
        HttpClient httpClient,
        IOptions<ProviderDiscoveryConfig> options)
    {
        _logger = logger;
        _httpClient = httpClient;
        _config = options.Value.Ollama;
        _httpClient.BaseAddress = new Uri(_config.BaseUrl.TrimEnd('/'));
        _httpClient.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "OpenCodeMcpServer/1.0");
    }

    public async Task<FreeModel[]> FetchModelsAsync(ModelSearchQuery query, CancellationToken cancellationToken = default)
    {
        var models = new List<FreeModel>();

        try
        {
            // 调用 Ollama REST API: GET /api/tags
            var response = await _httpClient.GetStringAsync("/api/tags", cancellationToken);
            var json = JsonSerializer.Deserialize<JsonElement>(response);
            var tags = json.TryGetProperty("models", out var tagsProp) ? tagsProp : default;

            if (tags.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("Ollama API returned non-array 'models' field");
                return Array.Empty<FreeModel>();
            }

            foreach (var tag in tags.EnumerateArray())
            {
                try
                {
                    var model = ParseOllamaTag(tag);
                    if (model != null)
                    {
                        // 关键词过滤
                        if (!string.IsNullOrWhiteSpace(query.Keyword))
                        {
                            var keyword = query.Keyword.ToLowerInvariant();
                            if (!model.Name.ToLowerInvariant().Contains(keyword) &&
                                !model.Description.ToLowerInvariant().Contains(keyword))
                            {
                                continue;
                            }
                        }

                        if (query.FreeOnly != true || model.Pricing.IsFree)
                        {
                            models.Add(model);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse Ollama model tag");
                }
            }

            // 按搜索量排序（如果指定了）
            if (query.SortBy.ToLowerInvariant() == "updated" && query.SortDescending)
            {
                models.Sort((a, b) =>
                {
                    var aPulls = a.Metadata.TryGetValue("size_bytes", out var av) ? (long?)av as long? ?? 0 : 0;
                    var bPulls = b.Metadata.TryGetValue("size_bytes", out var bv) ? (long?)bv as long? ?? 0 : 0;
                    return bPulls.CompareTo(aPulls);
                });
            }
        }
        catch (Exception ex)
        {
            if (query.Source == ModelSource.Ollama)
            {
                _logger.LogError(ex, "Failed to fetch models from Ollama at {BaseUrl}", _config.BaseUrl);
            }
            else
            {
                _logger.LogDebug(ex, "Ollama provider unavailable at {BaseUrl}", _config.BaseUrl);
            }
        }

        return models.Take(query.MaxResults).ToArray();
    }

    public async Task<FreeModel?> GetModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        try
        {
            var name = modelId.StartsWith("ollama:") ? modelId[7..] : modelId;

            // 调用 Ollama REST API: POST /api/show
            var requestBody = JsonSerializer.Serialize(new { name });
            var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("/api/show", content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            var json = JsonSerializer.Deserialize<JsonElement>(responseText);

            return ParseOllamaShowResponse(json, name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get model {ModelId} from Ollama", modelId);
            return null;
        }
    }

    private FreeModel? ParseOllamaTag(JsonElement tag)
    {
        try
        {
            var name = tag.GetProperty("name").GetString() ?? "";
            var modelId = tag.TryGetProperty("model", out var modelProp) ? modelProp.GetString() ?? name : name;
            var modifiedAt = tag.TryGetProperty("modified_at", out var modifiedProp) && DateTime.TryParse(modifiedProp.GetString(), out var dt)
                ? dt
                : DateTime.UtcNow;
            var sizeBytes = tag.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;

            // 从 model property 解析详细信息
            var details = tag.TryGetProperty("details", out var detailsProp) ? detailsProp : default;
            var family = details.TryGetProperty("family", out var familyProp) ? familyProp.GetString() ?? "" : "";
            var parameterSize = details.TryGetProperty("parameter_size", out var paramSize) ? paramSize.GetString() ?? "" : "";
            var quantizationLevel = details.TryGetProperty("quantization_level", out var quantLevel) ? quantLevel.GetString() ?? "" : "";

            var contextLength = details.TryGetProperty("context_length", out var ctxProp) ? ctxProp.GetInt32() : 4096;

            var capabilities = new ModelCapabilities(
                SupportsChat: true,
                SupportsCompletion: true,
                SupportsEmbedding: false, // Ollama 通用模型不支持嵌入（除非特别标注）
                SupportsFunctionCalling: family.Contains("qwen2.5") || family.Contains("llama3") || family.Contains("mistral"),
                SupportsVision: family.Contains("llava") || family.Contains("moondream"),
                SupportsAudio: false,
                MaxContextTokens: contextLength,
                MaxOutputTokens: Math.Min(contextLength / 4, 8192),
                SupportedLanguages: new[] { "en", "zh", "multi" }
            );

            var metadata = new Dictionary<string, object>
            {
                ["name"] = name,
                ["model_id"] = modelId,
                ["family"] = family,
                ["parameter_size"] = parameterSize,
                ["quantization_level"] = quantizationLevel,
                ["size_bytes"] = sizeBytes,
                ["modified_at"] = modifiedAt.ToString("o")
            };

            return new FreeModel(
                Id: $"ollama:{modelId}",
                Name: name,
                Provider: "Ollama",
                Description: $"Ollama 模型: {name} ({parameterSize}, {quantizationLevel})",
                ModelUrl: $"https://ollama.com/library/{name}",
                ApiEndpoint: $"{_config.BaseUrl}/api/generate",
                License: null,
                Capabilities: capabilities,
                Pricing: new ModelPricing(
                    IsFree: true,
                    FreeTierDetails: "Free to run locally with Ollama",
                    InputCostPer1kTokens: 0,
                    OutputCostPer1kTokens: 0,
                    Currency: "USD"
                ),
                DiscoveredAt: modifiedAt,
                LastUpdated: modifiedAt,
                Metadata: metadata
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Ollama tag");
            return null;
        }
    }

    private FreeModel? ParseOllamaShowResponse(JsonElement json, string modelName)
    {
        try
        {
            var details = json.TryGetProperty("details", out var detailsProp) ? detailsProp : default;
            var family = details.TryGetProperty("family", out var familyProp) ? familyProp.GetString() ?? "" : "";
            var parameterSize = details.TryGetProperty("parameter_size", out var paramSize) ? paramSize.GetString() ?? "" : "";
            var quantizationLevel = details.TryGetProperty("quantization_level", out var quantLevel) ? quantLevel.GetString() ?? "" : "";
            var contextLength = details.TryGetProperty("context_length", out var ctxProp) ? ctxProp.GetInt32() : 4096;
            var size = json.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;
            var modifiedAt = json.TryGetProperty("modified_at", out var modifiedProp) && DateTime.TryParse(modifiedProp.GetString(), out var dt)
                ? dt
                : DateTime.UtcNow;

            var description = !string.IsNullOrWhiteSpace(json.GetProperty("modelfile").GetString())
                ? ExtractDescriptionFromModelfile(json.GetProperty("modelfile").GetString() ?? "")
                : $"Ollama 模型 {modelName} ({parameterSize}, {quantizationLevel})";

            var capabilities = new ModelCapabilities(
                SupportsChat: true,
                SupportsCompletion: true,
                SupportsEmbedding: false,
                SupportsFunctionCalling: family.Contains("qwen2.5") || family.Contains("llama3") || family.Contains("mistral"),
                SupportsVision: family.Contains("llava") || family.Contains("moondream"),
                SupportsAudio: false,
                MaxContextTokens: contextLength,
                MaxOutputTokens: Math.Min(contextLength / 4, 8192),
                SupportedLanguages: new[] { "en", "zh", "multi" }
            );

            var metadata = new Dictionary<string, object>
            {
                ["name"] = modelName,
                ["family"] = family,
                ["parameter_size"] = parameterSize,
                ["quantization_level"] = quantizationLevel,
                ["size_bytes"] = size,
                ["modified_at"] = modifiedAt.ToString("o"),
                ["license"] = json.TryGetProperty("license", out var license) ? license.GetString() ?? "" : ""
            };

            return new FreeModel(
                Id: $"ollama:{modelName}",
                Name: modelName,
                Provider: "Ollama",
                Description: description,
                ModelUrl: $"https://ollama.com/library/{modelName}",
                ApiEndpoint: $"{_config.BaseUrl}/api/generate",
                License: metadata.TryGetValue("license", out var lic) ? lic?.ToString() : null,
                Capabilities: capabilities,
                Pricing: new ModelPricing(
                    IsFree: true,
                    FreeTierDetails: "Free to run locally with Ollama",
                    InputCostPer1kTokens: 0,
                    OutputCostPer1kTokens: 0,
                    Currency: "USD"
                ),
                DiscoveredAt: modifiedAt,
                LastUpdated: modifiedAt,
                Metadata: metadata
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Ollama show response for {ModelName}", modelName);
            return null;
        }
    }

    private string ExtractDescriptionFromModelfile(string modelfile)
    {
        try
        {
            var lines = modelfile.Split('\n');
            foreach (var line in lines)
            {
                if (line.StartsWith("# ") || line.StartsWith("## "))
                {
                    return line.TrimStart('#', ' ').Trim();
                }
            }
            return "";
        }
        catch
        {
            return "";
        }
    }
}