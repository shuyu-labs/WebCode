using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service;

public static class SuperpowersPromptBuilder
{
    private const string LegacyQuickSkillPrefix = "$superpowers ，使用superpowers技能，";

    public static string BuildContinuePrompt()
        => SuperpowersQuickActionDefaults.ContinuePrompt;

    public static string BuildExecutePlanPrompt()
        => SuperpowersQuickActionDefaults.ExecutePlanPrompt;

    public static string BuildSubagentExecutePlanPrompt()
        => SuperpowersQuickActionDefaults.ExecuteSubagentPlanPrompt;

    public static string BuildCompleteWorktreePrompt()
        => SuperpowersQuickActionDefaults.CompleteWorktreePrompt;

    public static string? BuildQuickSkillPrompt(string? input)
    {
        var trimmed = input?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var normalized = trimmed.StartsWith(SuperpowersQuickActionDefaults.QuickSkillPrefix, StringComparison.Ordinal)
            ? trimmed
            : trimmed.StartsWith(LegacyQuickSkillPrefix, StringComparison.Ordinal)
                ? $"{SuperpowersQuickActionDefaults.QuickSkillPrefix}{trimmed[LegacyQuickSkillPrefix.Length..]}"
                : $"{SuperpowersQuickActionDefaults.QuickSkillPrefix}{trimmed}";

        return normalized.Contains(SuperpowersQuickActionDefaults.PromptLanguagePolicy, StringComparison.Ordinal)
            ? normalized
            : $"{normalized}\n\n{SuperpowersQuickActionDefaults.PromptLanguagePolicy}";
    }
}
