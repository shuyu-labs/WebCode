namespace WebCodeCli.Domain.Domain.Model;

public class MessageDraft
{
    public string DraftId { get; set; } = Guid.NewGuid().ToString("N");

    public string SessionId { get; set; } = string.Empty;

    public string ToolId { get; set; } = string.Empty;

    public MessageSubmissionChannel Channel { get; set; }

    public string Text { get; set; } = string.Empty;

    public List<MessageDraftAttachmentInput> Attachments { get; set; } = new();

    public string? SubmittedBy { get; set; }
}
