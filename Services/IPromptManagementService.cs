using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services;

/// <summary>
/// 提示词管理服务接口
/// </summary>
public interface IPromptManagementService
{
    /// <summary>
    /// 获取所有提示词模板
    /// </summary>
    Task<PromptTemplate[]> GetAllPromptsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 按分类获取提示词
    /// </summary>
    Task<PromptTemplate[]> GetPromptsByCategoryAsync(string category, CancellationToken cancellationToken = default);

    /// <summary>
    /// 搜索提示词
    /// </summary>
    Task<PromptTemplate[]> SearchPromptsAsync(string keyword, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取提示词详情
    /// </summary>
    Task<PromptTemplate?> GetPromptAsync(string promptId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建自定义提示词
    /// </summary>
    Task<PromptTemplate> CreatePromptAsync(PromptTemplate prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新提示词
    /// </summary>
    Task<PromptTemplate> UpdatePromptAsync(string promptId, PromptTemplate prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除提示词
    /// </summary>
    Task<bool> DeletePromptAsync(string promptId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 切换提示词（应用变量替换）
    /// </summary>
    Task<string> SwitchPromptAsync(PromptSwitchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取内置提示词分类
    /// </summary>
    string[] GetCategories();

    /// <summary>
    /// 导入提示词（从文件/目录）
    /// </summary>
    Task<int> ImportPromptsAsync(string directoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 导出提示词
    /// </summary>
    Task ExportPromptsAsync(string directoryPath, string[]? promptIds = null, CancellationToken cancellationToken = default);
}