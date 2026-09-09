using System.ComponentModel;

using ModelContextProtocol.Server;

using OpenCodeMcpServer.Models;
using OpenCodeMcpServer.Services;

namespace OpenCodeMcpServer.Tools;

/// <summary>
/// 技能管理工具 - 发现、搜索、获取详情、安装、卸载、更新技能
/// </summary>
[McpServerToolType]
public sealed class SkillManagementTools {
    private readonly ISkillManagementService _skillService;

    public SkillManagementTools(ISkillManagementService skillService) {
        _skillService = skillService;
    }

    [McpServerTool, Description("发现可用技能")]
    public async Task<string> DiscoverSkills(
        [Description("分类过滤")] string? category = null,
        CancellationToken cancellationToken = default) {
        var skills = await _skillService.DiscoverSkillsAsync(category, cancellationToken);

        if (skills.Length == 0) {
            return "未找到技能";
        }

        var output = new System.Text.StringBuilder();
        output.AppendLine($"发现 {skills.Length} 个技能:");
        output.AppendLine();

        var grouped = skills.GroupBy(s => s.Category).OrderBy(g => g.Key);
        foreach (var group in grouped) {
            output.AppendLine($"## {group.Key}");
            foreach (var skill in group.OrderBy(s => s.Name)) {
                var installedTag = skill.IsInstalled ? " [已安装]" : "";
                output.AppendLine($"- **{skill.Name}** (`{skill.Id}`) v{skill.Version}{installedTag}");
                output.AppendLine($"  {skill.Description}");
                if (skill.Tags.Length > 0) {
                    output.AppendLine($"  标签: {string.Join(", ", skill.Tags)}");
                }
                output.AppendLine($"  依赖: {(skill.Dependencies.Length > 0 ? string.Join(", ", skill.Dependencies) : "无")}");
                output.AppendLine();
            }
        }

        return output.ToString();
    }

    [McpServerTool, Description("搜索技能")]
    public async Task<string> SearchSkills(
        [Description("搜索关键词")] string keyword,
        CancellationToken cancellationToken = default) {
        var skills = await _skillService.SearchSkillsAsync(keyword, cancellationToken);

        if (skills.Length == 0) {
            return $"未找到包含 '{keyword}' 的技能";
        }

        var output = new System.Text.StringBuilder();
        output.AppendLine($"找到 {skills.Length} 个匹配的技能:");
        output.AppendLine();

        foreach (var skill in skills) {
            var installedTag = skill.IsInstalled ? " [已安装]" : "";
            output.AppendLine($"## {skill.Name} (`{skill.Id}`) v{skill.Version}{installedTag}");
            output.AppendLine($"**分类**: {skill.Category}");
            output.AppendLine($"**描述**: {skill.Description}");
            output.AppendLine($"**作者**: {skill.Author}");
            output.AppendLine($"**仓库**: {skill.RepositoryUrl}");
            output.AppendLine($"**标签**: {string.Join(", ", skill.Tags)}");
            output.AppendLine();
        }

        return output.ToString();
    }

    [McpServerTool, Description("获取技能详情")]
    public async Task<string> GetSkill(
        [Description("技能 ID")] string skillId,
        CancellationToken cancellationToken = default) {
        var skill = await _skillService.GetSkillAsync(skillId, cancellationToken);
        if (skill == null) {
            return $"未找到技能: {skillId}";
        }

        var output = new System.Text.StringBuilder();
        output.AppendLine($"# {skill.Name} (`{skill.Id}`) v{skill.Version}");
        output.AppendLine($"**分类**: {skill.Category}");
        output.AppendLine($"**描述**: {skill.Description}");
        output.AppendLine($"**作者**: {skill.Author}");
        output.AppendLine($"**仓库**: {skill.RepositoryUrl}");
        output.AppendLine($"**发现时间**: {skill.DiscoveredAt:yyyy-MM-dd HH:mm:ss}");
        output.AppendLine($"**已安装**: {(skill.IsInstalled ? "是" : "否")}");
        output.AppendLine();
        output.AppendLine("## 标签");
        output.AppendLine(string.Join(", ", skill.Tags));
        output.AppendLine();
        output.AppendLine("## 依赖");
        if (skill.Dependencies.Length > 0) {
            foreach (var dep in skill.Dependencies) {
                output.AppendLine($"- {dep}");
            }
        } else {
            output.AppendLine("无");
        }
        output.AppendLine();
        output.AppendLine("## 安装信息");
        output.AppendLine($"- 安装命令: {skill.InstallInfo.InstallCommand}");
        output.AppendLine($"- 必需工具: {(skill.InstallInfo.RequiredTools.Length > 0 ? string.Join(", ", skill.InstallInfo.RequiredTools) : "无")}");
        output.AppendLine($"- 安装后脚本: {(string.IsNullOrWhiteSpace(skill.InstallInfo.PostInstallScript) ? "无" : "有")}");
        output.AppendLine($"- 环境变量: {(skill.InstallInfo.EnvironmentVariables.Count > 0 ? string.Join(", ", skill.InstallInfo.EnvironmentVariables.Select(kv => $"{kv.Key}={kv.Value}")) : "无")}");

        return output.ToString();
    }

    [McpServerTool, Description("安装技能")]
    public async Task<string> InstallSkill(
        [Description("技能 ID")] string skillId,
        CancellationToken cancellationToken = default) {
        var result = await _skillService.InstallSkillAsync(skillId, cancellationToken);

        var output = new System.Text.StringBuilder();
        output.AppendLine(result.Success ? "✅ 安装成功" : "❌ 安装失败");
        output.AppendLine($"**技能**: {result.SkillId}");
        output.AppendLine($"**消息**: {result.Message}");

        if (result.InstalledFiles?.Length > 0) {
            output.AppendLine("**安装文件**:");
            foreach (var file in result.InstalledFiles) {
                output.AppendLine($"- {file}");
            }
        }

        if (result.Errors?.Length > 0) {
            output.AppendLine("**错误**:");
            foreach (var error in result.Errors) {
                output.AppendLine($"- {error}");
            }
        }

        return output.ToString();
    }

    [McpServerTool, Description("卸载技能")]
    public async Task<string> UninstallSkill(
        [Description("技能 ID")] string skillId,
        CancellationToken cancellationToken = default) {
        var success = await _skillService.UninstallSkillAsync(skillId, cancellationToken);
        return success ? $"✅ 技能 {skillId} 卸载成功" : $"❌ 卸载失败: 技能不存在或已卸载";
    }

    [McpServerTool, Description("更新技能")]
    public async Task<string> UpdateSkill(
        [Description("技能 ID")] string skillId,
        CancellationToken cancellationToken = default) {
        var result = await _skillService.UpdateSkillAsync(skillId, cancellationToken);

        var output = new System.Text.StringBuilder();
        output.AppendLine(result.Success ? "✅ 更新成功" : "❌ 更新失败");
        output.AppendLine($"**技能**: {result.SkillId}");
        output.AppendLine($"**消息**: {result.Message}");

        return output.ToString();
    }

    [McpServerTool, Description("获取已安装技能列表")]
    public async Task<string> ListInstalledSkills(
        CancellationToken cancellationToken = default) {
        var skills = await _skillService.GetInstalledSkillsAsync(cancellationToken);

        if (skills.Length == 0) {
            return "暂无已安装技能";
        }

        var output = new System.Text.StringBuilder();
        output.AppendLine($"已安装 {skills.Length} 个技能:");
        output.AppendLine();

        foreach (var skill in skills.OrderBy(s => s.Name)) {
            output.AppendLine($"- **{skill.Name}** (`{skill.Id}`) v{skill.Version}");
            output.AppendLine($"  {skill.Description}");
            output.AppendLine();
        }

        return output.ToString();
    }

    [McpServerTool, Description("检查技能更新")]
    public async Task<string> CheckSkillUpdates(
        CancellationToken cancellationToken = default) {
        var updates = await _skillService.CheckUpdatesAsync(cancellationToken);

        if (updates.Length == 0) {
            return "所有技能均为最新版本";
        }

        var output = new System.Text.StringBuilder();
        output.AppendLine($"发现 {updates.Length} 个可更新技能:");
        output.AppendLine();

        foreach (var update in updates) {
            var breakingTag = update.IsBreakingChange ? " ⚠️ 破坏性变更" : "";
            output.AppendLine($"## {update.SkillId}");
            output.AppendLine($"- 当前版本: {update.CurrentVersion}");
            output.AppendLine($"- 最新版本: {update.LatestVersion}{breakingTag}");
            if (!string.IsNullOrWhiteSpace(update.Changelog)) {
                output.AppendLine($"- 更新日志: {update.Changelog}");
            }
            output.AppendLine();
        }

        return output.ToString();
    }

    [McpServerTool, Description("从注册表同步技能")]
    public async Task<string> SyncSkillsFromRegistry(
        [Description("注册表 URL")] string registryUrl,
        CancellationToken cancellationToken = default) {
        var count = await _skillService.SyncFromRegistryAsync(registryUrl, cancellationToken);
        return $"✅ 从 {registryUrl} 同步了 {count} 个技能";
    }

    [McpServerTool, Description("获取技能分类")]
    public string GetSkillCategories() {
        var categories = _skillService.GetCategories();
        return "可用技能分类:\n" + string.Join("\n", categories.Select(c => $"- {c}"));
    }
}