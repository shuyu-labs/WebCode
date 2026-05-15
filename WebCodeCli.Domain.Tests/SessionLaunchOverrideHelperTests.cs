using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Domain.Tests;

public class SessionLaunchOverrideHelperTests
{
    [Fact]
    public void NormalizeToolId_NormalizesKnownAliases()
    {
        Assert.Equal("claude-code", SessionLaunchOverrideHelper.NormalizeToolId("claude"));
        Assert.Equal("opencode", SessionLaunchOverrideHelper.NormalizeToolId("opencode-cli"));
        Assert.Equal("codex", SessionLaunchOverrideHelper.NormalizeToolId("codex"));
    }

    [Fact]
    public void ApplyOverride_CodexOverride_StoresTrimmedValues()
    {
        var updatedOverrides = SessionLaunchOverrideHelper.ApplyOverride(
            null,
            "codex",
            "  gpt-5.4  ",
            "  high ");

        var codexOverride = Assert.Single(updatedOverrides);
        Assert.Equal("codex", codexOverride.Key);
        Assert.Equal("gpt-5.4", codexOverride.Value.Model);
        Assert.Equal("high", codexOverride.Value.ReasoningEffort);
    }

    [Fact]
    public void ApplyPersistentProcessOverride_StoresProcessModeWithoutTouchingModelSettings()
    {
        var existingOverrides = new Dictionary<string, SessionToolLaunchOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = new() { Model = "gpt-5.4", ReasoningEffort = "high" }
        };

        var updatedOverrides = SessionLaunchOverrideHelper.ApplyPersistentProcessOverride(
            existingOverrides,
            "codex",
            true);

        var codexOverride = Assert.Single(updatedOverrides);
        Assert.Equal("codex", codexOverride.Key);
        Assert.Equal("gpt-5.4", codexOverride.Value.Model);
        Assert.Equal("high", codexOverride.Value.ReasoningEffort);
        Assert.True(codexOverride.Value.UsePersistentProcess);
    }

    [Fact]
    public void ApplyGoalRuntimeOverride_StoresGoalRuntimeWithoutTouchingLaunchSettings()
    {
        var existingOverrides = new Dictionary<string, SessionToolLaunchOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = new() { Model = "gpt-5.4", ReasoningEffort = "high", UsePersistentProcess = false }
        };

        var updatedOverrides = SessionLaunchOverrideHelper.ApplyGoalRuntimeOverride(
            existingOverrides,
            "codex",
            true);

        var codexOverride = Assert.Single(updatedOverrides);
        Assert.Equal("codex", codexOverride.Key);
        Assert.Equal("gpt-5.4", codexOverride.Value.Model);
        Assert.Equal("high", codexOverride.Value.ReasoningEffort);
        Assert.False(codexOverride.Value.UsePersistentProcess);
        Assert.True(codexOverride.Value.UseGoalRuntime);
    }

    [Fact]
    public void ApplyOverride_ClaudeReasoningOverride_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => SessionLaunchOverrideHelper.ApplyOverride(
            null,
            "claude-code",
            "sonnet",
            "high"));

        Assert.Contains("does not support reasoning effort", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyOverride_InvalidCodexReasoningOverride_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => SessionLaunchOverrideHelper.ApplyOverride(
            null,
            "codex",
            "gpt-5.4",
            "ultra"));

        Assert.Contains("Unsupported Codex reasoning effort", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyOverride_BlankFields_RemoveEntry()
    {
        var existingOverrides = new Dictionary<string, SessionToolLaunchOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = new() { Model = "gpt-5.4", ReasoningEffort = "high" },
            ["claude-code"] = new() { Model = "sonnet" }
        };

        var updatedOverrides = SessionLaunchOverrideHelper.ApplyOverride(
            existingOverrides,
            "codex",
            "   ",
            null);

        Assert.Single(updatedOverrides);
        Assert.False(updatedOverrides.ContainsKey("codex"));
        Assert.Equal("sonnet", updatedOverrides["claude-code"].Model);
    }

    [Fact]
    public void GetEffectiveOverride_UsesSnapshotToolId()
    {
        var session = new SessionHistory
        {
            ToolId = "claude-code",
            CcSwitchSnapshotToolId = "codex",
            ToolLaunchOverrides = new Dictionary<string, SessionToolLaunchOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new() { Model = "gpt-5.4", ReasoningEffort = "high" },
                ["claude-code"] = new() { Model = "sonnet" }
            }
        };

        var effectiveOverride = SessionLaunchOverrideHelper.GetEffectiveOverride(session);

        Assert.NotNull(effectiveOverride);
        Assert.Equal("gpt-5.4", effectiveOverride!.Model);
        Assert.Equal("high", effectiveOverride.ReasoningEffort);
    }

    [Fact]
    public void SerializeAndDeserialize_RoundTripsCamelCasePayload()
    {
        var overrides = new Dictionary<string, SessionToolLaunchOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = new() { Model = "gpt-5.4", ReasoningEffort = "high", UsePersistentProcess = true, UseGoalRuntime = true },
            ["claude-code"] = new() { Model = "sonnet" }
        };

        var json = SessionLaunchOverrideHelper.Serialize(overrides);

        Assert.NotNull(json);
        Assert.Contains("\"reasoningEffort\":\"high\"", json, StringComparison.Ordinal);
        Assert.Contains("\"usePersistentProcess\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"useGoalRuntime\":true", json, StringComparison.Ordinal);

        var roundTrippedOverrides = SessionLaunchOverrideHelper.Deserialize(json);

        Assert.Equal("gpt-5.4", roundTrippedOverrides["codex"].Model);
        Assert.Equal("high", roundTrippedOverrides["codex"].ReasoningEffort);
        Assert.True(roundTrippedOverrides["codex"].UsePersistentProcess);
        Assert.True(roundTrippedOverrides["codex"].UseGoalRuntime);
        Assert.Equal("sonnet", roundTrippedOverrides["claude-code"].Model);
    }

    [Fact]
    public void Deserialize_InvalidJson_ReturnsEmptyOverrides()
    {
        var overrides = SessionLaunchOverrideHelper.Deserialize("{invalid json");

        Assert.Empty(overrides);
    }
}
