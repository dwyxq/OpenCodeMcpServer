using System.Text;
using Microsoft.Extensions.Logging;
using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services.Logging;

/// <summary>
/// 【功能说明】：文件日志记录器，将全部日志（含主要流程与错误堆栈）按天写入 logs 目录，用于进程崩溃后追溯错误。
/// 【服务对象】：全局通用（通过 ILoggerProvider 注入，所有 ILogger&lt;T&gt; 自动落盘）
/// 【调用方式】：Program.cs 中 builder.Logging.AddProvider(new FileLoggerProvider(config.Logging))，业务代码一律用 ILogger&lt;T&gt;，禁止直接 new 本类
/// 【禁止重复】：项目内唯一文件日志实现，禁止再引入其他日志框架（Serilog/NLog 等）
/// </summary>
/// <remarks>
/// 设计要点：
/// 1. MCP stdio 模式 stdout 被协议占用、stderr 随宿主退出丢失，必须独立落盘才能追溯（如启动即崩溃的场景）。
/// 2. 按天滚动 opencode-mcp-yyyyMMdd.log，超过 MaxLogFileSizeMb 追加序号分段，保留最近 MaxLogFiles 个文件。
/// 3. 写入失败静默吞掉（日志故障绝不能拖垮宿主），并尽力写一条 CRITICAL 到控制台 stderr。
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLogWriter _writer;

    /// <summary>
    /// 【功能说明】：按日志配置创建文件日志 Provider
    /// 【调用方式】：仅 Program.cs 调用
    /// </summary>
    public FileLoggerProvider(LoggingConfig config)
    {
        _writer = new FileLogWriter(
            string.IsNullOrWhiteSpace(config.LogDirectory)
                ? Path.Combine(AppContext.BaseDirectory, "logs")
                : Path.IsPathRooted(config.LogDirectory)
                    ? config.LogDirectory
                    : Path.Combine(AppContext.BaseDirectory, config.LogDirectory),
            config.MaxLogFiles > 0 ? config.MaxLogFiles : 10,
            config.MaxLogFileSizeMb > 0 ? config.MaxLogFileSizeMb : 10,
            enabled: config.EnableFileLogging);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_writer, categoryName);

    public void Dispose() => _writer.Dispose();

    /// <summary>
    /// 【功能说明】：崩溃兜底写入——宿主尚未构建/配置加载失败/未处理异常时使用，直接写默认日志目录，不依赖 DI。
    /// 【服务对象】：Program.cs 全局异常钩子与配置加载 catch
    /// 【调用方式】：静态直接调用
    /// 【禁止重复】：项目内唯一崩溃日志入口
    /// </summary>
    /// <param name="message">错误描述</param>
    /// <param name="ex">异常对象（可空）</param>
    public static void Crash(string message, Exception? ex)
    {
        try
        {
            using var fallback = new FileLogWriter(Path.Combine(AppContext.BaseDirectory, "logs"), 10, 10, enabled: true);
            fallback.Write("CRITICAL", "Fatal", 0, message, ex);
        }
        catch
        {
            // 兜底再失败时只能放弃，绝不能再抛
        }
    }

    /// <summary>
    /// 【功能说明】：文件日志单条实现（由 FileLoggerProvider 创建，不单独注册）
    /// </summary>
    private sealed class FileLogger(FileLogWriter writer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            writer.Write(ToLevelName(logLevel), category, eventId.Id, formatter(state, exception), exception);
        }

        private static string ToLevelName(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "FATAL",
            _ => "OTHER",
        };
    }
}

/// <summary>
/// 【功能说明】：线程安全的日志文件写入器：按天/按大小滚动、旧文件清理、失败静默。
/// 【服务对象】：仅 FileLoggerProvider 内部使用
/// 【调用方式】：由 FileLoggerProvider 持有
/// 【禁止重复】：项目内唯一文件写入实现
/// </summary>
internal sealed class FileLogWriter : IDisposable
{
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly int _maxFiles;
    private readonly long _maxBytes;
    private readonly bool _enabled;
    private StreamWriter? _writer;
    private string? _currentPath;
    private long _currentSize;
    private int _segment;
    private bool _disposed;

    internal FileLogWriter(string directory, int maxFiles, int maxFileSizeMb, bool enabled)
    {
        _directory = directory;
        _maxFiles = maxFiles;
        _maxBytes = (long)maxFileSizeMb * 1024 * 1024;
        _enabled = enabled;
    }

    /// <summary>
    /// 【功能说明】：写入一条日志（含时间戳/级别/来源/EventId/异常堆栈），内部加锁保证并发安全
    /// </summary>
    internal void Write(string level, string category, int eventId, string message, Exception? exception)
    {
        if (!_enabled || _disposed) return;
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}" +
                       (eventId != 0 ? $"({eventId})" : string.Empty) + $" | {message}";
            lock (_sync)
            {
                if (_disposed) return;
                EnsureWriter();
                if (_writer is null) return;
                _writer.WriteLine(line);
                if (exception is not null)
                {
                    _writer.WriteLine(exception);
                }
                _writer.Flush();
                _currentSize += Encoding.UTF8.GetByteCount(line) + 2 + (exception?.ToString().Length ?? 0);
                if (_currentSize >= _maxBytes)
                {
                    RollSegment();
                }
            }
        }
        catch
        {
            // 日志写入失败绝不能影响业务流程
        }
    }

    private void EnsureWriter()
    {
        var path = Path.Combine(_directory, $"opencode-mcp-{DateTime.Now:yyyyMMdd}.log");
        if (_writer is not null && path == _currentPath) return;
        _writer?.Dispose();
        Directory.CreateDirectory(_directory);
        _currentPath = path;
        _segment = 0;
        var fi = new FileInfo(path);
        _currentSize = fi.Exists ? fi.Length : 0;
        _writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = false };
        CleanupOldFiles();
    }

    private void RollSegment()
    {
        _writer?.Dispose();
        _segment++;
        var path = Path.Combine(_directory!, $"opencode-mcp-{DateTime.Now:yyyyMMdd}.{_segment}.log");
        _currentPath = path;
        _currentSize = 0;
        _writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = false };
        CleanupOldFiles();
    }

    /// <summary>
    /// 【功能说明】：仅保留最近 MaxLogFiles 个日志文件，防止磁盘占满
    /// </summary>
    private void CleanupOldFiles()
    {
        try
        {
            var files = Directory.GetFiles(_directory!, "opencode-mcp-*.log")
                .OrderByDescending(f => System.IO.File.GetLastWriteTimeUtc(f))
                .ToList();
            for (var i = _maxFiles; i < files.Count; i++)
            {
                try { System.IO.File.Delete(files[i]); } catch { /* 被占用则跳过 */ }
            }
        }
        catch
        {
            // 清理失败忽略
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}
