# OpenCodeMcpServer

本地 MCP 服务 + OpenAI 兼容 HTTP 网关，为 OpenCode 提供**免费模型自动检索 / 智能路由 / 提示词切换 / 技能管理**能力。
基于 .NET 10 + ModelContextProtocol SDK，通过 **stdio** 传输与 OpenCode 通信，同时提供 **Kestrel HTTP 端点**（SuperModel 别名网关）。

---

## 功能概览

| 能力 | 说明 |
|------|------|
| **模型发现** | HuggingFace / Ollama / OpenRouter 多源聚合，自动定时刷新 |
| **智能路由** | 健康评分选路 + 故障自动切换（最多 4 候选），429 限流自动避让 |
| **SuperModel 别名** | 固定模型名 `SuperModel`，路由到健康评分最高的真实提供商 |
| **OpenAI 兼容 HTTP 网关** | Kestrel 监听 `127.0.0.1:5679`，OpenCode 可直接作为 provider 使用 |
| **MCP stdio 工具** | 26 个工具：模型搜索/提供商健康/聊天代理/提示词管理/技能管理 |
| **提示词管理** | 内置 6 个模板，支持自定义创建/切换/变量替换 |
| **技能管理** | 技能发现/安装/更新，支持远程注册表同步 |

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

> 进程启动后**不会有窗口输出**——它等待 stdin 的 JSON-RPC 输入，日志全部输出到 **stderr**（stdout 被 MCP 协议独占）。按 `Ctrl+C` 退出。

### 4. 发布为固定版本（推荐长期使用）

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

发布产物目录中同时包含 `appsettings.json`，可整体拷到任意位置部署。

---

## 二、两种接入方式

OpenCodeMcpServer 提供**两种并行的接入方式**，可同时使用：

### 方式 A：MCP stdio（传统方式）

OpenCode 通过 MCP 协议与服务器通信，调用 26 个工具（模型搜索、聊天代理、提示词管理等）。

**opencode.json 配置：**

```json
"mcp": {
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
}
```

**在 OpenCode 中验证：**

重启后在 OpenCode 会话中直接对话触发，例如：

```
用 search_free_models 搜索支持中文的免费模型
用 get_supported_sources 查看当前启用的数据源
用 chat_completion 测试 LongCat-2.0
用 list_prompts 列出所有提示词模板
```

### 方式 B：HTTP 网关（SuperModel 别名）

OpenCodeMcpServer 内置 Kestrel HTTP 服务（端口 5679），暴露 OpenAI 兼容 API。OpenCode 可将其当作普通 OpenAI 兼容 provider 使用，无需经过 MCP stdio。

**opencode.json 配置（providers 节）：**

```json
"SuperModel": {
  "npm": "@ai-sdk/openai-compatible",
  "options": {
    "baseURL": "http://127.0.0.1:5679/v1",
    "apiKey": ""
  },
  "models": {
    "SuperModel": {
      "name": "SuperModel",
      "limit": {
        "context": 131072,
        "output": 8192
      }
    }
  }
}
```

**使用方式：**

- 启动新会话时通过 `/models` 选择 `SuperModel`
- 或在 opencode.json 顶层加 `"model": "SuperModel/SuperModel"` 设为默认模型

**工作流程：**

```
OpenCode → SuperModel (127.0.0.1:5679/v1)
  ↓
OpenAiCompatibleHttpServer 接收请求
  ↓
健康评分自动选提供商 (modelscope > nvidia-nim > openrouter > ...)
  ↓
转发到真实提供商，返回 OpenAI 兼容格式结果
```

**HTTP 端点：**

| 端点 | 方法 | 说明 |
|------|------|------|
| `/v1/models` | GET | 列出可用模型（固定返回 SuperModel） |
| `/models` | GET | 兼容部分客户端省略 /v1 前缀 |
| `/v1/chat/completions` | POST | 聊天补全，model 缺省为 SuperModel |

**请求示例：**

```bash
# 查看模型列表
curl http://127.0.0.1:5679/v1/models

# 聊天补全
curl -X POST http://127.0.0.1:5679/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"SuperModel","messages":[{"role":"user","content":"Hello"}]}'
```

---

## 三、配置文件说明（appsettings.json）

配置文件位于 **exe 同目录**（编译时自动复制 `CopyToOutputDirectory=PreserveNewest`）。修改后需重启服务生效。
支持 `//` 注释；支持环境变量覆盖，**前缀 `OPENCODE_MCP_`，层级用 `__` 分隔**，例如：

```powershell
$env:OPENCODE_MCP_ModelDiscovery__RefreshIntervalHours = "12"
```

### 1. ModelDiscovery — 模型发现与自动刷新

| 键 | 默认值 | 说明 |
|----|--------|------|
| `Enabled` | `true` | 总开关 |
| `EnabledSources` | `["HuggingFace","Ollama","OpenRouter","Custom"]` | 启用的数据源 |
| `RefreshOnStartup` | `true` | 启动后立即执行一次全量刷新 |
| `WarmupDelaySeconds` | `5` | 首刷延迟秒数 |
| `RefreshIntervalHours` | `24` | 定时刷新周期（小时） |
| `MaxModelsPerSource` | `100` | 每个源最多拉取模型数 |
| `AutoUpdate` | `true` | 刷新时增量更新缓存 |
| `CacheDirectory` | `./cache/models` | 磁盘缓存目录 |
| `CacheTtlMinutes` | `30` | 内存查询缓存 TTL |
| `HealthProbeIntervalHours` | `6` | 健康探测周期（0 禁用） |

### 2. ProviderEndpoints — 各提供商 API

#### 基础源（模型发现）

| 提供商 | 默认 BaseUrl | 需要 Key？ | 说明 |
|--------|-------------|-----------|------|
| `HuggingFace` | `https://huggingface.co/api` | 否（可选 `HF_TOKEN`） | 免费模型主力源 |
| `Ollama` | `http://localhost:11434` | 否 | 本地 Ollama 运行时 |
| `OpenRouter` | `https://openrouter.ai/api/v1` | 否（可选 Key） | 含免费 `[free]` 模型 |
| `TogetherAi` | `https://api.together.xyz` | 是 | 默认 `Enabled: false` |
| `Groq` | `https://api.groq.com/openai/v1` | 是 | 默认 `Enabled: false` |

#### 自定义 OpenAI 兼容提供商（代理/模型目录/自动优化）

| 提供商 ID | BaseUrl | 环境变量 | 代表模型 |
|-----------|---------|----------|----------|
| agnes-ai-cn | https://api.agnes-ai.cn/v1 | `AGNES_CN_API_KEY` | agnes-2.5-pro / flash |
| agnes-ai | https://apihub.agnes-ai.com/v1 | `AGNES_API_KEY` | agnes-2.5-flash / 2.0-flash |
| stepfun | https://api.stepfun.com/step_plan/v1 | `STEPFUN_API_KEY` | step-router-v1 / step-3.7-flash |
| sensenova | https://token.sensenova.cn/v1 | `SENSONOVA_API_KEY` | kimi-k3 / glm-5.2 / deepseek-v4 |
| longcat | https://api.longcat.chat/openai/v1 | `LONGCAT_API_KEY` | LongCat-2.0 |
| nvidia-nim | https://integrate.api.nvidia.com/v1 | `NVIDIA_NIM_API_KEY` | nemotron-3-ultra-550b 等 |
| opencode-zen | https://opencode.ai/zen/v1 | `OPENCODE_ZEN_API_KEY` | big-pickle / nemotron-3-*-free |
| openrouter | https://openrouter.ai/api/v1 | `OPENROUTER_API_KEY` | 各 `:free` 模型 |
| tokenrhythm | https://tokenrhythm.studio/v1 | `TOKENRHYTHM_API_KEY` | qwen3.8-max / glm-5.3 等 |
| amd-radeon | https://developer.amd.com.cn/radeon/api/v1 | `AMD_RADEON_API_KEY` | DeepSeek-V4-Flash(-Vision) |
| modelscope | https://api-inference.modelscope.cn/v1 | `MODELSCOPE_API_KEY` | GLM-5.2 / DeepSeek-V4 系列 |

### 3. Routing — 智能路由

| 键 | 默认值 | 说明 |
|----|--------|------|
| `Strategy` | `"auto"` | 路由策略：`auto` 按健康评分自动择优 / `sticky` 会话粘滞 / `balanced` 轮询均衡 |
| `MaxRetry` | `3` | 单请求最大重试/切换次数（含首选 = 最多 4 次尝试） |
| `FirstTokenTimeoutMs` | `60000` | 单次请求首 token 超时毫秒 |
| `TotalBudgetMs` | `60000` | 整次 chat 请求总预算毫秒（含所有重试） |
| `RetryBackoffMs` | `500` | 失败切换下一候选前的退避毫秒 |
| `HotPoolThreshold` | `80` | 健康评分 ≥ 此值的提供商进入热池优先选用 |
| `ExplorationEnabled` | `true` | 热池为空时是否降级探索通道 |
| `RateLimitRetryAfterMs` | `30000` | 429 限流后默认避让毫秒（有 Retry-After 头时用头值） |
| `ModelAlias` | `"SuperModel"` | 固定模型别名 |

### 4. HttpServer — OpenAI 兼容 HTTP 网关

| 键 | 默认值 | 说明 |
|----|--------|------|
| `Enabled` | `true` | 是否启用 Kestrel HTTP 端点 |
| `Url` | `"http://127.0.0.1:5679"` | 监听地址（OpenCode 的 baseURL 指向 `{url}/v1`） |
| `ApiKey` | `""` | 可选 Bearer 令牌；空则不鉴权（仅本机回环建议留空） |
| `IncludeRealModels` | `false` | 是否把真实提供商模型一并列入 /v1/models |

### 5. ApiKeys — API 密钥

**三种注入方式，按优先级从高到低**：

1. `appsettings.json` 的 `ApiKeys` 节（不推荐明文提交）
2. `appsettings.{环境名}.json`
3. **环境变量（推荐）**：`HF_TOKEN`、`OPENROUTER_API_KEY`、`MODELSCOPE_API_KEY` 等

在 OpenCode 中通过 MCP 条目的 `environment` 字段注入（无需污染系统环境变量）。

### 6. 其他配置

| 节点 | 关键键 | 说明 |
|------|--------|------|
| `PromptManagement` | `PromptsDirectory: ./prompts` | 提示词 JSON 存储目录；内置 6 个模板；支持 `{{var}}` 与 `${var}` 变量替换 |
| `SkillManagement` | `SkillsDirectory: ./skills` | 技能目录与注册表同步地址 |
| `Cache` | `DefaultTtlMinutes: 30` | 全局查询缓存 |
| `Logging` | `Level: Information` | 日志级别（stderr 输出） |

---

## 四、智能路由机制

### 健康评分

- 评分 = 成功率(0-70) + 延迟得分(0-30)；未探测默认中性 50
- 连续失败 3 次 → 15 分钟冷却（`IsUnavailableForRouting`，评分沉底，不参与选路）
- 429 限流 → 自动避让（解析 Retry-After 头或使用默认退避时间）
- 401/403 视为"可达但缺密钥"，不计失败冷却
- 状态持久化于 `data/provider-health.json`，重启不丢失

### 故障转移流程

```
chat_completion 请求
  ↓
ResolveCandidates: 过滤 IsUnavailableForRouting（限流/冷却中）的提供商
  ↓
按健康评分排序，取前 N 个候选
  ↓
依次尝试 SendOnceAsync
  ├─ 成功 → 返回结果
  ├─ 429 → ReportRateLimited + 解析 Retry-After → 跳到下一候选
  ├─ 连续失败 ≥3 → ReportFailure → 进入 15 分钟冷却 → 跳到下一候选
  └─ 全部失败 → 返回 ProxyUpstreamException（含已尝试链）
```

---

## 五、MCP 工具列表（26 个）

### 模型发现（5 个）

| 工具 | 功能 |
|------|------|
| `search_free_models` | 搜索免费模型（支持关键词/能力/上下文长度过滤） |
| `get_model_details` | 获取模型详细信息 |
| `discover_new_models` | 发现新模型 |
| `get_supported_sources` | 查看当前启用的数据源 |
| `refresh_model_cache` | 手动触发模型缓存刷新 |

### 模型提供商（5 个）

| 工具 | 功能 |
|------|------|
| `list_provider_models` | 列出全部提供商、静态模型目录、密钥配置状态与健康评分 |
| `check_provider_health` | 立即探测所有提供商延迟/成功率 |
| `get_provider_status` | 查看当前健康排序（不发起新探测） |
| `chat_completion` | 转发 OpenAI 兼容聊天请求，按健康评分自动择优，失败自动切换 |
| `get_routing_config` | 查看当前路由策略/重试/预算配置 |

### 提示词管理（7 个）

| 工具 | 功能 |
|------|------|
| `list_prompts` | 列出所有提示词模板 |
| `search_prompts` | 搜索提示词 |
| `get_prompt` | 获取提示词详情 |
| `create_prompt` | 创建自定义提示词 |
| `switch_prompt` | 切换/应用提示词（支持变量替换） |
| `get_categories` | 获取提示词分类 |
| `import_prompts` / `export_prompts` | 导入/导出提示词模板 |

### 技能管理（7 个）

| 工具 | 功能 |
|------|------|
| `discover_skills` | 发现可用技能 |
| `search_skills` | 搜索技能 |
| `get_skill` | 获取技能详情 |
| `install_skill` / `uninstall_skill` | 安装/卸载技能 |
| `update_skill` | 更新技能 |
| `list_installed_skills` | 获取已安装技能列表 |
| `check_skill_updates` | 检查技能更新 |
| `sync_skills_from_registry` | 从注册表同步技能 |
| `get_skill_categories` | 获取技能分类 |

---

## 六、验证配置执行情况

### 方法 1：观察 stderr 启动日志

```powershell
cd D:\LiHWork\OpenCodeMcpServer\bin\Debug\net10.0\win-x64
cmd /c "OpenCodeMcpServer.exe < NUL 2> start.log"
Get-Content start.log -Encoding UTF8
```

正常应看到：

```
info: OpenCodeMcpServer.Services.ModelCacheRefreshService[0]
      模型缓存刷新服务已启动，延迟 5 秒，周期 24 小时
info: OpenAI 兼容 HTTP 服务已启动: http://127.0.0.1:5679 (模型别名 SuperModel)
```

### 方法 2：JSON-RPC 手动握手 + 工具列表验证

```powershell
cd D:\LiHWork\OpenCodeMcpServer\bin\Debug\net10.0\win-x64
'{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}' | Out-File -Encoding ascii req.txt
Start-Process -FilePath .\OpenCodeMcpServer.exe -RedirectStandardInput req.txt -RedirectStandardOutput out.json -RedirectStandardError err.log -NoNewWindow -Wait
(Get-Content out.json -Raw)
```

`out.json` 中应包含全部 26 个工具。

### 方法 3：HTTP 端点验证

```powershell
# 确保 OpenCodeMcpServer 正在运行，然后：
Invoke-RestMethod http://127.0.0.1:5679/v1/models
Invoke-RestMethod -Uri http://127.0.0.1:5679/v1/chat/completions -Method POST -ContentType "application/json" -Body '{"model":"SuperModel","messages":[{"role":"user","content":"Say OK"}]}'
```

### 方法 4：MCP Inspector（图形化调试）

```powershell
npx @modelcontextprotocol/inspector D:\LiHWork\OpenCodeMcpServer\bin\Debug\net10.0\win-x64\OpenCodeMcpServer.exe
```

---

## 七、新增提供商（零代码）

在 `appsettings.json` 的 `ProviderEndpoints:CustomProviders` 加一段即可：

```json
"my-provider": {
  "BaseUrl": "https://api.example.com/v1",
  "ApiKeyEnvVar": "MY_PROVIDER_API_KEY",
  "TimeoutSeconds": 60,
  "Enabled": true,
  "Models": [ "model-a", "model-b" ]
}
```

同时在 `ApiKeys:CustomProviders` 添加对应密钥条目（留空，通过环境变量注入）。

---

## 八、常见问题

| 现象 | 原因与处理 |
|------|-----------|
| OpenCode 里看不到工具 | exe 路径错误 / JSON 用了单反斜杠未转义 / 未重启 OpenCode |
| 启动即退出 | 查看 stderr；多为 `appsettings.json` JSON 语法错误 |
| HTTP 端点 502 | 所有提供商均返回错误——检查 `ApiKeys` 或对应环境变量是否配置 |
| HTTP 端点无响应 | `HttpServer.Enabled` 是否为 `true`；端口 5679 是否被占用 |
| HuggingFace 频繁 429 | 配置 `HF_TOKEN` 环境变量提高限速 |
| Ollama 源为空 | 本地未运行 Ollama，或 `ProviderEndpoints:Ollama:BaseUrl` 指向错误 |
| 结果为旧数据 | 调用 `refresh_model_cache` 手动刷新，或等待定时周期 |
| stdout 出现日志导致协议报错 | 绝不可向 stdout 写日志（Program.cs 已强制 `LogToStandardErrorThreshold=Trace`） |
| SuperModel 返回 401 | 提供商 API Key 未配置，检查 `appsettings.json` ApiKeys 或环境变量 |
| SuperModel 返回 429 | 被限流，系统会自动切换下一候选提供商；也可等待退避时间后重试 |

---

## 九、架构概览

```
OpenCodeMcpServer.exe (主进程)
├── MCP stdio 传输层
│   └── 26 个工具（模型发现/提供商健康/聊天代理/提示词/技能）
├── Kestrel HTTP 网关 (127.0.0.1:5679)
│   ├── GET  /v1/models          → 返回 SuperModel
│   └── POST /v1/chat/completions → 健康路由 + 故障转移
├── 模型发现服务（HuggingFace / Ollama / OpenRouter / Custom）
├── 提供商健康服务（评分/冷却/持久化）
├── 聊天代理服务（别名解析 + 选路 + 重试）
├── 提示词管理服务
├── 技能管理服务
└── 文件日志（./logs/）
```
