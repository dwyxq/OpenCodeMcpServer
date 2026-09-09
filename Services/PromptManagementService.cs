using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCodeMcpServer.Models;
using OpenCodeMcpServer.Configuration;

namespace OpenCodeMcpServer.Services;

/// <summary>
/// 提示词管理服务实现
/// </summary>
public class PromptManagementService : IPromptManagementService
{
    private readonly ILogger<PromptManagementService> _logger;
    private readonly IMemoryCache _cache;
    private readonly McpServerConfig _config;
    private readonly string _promptsDirectory;
    private readonly Dictionary<string, PromptTemplate> _builtInPrompts;

    public PromptManagementService(
        ILogger<PromptManagementService> logger,
        IMemoryCache cache,
        IOptions<McpServerConfig> config)
    {
        _logger = logger;
        _cache = cache;
        _config = config.Value;
        _promptsDirectory = Path.GetFullPath(_config.PromptManagement.PromptsDirectory);
        _builtInPrompts = InitializeBuiltInPrompts();

        // 确保目录存在
        Directory.CreateDirectory(_promptsDirectory);
    }

    public async Task<PromptTemplate[]> GetAllPromptsAsync(CancellationToken cancellationToken = default)
    {
        var cacheKey = "all_prompts";
        if (_cache.TryGetValue(cacheKey, out PromptTemplate[]? cached))
        {
            return cached!;
        }

        var prompts = new List<PromptTemplate>();
        prompts.AddRange(_builtInPrompts.Values);

        // 加载自定义提示词
        var customPrompts = await LoadCustomPromptsAsync(cancellationToken);
        prompts.AddRange(customPrompts);

        var result = prompts.ToArray();
        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(_config.Cache.DefaultTtlMinutes));
        return result;
    }

    public async Task<PromptTemplate[]> GetPromptsByCategoryAsync(string category, CancellationToken cancellationToken = default)
    {
        var allPrompts = await GetAllPromptsAsync(cancellationToken);
        return allPrompts.Where(p => p.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public async Task<PromptTemplate[]> SearchPromptsAsync(string keyword, CancellationToken cancellationToken = default)
    {
        var allPrompts = await GetAllPromptsAsync(cancellationToken);
        var lowerKeyword = keyword.ToLowerInvariant();

        return allPrompts.Where(p =>
            p.Name.ToLowerInvariant().Contains(lowerKeyword) ||
            p.Description.ToLowerInvariant().Contains(lowerKeyword) ||
            p.Tags.Any(t => t.ToLowerInvariant().Contains(lowerKeyword)) ||
            p.Template.ToLowerInvariant().Contains(lowerKeyword)
        ).ToArray();
    }

    public async Task<PromptTemplate?> GetPromptAsync(string promptId, CancellationToken cancellationToken = default)
    {
        if (_builtInPrompts.TryGetValue(promptId, out var builtIn))
        {
            return builtIn;
        }

        var customPrompts = await LoadCustomPromptsAsync(cancellationToken);
        return customPrompts.FirstOrDefault(p => p.Id == promptId);
    }

    public async Task<PromptTemplate> CreatePromptAsync(PromptTemplate prompt, CancellationToken cancellationToken = default)
    {
        var newPrompt = prompt with
        {
            Id = prompt.Id ?? Guid.NewGuid().ToString("N")[..12],
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsBuiltIn = false
        };

        var filePath = GetPromptFilePath(newPrompt.Id);
        await SavePromptToFileAsync(newPrompt, filePath, cancellationToken);

        _cache.Remove("all_prompts");
        _logger.LogInformation("Created prompt {PromptId}", newPrompt.Id);
        return newPrompt;
    }

    public async Task<PromptTemplate> UpdatePromptAsync(string promptId, PromptTemplate prompt, CancellationToken cancellationToken = default)
    {
        if (_builtInPrompts.ContainsKey(promptId))
        {
            throw new InvalidOperationException("Cannot modify built-in prompts");
        }

        var existing = await GetPromptAsync(promptId, cancellationToken);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Prompt {promptId} not found");
        }

        var updatedPrompt = prompt with
        {
            Id = promptId,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
            IsBuiltIn = false
        };

        var filePath = GetPromptFilePath(promptId);
        await SavePromptToFileAsync(updatedPrompt, filePath, cancellationToken);

        _cache.Remove("all_prompts");
        _logger.LogInformation("Updated prompt {PromptId}", promptId);
        return updatedPrompt;
    }

    public async Task<bool> DeletePromptAsync(string promptId, CancellationToken cancellationToken = default)
    {
        if (_builtInPrompts.ContainsKey(promptId))
        {
            throw new InvalidOperationException("Cannot delete built-in prompts");
        }

        var filePath = GetPromptFilePath(promptId);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            _cache.Remove("all_prompts");
            _logger.LogInformation("Deleted prompt {PromptId}", promptId);
            return true;
        }

        return false;
    }

    public async Task<string> SwitchPromptAsync(PromptSwitchRequest request, CancellationToken cancellationToken = default)
    {
        var targetPrompt = await GetPromptAsync(request.TargetPromptId, cancellationToken);
        if (targetPrompt == null)
        {
            throw new KeyNotFoundException($"Target prompt {request.TargetPromptId} not found");
        }

        var result = targetPrompt.Template;

        // 变量替换
        if (request.VariableValues != null && _config.PromptManagement.AllowVariableSubstitution)
        {
            foreach (var kvp in request.VariableValues)
            {
                result = result.Replace($"{{{{{kvp.Key}}}}}", kvp.Value);
                result = result.Replace($"${{{kvp.Key}}}", kvp.Value);
            }
        }

        // 检查未替换的变量
        var unsubstitutedVars = targetPrompt.Variables
            .Where(v => !request.VariableValues?.ContainsKey(v) ?? true)
            .ToArray();

        if (unsubstitutedVars.Length > 0)
        {
            _logger.LogWarning("Prompt {PromptId} has unsubstituted variables: {Vars}",
                request.TargetPromptId, string.Join(", ", unsubstitutedVars));
        }

        return result;
    }

    public string[] GetCategories()
    {
        return _config.PromptManagement.BuiltInCategories
            .Concat(_builtInPrompts.Values.Select(p => p.Category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<int> ImportPromptsAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");
        }

        var files = Directory.GetFiles(directoryPath, "*.json", SearchOption.AllDirectories);
        var imported = 0;

        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var prompt = JsonSerializer.Deserialize<PromptTemplate>(json);
                if (prompt != null)
                {
                    await CreatePromptAsync(prompt, cancellationToken);
                    imported++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import prompt from {File}", file);
            }
        }

        return imported;
    }

    public async Task ExportPromptsAsync(string directoryPath, string[]? promptIds = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directoryPath);

        var prompts = await GetAllPromptsAsync(cancellationToken);
        var toExport = promptIds != null
            ? prompts.Where(p => promptIds.Contains(p.Id)).ToArray()
            : prompts.Where(p => !p.IsBuiltIn).ToArray();

        foreach (var prompt in toExport)
        {
            var filePath = Path.Combine(directoryPath, $"{prompt.Id}.json");
            var json = JsonSerializer.Serialize(prompt, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json, cancellationToken);
        }
    }

    private async Task<PromptTemplate[]> LoadCustomPromptsAsync(CancellationToken cancellationToken)
    {
        var prompts = new List<PromptTemplate>();
        var files = Directory.GetFiles(_promptsDirectory, "*.json", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var prompt = JsonSerializer.Deserialize<PromptTemplate>(json);
                if (prompt != null)
                {
                    prompts.Add(prompt with { IsBuiltIn = false });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load prompt from {File}", file);
            }
        }

        return prompts.ToArray();
    }

    private async Task SavePromptToFileAsync(PromptTemplate prompt, string filePath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(prompt, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    private string GetPromptFilePath(string promptId)
    {
        return Path.Combine(_promptsDirectory, $"{promptId}.json");
    }

    private Dictionary<string, PromptTemplate> InitializeBuiltInPrompts()
    {
        var prompts = new Dictionary<string, PromptTemplate>();

        // 代码生成提示词
        prompts["code_generation"] = new PromptTemplate(
            Id: "code_generation",
            Name: "代码生成专家",
            Description: "生成高质量、可维护的代码",
            Category: "开发",
            Template: @"你是一位资深软件工程师。请根据以下需求生成代码：

**需求描述**：{{requirements}}

**技术栈**：{{tech_stack}}

**约束条件**：
- 遵循 {{coding_standards}} 编码规范
- 代码必须可测试、可维护
- 包含必要的错误处理和日志
- 添加适当的注释和文档

**输出要求**：
1. 完整的代码实现
2. 单元测试示例
3. 使用说明文档",
            Variables: new[] { "requirements", "tech_stack", "coding_standards" },
            Tags: new[] { "代码生成", "开发", "最佳实践" },
            Author: "OpenCodeMcpServer",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            IsBuiltIn: true,
            Metadata: new Dictionary<string, object> { ["version"] = "1.0" }
        );

        // 代码审查提示词
        prompts["code_review"] = new PromptTemplate(
            Id: "code_review",
            Name: "代码审查助手",
            Description: "全面的代码审查和改进建议",
            Category: "开发",
            Template: @"请对以下代码进行全面审查：

**代码内容**：
```{{language}}
{{code}}
```

**审查重点**：
1. **正确性**：逻辑错误、边界条件、异常处理
2. **性能**：时间/空间复杂度、不必要的分配、数据库查询优化
3. **安全性**：注入风险、敏感数据泄露、权限控制
4. **可维护性**：命名规范、代码结构、解耦程度、测试覆盖
5. **最佳实践**：设计模式应用、SOLID原则、DRY/KISS/YAGNI

**输出格式**：
- 严重问题（必须修复）
- 建议改进（推荐修复）
- 优化建议（可选）
- 优秀之处（值得保持）",
            Variables: new[] { "code", "language" },
            Tags: new[] { "代码审查", "质量保证", "重构" },
            Author: "OpenCodeMcpServer",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            IsBuiltIn: true,
            Metadata: new Dictionary<string, object> { ["version"] = "1.0" }
        );

        // 架构设计提示词
        prompts["architecture_design"] = new PromptTemplate(
            Id: "architecture_design",
            Name: "系统架构设计师",
            Description: "设计可扩展、高可用的系统架构",
            Category: "架构",
            Template: @"你是一位系统架构师。请为以下业务场景设计技术架构：

**业务场景**：{{business_scenario}}

**非功能性需求**：
- 性能要求：{{performance_requirements}}
- 可用性要求：{{availability_requirements}}
- 扩展性要求：{{scalability_requirements}}
- 安全性要求：{{security_requirements}}
- 预算约束：{{budget_constraints}}

**技术约束**：
- 首选技术栈：{{preferred_stack}}
- 必须集成的系统：{{integration_systems}}
- 团队技能水平：{{team_skills}}

**输出要求**：
1. 架构概览图（文字描述）
2. 核心组件设计
3. 数据流设计
4. 部署拓扑
5. 技术选型理由
6. 风险评估与缓解方案
7. 实施路线图",
            Variables: new[] { "business_scenario", "performance_requirements", "availability_requirements", "scalability_requirements", "security_requirements", "budget_constraints", "preferred_stack", "integration_systems", "team_skills" },
            Tags: new[] { "架构设计", "系统设计", "技术选型" },
            Author: "OpenCodeMcpServer",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            IsBuiltIn: true,
            Metadata: new Dictionary<string, object> { ["version"] = "1.0" }
        );

        // 提示词工程提示词
        prompts["prompt_engineering"] = new PromptTemplate(
            Id: "prompt_engineering",
            Name: "提示词工程专家",
            Description: "优化和设计高效的提示词",
            Category: "AI",
            Template: @"你是一位提示词工程专家。请优化以下提示词：

**原始提示词**：
{{original_prompt}}

**目标任务**：{{task_description}}

**期望输出格式**：{{output_format}}

**优化原则**：
1. 指令清晰、具体、无歧义
2. 提供足够的上下文和示例
3. 使用结构化格式（XML/Markdown/JSON）
4. 设置明确的约束和边界
5. 引导模型逐步推理（Chain of Thought）
6. 包含正反例对比

**输出要求**：
1. 优化后的提示词
2. 优化说明（改进了什么）
3. 3-5 个测试用例
4. 可能的失败模式及应对",
            Variables: new[] { "original_prompt", "task_description", "output_format" },
            Tags: new[] { "提示词工程", "优化", "AI" },
            Author: "OpenCodeMcpServer",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            IsBuiltIn: true,
            Metadata: new Dictionary<string, object> { ["version"] = "1.0" }
        );

        // 学习计划提示词
        prompts["learning_plan"] = new PromptTemplate(
            Id: "learning_plan",
            Name: "个性化学习规划师",
            Description: "制定结构化、可执行的学习计划",
            Category: "学习",
            Template: @"你是一位资深技术导师。请为学习者制定个性化学习计划：

**学习目标**：{{learning_goal}}

**当前技能水平**：{{current_skills}}

**可用时间**：{{available_time}}（每周小时数）

**学习偏好**：
- 理论 vs 实践比例：{{theory_practice_ratio}}
- 偏好学习资源类型：{{preferred_resources}}
- 是否需要项目实战：{{need_projects}}

**输出要求**：
1. 学习路径图（阶段划分）
2. 每阶段详细内容与里程碑
3. 推荐学习资源（书籍、课程、文档、项目）
4. 时间分配建议
5. 进度检查点与自我评估标准
6. 常见坑点与避坑指南",
            Variables: new[] { "learning_goal", "current_skills", "available_time", "theory_practice_ratio", "preferred_resources", "need_projects" },
            Tags: new[] { "学习规划", "技能提升", "职业发展" },
            Author: "OpenCodeMcpServer",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            IsBuiltIn: true,
            Metadata: new Dictionary<string, object> { ["version"] = "1.0" }
        );

        // 问题排查提示词
        prompts["troubleshooting"] = new PromptTemplate(
            Id: "troubleshooting",
            Name: "问题排查专家",
            Description: "系统化诊断和解决技术问题",
            Category: "运维",
            Template: @"你是一位资深运维专家。请帮助诊断并解决以下技术问题：

**问题现象**：{{problem_description}}

**错误信息**：{{error_messages}}

**环境信息**：
- 操作系统：{{os}}
- 运行时/框架版本：{{runtime_version}}
- 相关组件版本：{{component_versions}}
- 部署方式：{{deployment_mode}}

**已尝试方案**：{{tried_solutions}}

**排查步骤**：
1. 复现问题的最小步骤
2. 日志分析要点
3. 可能的根因分析（按可能性排序）
4. 验证假设的具体方法
5. 解决方案及验证步骤
6. 预防复发措施

**输出格式**：结构化排查报告",
            Variables: new[] { "problem_description", "error_messages", "os", "runtime_version", "component_versions", "deployment_mode", "tried_solutions" },
            Tags: new[] { "问题排查", "故障诊断", "运维" },
            Author: "OpenCodeMcpServer",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            IsBuiltIn: true,
            Metadata: new Dictionary<string, object> { ["version"] = "1.0" }
        );

        return prompts;
    }
}