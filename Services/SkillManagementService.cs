using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenCodeMcpServer.Models;
using OpenCodeMcpServer.Configuration;

namespace OpenCodeMcpServer.Services;

/// <summary>
/// 技能管理服务实现 - 发现、搜索、安装、卸载、更新技能
/// </summary>
public class SkillManagementService : ISkillManagementService {
    private readonly ILogger<SkillManagementService> _logger;
    private readonly IMemoryCache _cache;
    private readonly McpServerConfig _config;
    private readonly HttpClient _httpClient;
    private readonly string _skillsDirectory;
    private readonly Dictionary<string, SkillInfo> _skillRegistry;

    public SkillManagementService(
        ILogger<SkillManagementService> logger,
        IMemoryCache cache,
        IOptions<McpServerConfig> config,
        HttpClient httpClient) {
        _logger = logger;
        _cache = cache;
        _config = config.Value;
        _httpClient = httpClient;
        _skillsDirectory = Path.GetFullPath(_config.SkillManagement.SkillsDirectory);
        _skillRegistry = InitializeSkillRegistry();

        Directory.CreateDirectory(_skillsDirectory);
    }

    public async Task<SkillInfo[]> DiscoverSkillsAsync(string? category = null, CancellationToken cancellationToken = default) {
        var cacheKey = $"skills_discover_{category ?? "all"}";
        if (_cache.TryGetValue(cacheKey, out SkillInfo[]? cached)) {
            return cached!;
        }

        var skills = new List<SkillInfo>();

        // 从本地注册表加载
        skills.AddRange(_skillRegistry.Values);

        // 从远程注册表同步（如果启用）
        if (_config.SkillManagement.AutoDiscover) {
            foreach (var registryUrl in _config.SkillManagement.SkillRegistries) {
                try {
                    var remoteSkills = await FetchSkillsFromRegistryAsync(registryUrl, cancellationToken);
                    skills.AddRange(remoteSkills);
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "Failed to fetch skills from registry {Registry}", registryUrl);
                }
            }
        }

        // 从本地技能目录扫描
        var localSkills = await ScanLocalSkillsAsync(cancellationToken);
        skills.AddRange(localSkills);

        // 去重
        var uniqueSkills = skills
            .GroupBy(s => s.Id)
            .Select(g => g.First())
            .ToList();

        // 分类过滤
        if (!string.IsNullOrWhiteSpace(category)) {
            uniqueSkills = uniqueSkills.Where(s => s.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // 标记已安装状态
        var installedSkills = await GetInstalledSkillsAsync(cancellationToken);
        var installedIds = installedSkills.Select(s => s.Id).ToHashSet();
        foreach (var skill in uniqueSkills) {
            if (installedIds.Contains(skill.Id)) {
                // 更新已安装标记（由于 record 不可变，这里只能返回新对象）
            }
        }

        var result = uniqueSkills.Take(_config.SkillManagement.MaxSkills).ToArray();
        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(_config.Cache.DefaultTtlMinutes));
        return result;
    }

    public async Task<SkillInfo[]> SearchSkillsAsync(string keyword, CancellationToken cancellationToken = default) {
        var allSkills = await DiscoverSkillsAsync(null, cancellationToken);
        var lowerKeyword = keyword.ToLowerInvariant();

        return allSkills.Where(s =>
            s.Name.ToLowerInvariant().Contains(lowerKeyword) ||
            s.Description.ToLowerInvariant().Contains(lowerKeyword) ||
            s.Tags.Any(t => t.ToLowerInvariant().Contains(lowerKeyword)) ||
            s.Category.ToLowerInvariant().Contains(lowerKeyword)
        ).ToArray();
    }

    public async Task<SkillInfo?> GetSkillAsync(string skillId, CancellationToken cancellationToken = default) {
        var skills = await DiscoverSkillsAsync(null, cancellationToken);
        return skills.FirstOrDefault(s => s.Id == skillId);
    }

    public async Task<SkillInstallResult> InstallSkillAsync(string skillId, CancellationToken cancellationToken = default) {
        var skill = await GetSkillAsync(skillId, cancellationToken);
        if (skill == null) {
            return new SkillInstallResult(false, skillId, $"Skill {skillId} not found", null, new[] { "Skill not found" });
        }

        if (skill.IsInstalled) {
            return new SkillInstallResult(false, skillId, "Skill already installed", null, new[] { "Already installed" });
        }

        try {
            _logger.LogInformation("Installing skill {SkillId}...", skillId);

            var installDir = Path.Combine(_skillsDirectory, skillId);
            Directory.CreateDirectory(installDir);

            var installedFiles = new List<string>();
            var errors = new List<string>();

            // 执行安装命令
            if (!string.IsNullOrWhiteSpace(skill.InstallInfo.InstallCommand)) {
                var result = await ExecuteCommandAsync(skill.InstallInfo.InstallCommand, installDir, cancellationToken);
                if (!result.Success) {
                    errors.AddRange(result.Errors);
                }
            }

            // 检查必需工具
            foreach (var tool in skill.InstallInfo.RequiredTools) {
                if (!await IsToolAvailableAsync(tool, cancellationToken)) {
                    errors.Add($"Required tool not found: {tool}");
                }
            }

            // 设置环境变量
            foreach (var env in skill.InstallInfo.EnvironmentVariables) {
                Environment.SetEnvironmentVariable(env.Key, env.Value);
            }

            // 执行安装后脚本
            if (!string.IsNullOrWhiteSpace(skill.InstallInfo.PostInstallScript)) {
                var result = await ExecuteCommandAsync(skill.InstallInfo.PostInstallScript, installDir, cancellationToken);
                if (!result.Success) {
                    errors.AddRange(result.Errors);
                }
            }

            // 创建技能清单文件
            var manifestPath = Path.Combine(installDir, "skill.manifest.json");
            var manifest = JsonSerializer.Serialize(skill with { IsInstalled = true }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(manifestPath, manifest, cancellationToken);
            installedFiles.Add(manifestPath);

            if (errors.Count > 0) {
                return new SkillInstallResult(false, skillId, "Installation completed with errors", installedFiles.ToArray(), errors.ToArray());
            }

            _cache.Remove("installed_skills");
            _logger.LogInformation("Successfully installed skill {SkillId}", skillId);
            return new SkillInstallResult(true, skillId, "Successfully installed", installedFiles.ToArray(), Array.Empty<string>());
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to install skill {SkillId}", skillId);
            return new SkillInstallResult(false, skillId, ex.Message, null, new[] { ex.Message });
        }
    }

    public async Task<bool> UninstallSkillAsync(string skillId, CancellationToken cancellationToken = default) {
        var installDir = Path.Combine(_skillsDirectory, skillId);
        if (Directory.Exists(installDir)) {
            try {
                Directory.Delete(installDir, true);
                _cache.Remove("installed_skills");
                _logger.LogInformation("Uninstalled skill {SkillId}", skillId);
                return true;
            } catch (Exception ex) {
                _logger.LogError(ex, "Failed to uninstall skill {SkillId}", skillId);
                return false;
            }
        }
        return false;
    }

    public async Task<SkillInstallResult> UpdateSkillAsync(string skillId, CancellationToken cancellationToken = default) {
        await UninstallSkillAsync(skillId, cancellationToken);
        return await InstallSkillAsync(skillId, cancellationToken);
    }

    public async Task<SkillInfo[]> GetInstalledSkillsAsync(CancellationToken cancellationToken = default) {
        var cacheKey = "installed_skills";
        if (_cache.TryGetValue(cacheKey, out SkillInfo[]? cached)) {
            return cached!;
        }

        var skills = new List<SkillInfo>();

        if (Directory.Exists(_skillsDirectory)) {
            var dirs = Directory.GetDirectories(_skillsDirectory);
            foreach (var dir in dirs) {
                var manifestPath = Path.Combine(dir, "skill.manifest.json");
                if (File.Exists(manifestPath)) {
                    try {
                        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                        var skill = JsonSerializer.Deserialize<SkillInfo>(json);
                        if (skill != null) {
                            skills.Add(skill with { IsInstalled = true });
                        }
                    } catch (Exception ex) {
                        _logger.LogWarning(ex, "Failed to load skill manifest from {Path}", manifestPath);
                    }
                }
            }
        }

        var result = skills.ToArray();
        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(_config.Cache.DefaultTtlMinutes));
        return result;
    }

    public async Task<SkillUpdateInfo[]> CheckUpdatesAsync(CancellationToken cancellationToken = default) {
        var installedSkills = await GetInstalledSkillsAsync(cancellationToken);
        var updates = new List<SkillUpdateInfo>();

        foreach (var skill in installedSkills) {
            var latestSkill = await GetSkillAsync(skill.Id, cancellationToken);
            if (latestSkill != null && IsNewerVersion(latestSkill.Version, skill.Version)) {
                updates.Add(new SkillUpdateInfo(
                    skill.Id,
                    skill.Version,
                    latestSkill.Version,
                    latestSkill.Metadata.TryGetValue("changelog", out var cl) ? cl?.ToString() ?? "" : "",
                    IsBreakingChange(skill.Version, latestSkill.Version)
                ));
            }
        }

        return updates.ToArray();
    }

    public async Task<int> SyncFromRegistryAsync(string registryUrl, CancellationToken cancellationToken = default) {
        var skills = await FetchSkillsFromRegistryAsync(registryUrl, cancellationToken);
        var synced = 0;

        foreach (var skill in skills) {
            var manifestPath = Path.Combine(_skillsDirectory, "registry", skill.Id, "skill.json");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            var json = JsonSerializer.Serialize(skill, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(manifestPath, json, cancellationToken);
            synced++;
        }

        _cache.Remove("skills_discover_all");
        return synced;
    }

    public string[] GetCategories() {
        return _skillRegistry.Values.Select(s => s.Category).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<SkillInfo[]> FetchSkillsFromRegistryAsync(string registryUrl, CancellationToken cancellationToken) {
        try {
            var response = await _httpClient.GetStringAsync(registryUrl, cancellationToken);
            var skills = JsonSerializer.Deserialize<SkillInfo[]>(response);
            return skills ?? Array.Empty<SkillInfo>();
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to fetch skills from registry {Url}", registryUrl);
            return Array.Empty<SkillInfo>();
        }
    }

    private async Task<SkillInfo[]> ScanLocalSkillsAsync(CancellationToken cancellationToken) {
        var skills = new List<SkillInfo>();

        if (Directory.Exists(_skillsDirectory)) {
            var dirs = Directory.GetDirectories(_skillsDirectory);
            foreach (var dir in dirs) {
                var manifestPath = Path.Combine(dir, "skill.json");
                if (File.Exists(manifestPath)) {
                    try {
                        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                        var skill = JsonSerializer.Deserialize<SkillInfo>(json);
                        if (skill != null) {
                            skills.Add(skill with { IsInstalled = Directory.Exists(Path.Combine(_skillsDirectory, skill.Id)) });
                        }
                    } catch (Exception ex) {
                        _logger.LogWarning(ex, "Failed to load local skill from {Path}", manifestPath);
                    }
                }
            }
        }

        return skills.ToArray();
    }

    private async Task<(bool Success, string[] Errors)> ExecuteCommandAsync(string command, string workingDirectory, CancellationToken cancellationToken) {
        var errors = new List<string>();

        try {
            var processInfo = new System.Diagnostics.ProcessStartInfo {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(processInfo);
            if (process == null) {
                return (false, new[] { "Failed to start process" });
            }

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0) {
                var error = await process.StandardError.ReadToEndAsync();
                errors.Add($"Command exited with code {process.ExitCode}: {error}");
                return (false, errors.ToArray());
            }

            return (true, Array.Empty<string>());
        } catch (Exception ex) {
            errors.Add(ex.Message);
            return (false, errors.ToArray());
        }
    }

    private async Task<bool> IsToolAvailableAsync(string tool, CancellationToken cancellationToken) {
        try {
            var processInfo = new System.Diagnostics.ProcessStartInfo {
                FileName = "where",
                Arguments = tool,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(processInfo);
            if (process == null) return false;
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        } catch {
            return false;
        }
    }

    private bool IsNewerVersion(string latest, string current) {
        try {
            var latestVer = new Version(latest);
            var currentVer = new Version(current);
            return latestVer > currentVer;
        } catch {
            return string.Compare(latest, current, StringComparison.Ordinal) > 0;
        }
    }

    private bool IsBreakingChange(string current, string latest) {
        try {
            var currentVer = new Version(current);
            var latestVer = new Version(latest);
            return latestVer.Major > currentVer.Major;
        } catch {
            return false;
        }
    }

    private Dictionary<string, SkillInfo> InitializeSkillRegistry() {
        var skills = new Dictionary<string, SkillInfo>();

        // 内置技能示例
        skills["opencode_model_discovery"] = new SkillInfo(
            Id: "opencode_model_discovery",
            Name: "OpenCode 模型发现",
            Description: "自动检索和管理免费 AI 模型",
            Category: "AI/模型管理",
            Version: "1.0.0",
            Author: "OpenCodeMcpServer",
            RepositoryUrl: "https://github.com/opencode/skill-model-discovery",
            Tags: new[] { "模型发现", "免费模型", "AI" },
            Dependencies: Array.Empty<string>(),
            InstallInfo: new SkillInstallInfo(
                InstallCommand: "echo 'Model discovery skill installed'",
                RequiredTools: Array.Empty<string>(),
                PostInstallScript: null,
                EnvironmentVariables: new Dictionary<string, string>()
            ),
            DiscoveredAt: DateTime.UtcNow,
            IsInstalled: true,
            Metadata: new Dictionary<string, object> { ["builtin"] = true }
        );

        skills["opencode_prompt_manager"] = new SkillInfo(
            Id: "opencode_prompt_manager",
            Name: "OpenCode 提示词管理",
            Description: "提示词模板管理、切换和变量替换",
            Category: "AI/提示词工程",
            Version: "1.0.0",
            Author: "OpenCodeMcpServer",
            RepositoryUrl: "https://github.com/opencode/skill-prompt-manager",
            Tags: new[] { "提示词管理", "模板", "变量替换" },
            Dependencies: Array.Empty<string>(),
            InstallInfo: new SkillInstallInfo(
                InstallCommand: "echo 'Prompt manager skill installed'",
                RequiredTools: Array.Empty<string>(),
                PostInstallScript: null,
                EnvironmentVariables: new Dictionary<string, string>()
            ),
            DiscoveredAt: DateTime.UtcNow,
            IsInstalled: true,
            Metadata: new Dictionary<string, object> { ["builtin"] = true }
        );

        skills["opencode_skill_manager"] = new SkillInfo(
            Id: "opencode_skill_manager",
            Name: "OpenCode 技能管理",
            Description: "技能发现、安装、更新和管理",
            Category: "工具/技能管理",
            Version: "1.0.0",
            Author: "OpenCodeMcpServer",
            RepositoryUrl: "https://github.com/opencode/skill-manager",
            Tags: new[] { "技能管理", "插件", "扩展" },
            Dependencies: Array.Empty<string>(),
            InstallInfo: new SkillInstallInfo(
                InstallCommand: "echo 'Skill manager installed'",
                RequiredTools: Array.Empty<string>(),
                PostInstallScript: null,
                EnvironmentVariables: new Dictionary<string, string>()
            ),
            DiscoveredAt: DateTime.UtcNow,
            IsInstalled: true,
            Metadata: new Dictionary<string, object> { ["builtin"] = true }
        );

        return skills;
    }
}