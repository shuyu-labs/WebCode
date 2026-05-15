using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service.Channels;

namespace WebCodeCli.Domain.Tests;

public sealed class ReplyTtsStartupHostedServiceTests
{
    [Fact]
    public async Task StartAsync_WhenNoReplyTtsConfigIsEnabled_DoesNotStartLocalService()
    {
        using var harness = new Harness(replyTtsEnabled: false);

        await harness.Service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.EnablementService.CallCount);
        Assert.Equal(0, harness.PlatformService.EnsureStartedCallCount);
    }

    [Fact]
    public async Task StartAsync_WhenAnyReplyTtsConfigIsEnabled_StartsLocalService()
    {
        using var harness = new Harness(replyTtsEnabled: true);

        await harness.Service.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.EnablementService.CallCount);
        Assert.Equal(1, harness.PlatformService.EnsureStartedCallCount);
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;

        public Harness(bool replyTtsEnabled)
        {
            EnablementService = new StubReplyTtsEnablementService(replyTtsEnabled);
            PlatformService = new StubFeishuReplyTtsPlatformService();

            var services = new ServiceCollection();
            services.AddScoped<IReplyTtsEnablementService>(_ => EnablementService);
            services.AddScoped<IFeishuReplyTtsPlatformService>(_ => PlatformService);
            _serviceProvider = services.BuildServiceProvider();

            Service = new ReplyTtsStartupHostedService(
                _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ReplyTtsStartupHostedService>.Instance);
        }

        public StubReplyTtsEnablementService EnablementService { get; }

        public StubFeishuReplyTtsPlatformService PlatformService { get; }

        public ReplyTtsStartupHostedService Service { get; }

        public void Dispose()
        {
            _serviceProvider.Dispose();
        }
    }

    private sealed class StubReplyTtsEnablementService(bool replyTtsEnabled) : IReplyTtsEnablementService
    {
        public int CallCount { get; private set; }

        public Task<bool> HasEnabledReplyTtsAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(replyTtsEnabled);
        }
    }

    private sealed class StubFeishuReplyTtsPlatformService : IFeishuReplyTtsPlatformService
    {
        public int EnsureStartedCallCount { get; private set; }

        public Task<FeishuReplyTtsHealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<FeishuReplyTtsVoiceOption>> GetVoicesAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FeishuReplyTtsVoiceResolutionResult> ResolveVoiceOrFallbackAsync(
            string? savedVoiceId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<FeishuReplyTtsHealthStatus> EnsureServiceStartedAsync(CancellationToken cancellationToken = default)
        {
            EnsureStartedCallCount++;
            return Task.FromResult(new FeishuReplyTtsHealthStatus
            {
                IsAvailable = true,
                ServiceStatus = "ok"
            });
        }
    }
}
