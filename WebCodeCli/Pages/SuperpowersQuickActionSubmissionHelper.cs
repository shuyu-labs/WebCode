using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Pages;

public enum SuperpowersQuickActionRequestType
{
    Continue,
    ExecutePlan,
    ExecuteSubagentPlan,
    QuickInput
}

public static class SuperpowersQuickActionSubmissionHelper
{
    public static string? BuildMessage(SuperpowersQuickActionRequestType requestType, string? quickInput)
    {
        return requestType switch
        {
            SuperpowersQuickActionRequestType.Continue => SuperpowersPromptBuilder.BuildContinuePrompt(),
            SuperpowersQuickActionRequestType.ExecutePlan => SuperpowersPromptBuilder.BuildExecutePlanPrompt(),
            SuperpowersQuickActionRequestType.ExecuteSubagentPlan => SuperpowersPromptBuilder.BuildSubagentExecutePlanPrompt(),
            SuperpowersQuickActionRequestType.QuickInput => SuperpowersPromptBuilder.BuildQuickSkillPrompt(quickInput),
            _ => null
        };
    }
}
