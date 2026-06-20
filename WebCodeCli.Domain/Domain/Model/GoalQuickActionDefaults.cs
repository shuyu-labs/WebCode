namespace WebCodeCli.Domain.Domain.Model;

public static class GoalQuickActionDefaults
{
    public const string QuickInputFieldName = "goal_quick_input";
    public const string QuickInputPlaceholder = "输入内容后回车，未写前缀时会自动补成 /goal ";
    public const string InstructionText = "使用 /goal 不间断执行，会自动补前缀：\"/goal \"。用于在当前 app-server 持续会话中设置或更新工作目标，让 Codex 围绕目标持续推进；可配合 /goal pause、/goal clear、/goal resume 管理执行状态。";
    public const string QuickSubmitButtonText = "提交";
    public const string QuickGoalPrefix = "/goal ";
    public const string StatusButtonText = "/goal";
    public const string PauseButtonText = "/goal pause";
    public const string ClearButtonText = "/goal clear";
    public const string ResumeButtonText = "/goal resume";
    public const string TemporaryExitButtonText = "临时退出补充会话内容";
    public const string StatusPrompt = "/goal";
    public const string PausePrompt = "/goal pause";
    public const string ClearPrompt = "/goal clear";
    public const string ResumePrompt = "/goal resume";

    public const string CapabilityCheckingText = "正在检查 /goal 能力...";
    public const string CapabilityUnavailableText = "当前 Codex 版本或 feature 不支持 /goal";
    public const string CapabilityProbeFailedText = "检查 /goal 能力失败，请重试";
    public const string CapabilityRetryButtonText = "重新检查";
    public const string PersistentProcessRequiredText = "/goal 仅在 Codex 目标会话中可用。";
}
