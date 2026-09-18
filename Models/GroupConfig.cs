// <summary>
/// 【功能说明】：模型分组配置 - 定义分组别名、显示名、描述及默认模型，供 HTTP 网关路由使用
/// 【服务对象】：OpenAiCompatibleHttpServer（/v1/models 列表）、ProviderProxyTools（list_group_configs）
/// 【调用方式】：单例服务 GroupConfigService，从 group-config.json 读取；JSON 文件由 opencode.json 对应配置同步
/// 【禁止重复】：项目内唯一分组配置实现，新增分组只需编辑 group-config.json
/// </summary>
using System.Text.Json.Serialization;

namespace OpenCodeMcpServer.Models;

/// <summary>
/// 单个分组配置
/// </summary>
public record GroupConfig(
    /// <summary>分组 ID（与 ProviderEndpoints:CustomProviders 的 Groups 数组中的值匹配）</summary>
    string GroupId,
    /// <summary>显示名称（如"高能力组"）</summary>
    string Label,
    /// <summary>分组描述（说明适用场景）</summary>
    string? Description = null,
    /// <summary>该分组的默认模型 ID（可选，缺省由提供商 Models 数组首个提供）</summary>
    string? DefaultModel = null
);

/// <summary>
/// 分组配置文件根节点
/// </summary>
public record GroupConfigFile(
    /// <summary>分组列表</summary>
    [property: JsonPropertyName("groups")]
    GroupConfig[] Groups,
    /// <summary>模型别名（如 SuperModel），用于构建分组别名格式 {ModelAlias}-{GroupId}）</summary>
    [property: JsonPropertyName("modelAlias")]
    string ModelAlias = "SuperModel"
);
