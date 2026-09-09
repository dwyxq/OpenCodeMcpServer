// <summary>
/// 【功能说明】：提供商健康服务接口 - 健康探测、评分排序、失败冷却（自动优化核心）
/// 【服务对象】：OpenAI 兼容提供商代理与模型目录服务
/// 【调用方式】：依赖注入 IProviderHealthService
/// 【禁止重复】：项目内唯一健康评分实现，禁止在其他服务重复统计
/// </summary>
using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services;

/// <summary>
/// 提供商健康服务接口
/// </summary>
public interface IProviderHealthService
{
    /// <summary>探测所有启用的 OpenAI 兼容提供商（GET /models）</summary>
    Task<ProviderHealthReport> ProbeAllAsync(CancellationToken cancellationToken = default);

    /// <summary>获取当前健康状态（不发起新探测）</summary>
    ProviderHealthReport GetReport();

    /// <summary>按健康评分从高到低返回可用提供商（冷却期内的排在末尾且标记不可用）</summary>
    IReadOnlyList<ProviderHealthEntry> GetRankedProviders();

    /// <summary>请求成功反馈（代理转发后回报，纳入统计）</summary>
    void ReportSuccess(string providerId, long latencyMs);

    /// <summary>请求失败反馈（连续失败达到阈值进入冷却期）</summary>
    void ReportFailure(string providerId, string reason);

    /// <summary>解析提供商 API Key：ApiKeys:CustomProviders → ApiKey 配置 → ApiKeyEnvVar 环境变量</summary>
    string? ResolveApiKey(ProviderEndpointConfig endpoint);

    /// <summary>判断健康条目是否达到热池资格（可用且评分 ≥ 阈值）</summary>
    bool IsQualifiedForHotPool(ProviderHealthEntry entry);

    /// <summary>从候选中挑选一个探索通道提供商（未达热池阈值或探测数据不足，用于积累健康数据）</summary>
    ProviderHealthEntry? PickExplorationModel(IReadOnlyList<ProviderHealthEntry> candidatesCounter);

    /// <summary>上报提供商被限流（HTTP 429），进入限流避让期；retryAfterMs 为避让时长，<=0 用配置默认</summary>
    void ReportRateLimited(string providerId, int retryAfterMs);

    /// <summary>判断提供商当前是否处于限流避让期</summary>
    bool IsRateLimited(string providerId);

    /// <summary>获取当前处于限流状态的提供商列表（含避让到期时间），用于报告展示</summary>
    IReadOnlyList<ProviderRateLimitEntry> GetRateLimitReport();

    /// <summary>清除全部限流状态（供探测成功后重置）</summary>
    void ClearRateLimits();
}

/// <summary>
/// 单个提供商健康状态
/// </summary>
public record ProviderHealthEntry(
    string ProviderId,
    string DisplayName,
    bool Enabled,
    bool Available,
    int SuccessCount,
    int FailCount,
    int ConsecutiveFails,
    long? LastLatencyMs,
    double SuccessRate,
    double Score,
    DateTime? LastCheckedAt,
    DateTime? CooldownUntil,
    string? LastError
);

/// <summary>
/// 健康检查整体报告
/// </summary>
public record ProviderHealthReport(
    DateTime? CheckedAt,
    int TotalProbed,
    int AvailableCount,
    ProviderHealthEntry[] Providers
);

/// <summary>
/// 单个提供商的限流避让状态
/// </summary>
public record ProviderRateLimitEntry(
    string ProviderId,
    DateTime RateLimitedAt,
    DateTime RetryAfterAt,
    int RetryAfterMs
);
