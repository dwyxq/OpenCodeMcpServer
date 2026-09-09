using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenCodeMcpServer.Models;
using OpenCodeMcpServer.Configuration;

namespace OpenCodeMcpServer.Services;

/// <summary>
/// 模型发现服务实现
/// </summary>
public class ModelDiscoveryService : IModelDiscoveryService {
    private readonly ILogger<ModelDiscoveryService> _logger;
    private readonly IMemoryCache _cache;
    private readonly McpServerConfig _config;
    private readonly IEnumerable<IModelSourceProvider> _providers;

    public ModelDiscoveryService(
        ILogger<ModelDiscoveryService> logger,
        IMemoryCache cache,
        IOptions<McpServerConfig> config,
        IEnumerable<IModelSourceProvider> providers) {
        _logger = logger;
        _cache = cache;
        _config = config.Value;
        _providers = providers;
    }

    public ModelSource[] GetSupportedSources() {
        return _providers.Select(p => p.Source).ToArray();
    }

    public async Task<SearchResult<FreeModel>> SearchModelsAsync(ModelSearchQuery query, CancellationToken cancellationToken = default) {
        var cacheKey = $"models_search_{JsonSerializer.Serialize(query)}";
        if (_cache.TryGetValue(cacheKey, out SearchResult<FreeModel>? cached)) {
            return cached!;
        }

        var enabledSources = _config.ModelDiscovery.EnabledSources
            .Select(s => Enum.Parse<ModelSource>(s, true))
            .ToHashSet();

        var tasks = _providers
            .Where(p => enabledSources.Contains(p.Source))
            .Select(p => p.FetchModelsAsync(query, cancellationToken));

        var results = await Task.WhenAll(tasks);
        var allModels = results.SelectMany(r => r).ToList();

        // 过滤免费模型
        if (query.FreeOnly == true) {
            allModels = allModels.Where(m => m.Pricing.IsFree).ToList();
        }

        // 关键词过滤
        if (!string.IsNullOrWhiteSpace(query.Keyword)) {
            var keyword = query.Keyword.ToLowerInvariant();
            allModels = allModels.Where(m =>
                m.Name.ToLowerInvariant().Contains(keyword) ||
                m.Description.ToLowerInvariant().Contains(keyword) ||
                m.Provider.ToLowerInvariant().Contains(keyword) ||
                m.Metadata.Values.Any(v => v?.ToString()?.ToLowerInvariant().Contains(keyword) == true)
            ).ToList();
        }

        // 能力过滤
        if (query.Capabilities?.Length > 0) {
            allModels = allModels.Where(m => query.Capabilities!.All(c =>
                c.Equals("chat", StringComparison.OrdinalIgnoreCase) && m.Capabilities.SupportsChat ||
                c.Equals("completion", StringComparison.OrdinalIgnoreCase) && m.Capabilities.SupportsCompletion ||
                c.Equals("embedding", StringComparison.OrdinalIgnoreCase) && m.Capabilities.SupportsEmbedding ||
                c.Equals("function_calling", StringComparison.OrdinalIgnoreCase) && m.Capabilities.SupportsFunctionCalling ||
                c.Equals("vision", StringComparison.OrdinalIgnoreCase) && m.Capabilities.SupportsVision ||
                c.Equals("audio", StringComparison.OrdinalIgnoreCase) && m.Capabilities.SupportsAudio
            )).ToList();
        }

        // 上下文长度过滤
        if (query.MinContextTokens.HasValue) {
            allModels = allModels.Where(m => m.Capabilities.MaxContextTokens >= query.MinContextTokens.Value).ToList();
        }

        // 排序
        allModels = query.SortBy.ToLowerInvariant() switch {
            "name" => query.SortDescending
                ? allModels.OrderByDescending(m => m.Name).ToList()
                : allModels.OrderBy(m => m.Name).ToList(),
            "provider" => query.SortDescending
                ? allModels.OrderByDescending(m => m.Provider).ToList()
                : allModels.OrderBy(m => m.Provider).ToList(),
            "context" => query.SortDescending
                ? allModels.OrderByDescending(m => m.Capabilities.MaxContextTokens).ToList()
                : allModels.OrderBy(m => m.Capabilities.MaxContextTokens).ToList(),
            "updated" => query.SortDescending
                ? allModels.OrderByDescending(m => m.LastUpdated ?? m.DiscoveredAt).ToList()
                : allModels.OrderBy(m => m.LastUpdated ?? m.DiscoveredAt).ToList(),
            _ => allModels.OrderByDescending(m => m.DiscoveredAt).ToList()
        };

        var totalCount = allModels.Count;
        var pageSize = query.MaxResults > 0 ? query.MaxResults : 50;
        var page = query.Page > 0 ? query.Page : 1;
        var items = allModels.Skip((page - 1) * pageSize).Take(pageSize).ToArray();

        var result = new SearchResult<FreeModel>(
            items,
            totalCount,
            page,
            pageSize,
            page * pageSize < totalCount
        );

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(_config.Cache.DefaultTtlMinutes));
        return result;
    }

    public async Task<FreeModel?> GetModelAsync(string modelId, CancellationToken cancellationToken = default) {
        var cacheKey = $"model_{modelId}";
        if (_cache.TryGetValue(cacheKey, out FreeModel? cached)) {
            return cached;
        }

        foreach (var provider in _providers) {
            var model = await provider.GetModelAsync(modelId, cancellationToken);
            if (model != null) {
                _cache.Set(cacheKey, model, TimeSpan.FromMinutes(_config.Cache.DefaultTtlMinutes));
                return model;
            }
        }

        return null;
    }

    public async Task RefreshCacheAsync(CancellationToken cancellationToken = default) {
        _logger.LogInformation("Refreshing model cache from all sources...");
        var query = new ModelSearchQuery(
            Keyword: null,
            Source: null,
            FreeOnly: true,
            Capabilities: null,
            MinContextTokens: null,
            MaxResults: _config.ModelDiscovery.MaxModelsPerSource,
            Page: 1,
            SortBy: "updated",
            SortDescending: true
        );

        var tasks = _providers
            .Where(p => _config.ModelDiscovery.EnabledSources.Contains(p.Source.ToString(), StringComparer.OrdinalIgnoreCase))
            .Select(p => p.FetchModelsAsync(query, cancellationToken));

        await Task.WhenAll(tasks);
        _logger.LogInformation("Model cache refreshed successfully");
    }

    public async Task<FreeModel[]> DiscoverNewModelsAsync(CancellationToken cancellationToken = default) {
        _logger.LogInformation("Discovering new free models...");

        var query = new ModelSearchQuery(
            Keyword: null,
            Source: null,
            FreeOnly: true,
            Capabilities: null,
            MinContextTokens: null,
            MaxResults: 100,
            Page: 1,
            SortBy: "updated",
            SortDescending: true
        );

        var tasks = _providers
            .Where(p => _config.ModelDiscovery.EnabledSources.Contains(p.Source.ToString(), StringComparer.OrdinalIgnoreCase))
            .Select(p => p.FetchModelsAsync(query, cancellationToken));

        var results = await Task.WhenAll(tasks);
        var allModels = results.SelectMany(r => r).ToList();

        // 找出新发现的模型（不在缓存中）
        var newModels = new List<FreeModel>();
        foreach (var model in allModels) {
            var cacheKey = $"model_{model.Id}";
            if (!_cache.TryGetValue(cacheKey, out FreeModel? _)) {
                newModels.Add(model);
                _cache.Set(cacheKey, model, TimeSpan.FromMinutes(_config.Cache.DefaultTtlMinutes));
            }
        }

        _logger.LogInformation("Discovered {Count} new free models", newModels.Count);
        return newModels.ToArray();
    }
}