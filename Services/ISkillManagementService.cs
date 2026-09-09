using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services;

/// <summary>
/// 技能管理服务接口
/// </summary>
public interface ISkillManagementService
{
    /// <summary>
    /// 发现可用技能
    /// </summary>
    Task<SkillInfo[]> DiscoverSkillsAsync(string? category = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 搜索技能
    /// </summary>
    Task<SkillInfo[]> SearchSkillsAsync(string keyword, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取技能详情
    /// </summary>
    Task<SkillInfo?> GetSkillAsync(string skillId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 安装技能
    /// </summary>
    Task<SkillInstallResult> InstallSkillAsync(string skillId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 卸载技能
    /// </summary>
    Task<bool> UninstallSkillAsync(string skillId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新技能
    /// </summary>
    Task<SkillInstallResult> UpdateSkillAsync(string skillId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取已安装技能列表
    /// </summary>
    Task<SkillInfo[]> GetInstalledSkillsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 检查技能更新
    /// </summary>
    Task<SkillUpdateInfo[]> CheckUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 从注册表同步技能
    /// </summary>
    Task<int> SyncFromRegistryAsync(string registryUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取技能分类
    /// </summary>
    string[] GetCategories();
}

/// <summary>
/// 技能安装结果
/// </summary>
public record SkillInstallResult(
    bool Success,
    string SkillId,
    string Message,
    string[]? InstalledFiles,
    string[]? Errors
);

/// <summary>
/// 技能更新信息
/// </summary>
public record SkillUpdateInfo(
    string SkillId,
    string CurrentVersion,
    string LatestVersion,
    string Changelog,
    bool IsBreakingChange
);