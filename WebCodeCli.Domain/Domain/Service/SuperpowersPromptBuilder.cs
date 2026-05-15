using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service;

public static class SuperpowersPromptBuilder
{
    public static string BuildContinuePrompt()
        => SuperpowersQuickActionDefaults.ContinuePrompt;

    public static string BuildExecutePlanPrompt()
        => SuperpowersQuickActionDefaults.ExecutePlanPrompt;

    public static string BuildSubagentExecutePlanPrompt()
        => SuperpowersQuickActionDefaults.ExecuteSubagentPlanPrompt;

    public static string? BuildQuickSkillPrompt(string? input)
    {
        var trimmed = input?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        return trimmed.StartsWith(SuperpowersQuickActionDefaults.QuickSkillPrefix, StringComparison.Ordinal)
            ? trimmed
            : $"{SuperpowersQuickActionDefaults.QuickSkillPrefix}{trimmed}";
    }
}
