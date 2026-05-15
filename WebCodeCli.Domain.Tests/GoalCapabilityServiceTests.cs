using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Domain.Tests;

public sealed class GoalCapabilityServiceTests
{
    [Fact]
    public async Task ProbeAsync_ReturnsUnavailable_WhenToolIsNotCodex()
    {
        var service = CreateService(
            versionOutput: "codex-cli 0.128.0",
            featureOutput: "goals under development false");

        var result = await service.ProbeAsync(new GoalCapabilityContext
        {
            ToolId = "claude-code",
            ProviderId = "provider-a"
        });

        Assert.Equal(GoalCapabilityState.Unavailable, result.State);
        Assert.Equal(GoalCapabilityProbeOutcome.UnsupportedTool, result.Outcome);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsUnavailable_WhenVersionIsTooLow()
    {
        var service = CreateService(
            versionOutput: "codex-cli 0.127.0",
            featureOutput: "goals under development false");

        var result = await service.ProbeAsync(new GoalCapabilityContext
        {
            ToolId = "codex",
            ProviderId = "provider-b"
        });

        Assert.Equal(GoalCapabilityState.Unavailable, result.State);
        Assert.Equal(GoalCapabilityProbeOutcome.UnsupportedVersion, result.Outcome);
        Assert.Equal("0.127.0", result.DetectedVersion);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsUnavailable_WhenFeatureIsMissing()
    {
        var service = CreateService(
            versionOutput: "codex-cli 0.128.0",
            featureOutput: """
            multi_agent                         stable             true
            reasoning_summaries                 stable             true
            """);

        var result = await service.ProbeAsync(new GoalCapabilityContext
        {
            ToolId = "codex",
            ProviderId = "provider-c"
        });

        Assert.Equal(GoalCapabilityState.Unavailable, result.State);
        Assert.Equal(GoalCapabilityProbeOutcome.MissingFeature, result.Outcome);
        Assert.Equal("0.128.0", result.DetectedVersion);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsAvailable_WhenVersionAndFeatureMatch()
    {
        var service = CreateService(
            versionOutput: "codex-cli 0.128.0",
            featureOutput: """
            goals                               under development  false
            multi_agent                         stable             true
            """);

        var result = await service.ProbeAsync(new GoalCapabilityContext
        {
            ToolId = "codex",
            ProviderId = "provider-d"
        });

        Assert.Equal(GoalCapabilityState.Available, result.State);
        Assert.Equal(GoalCapabilityProbeOutcome.Available, result.Outcome);
        Assert.Equal("0.128.0", result.DetectedVersion);
        Assert.True(result.HasGoalsFeature);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsAvailable_WhenCodexUsesOneTimeProcess()
    {
        var service = CreateService(
            versionOutput: "codex-cli 0.128.0",
            featureOutput: """
            goals                               under development  false
            multi_agent                         stable             true
            """,
            usePersistentProcess: false);

        var result = await service.ProbeAsync(new GoalCapabilityContext
        {
            ToolId = "codex",
            ProviderId = "provider-persistent"
        });

        Assert.Equal(GoalCapabilityState.Available, result.State);
        Assert.Equal(GoalCapabilityProbeOutcome.Available, result.Outcome);
    }

    [Fact]
    public async Task ProbeAsync_UsesCache_UntilForceRefresh()
    {
        var service = CreateService(
            versionOutput: "codex-cli 0.127.0",
            featureOutput: "goals under development false");
        var context = new GoalCapabilityContext
        {
            ToolId = "codex",
            ProviderId = "provider-e"
        };

        var initial = await service.ProbeAsync(context);
        Assert.Equal(GoalCapabilityState.Unavailable, initial.State);

        service.VersionOutput = "codex-cli 0.128.0";
        service.FeatureOutput = "goals under development false";

        var cached = await service.ProbeAsync(context);
        Assert.True(cached.FromCache);
        Assert.Equal(GoalCapabilityState.Unavailable, cached.State);

        var refreshed = await service.ProbeAsync(context, forceRefresh: true);
        Assert.False(refreshed.FromCache);
        Assert.Equal(GoalCapabilityState.Available, refreshed.State);
    }

    private static TestGoalCapabilityService CreateService(string versionOutput, string featureOutput, bool usePersistentProcess = true)
    {
        return new TestGoalCapabilityService(
            new StubCcSwitchService(),
            Options.Create(new CliToolsOption
            {
                Tools =
                [
                    new CliToolConfig
                    {
                        Id = "codex",
                        Name = "Codex",
                        Command = "codex",
                        UsePersistentProcess = usePersistentProcess
                    }
                ]
            }),
            NullLogger<GoalCapabilityService>.Instance)
        {
            VersionOutput = versionOutput,
            FeatureOutput = featureOutput
        };
    }

    private sealed class TestGoalCapabilityService : GoalCapabilityService
    {
        public TestGoalCapabilityService(
            ICcSwitchService ccSwitchService,
            IOptions<CliToolsOption> options,
            Microsoft.Extensions.Logging.ILogger<GoalCapabilityService> logger)
            : base(ccSwitchService, options, logger)
        {
        }

        public string VersionOutput { get; set; } = "codex-cli 0.128.0";

        public string FeatureOutput { get; set; } = "goals under development false";

        protected override Task<CommandProbeResult> RunCommandAsync(
            string command,
            string arguments,
            string? workingDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (arguments == "--version")
            {
                return Task.FromResult(new CommandProbeResult(true, VersionOutput));
            }

            if (arguments == "features list")
            {
                return Task.FromResult(new CommandProbeResult(true, FeatureOutput));
            }

            return Task.FromResult(new CommandProbeResult(false, string.Empty));
        }
    }

    private sealed class StubCcSwitchService : ICcSwitchService
    {
        public bool IsManagedTool(string toolId)
        {
            return toolId is "claude-code" or "codex" or "opencode";
        }

        public Task<CcSwitchStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<CcSwitchToolStatus> GetToolStatusAsync(string toolId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CcSwitchToolStatus
            {
                ToolId = toolId,
                ActiveProviderId = $"{toolId}-provider",
                IsManaged = true,
                IsLaunchReady = true
            });
        }

        public Task<IReadOnlyDictionary<string, CcSwitchToolStatus>> GetToolStatusesAsync(IEnumerable<string> toolIds, CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<string, CcSwitchToolStatus> result = toolIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    toolId => toolId,
                    toolId => new CcSwitchToolStatus
                    {
                        ToolId = toolId,
                        ActiveProviderId = $"{toolId}-provider",
                        IsManaged = true,
                        IsLaunchReady = true
                    },
                    StringComparer.OrdinalIgnoreCase);

            return Task.FromResult(result);
        }

        public Task<CcSwitchModelCatalog> GetModelCatalogAsync(string toolId, string? providerId = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
