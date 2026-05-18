using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Repositories.Base.ChatSession;

namespace WebCodeCli.Domain.Domain.Service.Channels;

internal static class GoalQuickActionVisibilityHelper
{
    public static bool ShouldShowButtons(
        ChatSessionEntity? session,
        ICliExecutorService cliExecutor,
        string? toolId)
    {
        var effectiveToolId = SessionLaunchOverrideHelper.ResolveEffectiveToolId(
            toolId ?? session?.ToolId,
            session?.CcSwitchSnapshotToolId);
        if (string.IsNullOrWhiteSpace(effectiveToolId))
        {
            return false;
        }

        var tool = cliExecutor.GetTool(effectiveToolId, session?.Username);
        if (tool == null)
        {
            return false;
        }

        var launchOverride = SessionLaunchOverrideHelper.GetEffectiveOverride(
            SessionLaunchOverrideHelper.Deserialize(session?.ToolLaunchOverridesJson),
            effectiveToolId,
            session?.ToolId,
            session?.CcSwitchSnapshotToolId);

        if (launchOverride?.UseGoalRuntime == true)
        {
            return true;
        }

        return launchOverride?.UsePersistentProcess ?? tool.UsePersistentProcess;
    }
}
