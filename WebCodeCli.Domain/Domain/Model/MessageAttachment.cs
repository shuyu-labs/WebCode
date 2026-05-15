namespace WebCodeCli.Domain.Domain.Model;

public class MessageAttachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; set; } = string.Empty;

    public string MimeType { get; set; } = "application/octet-stream";

    public string Extension { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public MessageAttachmentKind Kind { get; set; }

    public string WorkspaceRelativePath { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
