// <summary>
/// 【功能说明】：分组配置服务接口
/// 【服务对象】：OpenAiCompatibleHttpServer、ProviderProxyTools
/// 【调用方式】：依赖注入 IGroupConfigService
/// 【禁止重复】：项目内唯一分组配置接口
/// </summary>
using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services;

/// <summary>
/// 分组配置服务接口
/// </summary>
public interface IGroupConfigService {
    /// <summary>所有分组配置列表</summary>
    IReadOnlyList<GroupConfig> Groups { get; }

    /// <summary>
    /// 解析分组别名（如 SuperModel-高能力组 → GroupConfig）；非别名返回 null
    /// </summary>
    /// <param name="alias">模型名称</param>
    /// <returns>分组配置，若无匹配返回 null</returns>
    GroupConfig? ResolveAlias(string alias);

    /// <summary>判断是否为分组别名（含 -GroupId 后缀）</summary>
    bool IsGroupAlias(string model);
}
