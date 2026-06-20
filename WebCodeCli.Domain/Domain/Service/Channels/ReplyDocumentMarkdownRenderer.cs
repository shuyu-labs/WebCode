using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebCodeCli.Domain.Common.Options;

namespace WebCodeCli.Domain.Domain.Service.Channels;

public sealed class ReplyDocumentMarkdownRenderer
{
    private readonly ILogger<ReplyDocumentMarkdownRenderer> _logger;

    public ReplyDocumentMarkdownRenderer(ILogger<ReplyDocumentMarkdownRenderer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RenderAsync(
        IFeishuCardKitClient cardKitClient,
        string documentId,
        string rootBlockId,
        string markdown,
        FeishuOptions? optionsOverride,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cardKitClient);

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return;
        }

        JsonElement converted;
        try
        {
            converted = await cardKitClient.ConvertMarkdownToCloudDocumentBlocksAsync(
                markdown,
                cancellationToken,
                optionsOverride);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                ex,
                "Reply document markdown conversion fell back to plain text for document {DocumentId}",
                documentId);

            await cardKitClient.AppendCloudDocumentTextAsync(
                documentId,
                rootBlockId,
                markdown,
                cancellationToken,
                optionsOverride);
            return;
        }

        var blocks = ExtractBlocks(converted);
        if (blocks.Count == 0)
        {
            await cardKitClient.AppendCloudDocumentTextAsync(
                documentId,
                rootBlockId,
                markdown,
                cancellationToken,
                optionsOverride);
            return;
        }

        try
        {
            await cardKitClient.AppendCloudDocumentBlocksAsync(
                documentId,
                rootBlockId,
                blocks,
                cancellationToken,
                optionsOverride);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Reply document markdown block append failed for document {DocumentId}; falling back to plain text append",
                documentId);

            await cardKitClient.AppendCloudDocumentTextAsync(
                documentId,
                rootBlockId,
                markdown,
                cancellationToken,
                optionsOverride);
        }
    }

    private static IReadOnlyList<JsonElement> ExtractBlocks(JsonElement converted)
    {
        if (!converted.TryGetProperty("blocks", out var blocksElement)
            || blocksElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return blocksElement.EnumerateArray()
            .Select(static block => block.Clone())
            .ToArray();
    }
}
