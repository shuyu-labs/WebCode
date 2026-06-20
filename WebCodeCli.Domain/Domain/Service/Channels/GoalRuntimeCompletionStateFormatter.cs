namespace WebCodeCli.Domain.Domain.Service.Channels;

internal static class GoalRuntimeCompletionStateFormatter
{
    public static string WithTurnFinishedState(string baseStatusMarkdown)
        => AppendState(baseStatusMarkdown, "本轮已结束");

    public static string WithGoalContinuingState(string baseStatusMarkdown)
        => AppendState(baseStatusMarkdown, "本轮已结束 / Goal继续中");

    public static string WithGoalPausedState(string baseStatusMarkdown)
        => AppendState(baseStatusMarkdown, "Goal 已暂停");

    private static string AppendState(string baseStatusMarkdown, string state)
        => string.IsNullOrWhiteSpace(baseStatusMarkdown)
            ? state
            : $"{baseStatusMarkdown} · {state}";
}
