namespace WebCodeCli.Domain.Domain.Model;

public class MessageDraftAttachmentInput
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public byte[] Content { get; set; } = [];
}
