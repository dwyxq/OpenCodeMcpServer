using System.ComponentModel;

using ModelContextProtocol.Server;

using OpenCodeMcpServer.Models;
using OpenCodeMcpServer.Services;

namespace OpenCodeMcpServer.Tools;

/// <summary>
/// 提示词管理工具 - 列出、搜索、获取详情、创建、切换、导入导出提示词模板
/// </summary>
[McpServerToolType]
public sealed class PromptManagementTools {
    private readonly IPromptManagementService _promptService;

    public PromptManagementTools(IPromptManagementService promptService) {
        _promptService = promptService;
    }

    [McpServerTool, Description("获取所有提示词模板")]
    public async Task<string> ListPrompts(
        [Description("分类过滤")] string? category = null,
        CancellationToken cancellationToken = default) {
        var prompts = string.IsNullOrWhiteSpace(category)
            ? await _promptService.GetAllPromptsAsync(cancellationToken)
            : await _promptService.GetPromptsByCategoryAsync(category!, cancellationToken);

        if (prompts.Length == 0) {
            return "未找到提示词模板";
        }

        var output = new System.Text.StringBuilder();
        output.AppendLine($"找到 {prompts.Length} 个提示词模板:");
        output.AppendLine();

        var grouped = prompts.GroupBy(p => p.Category).OrderBy(g => g.Key);
        foreach (var group in grouped) {
            output.AppendLine($"## {group.Key}");
            foreach (var prompt in group.OrderBy(p => p.Name)) {
                var builtInTag = prompt.IsBuiltIn ? " [内置]" : "";
                output.AppendLine($"- **{prompt.Name}** (`{prompt.Id}`){builtInTag}");
                output.AppendLine($"  {prompt.Description}");
                if (prompt.Tags.Length > 0) {
                    output.AppendLine($"  标签: {string.Join(", ", prompt.Tags)}");
                }
                output.AppendLine($"  变量: {string.Join(", ", prompt.Variables)}");
                output.AppendLine();
            }
        }

        return output.ToString();
    }

    [McpServerTool, Description("搜索提示词模板")]
    public async Task<string> SearchPrompts(
        [Description("搜索关键词")] string keyword,
        CancellationToken cancellationToken = default) {
        var prompts = await _promptService.SearchPromptsAsync(keyword, cancellationToken);

        if (prompts.Length == 0) {
            return $"未找到包含 '{keyword}' 的提示词模板";
        }

        var output = new System.Text.StringBuilder();
        output.AppendLine($"找到 {prompts.Length} 个匹配的提示词模板:");
        output.AppendLine();

        foreach (var prompt in prompts) {
            var builtInTag = prompt.IsBuiltIn ? " [内置]" : "";
            output.AppendLine($"## {prompt.Name} (`{prompt.Id}`){builtInTag}");
            output.AppendLine($"**分类**: {prompt.Category}");
            output.AppendLine($"**描述**: {prompt.Description}");
            output.AppendLine($"**变量**: {string.Join(", ", prompt.Variables)}");
            output.AppendLine($"**标签**: {string.Join(", ", prompt.Tags)}");
            output.AppendLine();
        }

        return output.ToString();
    }

    [McpServerTool, Description("获取提示词详情")]
    public async Task<string> GetPrompt(
        [Description("提示词 ID")] string promptId,
        CancellationToken cancellationToken = default) {
        var prompt = await _promptService.GetPromptAsync(promptId, cancellationToken);
        if (prompt == null) {
            return $"未找到提示词: {promptId}";
        }

        var output = new System.Text.StringBuilder();
        output.AppendLine($"# {prompt.Name} (`{prompt.Id}`)");
        output.AppendLine($"**分类**: {prompt.Category}");
        output.AppendLine($"**描述**: {prompt.Description}");
        output.AppendLine($"**作者**: {prompt.Author}");
        output.AppendLine($"**创建时间**: {prompt.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        output.AppendLine($"**更新时间**: {prompt.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        output.AppendLine($"**内置**: {(prompt.IsBuiltIn ? "是" : "否")}");
        output.AppendLine();
        output.AppendLine("## 变量");
        foreach (var variable in prompt.Variables) {
            output.AppendLine($"- {variable}");
        }
        output.AppendLine();
        output.AppendLine("## 标签");
        output.AppendLine(string.Join(", ", prompt.Tags));
        output.AppendLine();
        output.AppendLine("## 模板内容");
        output.AppendLine("```");
        output.AppendLine(prompt.Template);
        output.AppendLine("```");

        return output.ToString();
    }

    [McpServerTool, Description("创建自定义提示词")]
    public async Task<string> CreatePrompt(
        [Description("提示词名称")] string name,
        [Description("提示词描述")] string description,
        [Description("分类")] string category,
        [Description("模板内容")] string template,
        [Description("变量列表（逗号分隔）")] string variables,
        [Description("标签（逗号分隔）")] string tags,
        CancellationToken cancellationToken = default) {
        var prompt = new PromptTemplate(
            Id: Guid.NewGuid().ToString("N")[..12],
            Name: name,
            Description: description,
            Category: category,
            Template: template,
            Variables: variables.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Tags: tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Author: "User",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            IsBuiltIn: false,
            Metadata: new Dictionary<string, object>()
        );

        var created = await _promptService.CreatePromptAsync(prompt, cancellationToken);
        return $"提示词创建成功: {created.Name} (ID: {created.Id})";
    }

    [McpServerTool, Description("切换/应用提示词（支持变量替换）")]
    public async Task<string> SwitchPrompt(
        [Description("目标提示词 ID")] string targetPromptId,
        [Description("当前提示词 ID（可选）")] string? currentPromptId = null,
        [Description("变量值（JSON 格式）")] string? variableValuesJson = null,
        [Description("保留上下文")] bool preserveContext = false,
        CancellationToken cancellationToken = default) {
        Dictionary<string, string>? variableValues = null;
        if (!string.IsNullOrWhiteSpace(variableValuesJson)) {
            try {
                variableValues = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(variableValuesJson);
            } catch (Exception ex) {
                return $"变量值 JSON 解析失败: {ex.Message}";
            }
        }

        var request = new PromptSwitchRequest(
            CurrentPromptId: currentPromptId ?? "",
            TargetPromptId: targetPromptId,
            VariableValues: variableValues,
            PreserveContext: preserveContext
        );

        try {
            var result = await _promptService.SwitchPromptAsync(request, cancellationToken);
            return $"提示词已切换到 `{targetPromptId}`:\n\n{result}";
        } catch (Exception ex) {
            return $"切换提示词失败: {ex.Message}";
        }
    }

    [McpServerTool, Description("获取提示词分类")]
    public string GetCategories() {
        var categories = _promptService.GetCategories();
        return "可用提示词分类:\n" + string.Join("\n", categories.Select(c => $"- {c}"));
    }

    [McpServerTool, Description("导入提示词模板")]
    public async Task<string> ImportPrompts(
        [Description("目录路径")] string directoryPath,
        CancellationToken cancellationToken = default) {
        try {
            var count = await _promptService.ImportPromptsAsync(directoryPath, cancellationToken);
            return $"成功导入 {count} 个提示词模板";
        } catch (Exception ex) {
            return $"导入失败: {ex.Message}";
        }
    }

    [McpServerTool, Description("导出提示词模板")]
    public async Task<string> ExportPrompts(
        [Description("导出目录")] string directoryPath,
        [Description("提示词 ID 列表（逗号分隔，不填则导出所有自定义）")] string? promptIds = null,
        CancellationToken cancellationToken = default) {
        try {
            var ids = string.IsNullOrWhiteSpace(promptIds)
                ? null
                : promptIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            await _promptService.ExportPromptsAsync(directoryPath, ids, cancellationToken);
            return $"提示词已导出到: {directoryPath}";
        } catch (Exception ex) {
            return $"导出失败: {ex.Message}";
        }
    }
}