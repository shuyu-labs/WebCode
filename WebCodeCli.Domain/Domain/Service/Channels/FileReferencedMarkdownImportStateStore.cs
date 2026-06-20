using System.Text.Json;
using WebCodeCli.Domain.Common.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace WebCodeCli.Domain.Domain.Service.Channels;

[ServiceDescription(typeof(IReferencedMarkdownImportStateStore), ServiceLifetime.Singleton)]
public sealed class FileReferencedMarkdownImportStateStore : IReferencedMarkdownImportStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly string _stateFilePath;

    public FileReferencedMarkdownImportStateStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WebCodeCli");
        Directory.CreateDirectory(root);
        _stateFilePath = Path.Combine(root, "FeishuMarkdownImportState.json");
    }

    public async Task<ReferencedMarkdownImportStateEntry?> GetAsync(
        string folderToken,
        string absolutePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var entries = await LoadEntriesAsync(cancellationToken);
            return entries.TryGetValue(BuildKey(folderToken, absolutePath), out var entry)
                ? entry
                : null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task UpsertAsync(
        ReferencedMarkdownImportStateEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var entries = await LoadEntriesAsync(cancellationToken);
            entries[BuildKey(entry.FolderToken, entry.AbsolutePath)] = entry;
            await SaveEntriesAsync(entries, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<Dictionary<string, ReferencedMarkdownImportStateEntry>> LoadEntriesAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_stateFilePath))
        {
            return new Dictionary<string, ReferencedMarkdownImportStateEntry>(StringComparer.OrdinalIgnoreCase);
        }

        await using var stream = File.Open(_stateFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var entries = await JsonSerializer.DeserializeAsync<Dictionary<string, ReferencedMarkdownImportStateEntry>>(
            stream,
            JsonOptions,
            cancellationToken);

        return entries ?? new Dictionary<string, ReferencedMarkdownImportStateEntry>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task SaveEntriesAsync(
        Dictionary<string, ReferencedMarkdownImportStateEntry> entries,
        CancellationToken cancellationToken)
    {
        var tempFilePath = _stateFilePath + ".tmp";
        await using (var stream = File.Open(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken);
        }

        File.Move(tempFilePath, _stateFilePath, overwrite: true);
    }

    private static string BuildKey(string folderToken, string absolutePath)
        => $"{folderToken.Trim()}\n{absolutePath.Trim()}";
}
