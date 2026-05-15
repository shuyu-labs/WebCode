using System.Text;
using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service.Adapters;

public class CliExecutionRequest
{
    public string SessionId { get; set; } = string.Empty;

    public string ToolId { get; set; } = string.Empty;

    public string PromptText { get; set; } = string.Empty;

    public CliSessionContext SessionContext { get; set; } = new();

    public List<CliExecutionAttachment> NativeAttachments { get; set; } = new();

    public List<CliExecutionAttachment> ReferenceAttachments { get; set; } = new();

    public List<MessageSubmissionWarning> Warnings { get; set; } = new();

    public CliExecutionRequest WithSessionContext(CliSessionContext sessionContext)
    {
        return new CliExecutionRequest
        {
            SessionId = SessionId,
            ToolId = ToolId,
            PromptText = PromptText,
            SessionContext = sessionContext,
            NativeAttachments = [.. NativeAttachments],
            ReferenceAttachments = [.. ReferenceAttachments],
            Warnings = [.. Warnings]
        };
    }

    public string BuildPromptText()
    {
        if (ReferenceAttachments.Count == 0)
        {
            return PromptText;
        }

        var builder = new StringBuilder();
        builder.AppendLine("[WEBCODE_REFERENCE_ATTACHMENTS]");

        foreach (var attachment in ReferenceAttachments)
        {
            builder.Append("- displayName: ").AppendLine(GetDisplayName(attachment));
            builder.Append("  kind: ").AppendLine(attachment.Kind.ToString());
            builder.Append("  path: ").AppendLine(GetReferencePath(attachment));
        }

        builder.Append("[/WEBCODE_REFERENCE_ATTACHMENTS]");

        if (!string.IsNullOrWhiteSpace(PromptText))
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.Append(PromptText);
        }

        return builder.ToString();
    }

    private static string GetDisplayName(CliExecutionAttachment attachment)
    {
        if (!string.IsNullOrWhiteSpace(attachment.DisplayName))
        {
            return attachment.DisplayName;
        }

        var referencePath = GetReferencePath(attachment);
        return string.IsNullOrWhiteSpace(referencePath) ? "(unnamed attachment)" : referencePath;
    }

    private static string GetReferencePath(CliExecutionAttachment attachment)
    {
        if (!string.IsNullOrWhiteSpace(attachment.WorkspaceRelativePath))
        {
            return attachment.WorkspaceRelativePath;
        }

        return attachment.AbsolutePath;
    }
}
