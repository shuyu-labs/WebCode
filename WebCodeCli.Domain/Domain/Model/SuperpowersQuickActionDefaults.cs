namespace WebCodeCli.Domain.Domain.Model;

public static class SuperpowersQuickActionDefaults
{
    public const string QuickInputFieldName = "superpowers_quick_input";
    public const string QuickInputPlaceholder = "输入内容后回车，未写前缀时会自动补成$using-superpowers ，使用superpowers技能，";
    public const string InstructionText = "使用superpowers工作流，会自动补前缀：\"$using-superpowers ，使用superpowers技能，\"";
    public const string QuickSubmitButtonText = "提交";

    public const string ContinueButtonText = "继续";
    public const string StopButtonText = "停止";
    public const string ExecutePlanButtonText = "执行 plan";
    public const string ExecuteSubagentPlanButtonText = "子代理执行 plan";

    public const string PromptLanguagePolicy = "Reply to the user in Chinese. Write documentation and code comments in English only. Keep exception and error messages in Chinese.";
    public const string ContinuePrompt = "Resume the current Codex thread and continue the approved superpowers workflow. Do not send any extra resume command inside the conversation. " + PromptLanguagePolicy;
    public const string ExecutePlanPrompt = "Use the superpowers executing-plans skill to execute the approved plan. " + PromptLanguagePolicy;
    public const string ExecuteSubagentPlanPrompt = "Use the superpowers executing-plans skill to execute the approved plan, and use the superpowers subagent-driven-development skill when parallel implementation helps. " + PromptLanguagePolicy;
    public const string QuickSkillPrefix = "$using-superpowers ，使用superpowers技能，";
    public const string CapabilityCheckingText = "正在检测当前 Provider 的 superpowers 能力...";
    public const string CapabilityUnavailableText = "当前 Provider 缺少 superpowers 能力";
    public const string CapabilityProbeFailedText = "检测 superpowers 能力失败，请重试";
    public const string CapabilityRetryButtonText = "重新检测";
}
