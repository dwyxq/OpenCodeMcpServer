# AGENTS.md — OpenCodeMcpServer

本地 MCP 服务（.NET 10 / ModelContextProtocol SDK），通过 **stdio** 与 OpenCode 通信。
功能：免费模型自动检索、缓存刷新、健康探测与智能路由、提示词/技能管理。

## 构建与运行

```powershell
cd D:\LiHWork\OpenCodeMcpServer
dotnet build -c Debug
```

- 产物：`bin\Debug\net10.0\win-x64\OpenCodeMcpServer.exe`（单文件自包含，copy `appsettings.json` 到输出）。
- **改代码后构建前必须先杀进程**：exe 是单文件，运行中会被锁导致构建失败。`Stop-Process -Name OpenCodeMcpServer -Force` 后再 `dotnet build`。
- 启动后无窗口输出是正常行为：stdout 被 MCP 协议独占，**日志全走 stderr**，按 `Ctrl+C` 退出。
- 发布单文件：`dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`。

## 架构地图（扁平单项目）

| 层 | 文件 | 职责 |
|---|---|---|
| 入口 | `Program.cs` | Host 构建；stdout=协议/日志强制 stderr；注册 DI、HttpClient、HostedService、MCP Server |
| 配置 | `Configuration/McpServerConfigBinder.cs` | **手工绑定**（`McpServerConfigBinder.Load`），因 record 无参构造缺失不能用 `Configure<T>` |
| 模型 | `Models/FreeModel.cs` `Models/ProviderConfig.cs` | 配置 record（位置参数） |
| 服务 | `Services/*.cs` | `ModelCacheRefreshService`（定时器）、`ProviderHealthService`（评分/冷却）、`ChatCompletionProxyService`（转发/故障转移/路由策略）、`ModelDiscoveryService`、`PromptManagementService`、`SkillManagementService` |
| 提供商 | `Services/Providers/*.cs` | 各数据源（HuggingFace/Ollama/OpenRouter/...） |
| 工具 | `Tools/*.cs` | 注册进 MCP；**方法名被 SDK 自动转 snake_case** |
| 日志 | `Services/Logging/FileLoggerProvider.cs` | 落盘 `logs/opencode-mcp-{yyyyMMdd}.log`，按天滚动/超 10MB 分段/留 10 文件；`FileLoggerProvider.Crash` 兜底 |

## 关键约定（易踩坑）

- **`appsettings.json` 用 `[System.IO.File]::ReadAllText`（UTF8）读，禁用 `Get-Content`**（中文会乱码）。
- **文件内含用户 provider 明文 key，绝不在回复中外显**；也不触碰 `CustomProviders` 的 `ApiKeyEnvVar` 明文值。
- **JSON 重复键会在启动时抛异常**，DI/日志未就绪 → 进程静默退出。已在 `Program.cs` 用 `FileLoggerProvider.Crash` 兜底落盘；改配置后查看 `logs/` 下最新日志定位。
- 新增 `appsettings.json` 配置键**必须写中文注释**（项目规范）。
- 环境变量覆盖：前缀 `OPENCODE_MCP_`，层级用 `__`，如 `OPENCODE_MCP_ModelDiscovery__RefreshIntervalHours=12`。
- 校验结果：`dotnet build` 必须 **0 错误 0 警告**。

## 已实现模块（功能对标审计见 `docs/功能对标审计.md`）

- **模块 1 智能路由**（已完成）：模型打分 `ToEntry`（成功率×70+延迟分-冷却100）、故障转移（失败切下一候选）、策略注册表 sticky(`_sessionMap`)/balanced(`_roundRobin`)、热池(`IsQualifiedForHotPool`)/探索(`PickExplorationModel`)、预算控制(`RoutingConfig`：totalBudgetMs/retryBackoffMs/maxRetry/firstTokenTimeoutMs)、代理层能力过滤(`SupportsCapabilities`)。
- **模块 3 后台健康探测**：`ModelCacheRefreshService` 独立 `_healthTimer` 定时调 `ProbeAllAsync`；`HealthProbeIntervalHours`（默认 6，0 禁用）。
- 健康数据持久化 `bin\data\provider-health.json`；评分=成功率(0-70)+延迟(0-30)，连续失败≥3→冷却15分钟，401/403 不计失败。

## 测试方式（无单测，走 JSON-RPC 实测）

- 标准脚本模式：here-string 写 `.ps1` 到 `obj\`，再 `powershell -NoProfile -ExecutionPolicy Bypass -File` 执行（参考 `obj\diag*.ps1`）。
- 脚本流程：启动 exe → 写 `initialize` → `notifications/initialized` → `tools/call`，读 stdout 结果 + stderr 日志。
- 先 `Stop-Process` 清理上一个实例，避免端口/文件锁冲突。

## 项目规范（来自全局 AGENTS.md）

- 每次回复中文；禁止重复实现；统一架构、通用逻辑抽公共模块；注释格式 `【功能说明】【服务对象】【调用方式】【禁止重复】`。
- 新功能先检查是否已有同类模块，逐步重构、分步实施、先测试再交付。