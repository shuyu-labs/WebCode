using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service;

public static class GoalPromptBuilder
{
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
