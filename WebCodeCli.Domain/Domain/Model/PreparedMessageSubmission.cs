using WebCodeCli.Domain.Domain.Service.Adapters;

namespace WebCodeCli.Domain.Domain.Model;

public class PreparedMessageSubmission
{
    public string SessionId { get; set; } = string.Empty;

    public string ToolId { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public List<MessageAttachment> Attachments { get; set; } = new();

    public string StagingRootRelativePath { get; set; } = string.Empty;

    public ChatMessage UserMessage { get; set; } = new();

    public CliExecutionRequest ExecutionRequest { get; set; } = new();

    public List<MessageSubmissionWarning> Warnings { get; set; } = new();
}
