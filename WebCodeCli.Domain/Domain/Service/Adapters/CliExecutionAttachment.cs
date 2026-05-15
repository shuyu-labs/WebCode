using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service.Adapters;

public class CliExecutionAttachment
{
    public string DisplayName { get; set; } = string.Empty;

    public MessageAttachmentKind Kind { get; set; }

    public string WorkspaceRelativePath { get; set; } = string.Empty;

    public string AbsolutePath { get; set; } = string.Empty;
}
