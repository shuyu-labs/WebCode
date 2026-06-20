using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Repositories.Base.UserFeishuBotConfig;

namespace WebCodeCli.Domain.Tests;

public class UserFeishuBotRuntimeServiceTests
{
    [Fact]
    public async Task StartAsync_PersistsAutoStartAndLastStartedAt()
    {
        var configService = new InMemoryUserFeishuBotConfigService();
        configService.Store(new UserFeishuBotConfigEntity
        {
            Username = "alice",
            IsEnabled = true,
            AppId = "cli_alice",
            AppSecret = "secret"
        });

        var runtimeService = CreateService(configService);
        runtimeService.EnqueueHostedService(new TestHostedService());

        var status = await runtimeService.StartAsync("alice");
        var stored = await configService.GetByUsernameAsync("alice");

        Assert.Equal(UserFeishuBotRuntimeState.Connected, status.State);
        Assert.True(status.ShouldAutoStart);
        Assert.NotNull(status.LastStartedAt);
        Assert.NotNull(stored);
        Assert.True(stored!.AutoStartEnabled);
        Assert.NotNull(stored.LastStartedAt);
    }

    [Fact]
    public async Task HostedServiceStartAsync_RestoresRememberedBots()
    {
        var configService = new InMemoryUserFeishuBotConfigService();
        configService.Store(new UserFeishuBotConfigEntity
        {
            Username = "alice",
            IsEnabled = true,
            AutoStartEnabled = true,
            AppId = "cli_alice",
            AppSecret = "secret",
            LastStartedAt = DateTime.Now.AddMinutes(-5)
        });

        var runtimeService = CreateService(configService);
        runtimeService.EnqueueHostedService(new TestHostedService());

        await ((IHostedService)runtimeService).StartAsync(CancellationToken.None);
        var status = await runtimeService.GetStatusAsync("alice");

        Assert.Equal(1, runtimeService.CreateRuntimeEntryCallCount);
        Assert.Equal(UserFeishuBotRuntimeState.Connected, status.State);
        Assert.True(status.ShouldAutoStart);
    }

    [Fact]
    public async Task StopAsync_ClearsRememberedState_ButHostStopPreservesIt()
    {
        var configService = new InMemoryUserFeishuBotConfigService();
        configService.Store(new UserFeishuBotConfigEntity
        {
            Username = "alice",
            IsEnabled = true,
            AppId = "cli_alice",
            AppSecret = "secret"
        });

        var runtimeService = CreateService(configService);
        runtimeService.EnqueueHostedService(new TestHostedService());

        await runtimeService.StartAsync("alice");
        var manualStopStatus = await runtimeService.StopAsync("alice");
        var afterManualStop = await configService.GetByUsernameAsync("alice");

        Assert.Equal(UserFeishuBotRuntimeState.Stopped, manualStopStatus.State);
        Assert.False(manualStopStatus.ShouldAutoStart);
        Assert.NotNull(afterManualStop);
        Assert.False(afterManualStop!.AutoStartEnabled);

        runtimeService.EnqueueHostedService(new TestHostedService());
        await runtimeService.StartAsync("alice");
        await ((IHostedService)runtimeService).StopAsync(CancellationToken.None);
        var afterHostStop = await configService.GetByUsernameAsync("alice");

        Assert.NotNull(afterHostStop);
        Assert.True(afterHostStop!.AutoStartEnabled);
    }

    [Fact]
    public async Task RecoverAsync_RestartsRememberedBotsWithoutClearingAutoStart()
    {
        var configService = new InMemoryUserFeishuBotConfigService();
        configService.Store(new UserFeishuBotConfigEntity
        {
            Username = "alice",
            IsEnabled = true,
            AppId = "cli_alice",
            AppSecret = "secret"
        });

        var runtimeService = CreateService(configService);
        var initialHostedService = new TestHostedService();
        var recoveredHostedService = new TestHostedService();
        runtimeService.EnqueueHostedService(initialHostedService);
        runtimeService.EnqueueHostedService(recoveredHostedService);

        await runtimeService.StartAsync("alice");
        await runtimeService.RecoverAsync();

        var status = await runtimeService.GetStatusAsync("alice");
        var stored = await configService.GetByUsernameAsync("alice");

        Assert.Equal(2, runtimeService.CreateRuntimeEntryCallCount);
        Assert.Equal(1, initialHostedService.StartCallCount);
        Assert.Equal(1, initialHostedService.StopCallCount);
        Assert.Equal(1, recoveredHostedService.StartCallCount);
        Assert.Equal(0, recoveredHostedService.StopCallCount);
        Assert.Equal(UserFeishuBotRuntimeState.Connected, status.State);
        Assert.True(status.ShouldAutoStart);
        Assert.NotNull(stored);
        Assert.True(stored!.AutoStartEnabled);
    }

    private static TestableUserFeishuBotRuntimeService CreateService(InMemoryUserFeishuBotConfigService configService)
    {
        var scopeFactory = new TestScopeFactory(configService);
        return new TestableUserFeishuBotRuntimeService(
            new ServiceCollection().BuildServiceProvider(),
            scopeFactory,
            NullLogger<UserFeishuBotRuntimeService>.Instance);
    }

    private sealed class TestableUserFeishuBotRuntimeService : UserFeishuBotRuntimeService
    {
        private readonly Queue<IHostedService> _hostedServices = new();

        public TestableUserFeishuBotRuntimeService(
            IServiceProvider rootServiceProvider,
            IServiceScopeFactory scopeFactory,
            ILogger<UserFeishuBotRuntimeService> logger)
            : base(rootServiceProvider, scopeFactory, logger)
        {
        }

        public int CreateRuntimeEntryCallCount { get; private set; }

        public void EnqueueHostedService(IHostedService hostedService)
        {
            _hostedServices.Enqueue(hostedService);
        }

        protected override RuntimeEntry CreateRuntimeEntry(FeishuOptions options)
        {
            CreateRuntimeEntryCallCount++;
            var provider = new ServiceCollection().BuildServiceProvider();
            var hostedService = _hostedServices.Count > 0
                ? _hostedServices.Dequeue()
                : new TestHostedService();
            return new RuntimeEntry(options.AppId, provider, hostedService);
        }
    }

    private sealed class TestScopeFactory(InMemoryUserFeishuBotConfigService configService) : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IUserFeishuBotConfigService))
            {
                return configService;
            }

            if (serviceType == typeof(IServiceScopeFactory))
            {
                return this;
            }

            return null;
        }

        public void Dispose()
        {
        }
    }

    private sealed class InMemoryUserFeishuBotConfigService : IUserFeishuBotConfigService
    {
        private readonly Dictionary<string, UserFeishuBotConfigEntity> _configs = new(StringComparer.OrdinalIgnoreCase);

        public void Store(UserFeishuBotConfigEntity entity)
        {
            _configs[entity.Username] = Clone(entity);
        }

        public Task<UserFeishuBotConfigEntity?> GetByUsernameAsync(string username)
        {
            return Task.FromResult(_configs.TryGetValue(username, out var entity) ? Clone(entity) : null);
        }

        public Task<UserFeishuBotConfigEntity?> GetByAppIdAsync(string appId)
        {
            var entity = _configs.Values.FirstOrDefault(x => string.Equals(x.AppId, appId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(entity == null ? null : Clone(entity));
        }

        public Task<UserFeishuBotConfigSaveResult> SaveAsync(UserFeishuBotConfigEntity config)
        {
            Store(config);
            return Task.FromResult(UserFeishuBotConfigSaveResult.Saved());
        }

        public Task<bool> DeleteAsync(string username)
        {
            return Task.FromResult(_configs.Remove(username));
        }

        public Task<string?> FindConflictingUsernameByAppIdAsync(string username, string? appId)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<List<UserFeishuBotConfigEntity>> GetAutoStartCandidatesAsync()
        {
            var list = _configs.Values
                .Where(x => x.AutoStartEnabled)
                .Select(Clone)
                .ToList();
            return Task.FromResult(list);
        }

        public Task<bool> UpdateRuntimePreferenceAsync(string username, bool autoStartEnabled, DateTime? lastStartedAt = null)
        {
            if (!_configs.TryGetValue(username, out var entity))
            {
                return Task.FromResult(false);
            }

            entity.AutoStartEnabled = autoStartEnabled;
            if (lastStartedAt.HasValue)
            {
                entity.LastStartedAt = lastStartedAt.Value;
            }

            entity.UpdatedAt = DateTime.Now;
            return Task.FromResult(true);
        }

        public FeishuOptions GetSharedDefaults() => new()
        {
            Enabled = true,
            DefaultCardTitle = "AI助手",
            ThinkingMessage = "思考中..."
        };

        public Task<FeishuOptions> GetEffectiveOptionsAsync(string? username)
        {
            var config = string.IsNullOrWhiteSpace(username)
                ? null
                : _configs.TryGetValue(username, out var entity) ? entity : null;
            var effective = UserFeishuBotOptionsFactory.CreateEffectiveOptions(GetSharedDefaults(), config);
            return Task.FromResult(effective ?? GetSharedDefaults());
        }

        public Task<FeishuOptions?> GetEffectiveOptionsByAppIdAsync(string? appId)
        {
            if (string.IsNullOrWhiteSpace(appId))
            {
                return Task.FromResult<FeishuOptions?>(null);
            }

            var entity = _configs.Values.FirstOrDefault(x => string.Equals(x.AppId, appId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(UserFeishuBotOptionsFactory.CreateEffectiveOptions(GetSharedDefaults(), entity));
        }

        private static UserFeishuBotConfigEntity Clone(UserFeishuBotConfigEntity entity)
        {
            return new UserFeishuBotConfigEntity
            {
                Id = entity.Id,
                Username = entity.Username,
                IsEnabled = entity.IsEnabled,
                AutoStartEnabled = entity.AutoStartEnabled,
                AppId = entity.AppId,
                AppSecret = entity.AppSecret,
                EncryptKey = entity.EncryptKey,
                VerificationToken = entity.VerificationToken,
                DefaultCardTitle = entity.DefaultCardTitle,
                ThinkingMessage = entity.ThinkingMessage,
                HttpTimeoutSeconds = entity.HttpTimeoutSeconds,
                StreamingThrottleMs = entity.StreamingThrottleMs,
                LastStartedAt = entity.LastStartedAt,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt
            };
        }
    }

    private sealed class TestHostedService : BackgroundService
    {
        public int StartCallCount { get; private set; }
        public int StopCallCount { get; private set; }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            StartCallCount++;
            return base.StartAsync(cancellationToken);
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            StopCallCount++;
            return base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
    }
}
