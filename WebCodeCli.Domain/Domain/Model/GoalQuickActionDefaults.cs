namespace WebCodeCli.Domain.Domain.Model;

public static class GoalQuickActionDefaults
{
    public const string QuickInputFieldName = "goal_quick_input";
    public const string QuickInputPlaceholder = "输入内容后回车，未写前缀时会自动补成 /goal ";
    public const string InstructionText = "使用 /goal 命令，会自动补前缀：\"/goal \"。用于设置当前工作目标，让 Codex 围绕目标持续推进。";
    public const string QuickSubmitButtonText = "提交";
    public const string QuickGoalPrefix = "/goal ";

    public const string CapabilityCheckingText = "正在检查 /goal 能力...";
    public const string CapabilityUnavailableText = "当前 Codex 版本或配置不支持 /goal";
    public const string CapabilityProbeFailedText = "检查 /goal 能力失败，请重试";
    public const string CapabilityRetryButtonText = "重新检查";
}
