namespace WebCodeCli.Domain.Domain.Model.Channels;

public class FeishuIncomingAttachment
{
    public string MessageType { get; set; } = string.Empty;

    public string AttachmentKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string MimeType { get; set; } = "application/octet-stream";

    public long? SizeBytes { get; set; }
}
