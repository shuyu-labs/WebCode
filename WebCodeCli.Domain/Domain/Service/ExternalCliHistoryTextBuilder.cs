using System.Text;
using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service;

public static class ExternalCliHistoryTextBuilder
{
    public static string Build(
        string title,
        IReadOnlyList<ExternalCliHistoryMessage> messages,
        string toolLabel,
        string? workspacePath,
        string? cliThreadId,
        string? sourcePath = null,
        DateTime? lastActiveTime = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.IsNullOrWhiteSpace(title) ? "当前 CLI 会话历史" : title.Trim());
        builder.AppendLine($"历史来源: {FormatSourcePath(sourcePath)}");
        builder.AppendLine($"CLI 工具: {toolLabel}");
        builder.AppendLine($"工作目录: {workspacePath ?? "(工作区未初始化或已失效)"}");
        builder.AppendLine($"原生 Thread ID: {FormatThreadId(cliThreadId)}");

        if (lastActiveTime.HasValue)
        {
            builder.AppendLine($"最后活跃: {lastActiveTime:yyyy-MM-dd HH:mm}");
        }

        builder.AppendLine();

        if (messages.Count == 0)
        {
            builder.AppendLine("该 CLI 会话暂无可解析的历史消息。");
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine($"显示条数: 最近 {messages.Count} 条");
        builder.AppendLine();

        foreach (var message in messages)
        {
            var roleLabel = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                ? "用户"
                : "助手";

            if (message.CreatedAt.HasValue)
            {
                builder.AppendLine($"[{roleLabel}] {message.CreatedAt:HH:mm}");
            }
            else
            {
                builder.AppendLine($"[{roleLabel}]");
            }

            builder.AppendLine(NormalizeHistoryContent(message.Content));
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatThreadId(string? cliThreadId)
    {
        return string.IsNullOrWhiteSpace(cliThreadId) ? "未绑定" : cliThreadId.Trim();
    }

    private static string FormatSourcePath(string? sourcePath)
    {
        return string.IsNullOrWhiteSpace(sourcePath) ? "未定位" : sourcePath.Trim();
    }

    private static string NormalizeHistoryContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        return content.Replace("\r\n", "\n").Trim();
    }
}
