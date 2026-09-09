using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services;

/// <summary>
/// 模型发现服务接口
/// </summary>
public interface IModelDiscoveryService
{
    /// <summary>
    /// 搜索免费模型
    /// </summary>
    Task<SearchResult<FreeModel>> SearchModelsAsync(ModelSearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取模型详情
    /// </summary>
    Task<FreeModel?> GetModelAsync(string modelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 刷新模型缓存
    /// </summary>
    Task RefreshCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取支持的模型源
    /// </summary>
    ModelSource[] GetSupportedSources();

    /// <summary>
    /// 发现新模型（自动检索）
    /// </summary>
    Task<FreeModel[]> DiscoverNewModelsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 模型源提供者接口
/// </summary>
public interface IModelSourceProvider
{
    ModelSource Source { get; }
    Task<FreeModel[]> FetchModelsAsync(ModelSearchQuery query, CancellationToken cancellationToken = default);
    Task<FreeModel?> GetModelAsync(string modelId, CancellationToken cancellationToken = default);
}