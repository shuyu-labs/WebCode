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
            _ => BuildGoalPrompt(input)
        };
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
}
