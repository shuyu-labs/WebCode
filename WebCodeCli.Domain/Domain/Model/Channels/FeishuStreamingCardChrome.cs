using System.Text.Json;
using System.Text.RegularExpressions;
using WebCodeCli.Domain.Domain.Service.Adapters;

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
    /// 回复内容下方展示的最后一条工具调用摘要
    /// </summary>
    public string? LatestToolCallMarkdown { get; set; }

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

    /// <summary>
    /// 卡片底部工作流区域的附加说明文案
    /// </summary>
    public List<string> BottomNoticeMarkdowns { get; set; } = [];
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

internal static class FeishuStreamingToolSummaryFormatter
{
    private const int MaxSummaryLength = 360;
    private static readonly Regex WindowsPathRegex = new(
        "(?:\"(?<path>[A-Za-z]:\\\\[^\"]+)\"|'(?<path>[A-Za-z]:\\\\[^']+)'|(?<path>[A-Za-z]:\\\\[^\\s\"']+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> FilePathPropertyNames =
    [
        "file_path",
        "notebook_path"
    ];
    private static readonly HashSet<string> GenericPathPropertyNames =
    [
        "path",
        "source",
        "target",
        "from",
        "to"
    ];
    private static readonly HashSet<string> ExtensionlessFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile",
        "Makefile",
        "README",
        "LICENSE"
    };

    public static string? BuildLatestToolCallMarkdown(CliOutputEvent outputEvent)
    {
        if (!IsToolEvent(outputEvent))
        {
            return null;
        }

        var summary = BuildSummaryText(outputEvent);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        summary = CollapseWhitespace(summary);
        if (summary.Length > MaxSummaryLength)
        {
            summary = summary[..(MaxSummaryLength - 1)].TrimEnd() + "…";
        }

        return $"**调用工具：** `{EscapeInlineCode(summary)}`";
    }

    private static bool IsToolEvent(CliOutputEvent outputEvent)
    {
        return outputEvent.EventType is "tool_use"
               || outputEvent.ItemType is "command_execution"
               || outputEvent.ItemType is "mcp_tool_call"
               || outputEvent.ItemType is "web_search"
               || outputEvent.ItemType is "tool_use";
    }

    private static string? BuildSummaryText(CliOutputEvent outputEvent)
    {
        var rawToolContext = TryExtractToolContext(outputEvent.RawJson);
        var toolName = rawToolContext?.ToolName
            ?? ExtractLabeledLine(outputEvent.Content, "工具:");
        var command = rawToolContext?.Command
            ?? outputEvent.CommandExecution?.Command
            ?? ExtractLabeledLine(outputEvent.Content, "命令:")
            ?? ExtractLabeledLine(outputEvent.Content, "操作:");
        var parameters = ExtractLabeledLine(outputEvent.Content, "参数:");
        var fileNames = rawToolContext?.FileNames
            ?? [];

        var summaryParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            summaryParts.Add(NormalizeSummaryText(toolName));
        }

        if (!string.IsNullOrWhiteSpace(command))
        {
            summaryParts.Add(NormalizeSummaryText(command));
        }
        else if (!string.IsNullOrWhiteSpace(parameters))
        {
            summaryParts.Add(NormalizeSummaryText(parameters));
        }

        var fileSummary = BuildFileSummary(summaryParts, fileNames);
        if (!string.IsNullOrWhiteSpace(fileSummary))
        {
            summaryParts.Add(fileSummary);
        }

        if (summaryParts.Count > 0)
        {
            return string.Join(" · ", summaryParts);
        }

        var firstUsefulLine = ExtractFirstUsefulLine(outputEvent.Content);
        if (!string.IsNullOrWhiteSpace(firstUsefulLine))
        {
            return NormalizeSummaryText(firstUsefulLine);
        }

        return outputEvent.Title;
    }

    private static string? ExtractLabeledLine(string? content, string prefix)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var value = trimmed[prefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ExtractFirstUsefulLine(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith("输出:", StringComparison.Ordinal) ||
                trimmed.StartsWith("状态:", StringComparison.Ordinal) ||
                trimmed.StartsWith("退出代码:", StringComparison.Ordinal))
            {
                continue;
            }

            return trimmed;
        }

        return null;
    }

    private static string CollapseWhitespace(string value)
    {
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string NormalizeSummaryText(string value)
    {
        return CollapseWhitespace(ShortenWindowsPaths(value));
    }

    private static string ShortenWindowsPaths(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return WindowsPathRegex.Replace(value, match =>
        {
            var rawPath = match.Groups["path"].Value;
            var displayName = GetPathDisplayName(rawPath, allowExtensionlessFileName: true);
            return string.IsNullOrWhiteSpace(displayName)
                ? match.Value
                : displayName;
        });
    }

    private static string? BuildFileSummary(IEnumerable<string> existingSummaryParts, IReadOnlyList<string> fileNames)
    {
        if (fileNames.Count == 0)
        {
            return null;
        }

        var existingSummaryText = string.Join(" ", existingSummaryParts);
        var namesToShow = fileNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !existingSummaryText.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (namesToShow.Count == 0)
        {
            return null;
        }

        var visibleNames = namesToShow.Take(3).ToList();
        var suffix = namesToShow.Count > visibleNames.Count
            ? $" +{namesToShow.Count - visibleNames.Count}"
            : string.Empty;

        return $"文件: {string.Join(", ", visibleNames)}{suffix}";
    }

    private static ToolSummaryContext? TryExtractToolContext(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (!TryFindToolPayload(document.RootElement, out var toolPayload))
            {
                return null;
            }

            var toolName = GetStringProperty(toolPayload, "name") ?? GetStringProperty(toolPayload, "tool");
            string? command = null;
            var fileNames = new List<string>();

            if (toolPayload.TryGetProperty("input", out var inputElement))
            {
                if (inputElement.ValueKind == JsonValueKind.Object)
                {
                    command = GetStringProperty(inputElement, "command");
                    CollectFileNames(inputElement, fileNames);
                }
                else if (inputElement.ValueKind == JsonValueKind.String)
                {
                    command = inputElement.GetString();
                }
            }

            if (string.IsNullOrWhiteSpace(command))
            {
                command = GetStringProperty(toolPayload, "command");
            }

            return new ToolSummaryContext(toolName, command, fileNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryFindToolPayload(JsonElement element, out JsonElement toolPayload)
    {
        if (IsToolPayload(element))
        {
            toolPayload = element;
            return true;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindToolPayload(property.Value, out toolPayload))
                    {
                        return true;
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindToolPayload(item, out toolPayload))
                    {
                        return true;
                    }
                }
                break;
        }

        toolPayload = default;
        return false;
    }

    private static bool IsToolPayload(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = GetStringProperty(element, "type");
        if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return element.TryGetProperty("input", out _)
               && (element.TryGetProperty("name", out _) || element.TryGetProperty("tool", out _));
    }

    private static void CollectFileNames(JsonElement element, List<string> fileNames, string? propertyName = null)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectFileNames(property.Value, fileNames, property.Name);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectFileNames(item, fileNames, propertyName);
                }
                break;
            case JsonValueKind.String:
                if (TryGetFileDisplayName(element.GetString(), propertyName) is { } fileName)
                {
                    fileNames.Add(fileName);
                }
                break;
        }
    }

    private static string? TryGetFileDisplayName(string? value, string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return null;
        }

        if (FilePathPropertyNames.Contains(propertyName))
        {
            return GetPathDisplayName(value, allowExtensionlessFileName: true);
        }

        if (GenericPathPropertyNames.Contains(propertyName))
        {
            var displayName = GetPathDisplayName(value, allowExtensionlessFileName: true);
            return LooksLikeFileName(displayName) ? displayName : null;
        }

        return null;
    }

    private static string? GetPathDisplayName(string? value, bool allowExtensionlessFileName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(trimmed)
            || (trimmed.Contains("://", StringComparison.Ordinal) && !trimmed.Contains(":\\", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var normalized = trimmed.TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var displayName = normalized.Contains('\\') || normalized.Contains('/')
            ? Path.GetFileName(normalized)
            : normalized;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        return allowExtensionlessFileName || LooksLikeFileName(displayName)
            ? displayName
            : null;
    }

    private static bool LooksLikeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Path.HasExtension(value)
               || value.StartsWith(".", StringComparison.Ordinal)
               || ExtensionlessFileNames.Contains(value);
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement)
               && propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString()
            : null;
    }

    private static string EscapeInlineCode(string value)
    {
        return CollapseWhitespace(value).Replace('`', '\'');
    }

    private sealed record ToolSummaryContext(
        string? ToolName,
        string? Command,
        IReadOnlyList<string> FileNames);
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

    /// <summary>
    /// 底部按钮所在的逻辑行分组
    /// </summary>
    public string RowKey { get; set; } = string.Empty;
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
