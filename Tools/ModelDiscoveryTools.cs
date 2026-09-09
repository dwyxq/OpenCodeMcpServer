using System.ComponentModel;

using ModelContextProtocol.Server;

using OpenCodeMcpServer.Models;
using OpenCodeMcpServer.Services;

namespace OpenCodeMcpServer.Tools;

/// <summary>
/// 模型发现工具 - 搜索免费模型、获取模型详情、发现新模型、刷新缓存
/// </summary>
[McpServerToolType]
public sealed class ModelDiscoveryTools {
    private readonly IModelDiscoveryService _modelDiscoveryService;

    public ModelDiscoveryTools(IModelDiscoveryService modelDiscoveryService) {
        _modelDiscoveryService = modelDiscoveryService;
    }

    [McpServerTool, Description("搜索免费 AI 模型")]
    public async Task<string> SearchFreeModels(
        [Description("搜索关键词")] string? keyword = null,
        [Description("模型来源")] string? source = null,
        [Description("仅显示免费模型")] bool freeOnly = true,
        [Description("所需能力")] string[]? capabilities = null,
        [Description("最小上下文长度")] int? minContextTokens = null,
        [Description("最大结果数")] int maxResults = 20,
        [Description("页码")] int page = 1,
        [Description("排序字段")] string sortBy = "updated",
        [Description("降序排列")] bool sortDescending = true,
        CancellationToken cancellationToken = default) {
        var query = new ModelSearchQuery(
            Keyword: keyword,
            Source: string.IsNullOrWhiteSpace(source) ? null : Enum.Parse<ModelSource>(source, true),
            FreeOnly: freeOnly,
            Capabilities: capabilities,
            MinContextTokens: minContextTokens,
            MaxResults: maxResults,
            Page: page,
            SortBy: sortBy,
            SortDescending: sortDescending
        );

        var result = await _modelDiscoveryService.SearchModelsAsync(query, cancellationToken);

        var output = new System.Text.StringBuilder();
        output.AppendLine($"找到 {result.TotalCount} 个模型 (第 {result.Page} 页，共 {((result.TotalCount + result.PageSize - 1) / result.PageSize)} 页):");
        output.AppendLine();

        foreach (var model in result.Items) {
            output.AppendLine($"## {model.Name} ({model.Id})");
            output.AppendLine($"**提供商**: {model.Provider}");
            output.AppendLine($"**描述**: {model.Description}");
            output.AppendLine($"**免费**: {(model.Pricing.IsFree ? "是" : "否")}");
            output.AppendLine($"**上下文**: {model.Capabilities.MaxContextTokens:N0} tokens");
            output.AppendLine($"**能力**: 聊天={model.Capabilities.SupportsChat}, 补全={model.Capabilities.SupportsCompletion}, 嵌入={model.Capabilities.SupportsEmbedding}, 函数调用={model.Capabilities.SupportsFunctionCalling}, 视觉={model.Capabilities.SupportsVision}, 音频={model.Capabilities.SupportsAudio}");
            output.AppendLine($"**链接**: {model.ModelUrl}");
            if (!string.IsNullOrEmpty(model.ApiEndpoint)) {
                output.AppendLine($"**API**: {model.ApiEndpoint}");
            }
            output.AppendLine();
        }

        if (result.HasMore) {
            output.AppendLine("... 还有更多结果，增加 page 参数查看下一页");
        }

        return output.ToString();
    }

    [McpServerTool, Description("获取模型详细信息")]
    public async Task<string> GetModelDetails(
        [Description("模型 ID")] string modelId,
        CancellationToken cancellationToken = default) {
        var model = await _modelDiscoveryService.GetModelAsync(modelId, cancellationToken);
        if (model == null) {
            return $"未找到模型: {modelId}";
        }

        var output = new System.Text.StringBuilder();
        output.AppendLine($"# {model.Name}");
        output.AppendLine($"**ID**: {model.Id}");
        output.AppendLine($"**提供商**: {model.Provider}");
        output.AppendLine($"**描述**: {model.Description}");
        output.AppendLine($"**模型链接**: {model.ModelUrl}");
        if (!string.IsNullOrEmpty(model.ApiEndpoint)) {
            output.AppendLine($"**API 端点**: {model.ApiEndpoint}");
        }
        output.AppendLine($"**许可证**: {model.License ?? "未知"}");
        output.AppendLine();
        output.AppendLine("## 定价信息");
        output.AppendLine($"**免费**: {(model.Pricing.IsFree ? "是" : "否")}");
        if (!string.IsNullOrEmpty(model.Pricing.FreeTierDetails)) {
            output.AppendLine($"**免费详情**: {model.Pricing.FreeTierDetails}");
        }
        if (model.Pricing.InputCostPer1kTokens.HasValue) {
            output.AppendLine($"**输入成本**: {model.Pricing.InputCostPer1kTokens} {model.Pricing.Currency}/1k tokens");
        }
        if (model.Pricing.OutputCostPer1kTokens.HasValue) {
            output.AppendLine($"**输出成本**: {model.Pricing.OutputCostPer1kTokens} {model.Pricing.Currency}/1k tokens");
        }
        output.AppendLine();
        output.AppendLine("## 能力");
        output.AppendLine($"- 聊天: {model.Capabilities.SupportsChat}");
        output.AppendLine($"- 补全: {model.Capabilities.SupportsCompletion}");
        output.AppendLine($"- 嵌入: {model.Capabilities.SupportsEmbedding}");
        output.AppendLine($"- 函数调用: {model.Capabilities.SupportsFunctionCalling}");
        output.AppendLine($"- 视觉: {model.Capabilities.SupportsVision}");
        output.AppendLine($"- 音频: {model.Capabilities.SupportsAudio}");
        output.AppendLine($"- 最大上下文: {model.Capabilities.MaxContextTokens:N0} tokens");
        output.AppendLine($"- 最大输出: {model.Capabilities.MaxOutputTokens:N0} tokens");
        output.AppendLine($"- 支持语言: {string.Join(", ", model.Capabilities.SupportedLanguages)}");
        output.AppendLine();
        output.AppendLine("## 元数据");
        foreach (var kvp in model.Metadata) {
            output.AppendLine($"- {kvp.Key}: {kvp.Value}");
        }

        return output.ToString();
    }

    [McpServerTool, Description("发现新的免费模型")]
    public async Task<string> DiscoverNewModels(
        CancellationToken cancellationToken = default) {
        var models = await _modelDiscoveryService.DiscoverNewModelsAsync(cancellationToken);

        if (models.Length == 0) {
            return "未发现新模型";
        }

        var output = new System.Text.StringBuilder();
        output.AppendLine($"发现 {models.Length} 个新免费模型:");
        output.AppendLine();

        foreach (var model in models) {
            output.AppendLine($"## {model.Name} ({model.Id})");
            output.AppendLine($"**提供商**: {model.Provider}");
            output.AppendLine($"**描述**: {model.Description}");
            output.AppendLine($"**上下文**: {model.Capabilities.MaxContextTokens:N0} tokens");
            output.AppendLine($"**链接**: {model.ModelUrl}");
            output.AppendLine();
        }

        return output.ToString();
    }

    [McpServerTool, Description("获取支持的模型来源")]
    public string GetSupportedSources() {
        var sources = _modelDiscoveryService.GetSupportedSources();
        return "支持的模型来源:\n" + string.Join("\n", sources.Select(s => $"- {s}"));
    }

    [McpServerTool, Description("刷新模型缓存")]
    public async Task<string> RefreshModelCache(
        CancellationToken cancellationToken = default) {
        await _modelDiscoveryService.RefreshCacheAsync(cancellationToken);
        return "模型缓存已刷新";
    }
}