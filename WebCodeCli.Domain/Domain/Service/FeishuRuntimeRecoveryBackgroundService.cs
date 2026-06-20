using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebCodeCli.Domain.Domain.Service;

public sealed class FeishuRuntimeRecoveryBackgroundService : BackgroundService
{
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SuspendGapThreshold = TimeSpan.FromSeconds(90);

    private readonly IUserFeishuBotRuntimeService _runtimeService;
    private readonly ILogger<FeishuRuntimeRecoveryBackgroundService> _logger;

    public FeishuRuntimeRecoveryBackgroundService(
        IUserFeishuBotRuntimeService runtimeService,
        ILogger<FeishuRuntimeRecoveryBackgroundService> logger)
    {
        _runtimeService = runtimeService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastObservationUtc = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(ObservationInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTimeOffset.UtcNow;
            var gap = now - lastObservationUtc;
            lastObservationUtc = now;

            if (gap < SuspendGapThreshold)
            {
                continue;
            }

            _logger.LogInformation(
                "检测到宿主长时间挂起，准备重建飞书机器人运行态: Gap={Gap}",
                gap);

            try
            {
                await _runtimeService.RecoverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "重建飞书机器人运行态失败: Gap={Gap}", gap);
            }
        }
    }
}
