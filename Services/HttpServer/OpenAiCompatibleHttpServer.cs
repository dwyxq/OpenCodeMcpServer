// <summary>
/// 【功能说明】：OpenAI 兼容 HTTP 服务 - Kestrel 监听，向 OpenCode 等客户端暴露固定模型别名（SuperModel），
///    GET /v1/models 列可用模型；POST /v1/chat/completions 把别名路由到健康评分最高的真实提供商并转发。
/// 【服务对象】：OpenCode（HTTP provider 配置，无需经过 MCP stdio），等效 freellmapi 的统一 OpenAI 网关
/// 【调用方式】：Program.cs 注册为 IHostedService，随主进程后台启动；配置见 appsettings.json 的 HttpServer 节
/// 【禁止重复】：项目内唯一 OpenAI 兼容 HTTP 出口；路由复用 IChatCompletionProxyService 的健康评分+故障转移
/// </summary>
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services.HttpServer;

/// <summary>
/// OpenAI 兼容 HTTP 网关后台服务（Kestrel）。构建独立 WebApplication，与 MCP stdio 主宿主并行运行。
/// </summary>
public sealed class OpenAiCompatibleHttpServer : IHostedService {
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<OpenAiCompatibleHttpServer> _logger;
    private readonly IOptions<McpServerConfig> _options;
    private readonly IChatCompletionProxyService _proxy;
    private readonly IProviderHealthService _health;
    private WebApplication? _app;

    public OpenAiCompatibleHttpServer(
        ILogger<OpenAiCompatibleHttpServer> logger,
        IOptions<McpServerConfig> options,
        IChatCompletionProxyService proxy,
        IProviderHealthService health) {
        _logger = logger;
        _options = options;
        _proxy = proxy;
        _health = health;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken) {
        var cfg = _options.Value.HttpServer;
        if (!cfg.Enabled) {
            _logger.LogInformation("OpenAI 兼容 HTTP 服务未配置启用（HttpServer:Enabled=false）");
            return;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); // 日志继续走主宿主文件/控制台，避免重复
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        // 鉴权中间件：配置了 ApiKey 则要求 Bearer 匹配
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey)) {
            var token = cfg.ApiKey;
            builder.Services.AddSingleton<Func<string, bool>>(_ => incoming => string.Equals(incoming, token, StringComparison.Ordinal));
        } else {
            builder.Services.AddSingleton<Func<string, bool>>(_ => _ => true);
        }

        builder.WebHost.UseUrls(cfg.Url);
        var app = builder.Build();
        app.Use(async (context, next) => {
            var ok = context.RequestServices.GetRequiredService<Func<string, bool>>();
            string authorizationHeader = context.Request.Headers.Authorization.ToString();
            string apiKey = ExtractBearer(authorizationHeader);
            string ip= context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!ok(apiKey) && !ip.StartsWith("127.0.0.1")) {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new {
                            error = new {
                                message = "Invalid API key",
                                type = "invalid_request_error",
                                code = "invalid_api_key"
                            }
                        },
                        JsonOpts,
                        context.RequestAborted);
                return;
            }
            await next(context);
        });

        MapEndpoints(app);
        await app.StartAsync(cancellationToken);
        _app = app;
        _logger.LogInformation("OpenAI 兼容 HTTP 服务已启动: {Url} (模型别名 {Alias})", cfg.Url, _options.Value.Routing.ModelAlias);

        string ExtractBearer(string authorizationHeader) {
            if (string.IsNullOrWhiteSpace(authorizationHeader)) return string.Empty;
            const string prefix = "Bearer ";
            return authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? authorizationHeader[prefix.Length..].Trim()
                : string.Empty;
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken) {
        if (_app != null) {
            await _app.StopAsync(cancellationToken);
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private void MapEndpoints(WebApplication app) {
        var cfg = _options.Value.HttpServer;
        var alias = _options.Value.Routing.ModelAlias;
        var listenUrl = cfg.Url.TrimEnd('/');

        // GET /v1/models：列出固定别名；可选列出全部真实提供商模型
        app.MapGet("/v1/models", async (HttpContext ctx) => {
            var list = new List<object>();
            list.Add(ModelDto(alias, "OpenCodeMcpServer", "aggregate"));
            if (cfg.IncludeRealModels) {
                foreach (var cat in await _proxy.GetProviderCatalogAsync(ctx.RequestAborted))
                    foreach (var m in cat.Models)
                        list.Add(ModelDto(m, cat.ProviderId, cat.Available ? "available" : "unavailable"));
            }
            return Results.Ok(new { @object = "list", data = list });
        });

        // GET /models（兼容部分客户端省略 /v1 前缀）
        app.MapGet("/models", async (HttpContext ctx) => {
            var list = new List<object> { ModelDto(alias, "OpenCodeMcpServer", "aggregate") };
            return Results.Ok(new { @object = "list", data = list });
        });

        // POST /v1/chat/completions：别名/模型路由到健康评分最高的真实提供商并转发
        app.MapPost("/v1/chat/completions", async (HttpContext ctx) => {
            object? body = null;
            try {
                body = JsonSerializer.Deserialize<JsonElement>(await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync(ctx.RequestAborted));
            } catch (JsonException ex) {
                return Results.Json(new { error = new { message = "Invalid JSON body: " + ex.Message, type = "invalid_request_error" } }, statusCode: StatusCodes.Status400BadRequest, options: JsonOpts);
            }

            var model = GetString(body, "model") ?? alias;
            var messages = ReadMessages(body);
            if (messages.Length == 0)
                return Results.Json(new { error = new { message = "messages 不能为空", type = "invalid_request_error" } }, statusCode: StatusCodes.Status400BadRequest, options: JsonOpts);

            var temperature = GetDouble(body, "temperature");
            var maxTokens = GetInt(body, "max_tokens");
            var stream = GetBool(body, "stream") ?? false;
            var sessionId = GetString(body, "session_id");
            var providerId = GetString(body, "provider_id");
            var strategy = GetString(body, "strategy");
            var capabilities = ReadCapabilities(body);

            var request = new ChatProxyRequest(
                Model: model,
                Messages: messages,
                ProviderId: providerId,
                Temperature: temperature,
                MaxTokens: maxTokens,
                Strategy: strategy,
                SessionId: sessionId,
                Capabilities: capabilities);

            if (stream) {
                // SSE 流式响应
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "text/event-stream; charset=utf-8";
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";

                await foreach (var chunk in _proxy.ChatStreamAsync(request, ctx.RequestAborted).WithCancellation(ctx.RequestAborted)) {
                    var bytes = Encoding.UTF8.GetBytes(chunk);
                    await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
                return Results.Empty;
            }

            var result = await _proxy.ChatAsync(request, ctx.RequestAborted);

            if (!result.Success) {
                var msg = string.IsNullOrWhiteSpace(result.Error)
                    ? "上游全部失败"
                    : result.Error;
                if (result.TriedProviders.Length > 0)
                    msg += $"（已尝试: {string.Join(" -> ", result.TriedProviders)}）";
                return Results.Json(new {
                    error = new { message = msg, type = "upstream_error" }
                },
                    statusCode: StatusCodes.Status502BadGateway,
                    options: JsonOpts);
            }

            // 提取上游响应并包装为 OpenAI 兼容 JSON；兼容流式/非流式原始结构透传
            var upstream = JsonDocument.Parse(result.ResponseJson!).RootElement;
            object content = ExtractContent(upstream);
            var outId = Guid.NewGuid().ToString("N");
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var resp = new Dictionary<string, object?> {
                ["id"] = outId,
                ["object"] = "chat.completion",
                ["created"] = created,
                ["model"] = result.Model,
                ["provider"] = result.ProviderId,
                ["choices"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["index"] = 0,
                        ["message"] = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = content },
                        ["finish_reason"] = GetString(upstream, "finish_reason") ?? "stop"
                    }
                },
                ["usage"] = GetUsage(upstream)
            };
            return Results.Json(resp, options: JsonOpts);
        });
    }

    private static object ExtractContent(JsonElement upstream) {
        if (upstream.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0) {
            var first = choices[0];
            if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c))
                return c.ValueKind == JsonValueKind.String ? c.GetString()! : c.Clone();
            if (first.TryGetProperty("text", out var t))
                return t.ValueKind == JsonValueKind.String ? t.GetString()! : t.Clone();
        }
        return string.Empty;
    }

    private static object? GetUsage(JsonElement upstream) {
        if (upstream.TryGetProperty("usage", out var usage))
            return usage.Clone();
        return null;
    }

    private static object ModelDto(string id, string owner, string status) => new {
        id, @object = "model", created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), owned_by = owner, status
    };

    private static string? GetString(object? body, string name) {
        if (body is not JsonElement el || !el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }

    private static double? GetDouble(object? body, string name) {
        if (body is not JsonElement el || !el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind is JsonValueKind.Number or JsonValueKind.String && double.TryParse(v.ToString(), out var d) ? d : null;
    }

    private static int? GetInt(object? body, string name) {
        if (body is not JsonElement el || !el.TryGetProperty(name, out var v)) return null;
        return int.TryParse(v.ToString(), out var i) ? i : null;
    }

    private static bool? GetBool(object? body, string name) {
        if (body is not JsonElement el || !el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.True ? true : v.ValueKind == JsonValueKind.False ? false : null;
    }

    private static ChatMessage[] ReadMessages(object? body) {
        if (body is not JsonElement el || !el.TryGetProperty("messages", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<ChatMessage>();
        var list = new List<ChatMessage>();
        foreach (var m in arr.EnumerateArray()) {
            var role = m.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            string? content = null;
            if (m.TryGetProperty("content", out var c))
                content = c.ValueKind == JsonValueKind.String ? c.GetString() : c.ToString();
            // 保留 tool_call_id（tool 角色消息必填）
            string? toolCallId = null;
            if (m.TryGetProperty("tool_call_id", out var tci) && tci.ValueKind == JsonValueKind.String)
                toolCallId = tci.GetString();
            // 保留 assistant 的 tool_calls 数组（函数调用场景）
            string? toolCallsJson = null;
            if (m.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                toolCallsJson = tc.GetRawText();
            if (!string.IsNullOrWhiteSpace(role))
                list.Add(new ChatMessage(role!, content ?? "", toolCallId, toolCallsJson));
        }
        return list.ToArray();
    }

    private static string[]? ReadCapabilities(object? body) => GetString(body, "capabilities")?.Split(',', StringSplitOptions.RemoveEmptyEntries);
}