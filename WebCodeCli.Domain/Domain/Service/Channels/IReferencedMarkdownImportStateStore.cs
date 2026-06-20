namespace WebCodeCli.Domain.Domain.Service.Channels;

public interface IReferencedMarkdownImportStateStore
{
    Task<ReferencedMarkdownImportStateEntry?> GetAsync(
        string folderToken,
        string absolutePath,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        ReferencedMarkdownImportStateEntry entry,
        CancellationToken cancellationToken = default);
}
