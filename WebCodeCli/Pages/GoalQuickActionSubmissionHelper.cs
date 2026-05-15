using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Pages;

public static class GoalQuickActionSubmissionHelper
{
    public static string? BuildMessage(string? quickInput)
    {
        return GoalPromptBuilder.BuildGoalPrompt(quickInput);
    }
}
