# OpenCodeMcpServer 配置使用说明

本地 MCP 服务，为 OpenCode 提供 **免费模型自动检索 / 缓存刷新 / 提示词切换 / 技能管理** 能力。
基于 .NET 10 + ModelContextProtocol SDK，通过 **stdio** 传输与 OpenCode 通信。

---

## 一、快速开始

### 1. 环境要求

| 项目 | 版本 |
|------|------|
| .NET SDK | 10.0+（`dotnet --version` 验证） |
| OpenCode | 支持 MCP 的版本 |
| 操作系统 | Windows（win-x64） |

### 2. 编译

```powershell
cd D:\LiHWork\OpenCodeMcpServer
dotnet build -c Debug
```

产物路径（接入 OpenCode 时使用）：

```
D:\LiHWork\OpenCodeMcpServer\bin\Debug\net10.0\win-x64\OpenCodeMcpServer.exe
```

### 3. 手动运行（可选）

```powershell
.\bin\Debug\net10.0\win-x64\OpenCodeMcpServer.exe
```

> 进程启动后**不会有窗口输出**——它等待 stdin 的 JSON-RPC 输入，日志全部输出到 **stderr**（stdout 被 MCP 协议独占，这是正确行为，不是卡死）。按 `Ctrl+C` 退出。

---

## 二、配置文件说明（appsettings.json）

配置文件位于 **exe 同目录**（编译时自动复制 `CopyToOutputDirectory=PreserveNewest`）。修改后需重启服务生效。
支持 `//` 注释；支持环境变量覆盖，**前缀 `OPENCODE_MCP_`，层级用 `__` 分隔**，例如：

```powershell
$env:OPENCODE_MCP_ModelDiscovery__RefreshIntervalHours = "12"
```

### 1. ModelDiscovery — 模型发现与自动刷新

| 键 | 默认值 | 说明 |
|----|--------|------|
| `Enabled` | `true` | 总开关 |
| `EnabledSources` | `["HuggingFace","Ollama","OpenRouter"]` | 启用的数据源（可加 `TogetherAi`、`Groq`，需同时启用对应 ProviderEndpoints 节点） |
| `RefreshOnStartup` | `true` | 启动后立即执行一次全量刷新 |
| `WarmupDelaySeconds` | `5` | 首刷延迟秒数（避免与 MCP 握手抢资源） |
| `RefreshIntervalHours` | `24` | 定时刷新周期（小时） |
| `MaxModelsPerSource` | `100` | 每个源最多拉取模型数 |
| `AutoUpdate` | `true` | 刷新时增量更新缓存 |
| `CacheDirectory` | `./cache/models` | 磁盘缓存目录（相对 exe） |
| `CacheTtlMinutes` | `30` | 内存查询缓存 TTL |

**自动刷新流程**：启动 → 延迟 `WarmupDelaySeconds` 秒 → 首轮刷新（并发拉取各启用源）→ 以后每 `RefreshIntervalHours` 小时重复一轮，带 SemaphoreSlim 防重入。

### 2. ProviderEndpoints — 各提供商 API

| 提供商 | 默认 BaseUrl | 需要 Key？ | 说明 |
|--------|-------------|-----------|------|
| `HuggingFace` | `https://huggingface.co/api` | 否（可选 `HF_TOKEN` 提速/私有模型） | 免费模型主力源 |
| `Ollama` | `http://localhost:11434` | 否 | 本地 Ollama 运行时；远程服务器改 BaseUrl 即可 |
| `OpenRouter` | `https://openrouter.ai/api/v1` | 否（可选 Key 提高限额） | 含免费 `[free]` 模型 |
| `TogetherAi` | `https://api.together.xyz` | 是 | 默认 `Enabled: false`（无公开免 Key 列表 API） |
| `Groq` | `https://api.groq.com/openai/v1` | 是 | 默认 `Enabled: false` |

每个节点通用字段：

| 键 | 说明 |
|----|------|
| `BaseUrl` | API 根地址 |
| `ApiKeyEnvVar` | 读取 Key 的环境变量名 |
| `TimeoutSeconds` | HTTP 超时 |
| `Enabled` | 该源是否启用 |

### 3. ApiKeys — API 密钥

**三种注入方式，按优先级从高到低**：

1. `appsettings.json` 的 `ApiKeys` 节（不推荐明文提交）
2. `appsettings.{环境名}.json`
3. **环境变量（推荐）**：`HF_TOKEN`、`OPENROUTER_API_KEY`、`TOGETHER_API_KEY`、`GROQ_API_KEY`、`ANTHROPIC_API_KEY`

在 OpenCode 中通过 MCP 条目的 `environment` 字段注入（见第四节），**无需污染系统环境变量**。

### 4. PromptManagement / SkillManagement / Cache / Logging

| 节点 | 关键键 | 说明 |
|------|--------|------|
| `PromptManagement` | `PromptsDirectory: ./prompts` | 提示词 JSON 存储目录；内置 6 个模板（代码生成/评审/架构设计/提示工程/学习计划/故障排查）；`AllowVariableSubstitution` 支持 `{{var}}` 与 `${var}` |
| `SkillManagement` | `SkillsDirectory: ./skills` | 技能目录与注册表同步地址 |
| `Cache` | `DefaultTtlMinutes: 30` | 全局查询缓存 |
| `Logging` | `Level: Information` | 日志级别（stderr 输出） |

---

## 三、验证配置执行情况

### 方法 1：观察 stderr 启动日志（最直接）

```powershell
cd D:\LiHWork\OpenCodeMcpServer\bin\Debug\net10.0\win-x64
cmd /c "OpenCodeMcpServer.exe < NUL 2> start.log"
Get-Content start.log -Encoding UTF8
```

正常应看到（关键字样例）：

```
info: OpenCodeMcpServer.Services.ModelCacheRefreshService[0]
      模型缓存刷新服务已启动，延迟 5 秒，周期 24 小时
info: ... 开始刷新模型缓存，数据源: HuggingFace, Ollama, OpenRouter
info: ... HuggingFace 获取到 N 个模型
warn: ... Ollama 不可用（未启动本地 Ollama 时属预期）
```

- 看到各源「获取到 N 个模型」→ API 端点配置正确、网络可达。
- `warn`/`fail` 单源失败**不影响整体**（多源聚合容错设计）。
- Ollama 报错请先确认 `http://localhost:11434` 是否运行：`Invoke-RestMethod http://localhost:11434/api/tags`

### 方法 2：JSON-RPC 手动握手 + 工具列表验证

向 exe 的 stdin 依次写入 initialize / initialized / tools/list 三条 JSON-RPC 消息：

```powershell
cd D:\LiHWork\OpenCodeMcpServer\bin\Debug\net10.0\win-x64
'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}' | Out-File -Encoding ascii req.txt
Start-Process -FilePath .\OpenCodeMcpServer.exe -RedirectStandardInput req.txt -RedirectStandardOutput out.json -RedirectStandardError err.log -NoNewWindow -Wait
(Get-Content out.json -Raw)
```

`out.json` 中应包含全部 23 个工具（MCP SDK 自动将方法名转为 snake_case）：`search_free_models`、`get_model_details`、`discover_new_models`、`get_supported_sources`、`refresh_model_cache`、`list_prompts`、`search_prompts`、`get_prompt`、`create_prompt`、`switch_prompt`、`get_categories`、`import_prompts`、`export_prompts`、`discover_skills`、`search_skills`、`get_skill`、`install_skill`、`uninstall_skill`、`update_skill`、`list_installed_skills`、`check_skill_updates`、`sync_skills_from_registry`、`get_skill_categories`。

### 方法 3：调用工具验证 API Key 实际生效（tools/call）

在方法 2 的基础上追加一条调用（需先等待缓存预热完成约 10 秒）：

```json
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"search_free_models","arguments":{"keyword":"deepseek","maxResults":5,"includeFreeOnly":true}}}
```

- 返回候选模型列表 → 发现链路 + API Key 注入完整可用。
- `get_supported_sources` 返回当前启用的源列表，可验证 `EnabledSources` 配置是否被读取。
- `refresh_model_cache` 手动触发刷新，返回各源模型数量 → 验证定时刷新同一代码路径。

### 方法 4：MCP Inspector（图形化调试，可选）

```powershell
npx @modelcontextprotocol/inspector D:\LiHWork\OpenCodeMcpServer\bin\Debug\net10.0\win-x64\OpenCodeMcpServer.exe
```

浏览器中逐个点选工具执行，最直观地确认每个工具与配置的执行结果。

---

## 四、接入 OpenCode（当前环境已配置）

编辑 `C:\Users\LIH\.config\opencode\opencode.json`，在现有 `"mcp"` 节点内（与 `wechat` 平级）追加：

```json
"OpenCodeMcpServer": {
  "type": "local",
  "command": [
    "D:\\LiHWork\\OpenCodeMcpServer\\bin\\Debug\\net10.0\\win-x64\\OpenCodeMcpServer.exe"
  ],
  "environment": {
    "HF_TOKEN": "",
    "OPENROUTER_API_KEY": ""
  },
  "enabled": true
}
```

要点：

1. `command` 使用**完整路径数组**（不是 `dotnet run`，避免每次冷启动编译）。
2. Windows 路径在 JSON 中必须**双反斜杠** `\\`。
3. API Key 放 `environment` 注入子进程；**没有 Key 就留空或删除该行**——HuggingFace / Ollama / OpenRouter 基础检索均可匿名使用。
4. 修改后**重启 OpenCode**（MCP 服务随 OpenCode 启动为子进程）。

### 在 OpenCode 中验证

重启后在 OpenCode 会话中直接对话触发，例如：

```
用 search_free_models 搜索支持中文的免费模型
用 get_supported_sources 查看当前启用的数据源
用 list_prompts 列出所有提示词模板
用 switch_prompt 切换到 code_review 提示词
```

- OpenCode 能列出并调用这些工具 → 接入成功。
- 排查：`opencode` 启动日志中该 MCP 条目状态；或先在命令行用「方法 2」确认 exe 本身正常。

### 发布为固定版本（推荐长期使用）

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

将 `command` 改为 `D:\LiHWork\OpenCodeMcpServer\bin\Release\net10.0\win-x64\publish\OpenCodeMcpServer.exe`，发布产物目录中同时包含 `appsettings.json`，可整体拷到任意位置部署。

---

## 五、常见问题

| 现象 | 原因与处理 |
|------|-----------|
| OpenCode 里看不到工具 | exe 路径错误 / JSON 用了单反斜杠未转义 / 未重启 OpenCode |
| 启动即退出 | 查看 stderr；多为 `appsettings.json` JSON 语法错误 |
| HuggingFace 频繁 429 | 配置 `HF_TOKEN` 环境变量提高限速 |
| Ollama 源为空 | 本地未运行 Ollama，或 `ProviderEndpoints:Ollama:BaseUrl` 指向错误 |
| 结果为旧数据 | 调用 `refresh_model_cache` 手动刷新，或等待定时周期 |
| stdout 出现日志导致协议报错 | 绝不可向 stdout 写日志（Program.cs 已强制 `LogToStandardErrorThreshold=Trace`，自定义时须保留） |

## 六、模型代理与自动优化（OpenAI 兼容提供商）

参考 [freellmapi](https://github.com/tashfeenahmed/freellmapi) 移植的代理 / 模型目录 / 自动优化能力，统一由 `ProviderEndpoints:CustomProviders` 配置驱动。

### 6.1 内置提供商（appsettings.json 已预配 11 家）

| 提供商 ID | BaseUrl | 环境变量（密钥） | 代表模型 |
|-----------|---------|------------------|----------|
| agnes-ai-cn | https://api.agnes-ai.cn/v1 | `AGNES_CN_API_KEY` | agnes-2.5-pro / flash |
| agnes-ai | https://apihub.agnes-ai.com/v1 | `AGNES_API_KEY` | agnes-2.5-flash / 2.0-flash |
| stepfun | https://api.stepfun.com/step_plan/v1 | `STEPFUN_API_KEY` | step-router-v1 / step-3.7-flash |
| sensenova | https://token.sensenova.cn/v1 | `SENSONOVA_API_KEY` | kimi-k3 / glm-5.2 / deepseek-v4 |
| longcat | https://api.longcat.chat/openai/v1 | `LONGCAT_API_KEY` | LongCat-2.0 |
| nvidia-nim | https://integrate.api.nvidia.com/v1 | `NVIDIA_API_KEY` | nemotron-3-ultra-550b 等 |
| opencode-zen | https://opencode.ai/zen/v1 | `OPENCODE_API_KEY` | big-pickle / nemotron-3-*-free |
| openrouter | https://openrouter.ai/api/v1 | `OPENROUTER_API_KEY` | 各 `:free` 模型 |
| tokenrhythm | https://tokenrhythm.studio/v1 | `TOKENRHYTHM_API_KEY` | qwen3.8-max / glm-5.3 等 |
| amd-radeon | https://developer.amd.com.cn/radeon/api/v1 | `AMD_RADEON_API_KEY` | DeepSeek-V4-Flash(-Vision) |
| modelscope | https://api-inference.modelscope.cn/v1 | `MODELSCOPE_API_KEY` | GLM-5.2 / DeepSeek-V4 系列 |

密钥解析优先级：`ApiKeys:CustomProviders[providerId]` → `ApiKey` 配置 → `ApiKeyEnvVar` 环境变量。**禁止把密钥明文写入配置文件**。

### 6.2 新增 MCP 工具

| 工具 | 功能 |
|------|------|
| `list_provider_models` | 列出全部提供商、静态模型目录、密钥配置状态与健康评分 |
| `check_provider_health` | 并发探测所有提供商 `GET /models`，记录延迟/成功率 |
| `get_provider_status` | 查看当前健康排序（不发起新探测） |
| `chat_completion` | 转发 OpenAI 兼容聊天请求；未指定提供商时按健康评分自动择优，失败自动切换下一优（最多 4 候选） |

### 6.3 自动优化机制

- 评分 = 成功率(0-70) + 延迟得分(0-30)；未探测默认中性 50
- 连续失败 3 次 → 15 分钟冷却（评分沉底，不参与选路）；`check_provider_health` 成功即解除
- 401/403 视为"可达但缺密钥"，不计失败冷却
- 状态持久化于 `data/provider-health.json`，重启不丢失

### 6.4 新增提供商（零代码）

在 `appsettings.json` 的 `ProviderEndpoints:CustomProviders` 加一段即可（配置驱动，无需写新类）：

```json
"my-provider": {
  "BaseUrl": "https://api.example.com/v1",
  "ApiKeyEnvVar": "MY_PROVIDER_API_KEY",
  "TimeoutSeconds": 60,
  "Enabled": true,
  "ExtraHeaders": { "X-Custom": "value" },
  "Keyless": false,
  "Models": [ "model-a", "model-b" ]
}
```

`Models` 为静态目录（`/models` 端点不可用时的回退）；支持 `"provider/model"` 前缀写法消除同名模型歧义。
