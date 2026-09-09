// <summary>
/// 【功能说明】：模型缓存定时刷新服务 - 后台定时拉取最新免费模型列表
/// 【服务对象】：IModelDiscoveryService，在服务器运行期间自动刷新缓存
/// 【调用方式】：通过 IHostedService 自动启动，无需手动调用
/// 【禁止重复】：项目内唯一定时刷新实现，禁止在其他地方重复实现
/// </summary>
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenCodeMcpServer.Models;

namespace OpenCodeMcpServer.Services;

/// <summary>
/// 模型缓存定时刷新服务
/// </summary>
public class ModelCacheRefreshService : IHostedService, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ModelCacheRefreshService> _logger;
    private readonly McpServerConfig _config;
    private Timer? _timer;
    private Timer? _healthTimer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public ModelCacheRefreshService(
        IServiceProvider serviceProvider,
        ILogger<ModelCacheRefreshService> logger,
        IOptions<McpServerConfig> config)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config.Value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.ModelDiscovery.Enabled || !_config.ModelDiscovery.AutoUpdate)
        {
            _logger.LogInformation("Model auto-refresh disabled");
            return Task.CompletedTask;
        }

        var intervalHours = _config.ModelDiscovery.RefreshIntervalHours > 0
            ? _config.ModelDiscovery.RefreshIntervalHours
            : 24;

        var warmupDelay = _config.ModelDiscovery.WarmupDelaySeconds > 0
            ? _config.ModelDiscovery.WarmupDelaySeconds
            : 5;

        // 启动延迟（等待服务器完全启动）
        var dueTime = TimeSpan.FromSeconds(warmupDelay);
        var period = TimeSpan.FromHours(intervalHours);

        _timer = new Timer(RefreshCallback, null, dueTime, period);

        _logger.LogInformation(
            "Model cache refresh scheduled: first refresh in {Warmup}s, then every {Interval}h",
            warmupDelay, intervalHours);

        // 健康探测独立定时：默认每 6h 全量探测一次，0 或负值禁用
        var probeInterval = _config.ModelDiscovery.HealthProbeIntervalHours;
        if (probeInterval > 0)
        {
            var probePeriod = TimeSpan.FromHours(probeInterval);
            _healthTimer = new Timer(HealthProbeCallback, null, dueTime, probePeriod);
            _logger.LogInformation("Provider health probe scheduled: first probe in {Warmup}s, then every {Probe}h",
                warmupDelay, probeInterval);
        }
        else
        {
            _logger.LogInformation("Background provider health probe disabled (HealthProbeIntervalHours={Value})", probeInterval);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping model cache refresh service...");

        if (_timer != null)
        {
            await _timer.DisposeAsync();
            _timer = null;
        }

        if (_healthTimer != null)
        {
            await _healthTimer.DisposeAsync();
            _healthTimer = null;
        }

        _logger.LogInformation("Model cache refresh service stopped");
    }

    private async void RefreshCallback(object? state)
    {
        if (!await _refreshLock.WaitAsync(0))
        {
            _logger.LogDebug("Refresh already in progress, skipping");
            return;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var modelDiscovery = scope.ServiceProvider.GetRequiredService<IModelDiscoveryService>();

            _logger.LogInformation("Starting scheduled model cache refresh...");

            await modelDiscovery.RefreshCacheAsync();

            var newModels = await modelDiscovery.DiscoverNewModelsAsync();

            if (newModels.Length > 0)
            {
                _logger.LogInformation("Discovered {Count} new free models during scheduled refresh", newModels.Length);
            }

            _logger.LogInformation("Scheduled model cache refresh completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during scheduled model cache refresh");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>后台定时健康探测：全量探测所有启用提供商并更新评分/冷却/落盘</summary>
    private async void HealthProbeCallback(object? state)
    {
        if (!await _refreshLock.WaitAsync(0))
        {
            _logger.LogDebug("Health probe already in progress, skipping");
            return;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var health = scope.ServiceProvider.GetRequiredService<IProviderHealthService>();
            await health.ProbeAllAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during scheduled provider health probe");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _healthTimer?.Dispose();
        _refreshLock.Dispose();
    }
}
