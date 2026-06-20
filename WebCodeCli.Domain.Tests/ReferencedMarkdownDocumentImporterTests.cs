using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service.Channels;

namespace WebCodeCli.Domain.Tests;

public sealed class ReferencedMarkdownDocumentImporterTests
{
    [Fact]
    public async Task ImportMissingAsync_WhenTrackedMarkdownIsUnchanged_ReusesTrackedDocumentWithoutReimport()
    {
        var workspaceRoot = CreateWorkspaceWithFile("docs/agent-notes/2026-06-09.md", "# note");
        var stateStore = new InMemoryReferencedMarkdownImportStateStore();
        var candidate = new ReferencedMarkdownDocumentCandidate(
            AbsolutePath: Path.Combine(workspaceRoot, "docs", "agent-notes", "2026-06-09.md"),
            RelativePath: "docs/agent-notes/2026-06-09.md",
            Title: "docs/agent-notes/2026-06-09.md");

        try
        {
            var existingDocument = new FeishuCloudDocumentInfo
            {
                DocumentId = "doc-existing",
                RootBlockId = "root-existing",
                Url = "https://feishu.cn/docx/doc-existing"
            };

            await SeedTrackingStateAsync(stateStore, "fld-session", candidate, existingDocument);

            var cardKit = new TrackingFeishuCardKitClient();
            cardKit.ExistingFolderDocumentsByTitle[candidate.Title] = existingDocument;

            var importer = CreateImporter(stateStore);

            await importer.ImportMissingAsync(
                cardKit,
                "oc-chat",
                "fld-session",
                [candidate],
                documentAdminOpenId: null,
                optionsOverride: null,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Empty(cardKit.ImportedMarkdownDocuments);
            Assert.Empty(cardKit.ConvertedMarkdownRequests);
            Assert.Empty(cardKit.DeletedChildRanges);
            Assert.Single(cardKit.TextMessages);
            Assert.Contains("已复用Markdown在线文档", cardKit.TextMessages[0], StringComparison.Ordinal);
            Assert.Contains("https://feishu.cn/docx/doc-existing", cardKit.TextMessages[0], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ImportMissingAsync_WhenTrackedMarkdownChanges_OverwritesExistingDocumentAndKeepsLink()
    {
        var workspaceRoot = CreateWorkspaceWithFile("docs/agent-notes/2026-06-09.md", "# note");
        var stateStore = new InMemoryReferencedMarkdownImportStateStore();
        var candidate = new ReferencedMarkdownDocumentCandidate(
            AbsolutePath: Path.Combine(workspaceRoot, "docs", "agent-notes", "2026-06-09.md"),
            RelativePath: "docs/agent-notes/2026-06-09.md",
            Title: "docs/agent-notes/2026-06-09.md");

        try
        {
            var existingDocument = new FeishuCloudDocumentInfo
            {
                DocumentId = "doc-existing",
                RootBlockId = "root-existing",
                Url = "https://feishu.cn/docx/doc-existing"
            };

            await SeedTrackingStateAsync(stateStore, "fld-session", candidate, existingDocument);
            await File.WriteAllTextAsync(candidate.AbsolutePath, "# updated note", Encoding.UTF8, TestContext.Current.CancellationToken);

            var cardKit = new TrackingFeishuCardKitClient();
            cardKit.ExistingFolderDocumentsByTitle[candidate.Title] = existingDocument;
            cardKit.ChildBlockIdsByDocumentAndBlock[(existingDocument.DocumentId, existingDocument.RootBlockId)] = ["blk-1", "blk-2", "blk-3"];
            cardKit.ConvertedBlocks.Add(ParseJsonElement("""{"block_type":2,"text":{"elements":[{"text_run":{"content":"updated note","text_element_style":{}}}]}}"""));

            var importer = CreateImporter(stateStore);

            await importer.ImportMissingAsync(
                cardKit,
                "oc-chat",
                "fld-session",
                [candidate],
                documentAdminOpenId: null,
                optionsOverride: null,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Empty(cardKit.ImportedMarkdownDocuments);
            Assert.Single(cardKit.ConvertedMarkdownRequests);
            Assert.Equal("# updated note", cardKit.ConvertedMarkdownRequests[0]);
            Assert.Equal([("doc-existing", "root-existing", 0, 2)], cardKit.DeletedChildRanges);
            var appendedBatch = Assert.Single(cardKit.AppendedBlockBatches);
            Assert.Equal("doc-existing", appendedBatch.DocumentId);
            Assert.Equal("root-existing", appendedBatch.BlockId);
            Assert.Single(appendedBatch.Blocks);
            Assert.Single(cardKit.TextMessages);
            Assert.Contains("已更新Markdown在线文档", cardKit.TextMessages[0], StringComparison.Ordinal);
            Assert.Contains("https://feishu.cn/docx/doc-existing", cardKit.TextMessages[0], StringComparison.Ordinal);

            var updatedState = await stateStore.GetAsync("fld-session", candidate.AbsolutePath, TestContext.Current.CancellationToken);
            Assert.NotNull(updatedState);
            Assert.Equal("doc-existing", updatedState!.DocumentId);
            Assert.Equal("root-existing", updatedState.RootBlockId);
            Assert.Equal("https://feishu.cn/docx/doc-existing", updatedState.DocumentUrl);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ImportMissingAsync_WhenDocumentAlreadyExistsInFolder_ReusesExistingLink()
    {
        var cardKit = new TrackingFeishuCardKitClient();
        cardKit.ExistingFolderDocumentsByTitle["docs/agent-notes/2026-06-09.md"] = new FeishuCloudDocumentInfo
        {
            DocumentId = "doc-existing",
            RootBlockId = "doc-existing",
            Url = "https://feishu.cn/docx/doc-existing"
        };

        var importer = CreateImporter();
        var candidate = new ReferencedMarkdownDocumentCandidate(
            AbsolutePath: @"D:\VSWorkshop\WebCode\docs\agent-notes\2026-06-09.md",
            RelativePath: "docs/agent-notes/2026-06-09.md",
            Title: "docs/agent-notes/2026-06-09.md");

        await importer.ImportMissingAsync(
            cardKit,
            "oc-chat",
            "fld-session",
            [candidate],
            documentAdminOpenId: null,
            optionsOverride: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(cardKit.ImportedMarkdownDocuments);
        Assert.Contains("已复用Markdown在线文档：[docs/agent-notes/2026-06-09.md](https://feishu.cn/docx/doc-existing)", cardKit.TextMessages.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportMissingAsync_WhenDocumentMissing_ImportsMarkdownAndSendsCreatedLink()
    {
        var workspaceRoot = CreateWorkspaceWithFile("docs/agent-notes/2026-06-09.md", "# note");

        try
        {
            var cardKit = new TrackingFeishuCardKitClient();
            var importer = CreateImporter();
            var candidate = new ReferencedMarkdownDocumentCandidate(
                AbsolutePath: Path.Combine(workspaceRoot, "docs", "agent-notes", "2026-06-09.md"),
                RelativePath: "docs/agent-notes/2026-06-09.md",
                Title: "docs/agent-notes/2026-06-09.md");

            await importer.ImportMissingAsync(
                cardKit,
                "oc-chat",
                "fld-session",
                [candidate],
                documentAdminOpenId: null,
                optionsOverride: null,
                cancellationToken: TestContext.Current.CancellationToken);

            var imported = Assert.Single(cardKit.ImportedMarkdownDocuments);
            Assert.Equal("2026-06-09.md", imported.FileName);
            Assert.Equal("docs/agent-notes/2026-06-09.md", imported.Title);
            Assert.Equal("fld-session", imported.FolderToken);
            Assert.Contains("已生成Markdown在线文档：[docs/agent-notes/2026-06-09.md](", cardKit.TextMessages.Single(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ImportMissingAsync_WhenDocumentAdminConfigured_AppliesReadablePermissionAndAdminGrant()
    {
        var workspaceRoot = CreateWorkspaceWithFile("docs/agent-notes/2026-06-09.md", "# note");

        try
        {
            var cardKit = new TrackingFeishuCardKitClient();
            var importer = CreateImporter();
            var candidate = new ReferencedMarkdownDocumentCandidate(
                AbsolutePath: Path.Combine(workspaceRoot, "docs", "agent-notes", "2026-06-09.md"),
                RelativePath: "docs/agent-notes/2026-06-09.md",
                Title: "docs/agent-notes/2026-06-09.md");

            await importer.ImportMissingAsync(
                cardKit,
                "oc-chat",
                "fld-session",
                [candidate],
                documentAdminOpenId: "ou_doc_admin",
                optionsOverride: null,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(["doc-created-1"], cardKit.TenantReadableDocuments);
            Assert.Equal([("doc-created-1", "ou_doc_admin")], cardKit.DocumentAdminGrants);
            Assert.Equal([("fld-session", "ou_doc_admin")], cardKit.FolderAdminGrants);
            Assert.Contains("已生成Markdown在线文档：[docs/agent-notes/2026-06-09.md](", cardKit.TextMessages[0], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ImportMissingAsync_WhenOneCandidateFails_ContinuesRemainingCandidates()
    {
        var workspaceRoot = CreateWorkspaceWithFile("docs/agent-notes/2026-06-09.md", "# note");

        try
        {
            var cardKit = new TrackingFeishuCardKitClient
            {
                ImportMarkdownException = new InvalidOperationException("导入失败")
            };

            var importer = CreateImporter();
            var missingCandidate = new ReferencedMarkdownDocumentCandidate(
                AbsolutePath: Path.Combine(workspaceRoot, "docs", "agent-notes", "2026-06-09.md"),
                RelativePath: "docs/agent-notes/2026-06-09.md",
                Title: "docs/agent-notes/2026-06-09.md");
            var existingCandidate = new ReferencedMarkdownDocumentCandidate(
                AbsolutePath: Path.Combine(workspaceRoot, "docs", "agent-notes", "2026-06-09.md"),
                RelativePath: "docs/reused.md",
                Title: "docs/reused.md");

            cardKit.ExistingFolderDocumentsByTitle["docs/reused.md"] = new FeishuCloudDocumentInfo
            {
                DocumentId = "doc-reused",
                RootBlockId = "doc-reused",
                Url = "https://feishu.cn/docx/doc-reused"
            };

            await importer.ImportMissingAsync(
                cardKit,
                "oc-chat",
                "fld-session",
                [missingCandidate, existingCandidate],
                documentAdminOpenId: null,
                optionsOverride: null,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, cardKit.TextMessages.Count);
            Assert.Contains("Markdown在线文档处理失败", cardKit.TextMessages[0], StringComparison.Ordinal);
            Assert.Contains("已复用Markdown在线文档：[docs/reused.md](https://feishu.cn/docx/doc-reused)", cardKit.TextMessages[1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ImportMissingAsync_WhenAdminGrantFails_SendsWarningAfterSuccessLink()
    {
        var workspaceRoot = CreateWorkspaceWithFile("docs/agent-notes/2026-06-09.md", "# note");

        try
        {
            var cardKit = new TrackingFeishuCardKitClient
            {
                GrantDocumentAdminException = new InvalidOperationException("grant failed")
            };

            var importer = CreateImporter();
            var candidate = new ReferencedMarkdownDocumentCandidate(
                AbsolutePath: Path.Combine(workspaceRoot, "docs", "agent-notes", "2026-06-09.md"),
                RelativePath: "docs/agent-notes/2026-06-09.md",
                Title: "docs/agent-notes/2026-06-09.md");

            await importer.ImportMissingAsync(
                cardKit,
                "oc-chat",
                "fld-session",
                [candidate],
                documentAdminOpenId: "ou_doc_admin",
                optionsOverride: null,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, cardKit.TextMessages.Count);
            Assert.Contains("已生成Markdown在线文档", cardKit.TextMessages[0], StringComparison.Ordinal);
            Assert.Contains("文档管理员权限授予失败", cardKit.TextMessages[1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ImportMissingAsync_WhenFolderPlacementFails_FallsBackToDefaultDirectoryAndSendsPlacementWarning()
    {
        var workspaceRoot = CreateWorkspaceWithFile("docs/agent-notes/2026-06-09.md", "# note");

        try
        {
            var cardKit = new TrackingFeishuCardKitClient
            {
                ImportMarkdownExceptionWhenFolderTokenProvided = new HttpRequestException("API request failed: Status=NotFound, Content={\"code\":1061003,\"msg\":\"not found.\"}")
            };

            var importer = CreateImporter();
            var candidate = new ReferencedMarkdownDocumentCandidate(
                AbsolutePath: Path.Combine(workspaceRoot, "docs", "agent-notes", "2026-06-09.md"),
                RelativePath: "docs/agent-notes/2026-06-09.md",
                Title: "docs/agent-notes/2026-06-09.md");

            await importer.ImportMissingAsync(
                cardKit,
                "oc-chat",
                "fld-session",
                [candidate],
                documentAdminOpenId: null,
                optionsOverride: null,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(
                [("2026-06-09.md", "docs/agent-notes/2026-06-09.md", "fld-session"), ("2026-06-09.md", "docs/agent-notes/2026-06-09.md", null)],
                cardKit.ImportAttempts);
            Assert.Equal(2, cardKit.TextMessages.Count);
            Assert.Contains("已生成Markdown在线文档", cardKit.TextMessages[0], StringComparison.Ordinal);
            Assert.Contains("归档到会话文档文件夹时", cardKit.TextMessages[1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ImportMissingAsync_WhenFailureWarningSendFails_ContinuesRemainingCandidates()
    {
        var workspaceRoot = CreateWorkspaceWithFile("docs/agent-notes/2026-06-09.md", "# note");

        try
        {
            var cardKit = new TrackingFeishuCardKitClient
            {
                ImportMarkdownException = new InvalidOperationException("导入失败"),
                FailTextMessagePredicate = static content => content.Contains("Markdown在线文档处理失败", StringComparison.Ordinal)
            };

            cardKit.ExistingFolderDocumentsByTitle["docs/reused.md"] = new FeishuCloudDocumentInfo
            {
                DocumentId = "doc-reused",
                RootBlockId = "doc-reused",
                Url = "https://feishu.cn/docx/doc-reused"
            };

            var importer = CreateImporter();
            var failingCandidate = new ReferencedMarkdownDocumentCandidate(
                AbsolutePath: Path.Combine(workspaceRoot, "docs", "agent-notes", "2026-06-09.md"),
                RelativePath: "docs/agent-notes/2026-06-09.md",
                Title: "docs/agent-notes/2026-06-09.md");
            var reusedCandidate = new ReferencedMarkdownDocumentCandidate(
                AbsolutePath: Path.Combine(workspaceRoot, "docs", "agent-notes", "2026-06-09.md"),
                RelativePath: "docs/reused.md",
                Title: "docs/reused.md");

            await importer.ImportMissingAsync(
                cardKit,
                "oc-chat",
                "fld-session",
                [failingCandidate, reusedCandidate],
                documentAdminOpenId: null,
                optionsOverride: null,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Single(cardKit.TextMessages);
            Assert.Contains("已复用Markdown在线文档：[docs/reused.md](https://feishu.cn/docx/doc-reused)", cardKit.TextMessages[0], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    private static ReferencedMarkdownDocumentImporter CreateImporter(IReferencedMarkdownImportStateStore? stateStore = null)
        => new(
            NullLogger<ReferencedMarkdownDocumentImporter>.Instance,
            stateStore ?? new InMemoryReferencedMarkdownImportStateStore());

    private static async Task SeedTrackingStateAsync(
        IReferencedMarkdownImportStateStore stateStore,
        string folderToken,
        ReferencedMarkdownDocumentCandidate candidate,
        FeishuCloudDocumentInfo document)
    {
        var fingerprint = ReferencedMarkdownImportFingerprint.Compute(candidate.AbsolutePath);
        await stateStore.UpsertAsync(
            new ReferencedMarkdownImportStateEntry
            {
                FolderToken = folderToken,
                AbsolutePath = candidate.AbsolutePath,
                RelativePath = candidate.RelativePath,
                Title = candidate.Title,
                Fingerprint = fingerprint,
                DocumentId = document.DocumentId,
                RootBlockId = document.RootBlockId,
                DocumentUrl = document.Url
            },
            TestContext.Current.CancellationToken);
    }

    private static string CreateWorkspaceWithFile(string relativePath, string content)
    {
        var root = Path.Combine(Path.GetTempPath(), "webcode-md-importer-" + Guid.NewGuid().ToString("N"));
        var absolutePath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, content, Encoding.UTF8);
        return root;
    }

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class TrackingFeishuCardKitClient : IFeishuCardKitClient
    {
        public Exception? ImportMarkdownException { get; set; }

        public Exception? ImportMarkdownExceptionWhenFolderTokenProvided { get; set; }

        public Exception? GrantDocumentAdminException { get; set; }

        public Exception? GrantFolderAdminException { get; set; }

        public Func<string, bool>? FailTextMessagePredicate { get; set; }

        public Dictionary<string, FeishuCloudDocumentInfo> ExistingFolderDocumentsByTitle { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<(string DocumentId, string BlockId), List<string>> ChildBlockIdsByDocumentAndBlock { get; } = [];

        public List<(string FileName, string Title, string FolderToken, string Content)> ImportedMarkdownDocuments { get; } = [];

        public List<(string FileName, string Title, string? FolderToken)> ImportAttempts { get; } = [];

        public List<string> ConvertedMarkdownRequests { get; } = [];

        public List<(string DocumentId, string BlockId, IReadOnlyList<JsonElement> Blocks)> AppendedBlockBatches { get; } = [];

        public List<(string DocumentId, string BlockId, int StartIndex, int EndIndex)> DeletedChildRanges { get; } = [];

        public List<string> TextMessages { get; } = [];

        public List<string> TenantReadableDocuments { get; } = [];

        public List<(string DocumentId, string OpenId)> DocumentAdminGrants { get; } = [];

        public List<(string FolderToken, string OpenId)> FolderAdminGrants { get; } = [];

        public Task<FeishuCloudDocumentInfo?> FindCloudDocumentInFolderByTitleAsync(
            string folderToken,
            string title,
            CancellationToken cancellationToken = default,
            FeishuOptions? optionsOverride = null)
        {
            return Task.FromResult(ExistingFolderDocumentsByTitle.TryGetValue(title, out var existing) ? existing : null);
        }

        public Task<IReadOnlyList<string>> ListCloudDocumentChildBlockIdsAsync(
            string documentId,
            string blockId,
            CancellationToken cancellationToken = default,
            FeishuOptions? optionsOverride = null)
        {
            return Task.FromResult<IReadOnlyList<string>>(
                ChildBlockIdsByDocumentAndBlock.TryGetValue((documentId, blockId), out var blockIds)
                    ? [.. blockIds]
                    : []);
        }

        public Task DeleteCloudDocumentChildBlocksAsync(
            string documentId,
            string blockId,
            int startIndex,
            int endIndex,
            CancellationToken cancellationToken = default,
            FeishuOptions? optionsOverride = null)
        {
            DeletedChildRanges.Add((documentId, blockId, startIndex, endIndex));
            return Task.CompletedTask;
        }

        public Task<FeishuCloudDocumentInfo> ImportMarkdownFileAsCloudDocumentAsync(
            string fileName,
            byte[] content,
            string title,
            string? folderToken,
            CancellationToken cancellationToken = default,
            FeishuOptions? optionsOverride = null)
        {
            ImportAttempts.Add((fileName, title, folderToken));

            if (!string.IsNullOrWhiteSpace(folderToken) && ImportMarkdownExceptionWhenFolderTokenProvided != null)
            {
                throw ImportMarkdownExceptionWhenFolderTokenProvided;
            }

            if (ImportMarkdownException != null)
            {
                throw ImportMarkdownException;
            }

            ImportedMarkdownDocuments.Add((fileName, title, folderToken ?? string.Empty, Encoding.UTF8.GetString(content)));
            var number = ImportedMarkdownDocuments.Count;
            return Task.FromResult(new FeishuCloudDocumentInfo
            {
                DocumentId = $"doc-created-{number}",
                RootBlockId = $"doc-created-{number}",
                Url = $"https://feishu.cn/docx/doc-created-{number}"
            });
        }

        public Task SetCloudDocumentTenantReadableAsync(
            string documentId,
            CancellationToken cancellationToken = default,
            FeishuOptions? optionsOverride = null)
        {
            TenantReadableDocuments.Add(documentId);
            return Task.CompletedTask;
        }

        public Task GrantCloudDocumentMemberFullAccessAsync(
            string documentId,
            string openId,
            CancellationToken cancellationToken = default,
            FeishuOptions? optionsOverride = null)
        {
            if (GrantDocumentAdminException != null)
            {
                throw GrantDocumentAdminException;
            }

            DocumentAdminGrants.Add((documentId, openId));
            return Task.CompletedTask;
        }

        public Task GrantCloudFolderMemberFullAccessAsync(
            string folderToken,
            string openId,
            CancellationToken cancellationToken = default,
            FeishuOptions? optionsOverride = null)
        {
            if (GrantFolderAdminException != null)
            {
                throw GrantFolderAdminException;
            }

            FolderAdminGrants.Add((folderToken, openId));
            return Task.CompletedTask;
        }

        public Task<string> SendTextMessageAsync(
            string chatId,
            string content,
            CancellationToken cancellationToken = default,
            FeishuOptions? optionsOverride = null)
        {
            if (FailTextMessagePredicate?.Invoke(content) == true)
            {
                throw new InvalidOperationException("send failed");
            }

            TextMessages.Add(content);
            return Task.FromResult($"om_{TextMessages.Count}");
        }

        public Task<string> CreateCardAsync(string initialContent, string? title = null, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<FeishuCloudDocumentInfo> CreateCloudDocumentAsync(string title, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null, string? folderToken = null) => throw new NotSupportedException();
        public Task<FeishuStreamingHandle> CreateStreamingHandleAsync(string chatId, string? replyMessageId, string initialContent, string? title = null, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null, FeishuStreamingCardChrome? chrome = null) => throw new NotSupportedException();
        public Task<FeishuDownloadedAttachment> DownloadIncomingAttachmentAsync(FeishuIncomingAttachment attachment, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<(byte[] Content, string FileName, string MimeType)> DownloadMessageResourceAsync(string messageId, string fileKey, string resourceType, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task MoveCloudDocumentToFolderAsync(string documentId, string folderToken, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> EnsureCloudFolderAsync(string folderName, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> ReplyCardMessageAsync(string replyMessageId, string cardId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> ReplyElementsCardAsync(string replyMessageId, FeishuNetSdk.Im.Dtos.ElementsCardV2Dto card, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> ReplyRawCardAsync(string replyMessageId, string cardJson, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> ReplyTextMessageAsync(string replyMessageId, string content, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> SendCardMessageAsync(string chatId, string cardId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> SendRawCardAsync(string chatId, string cardJson, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task AppendCloudDocumentTextAsync(string documentId, string blockId, string text, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<JsonElement> ConvertMarkdownToCloudDocumentBlocksAsync(string markdown, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            ConvertedMarkdownRequests.Add(markdown);
            var blocksJson = string.Join(",", ConvertedBlocks.Select(static block => block.GetRawText()));
            using var document = JsonDocument.Parse($$"""{"blocks":[{{blocksJson}}]}""");
            return Task.FromResult(document.RootElement.Clone());
        }
        public Task AppendCloudDocumentBlocksAsync(string documentId, string blockId, IReadOnlyCollection<JsonElement> blocks, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            AppendedBlockBatches.Add((documentId, blockId, blocks.Select(static block => block.Clone()).ToArray()));
            return Task.CompletedTask;
        }
        public List<JsonElement> ConvertedBlocks { get; } = [];
        public Task<string> UploadCloudFileAsync(string fileName, byte[] content, string? folderToken, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<bool> UpdateCardAsync(string cardId, string content, int sequence, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
    }

    private sealed class InMemoryReferencedMarkdownImportStateStore : IReferencedMarkdownImportStateStore
    {
        private readonly Dictionary<(string FolderToken, string AbsolutePath), ReferencedMarkdownImportStateEntry> _entries = new();

        public Task<ReferencedMarkdownImportStateEntry?> GetAsync(
            string folderToken,
            string absolutePath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                _entries.TryGetValue((folderToken, absolutePath), out var entry)
                    ? entry with { }
                    : null);
        }

        public Task UpsertAsync(
            ReferencedMarkdownImportStateEntry entry,
            CancellationToken cancellationToken = default)
        {
            _entries[(entry.FolderToken, entry.AbsolutePath)] = entry with { };
            return Task.CompletedTask;
        }
    }
}
