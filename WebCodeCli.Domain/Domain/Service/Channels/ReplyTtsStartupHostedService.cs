using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebCodeCli.Domain.Domain.Service.Channels;

public sealed class ReplyTtsStartupHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReplyTtsStartupHostedService> _logger;

    public ReplyTtsStartupHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<ReplyTtsStartupHostedService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var enablementService = scope.ServiceProvider.GetRequiredService<IReplyTtsEnablementService>();
            if (!await enablementService.HasEnabledReplyTtsAsync(cancellationToken))
            {
                _logger.LogDebug("Skipping local reply TTS startup because no Feishu user has reply TTS enabled.");
                return;
            }

            var platformService = scope.ServiceProvider.GetRequiredService<IFeishuReplyTtsPlatformService>();
            var health = await platformService.EnsureServiceStartedAsync(cancellationToken);
            if (health.IsAvailable)
            {
                _logger.LogInformation("Local reply TTS service is ready at startup. Status={ServiceStatus}", health.ServiceStatus);
                return;
            }

            _logger.LogWarning(
                "Local reply TTS service was requested at startup but is unavailable. Status={ServiceStatus}, Message={Message}",
                health.ServiceStatus,
                health.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure local reply TTS service at startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
