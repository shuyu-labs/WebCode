namespace WebCodeCli.Domain.Domain.Model;

public class StagedMessageAttachment
{
    public string InputId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string AbsolutePath { get; set; } = string.Empty;

    public MessageAttachment Metadata { get; set; } = new();
}
