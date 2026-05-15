namespace WebCodeCli.Domain.Domain.Model.Channels;

/// <summary>
/// 流式卡片顶部附加信息
/// </summary>
public sealed class FeishuStreamingCardChrome
{
    /// <summary>
    /// 顶部状态栏 Markdown
    /// </summary>
    public string? StatusMarkdown { get; set; }

    /// <summary>
    /// 顶部右侧下拉菜单选项
    /// </summary>
    public List<FeishuStreamingCardOverflowOption> OverflowOptions { get; set; } = [];

    public List<FeishuStreamingCardTopChipGroup> TopChipGroups { get; set; } = [];

    /// <summary>
    /// 鍗＄墖搴曢儴鍔ㄤ綔鎸夐挳
    /// </summary>
    public List<FeishuStreamingCardBottomAction> BottomActions { get; set; } = [];

    /// <summary>
    /// 卡片底部少打断执行提示词表单
    /// </summary>
    public FeishuStreamingCardBottomPrompt? BottomPrompt { get; set; }

    /// <summary>
    /// 卡片底部附加提示词表单，按顺序显示在主表单之后
    /// </summary>
    public List<FeishuStreamingCardBottomPrompt> AdditionalBottomPrompts { get; set; } = [];
}

public sealed class FeishuStreamingCardTopChipGroup
{
    public string Kind { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public string? SummaryMarkdown { get; set; }

    public List<FeishuStreamingCardOverflowOption> OverflowOptions { get; set; } = [];

    public List<FeishuStreamingCardTopChipItem> Items { get; set; } = [];
}

public sealed class FeishuStreamingCardTopChipItem
{
    public string Text { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public bool IsEnabled { get; set; }

    public int? PreferredWidthPx { get; set; }

    public object? Value { get; set; }
}

internal static class FeishuStreamingStatusFormatter
{
    private static readonly string[] RunningFrames = ["／", "＼"];

    public static string WithRunningState(string baseStatusMarkdown, int frameIndex)
        => WithState(baseStatusMarkdown, $"处理中 {RunningFrames[Math.Abs(frameIndex) % RunningFrames.Length]}");

    public static string WithCompletedState(string baseStatusMarkdown)
        => WithState(baseStatusMarkdown, "已完成");

    public static string WithStoppedState(string baseStatusMarkdown)
        => WithState(baseStatusMarkdown, "已停止");

    public static string WithErrorState(string baseStatusMarkdown)
        => WithState(baseStatusMarkdown, "执行出错");

    private static string WithState(string baseStatusMarkdown, string state)
        => string.IsNullOrWhiteSpace(baseStatusMarkdown)
            ? state
            : $"{baseStatusMarkdown} · {state}";
}

internal static class FeishuStreamingErrorFormatter
{
    public static string AppendError(string? existingContent, string? errorMessage)
    {
        var normalizedError = string.IsNullOrWhiteSpace(errorMessage)
            ? "执行失败"
            : errorMessage.Trim();
        var formattedError = $"**错误：{normalizedError}**";

        if (string.IsNullOrWhiteSpace(existingContent))
        {
            return formattedError;
        }

        return $"{existingContent.TrimEnd()}\n\n---\n\n{formattedError}";
    }
}

internal sealed class FeishuStreamingStatusPulseGate
{
    private long _pauseUntilUtcTicks;

    public void PauseFor(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var pauseUntilUtcTicks = DateTimeOffset.UtcNow.Add(duration).UtcTicks;
        while (true)
        {
            var currentPauseUntil = Volatile.Read(ref _pauseUntilUtcTicks);
            if (currentPauseUntil >= pauseUntilUtcTicks)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _pauseUntilUtcTicks, pauseUntilUtcTicks, currentPauseUntil) == currentPauseUntil)
            {
                return;
            }
        }
    }

    public bool IsPaused()
    {
        return DateTimeOffset.UtcNow.UtcTicks < Volatile.Read(ref _pauseUntilUtcTicks);
    }
}

/// <summary>
/// 流式卡片下拉菜单项
/// </summary>
public sealed class FeishuStreamingCardOverflowOption
{
    /// <summary>
    /// 菜单文本
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// 按钮样式
    /// </summary>
    public string Type { get; set; } = "default";

    /// <summary>
    /// 选中后回传的动作值
    /// </summary>
    public object Value { get; set; } = new { };
}

/// <summary>
/// 娴佸紡鍗＄墖搴曢儴鍔ㄤ綔
/// </summary>
public sealed class FeishuStreamingCardBottomAction
{
    /// <summary>
    /// 鎸夐挳鏂囨湰
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// 鎸夐挳鏍峰紡
    /// </summary>
    public string Type { get; set; } = "default";

    /// <summary>
    /// 鐐瑰嚮鍚庡洖浼犵殑鍔ㄤ綔鍊?
    /// </summary>
    public object Value { get; set; } = new { };
}

/// <summary>
/// 流式卡片底部提示词表单
/// </summary>
public sealed class FeishuStreamingCardBottomPrompt
{
    public string FormName { get; set; } = "superpowers_quick_action_form";

    public string InputName { get; set; } = string.Empty;

    public string InputLabel { get; set; } = string.Empty;

    public string Placeholder { get; set; } = string.Empty;

    public string DefaultValue { get; set; } = string.Empty;

    public string ButtonText { get; set; } = string.Empty;

    public string ButtonType { get; set; } = "primary";

    public object Value { get; set; } = new { };
}
