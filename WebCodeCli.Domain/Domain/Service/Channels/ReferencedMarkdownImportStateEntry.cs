namespace WebCodeCli.Domain.Domain.Service.Channels;

public sealed record ReferencedMarkdownImportStateEntry
{
    public string FolderToken { get; init; } = string.Empty;

    public string AbsolutePath { get; init; } = string.Empty;

    public string RelativePath { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Fingerprint { get; init; } = string.Empty;

    public string DocumentId { get; init; } = string.Empty;

    public string RootBlockId { get; init; } = string.Empty;

    public string DocumentUrl { get; init; } = string.Empty;
}
