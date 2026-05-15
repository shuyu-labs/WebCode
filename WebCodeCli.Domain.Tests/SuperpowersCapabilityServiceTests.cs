using Microsoft.Extensions.Logging.Abstractions;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Domain.Tests;

public sealed class SuperpowersCapabilityServiceTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "SuperpowersCapabilityServiceTests", Guid.NewGuid().ToString("N"));
    private readonly string _userProfileRoot;
    private readonly string _workspaceRoot;

    public SuperpowersCapabilityServiceTests()
    {
        _userProfileRoot = CreateDirectory("user-profile");
        _workspaceRoot = CreateDirectory("workspace");
    }

    [Fact]
    public async Task ProbeAsync_ReturnsAvailable_WhenPluginManifestExistsRecursively()
    {
        CreateAllCodexSkills(
            Path.Combine(_userProfileRoot, ".codex", "plugins", "cache", "openai-curated", "superpowers", "3c463363", "skills"));
        CreatePluginManifest(
            Path.Combine(_userProfileRoot, ".codex", "plugins", "cache", "openai-curated", "superpowers", "3c463363", ".codex-plugin"));

        var service = CreateService();

        var result = await service.ProbeAsync(new SuperpowersCapabilityContext
        {
            ToolId = "codex",
            ProviderId = "provider-a",
            WorkspacePath = _workspaceRoot
        });

        Assert.Equal(SuperpowersCapabilityState.Available, result.State);
        Assert.Equal(SuperpowersCapabilityProbeOutcome.Available, result.Outcome);
        Assert.True(result.HasSuperpowersPlugin);
        Assert.False(result.UsedSkillFallback);
        Assert.True(File.Exists(Path.Combine(_workspaceRoot, ".codex", "skills", "using-superpowers", "SKILL.md")));
    }

    [Fact]
    public async Task ProbeAsync_ReturnsAvailable_WhenAllRequiredSkillsExistWithoutPlugin()
    {
        CreateAllCodexSkills(Path.Combine(_userProfileRoot, ".codex", "skills"));

        var service = CreateService();

        var result = await service.ProbeAsync(new SuperpowersCapabilityContext
        {
            ToolId = "codex",
            ProviderId = "provider-b",
            WorkspacePath = _workspaceRoot
        });

        Assert.Equal(SuperpowersCapabilityState.Available, result.State);
        Assert.Equal(SuperpowersCapabilityProbeOutcome.Available, result.Outcome);
        Assert.False(result.HasSuperpowersPlugin);
        Assert.True(result.UsedSkillFallback);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsUnavailable_WhenRequiredSkillsAreMissing()
    {
        CreateAllCodexSkills(Path.Combine(_userProfileRoot, ".codex", "skills"), missingSkillName: "writing-skills");

        var service = CreateService();

        var result = await service.ProbeAsync(new SuperpowersCapabilityContext
        {
            ToolId = "codex",
            ProviderId = "provider-c",
            WorkspacePath = _workspaceRoot
        });

        Assert.Equal(SuperpowersCapabilityState.Unavailable, result.State);
        Assert.Equal(SuperpowersCapabilityProbeOutcome.MissingCapability, result.Outcome);
        Assert.Equal("当前 Provider 缺少 superpowers 能力", result.Message);
        Assert.Contains("writing-skills", result.MissingSkills);
    }

    [Fact]
    public async Task ProbeAsync_UsesCachePerToolAndProvider_UntilForceRefresh()
    {
        var service = CreateService();
        var context = new SuperpowersCapabilityContext
        {
            ToolId = "codex",
            ProviderId = "provider-d",
            WorkspacePath = _workspaceRoot
        };

        var initial = await service.ProbeAsync(context);
        Assert.Equal(SuperpowersCapabilityState.Unavailable, initial.State);

        CreateAllCodexSkills(
            Path.Combine(_userProfileRoot, ".codex", "plugins", "cache", "openai-curated", "superpowers", "cache-hit", "skills"));
        CreatePluginManifest(
            Path.Combine(_userProfileRoot, ".codex", "plugins", "cache", "openai-curated", "superpowers", "cache-hit", ".codex-plugin"));

        var cached = await service.ProbeAsync(context);
        Assert.True(cached.FromCache);
        Assert.Equal(SuperpowersCapabilityState.Unavailable, cached.State);

        var refreshed = await service.ProbeAsync(context, forceRefresh: true);
        Assert.False(refreshed.FromCache);
        Assert.Equal(SuperpowersCapabilityState.Available, refreshed.State);
        Assert.True(File.Exists(Path.Combine(_workspaceRoot, ".codex", "skills", "using-superpowers", "SKILL.md")));

        var otherProviderState = await service.GetStateAsync(new SuperpowersCapabilityContext
        {
            ToolId = "codex",
            ProviderId = "provider-e",
            WorkspacePath = _workspaceRoot
        });
        Assert.Equal(SuperpowersCapabilityState.Unknown, otherProviderState.State);
    }

    [Fact]
    public async Task ProbeAsync_AllowsOpenCodeFallbackToClaudeSkills_WhenCompatibilityIsEnabled()
    {
        var originalValue = Environment.GetEnvironmentVariable("OPENCODE_DISABLE_CLAUDE_CODE_SKILLS");

        try
        {
            Environment.SetEnvironmentVariable("OPENCODE_DISABLE_CLAUDE_CODE_SKILLS", null);
            CreateAllClaudeSkills(Path.Combine(_userProfileRoot, ".claude", "skills"));

            var service = CreateService();

            var result = await service.ProbeAsync(new SuperpowersCapabilityContext
            {
                ToolId = "opencode",
                ProviderId = "provider-open",
                WorkspacePath = _workspaceRoot
            });

            Assert.Equal(SuperpowersCapabilityState.Available, result.State);
            Assert.True(result.UsedSkillFallback);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_DISABLE_CLAUDE_CODE_SKILLS", originalValue);
        }
    }

    [Fact]
    public async Task ProbeAsync_ReturnsProbeFailed_WhenPluginManifestCannotBeParsed()
    {
        var pluginDirectory = Path.Combine(_userProfileRoot, ".codex", "plugins", "cache", "openai-curated", "superpowers", "broken", ".codex-plugin");
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(pluginDirectory, "plugin.json"), "{invalid-json");

        var service = CreateService();
        var context = new SuperpowersCapabilityContext
        {
            ToolId = "codex",
            ProviderId = "provider-f",
            WorkspacePath = _workspaceRoot
        };

        var result = await service.ProbeAsync(context);
        Assert.Equal(SuperpowersCapabilityState.Unknown, result.State);
        Assert.Equal(SuperpowersCapabilityProbeOutcome.ProbeFailed, result.Outcome);

        var state = await service.GetStateAsync(context);
        Assert.Equal(SuperpowersCapabilityState.Unknown, state.State);
    }

    [Fact]
    public async Task GetStateAsync_ThrowsArgumentNullException_WhenContextIsNull()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.GetStateAsync(null!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private SuperpowersCapabilityService CreateService()
    {
        return new SuperpowersCapabilityService(
            new StubCcSwitchService(),
            NullLogger<SuperpowersCapabilityService>.Instance,
            () => _userProfileRoot);
    }

    private string CreateDirectory(params string[] segments)
    {
        var path = Path.Combine(new[] { _testRoot }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreatePluginManifest(string pluginDirectory)
    {
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(
            Path.Combine(pluginDirectory, "plugin.json"),
            """
            {
              "name": "superpowers",
              "version": "5.0.7",
              "interface": {
                "displayName": "Superpowers"
              }
            }
            """);
    }

    private static void CreateAllCodexSkills(string skillsRoot, string? missingSkillName = null)
    {
        CreateAllSkills(skillsRoot, missingSkillName);
    }

    private static void CreateAllClaudeSkills(string skillsRoot, string? missingSkillName = null)
    {
        CreateAllSkills(skillsRoot, missingSkillName);
    }

    private static void CreateAllSkills(string skillsRoot, string? missingSkillName = null)
    {
        string[] skillNames =
        [
            "brainstorming",
            "dispatching-parallel-agents",
            "executing-plans",
            "finishing-a-development-branch",
            "receiving-code-review",
            "requesting-code-review",
            "subagent-driven-development",
            "systematic-debugging",
            "test-driven-development",
            "using-git-worktrees",
            "using-superpowers",
            "verification-before-completion",
            "writing-plans",
            "writing-skills"
        ];

        foreach (var skillName in skillNames)
        {
            if (string.Equals(skillName, missingSkillName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var skillDirectory = Path.Combine(skillsRoot, skillName);
            Directory.CreateDirectory(skillDirectory);
            File.WriteAllText(
                Path.Combine(skillDirectory, "SKILL.md"),
                $$"""
                ---
                name: {{skillName}}
                description: test skill
                ---

                # {{skillName}}
                """);
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
