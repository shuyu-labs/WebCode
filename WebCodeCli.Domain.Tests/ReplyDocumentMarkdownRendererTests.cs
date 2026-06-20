using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service.Channels;

namespace WebCodeCli.Domain.Tests;

public sealed class ReplyDocumentMarkdownRendererTests
{
    [Fact]
    public async Task RenderAsync_WhenConvertSucceeds_AppendsConvertedBlocksOnly()
    {
        var cardKit = new TrackingFeishuCardKitClient();
        cardKit.ConvertedBlocks.Add(ParseJsonElement("""{"block_type":2,"text":{"elements":[{"text_run":{"content":"结论正文","text_element_style":{}}}]}}"""));

        var renderer = new ReplyDocumentMarkdownRenderer(NullLogger<ReplyDocumentMarkdownRenderer>.Instance);

        await renderer.RenderAsync(
            cardKit,
            "doc-1",
            "root-1",
            "# 结论正文",
            optionsOverride: null,
            TestContext.Current.CancellationToken);

        var appendedBatch = Assert.Single(cardKit.AppendedBlockBatches);
        Assert.Equal("doc-1", appendedBatch.DocumentId);
        Assert.Equal("root-1", appendedBatch.BlockId);
        Assert.Single(appendedBatch.Blocks);
        Assert.Empty(cardKit.AppendedTexts);
    }

    [Fact]
    public async Task RenderAsync_WhenConvertFails_FallsBackToPlainTextAppend()
    {
        var cardKit = new TrackingFeishuCardKitClient
        {
            ConvertMarkdownException = new HttpRequestException("convert failed")
        };

        var renderer = new ReplyDocumentMarkdownRenderer(NullLogger<ReplyDocumentMarkdownRenderer>.Instance);

        await renderer.RenderAsync(
            cardKit,
            "doc-1",
            "root-1",
            "# 结论正文",
            optionsOverride: null,
            TestContext.Current.CancellationToken);

        Assert.Empty(cardKit.AppendedBlockBatches);
        var appendedText = Assert.Single(cardKit.AppendedTexts);
        Assert.Equal("doc-1", appendedText.DocumentId);
        Assert.Equal("root-1", appendedText.BlockId);
        Assert.Equal("# 结论正文", appendedText.Text);
    }

    [Fact]
    public async Task RenderAsync_WhenAppendConvertedBlocksFails_FallsBackToPlainTextAppend()
    {
        var cardKit = new TrackingFeishuCardKitClient
        {
            AppendBlocksException = new HttpRequestException("append failed")
        };
        cardKit.ConvertedBlocks.Add(ParseJsonElement("""{"block_type":2,"text":{"elements":[{"text_run":{"content":"结论正文","text_element_style":{}}}]}}"""));

        var renderer = new ReplyDocumentMarkdownRenderer(NullLogger<ReplyDocumentMarkdownRenderer>.Instance);

        await renderer.RenderAsync(
            cardKit,
            "doc-1",
            "root-1",
            "# 结论正文",
            optionsOverride: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, cardKit.AppendBlocksCallCount);
        var appendedText = Assert.Single(cardKit.AppendedTexts);
        Assert.Equal("doc-1", appendedText.DocumentId);
        Assert.Equal("root-1", appendedText.BlockId);
        Assert.Equal("# 结论正文", appendedText.Text);
    }

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class TrackingFeishuCardKitClient : IFeishuCardKitClient
    {
        public Exception? ConvertMarkdownException { get; set; }

        public Exception? AppendBlocksException { get; set; }

        public List<JsonElement> ConvertedBlocks { get; } = [];

        public List<(string DocumentId, string BlockId, IReadOnlyList<JsonElement> Blocks)> AppendedBlockBatches { get; } = [];

        public List<(string DocumentId, string BlockId, string Text)> AppendedTexts { get; } = [];

        public int AppendBlocksCallCount { get; private set; }

        public Task<JsonElement> ConvertMarkdownToCloudDocumentBlocksAsync(
            string markdown,
            CancellationToken cancellationToken = default,
            FeishuOptions? optionsOverride = null)
        {
            if (ConvertMarkdownException != null)
            {
                throw ConvertMarkdownException;
            }

            var blocksJson = string.Join(",", ConvertedBlocks.Select(static block => block.GetRawText()));
            using var document = JsonDocument.Parse($$"""{"blocks":[{{blocksJson}}]}""");
            return Task.FromResult(document.RootElement.Clone());
        }

        public Task AppendCloudDocumentBlocksAsync(
            string documentId,
            string blockId,
            IReadOnlyCollection<JsonElement> blocks,
            CancellationToken cancellationToken = default,
            FeishuOptions? optionsOverride = null)
        {
            AppendBlocksCallCount++;
            if (AppendBlocksException != null)
            {
                throw AppendBlocksException;
            }

            AppendedBlockBatches.Add((documentId, blockId, blocks.Select(static block => block.Clone()).ToArray()));
            return Task.CompletedTask;
        }

        public Task AppendCloudDocumentTextAsync(
            string documentId,
            string blockId,
            string text,
            CancellationToken cancellationToken = default,
            FeishuOptions? optionsOverride = null)
        {
            AppendedTexts.Add((documentId, blockId, text));
            return Task.CompletedTask;
        }

        public Task<string> CreateCardAsync(string initialContent, string? title = null, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<FeishuCloudDocumentInfo> CreateCloudDocumentAsync(string title, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null, string? folderToken = null) => throw new NotSupportedException();
        public Task<FeishuStreamingHandle> CreateStreamingHandleAsync(string chatId, string? replyMessageId, string initialContent, string? title = null, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null, FeishuStreamingCardChrome? chrome = null) => throw new NotSupportedException();
        public Task<FeishuDownloadedAttachment> DownloadIncomingAttachmentAsync(FeishuIncomingAttachment attachment, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<(byte[] Content, string FileName, string MimeType)> DownloadMessageResourceAsync(string messageId, string fileKey, string resourceType, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
          public Task<FeishuCloudDocumentInfo?> FindCloudDocumentInFolderByTitleAsync(string folderToken, string title, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
          public Task<IReadOnlyList<string>> ListCloudDocumentChildBlockIdsAsync(string documentId, string blockId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
          public Task DeleteCloudDocumentChildBlocksAsync(string documentId, string blockId, int startIndex, int endIndex, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
          public Task GrantCloudDocumentMemberFullAccessAsync(string documentId, string openId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task GrantCloudFolderMemberFullAccessAsync(string folderToken, string openId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<FeishuCloudDocumentInfo> ImportMarkdownFileAsCloudDocumentAsync(string fileName, byte[] content, string title, string? folderToken, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task MoveCloudDocumentToFolderAsync(string documentId, string folderToken, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> EnsureCloudFolderAsync(string folderName, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> ReplyCardMessageAsync(string replyMessageId, string cardId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> ReplyElementsCardAsync(string replyMessageId, FeishuNetSdk.Im.Dtos.ElementsCardV2Dto card, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> ReplyRawCardAsync(string replyMessageId, string cardJson, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> ReplyTextMessageAsync(string replyMessageId, string content, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> SendCardMessageAsync(string chatId, string cardId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> SendRawCardAsync(string chatId, string cardJson, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> SendTextMessageAsync(string chatId, string content, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task SetCloudDocumentTenantReadableAsync(string documentId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> UploadCloudFileAsync(string fileName, byte[] content, string? folderToken, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<bool> UpdateCardAsync(string cardId, string content, int sequence, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
    }
}
