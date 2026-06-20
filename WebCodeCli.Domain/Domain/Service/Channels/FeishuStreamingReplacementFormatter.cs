namespace WebCodeCli.Domain.Domain.Service.Channels;

internal static class FeishuStreamingReplacementFormatter
{
    public const string ReplacementMessage = "当前回复已停止：当前卡片已停止更新，请查看新卡片继续结果。";

    public static string BuildTransferredContent(string latestContent)
    {
        if (string.IsNullOrWhiteSpace(latestContent))
        {
            return ReplacementMessage;
        }

        if (latestContent.Contains(ReplacementMessage, StringComparison.Ordinal))
        {
            return latestContent;
        }

        return $"{latestContent}\n\n{ReplacementMessage}";
    }
}
