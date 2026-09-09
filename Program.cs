using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using OpenCodeMcpServer.Services;
using OpenCodeMcpServer.Services.Providers;
using OpenCodeMcpServer.Tools;
using OpenCodeMcpServer.Configuration;
using OpenCodeMcpServer.Models;
using OpenCodeMcpServer.Services.Logging;

// 注意：appsettings.json 解析错误（如重复键）在 CreateApplicationBuilder 内部就会抛出，
// 此时 DI/日志框架均未就绪，必须在此捕获并用崩溃日志兜底，否则进程静默退出无法排查
HostApplicationBuilder builder;
try {
    builder = Host.CreateApplicationBuilder(args);
} catch (Exception ex) {
    FileLoggerProvider.Crash("主机构建失败（请检查 appsettings.json 格式，如重复键）", ex);
    throw;
}

// 配置
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables("OPENCODE_MCP_");

// 日志配置 - 关键：stdout 用于 MCP 协议，日志必须输出到 stderr
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => {
    options.LogToStandardErrorThreshold = LogLevel.Trace;
    options.FormatterName = "simple";
});
builder.Logging.AddSimpleConsole(options => {
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

// 加载配置（record 无参构造缺失，不能用 Configure<T>(section) 绑定，直接注册已加载实例）
// 配置文件错误（如重复键）会导致进程启动即崩溃，必须落盘崩溃日志才能追溯
McpServerConfig config;
try {
    config = McpServerConfigBinder.Load(builder.Configuration);
} catch (Exception ex) {
    FileLoggerProvider.Crash("配置加载失败，服务器无法启动（请检查 appsettings.json 格式）", ex);
    throw;
}

// 注册全局未处理异常兜底：任何线程的未捕获异常都写入日志文件后再退出
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    FileLoggerProvider.Crash("未处理异常（进程即将终止）", e.ExceptionObject as Exception);
TaskScheduler.UnobservedTaskException += (_, e) => {
    FileLoggerProvider.Crash("未观察的 Task 异常", e.Exception);
    e.SetObserved();
};

// 文件日志：所有 ILogger 输出同时写入 logs/ 目录（stdout 为 MCP 协议、stderr 随宿主丢失，落盘才能追溯）
builder.Logging.AddProvider(new FileLoggerProvider(config.Logging));

builder.Services.AddSingleton(Options.Create(config));
builder.Services.AddSingleton(Options.Create(config.ProviderEndpoints));
builder.Services.AddSingleton(Options.Create(config.ProviderApiKeys));

// 注册内存缓存
builder.Services.AddMemoryCache(options => {
    options.SizeLimit = 1024 * 1024 * 100; // 100MB
});

// 注册 HttpClient - 为每个 Provider 配置独立的 HttpClient
builder.Services.AddHttpClient<IModelSourceProvider, HuggingFaceModelProvider>(client => {
    client.BaseAddress = new Uri(config.ProviderEndpoints.HuggingFace.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(config.ProviderEndpoints.HuggingFace.TimeoutSeconds);
    client.DefaultRequestHeaders.Add("User-Agent", "OpenCodeMcpServer/1.0");
    if (!string.IsNullOrWhiteSpace(config.ProviderApiKeys.HuggingFaceToken)) {
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ProviderApiKeys.HuggingFaceToken}");
    }
});

builder.Services.AddHttpClient<IModelSourceProvider, OllamaModelProvider>(client => {
    client.BaseAddress = new Uri(config.ProviderEndpoints.Ollama.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(config.ProviderEndpoints.Ollama.TimeoutSeconds);
    client.DefaultRequestHeaders.Add("User-Agent", "OpenCodeMcpServer/1.0");
});

builder.Services.AddHttpClient<IModelSourceProvider, OpenRouterModelProvider>(client => {
    client.BaseAddress = new Uri(config.ProviderEndpoints.OpenRouter.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(config.ProviderEndpoints.OpenRouter.TimeoutSeconds);
    client.DefaultRequestHeaders.Add("User-Agent", "OpenCodeMcpServer/1.0");
    if (!string.IsNullOrWhiteSpace(config.ProviderApiKeys.OpenRouterApiKey)) {
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ProviderApiKeys.OpenRouterApiKey}");
    }
});

if (config.ProviderEndpoints.TogetherAi.Enabled) {
    builder.Services.AddHttpClient<IModelSourceProvider, TogetherAiModelProvider>(client => {
        client.BaseAddress = new Uri(config.ProviderEndpoints.TogetherAi.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(config.ProviderEndpoints.TogetherAi.TimeoutSeconds);
        client.DefaultRequestHeaders.Add("User-Agent", "OpenCodeMcpServer/1.0");
        if (!string.IsNullOrWhiteSpace(config.ProviderApiKeys.TogetherAiApiKey)) {
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ProviderApiKeys.TogetherAiApiKey}");
        }
    });
}

if (config.ProviderEndpoints.Groq.Enabled) {
    builder.Services.AddHttpClient<IModelSourceProvider, GroqModelProvider>(client => {
        client.BaseAddress = new Uri(config.ProviderEndpoints.Groq.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(config.ProviderEndpoints.Groq.TimeoutSeconds);
        client.DefaultRequestHeaders.Add("User-Agent", "OpenCodeMcpServer/1.0");
        if (!string.IsNullOrWhiteSpace(config.ProviderApiKeys.GroqApiKey)) {
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ProviderApiKeys.GroqApiKey}");
        }
    });
}

// OpenAI 兼容提供商共享 HttpClient（目录拉取/健康探测/代理转发统一使用，BaseAddress 每次请求指定完整 URL）
builder.Services.AddHttpClient("openai-compat", client => {
    client.Timeout = TimeSpan.FromSeconds(600); // 单次请求超时由 CancellationTokenSource 按提供商配置控制
    client.DefaultRequestHeaders.Add("User-Agent", "OpenCodeMcpServer/1.0");
});

// 注册 OpenAI 兼容模型目录源（agnes/stepfun/sensenova/longcat/nvidia/opencode/openrouter/tokenrhythm 等，配置驱动）
builder.Services.AddSingleton<IModelSourceProvider, OpenAiCompatibleCatalogProvider>();

// 注册提供商健康与聊天代理服务（自动优化：评分选路 + 故障转移）
builder.Services.AddSingleton<ProviderHealthService>();
builder.Services.AddSingleton<IProviderHealthService>(sp => sp.GetRequiredService<ProviderHealthService>());
builder.Services.AddSingleton<IChatCompletionProxyService, ChatCompletionProxyService>();

// 注册服务
builder.Services.AddSingleton<IModelDiscoveryService, ModelDiscoveryService>();
builder.Services.AddSingleton<IPromptManagementService, PromptManagementService>();
builder.Services.AddSingleton<ISkillManagementService, SkillManagementService>();

// 注册背景刷新服务（定时自动刷新模型缓存）
builder.Services.AddHostedService<ModelCacheRefreshService>();

// 注册 MCP 服务器
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

try {
    await app.RunAsync();
} catch (Exception ex) {
    FileLoggerProvider.Crash("主机运行异常退出", ex);
    throw;
}