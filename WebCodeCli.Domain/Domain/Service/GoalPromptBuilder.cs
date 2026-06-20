using WebCodeCli.Domain.Domain.Service.Channels;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service;

public static class GoalPromptBuilder
{
    public static string? BuildPromptForAction(string action, string? input)
    {
        return action switch
        {
            FeishuHelpCardAction.StatusGoalAction => GoalQuickActionDefaults.StatusPrompt,
            FeishuHelpCardAction.PauseGoalAction => GoalQuickActionDefaults.PausePrompt,
            FeishuHelpCardAction.ClearGoalAction => GoalQuickActionDefaults.ClearPrompt,
            FeishuHelpCardAction.ResumeGoalAction => GoalQuickActionDefaults.ResumePrompt,
            FeishuHelpCardAction.ExecuteSuperpowersGoalPlanAction => BuildSubagentPlanGoalPrompt(),
            _ => BuildGoalPrompt(input)
        };
    }

    public static string BuildSubagentPlanGoalPrompt(string? latestAssistantReply = null, string? workspaceRoot = null)
    {
        var referencedPlanDocument = TryResolvePlanDocumentReference(latestAssistantReply, workspaceRoot);
        if (!string.IsNullOrWhiteSpace(referencedPlanDocument))
        {
            return BuildGoalPrompt(
                $"使用Subagent-Driven完成plan文档 {referencedPlanDocument}，如有询问我的，先按你推荐的继续进行，需将该plan文档内的[ ]check list都检查收口后，变成[x]后才算goal完成")!;
        }

        return BuildGoalPrompt(SuperpowersQuickActionDefaults.ExecuteGoalPlanPromptInput)!;
    }

    public static string? BuildGoalPrompt(string? input)
    {
        var trimmed = input?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return trimmed.StartsWith("/goal", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{GoalQuickActionDefaults.QuickGoalPrefix}{trimmed}";
    }

    private static string? TryResolvePlanDocumentReference(string? latestAssistantReply, string? workspaceRoot)
    {
        var candidates = MarkdownReferenceExtractor.Extract(latestAssistantReply, workspaceRoot);
        return candidates
            .Where(IsPlanMarkdownCandidate)
            .OrderBy(GetPlanDocumentPriority)
            .ThenBy(static candidate => candidate.RelativePath.Length)
            .Select(static candidate => candidate.RelativePath)
            .FirstOrDefault();
    }

    private static bool IsPlanMarkdownCandidate(ReferencedMarkdownDocumentCandidate candidate)
    {
        var relativePath = candidate.RelativePath.Replace('\\', '/');
        var fileName = Path.GetFileName(relativePath);

        return relativePath.Contains("/plans/", StringComparison.OrdinalIgnoreCase)
               || relativePath.StartsWith("plans/", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains("plan", StringComparison.OrdinalIgnoreCase)
               || relativePath.Contains("plan", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetPlanDocumentPriority(ReferencedMarkdownDocumentCandidate candidate)
    {
        var relativePath = candidate.RelativePath.Replace('\\', '/');
        var fileName = Path.GetFileName(relativePath);

        if (fileName.Equals("approved-plan.md", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (relativePath.Contains("/plans/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("plans/", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (fileName.Contains("plan", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }
}
