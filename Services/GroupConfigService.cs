// <summary>
/// 【功能说明】：分组配置服务 - 从 group-config.json 读取分组元数据，支持别名解析
/// 【服务对象】：OpenAiCompatibleHttpServer（/v1/models 列表）、ProviderProxyTools（list_group_configs）
/// 【调用方式】：依赖注入 IGroupConfigService（单例）；构造函数同步加载 JSON 文件
/// 【禁止重复】：项目内唯一分组配置实现
/// </summary>
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services;

/// <summary>
/// 分组配置服务
/// </summary>
public class GroupConfigService : IGroupConfigService {
    private static readonly string ConfigFilePath = Path.Combine(AppContext.BaseDirectory, "group-config.json");
    private readonly ILogger<GroupConfigService> _logger;
    private readonly GroupConfigFile _config;

    public GroupConfigService(ILogger<GroupConfigService> logger) {
        _logger = logger;
        _config = LoadConfig();
    }

    /// <inheritdoc/>
    public IReadOnlyList<GroupConfig> Groups => _config.Groups;

    /// <inheritdoc/>
    public string ModelAlias => _config.ModelAlias;

    /// <inheritdoc/>
    public GroupConfig? ResolveAlias(string alias) {
        // 格式：{ModelAlias}-{groupId}，如 SuperModel-高能力组
        var prefix = $"{_config.ModelAlias}-";
        if (alias.Length > prefix.Length && alias.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
            var groupId = alias[prefix.Length..];
            return _config.Groups.FirstOrDefault(g =>
                g.GroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase), null);
        }
        return null;
    }

    /// <inheritdoc/>
    public bool IsGroupAlias(string model) =>
        !string.IsNullOrWhiteSpace(model) && model.Contains("-")
        && _config.Groups.Any(g => model.StartsWith($"{_config.ModelAlias}-{g.GroupId}", StringComparison.OrdinalIgnoreCase));

    private GroupConfigFile LoadConfig() {
        try {
            if (!File.Exists(ConfigFilePath)) {
                _logger.LogWarning("group-config.json 未找到，使用空配置");
                return new GroupConfigFile(Array.Empty<GroupConfig>(), "SuperModel");
            }
            var json = File.ReadAllText(ConfigFilePath);
            var parsed = JsonSerializer.Deserialize<GroupConfigFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed == null || parsed.Groups.Length == 0) {
                _logger.LogWarning("group-config.json 为空，使用默认配置");
                return new GroupConfigFile(Array.Empty<GroupConfig>(), "SuperModel");
            }
            return parsed;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "加载 group-config.json 失败，使用空配置");
            return new GroupConfigFile(Array.Empty<GroupConfig>(), "SuperModel");
        }
    }
}
