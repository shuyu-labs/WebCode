using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Domain.Service.Channels;
using WebCodeCli.Domain.Model;
using WebCodeCli.Domain.Repositories.Base.ChatSession;
using WebCodeCli.Domain.Repositories.Base.UserFeishuBotConfig;

namespace WebCodeCli.Domain.Tests;

public sealed class ReplyDocumentOrchestratorListeningTests
{
    [Fact]
    public async Task QueueCompletedReplyAsync_WhenListeningFullReplyDocumentEnabled_CreatesFormattedListeningDocument()
    {
        using var harness = new ReplyDocumentOrchestratorHarness(
            new UserFeishuBotConfigEntity
            {
                Username = "luhaiyan",
                AudioFullReplyDocEnabled = true
            });

        await harness.Orchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyDocumentRequest
        {
            ChatId = "oc-audio-full-chat",
            SessionId = "session-1",
            CliThreadId = "thread-1",
            OriginalUserQuestion = "question",
            Username = "luhaiyan",
            Output = "构建过了。/D:/repo/a.cs:1"
        });

        await WaitUntilAsync(() => harness.CardKit.CreatedDocuments.Count == 1);

        Assert.Matches(@"^question \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}$", harness.CardKit.CreatedDocuments.Single().Title);
        Assert.StartsWith("构建过了。文件内容1", harness.CardKit.AppendedTexts.Single().Text, StringComparison.Ordinal);
        Assert.Contains("文件内容1", harness.CardKit.AppendedTexts.Single().Text, StringComparison.Ordinal);
        Assert.Contains("文件内容1：/D:/repo/a.cs:1", harness.CardKit.AppendedTexts.Single().Text, StringComparison.Ordinal);
        Assert.EndsWith("## 用户内容\n\nquestion", harness.CardKit.AppendedTexts.Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueCompletedReplyAsync_WhenListeningFinalReplyDocumentEnabled_FormatsFinalAnswerOnly()
    {
        using var harness = new ReplyDocumentOrchestratorHarness(
            new UserFeishuBotConfigEntity
            {
                Username = "luhaiyan",
                AudioFinalReplyDocEnabled = true
            });

        await harness.Orchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyDocumentRequest
        {
            ChatId = "oc-audio-final-chat",
            SessionId = "session-2",
            CliThreadId = "thread-2",
            OriginalUserQuestion = "question",
            Username = "luhaiyan",
            Output = "过程 /D:/repo/raw.cs:2",
            FinalAnswerOutput = "结论 /D:/repo/final.cs:9"
        });

        await WaitUntilAsync(() => harness.CardKit.CreatedDocuments.Count == 1);

        Assert.Matches(@"^question \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}$", harness.CardKit.CreatedDocuments.Single().Title);
        Assert.DoesNotContain("/D:/repo/raw.cs:2", harness.CardKit.AppendedTexts.Single().Text, StringComparison.Ordinal);
        Assert.Contains("文件内容1：/D:/repo/final.cs:9", harness.CardKit.AppendedTexts.Single().Text, StringComparison.Ordinal);
        Assert.EndsWith("## 用户内容\n\nquestion", harness.CardKit.AppendedTexts.Single().Text, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Timed out waiting for the expected condition.");
    }

    private sealed class ReplyDocumentOrchestratorHarness : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;

        public ReplyDocumentOrchestratorHarness(UserFeishuBotConfigEntity config)
        {
            ConfigService = new TrackingUserFeishuBotConfigService(config);
            CardKit = new TrackingFeishuCardKitClient();
            ChatSessionRepository = new TrackingChatSessionRepository();
            HistoryService = new TrackingExternalCliSessionHistoryService();

            var services = new ServiceCollection();
            services.AddScoped<IUserFeishuBotConfigService>(_ => ConfigService);
            services.AddScoped<IFeishuCardKitClient>(_ => CardKit);
            services.AddScoped<IChatSessionRepository>(_ => ChatSessionRepository);
            services.AddScoped<IExternalCliSessionHistoryService>(_ => HistoryService);
            services.AddSingleton<IReferencedMarkdownImportStateStore, InMemoryReferencedMarkdownImportStateStore>();
            services.AddLogging();

            _serviceProvider = services.BuildServiceProvider();
            Orchestrator = new ReplyDocumentOrchestrator(
                _serviceProvider,
                NullLogger<ReplyDocumentOrchestrator>.Instance);
        }

        public TrackingUserFeishuBotConfigService ConfigService { get; }
        public TrackingFeishuCardKitClient CardKit { get; }
        public TrackingChatSessionRepository ChatSessionRepository { get; }
        public TrackingExternalCliSessionHistoryService HistoryService { get; }
        public ReplyDocumentOrchestrator Orchestrator { get; }

        public void Dispose()
        {
            _serviceProvider.Dispose();
        }
    }

    private sealed class InMemoryReferencedMarkdownImportStateStore : IReferencedMarkdownImportStateStore
    {
        public Task<ReferencedMarkdownImportStateEntry?> GetAsync(string folderToken, string absolutePath, CancellationToken cancellationToken = default)
            => Task.FromResult<ReferencedMarkdownImportStateEntry?>(null);

        public Task UpsertAsync(ReferencedMarkdownImportStateEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class TrackingUserFeishuBotConfigService(UserFeishuBotConfigEntity config) : IUserFeishuBotConfigService
    {
        public Task<UserFeishuBotConfigEntity?> GetByUsernameAsync(string username)
            => Task.FromResult<UserFeishuBotConfigEntity?>(string.Equals(username, config.Username, StringComparison.OrdinalIgnoreCase) ? config : null);

        public Task<UserFeishuBotConfigEntity?> GetByAppIdAsync(string appId) => Task.FromResult<UserFeishuBotConfigEntity?>(null);
        public Task<UserFeishuBotConfigSaveResult> SaveAsync(UserFeishuBotConfigEntity configEntity) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string username) => Task.FromResult(true);
        public Task<string?> FindConflictingUsernameByAppIdAsync(string username, string? appId) => Task.FromResult<string?>(null);
        public Task<List<UserFeishuBotConfigEntity>> GetAutoStartCandidatesAsync() => Task.FromResult(new List<UserFeishuBotConfigEntity>());
        public Task<bool> UpdateRuntimePreferenceAsync(string username, bool autoStartEnabled, DateTime? lastStartedAt = null) => Task.FromResult(true);
        public FeishuOptions GetSharedDefaults() => new() { Enabled = true, AppId = "shared-app-id", AppSecret = "shared-secret" };
        public Task<FeishuOptions> GetEffectiveOptionsAsync(string? username) => Task.FromResult(GetSharedDefaults());
        public Task<FeishuOptions?> GetEffectiveOptionsByAppIdAsync(string? appId) => Task.FromResult<FeishuOptions?>(null);
    }

    private sealed class TrackingChatSessionRepository : IChatSessionRepository
    {
        public SqlSugarScope GetDB() => throw new NotSupportedException();
        public List<ChatSessionEntity> GetList() => [];
        public Task<List<ChatSessionEntity>> GetListAsync() => Task.FromResult(new List<ChatSessionEntity>());
        public List<ChatSessionEntity> GetList(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => [];
        public Task<List<ChatSessionEntity>> GetListAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(new List<ChatSessionEntity>());
        public int Count(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => 0;
        public Task<int> CountAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(0);
        public PageList<ChatSessionEntity> GetPageList(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public PageList<P> GetPageList<P>(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public Task<PageList<ChatSessionEntity>> GetPageListAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public Task<PageList<P>> GetPageListAsync<P>(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public PageList<ChatSessionEntity> GetPageList(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, SqlSugar.OrderByType orderByType = SqlSugar.OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<ChatSessionEntity>> GetPageListAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, SqlSugar.OrderByType orderByType = SqlSugar.OrderByType.Asc) => throw new NotSupportedException();
        public PageList<P> GetPageList<P>(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, SqlSugar.OrderByType orderByType = SqlSugar.OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<P>> GetPageListAsync<P>(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, SqlSugar.OrderByType orderByType = SqlSugar.OrderByType.Asc) => throw new NotSupportedException();
        public PageList<ChatSessionEntity> GetPageList(List<SqlSugar.IConditionalModel> conditionalList, PageModel page) => throw new NotSupportedException();
        public Task<PageList<ChatSessionEntity>> GetPageListAsync(List<SqlSugar.IConditionalModel> conditionalList, PageModel page) => throw new NotSupportedException();
        public PageList<ChatSessionEntity> GetPageList(List<SqlSugar.IConditionalModel> conditionalList, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, SqlSugar.OrderByType orderByType = SqlSugar.OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<ChatSessionEntity>> GetPageListAsync(List<SqlSugar.IConditionalModel> conditionalList, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, SqlSugar.OrderByType orderByType = SqlSugar.OrderByType.Asc) => throw new NotSupportedException();
        public ChatSessionEntity GetById(dynamic id) => throw new NotSupportedException();
        public Task<ChatSessionEntity> GetByIdAsync(dynamic id) => Task.FromResult<ChatSessionEntity>(null!);
        public ChatSessionEntity GetSingle(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => throw new NotSupportedException();
        public Task<ChatSessionEntity> GetSingleAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => throw new NotSupportedException();
        public ChatSessionEntity GetFirst(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => throw new NotSupportedException();
        public Task<ChatSessionEntity> GetFirstAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => throw new NotSupportedException();
        public bool Insert(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<bool> InsertAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public bool InsertRange(List<ChatSessionEntity> objs) => throw new NotSupportedException();
        public Task<bool> InsertRangeAsync(List<ChatSessionEntity> objs) => throw new NotSupportedException();
        public int InsertReturnIdentity(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<int> InsertReturnIdentityAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public long InsertReturnBigIdentity(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<long> InsertReturnBigIdentityAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public bool DeleteByIds(dynamic[] ids) => throw new NotSupportedException();
        public Task<bool> DeleteByIdsAsync(dynamic[] ids) => throw new NotSupportedException();
        public bool Delete(dynamic id) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(dynamic id) => throw new NotSupportedException();
        public bool Delete(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public bool Delete(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => throw new NotSupportedException();
        public bool Update(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public bool UpdateRange(List<ChatSessionEntity> objs) => throw new NotSupportedException();
        public bool InsertOrUpdate(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<bool> InsertOrUpdateAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<bool> UpdateRangeAsync(List<ChatSessionEntity> objs) => throw new NotSupportedException();
        public bool IsAny(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => false;
        public Task<bool> IsAnyAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(false);
        public Task<List<ChatSessionEntity>> GetByUsernameAsync(string username) => Task.FromResult(new List<ChatSessionEntity>());
        public Task<ChatSessionEntity?> GetByIdAndUsernameAsync(string sessionId, string username) => Task.FromResult<ChatSessionEntity?>(null);
        public Task<bool> DeleteByIdAndUsernameAsync(string sessionId, string username) => throw new NotSupportedException();
        public Task<List<ChatSessionEntity>> GetByUsernameOrderByUpdatedAtAsync(string username) => throw new NotSupportedException();
        public Task<ChatSessionEntity?> GetByUsernameToolAndCliThreadIdAsync(string username, string toolId, string cliThreadId) => throw new NotSupportedException();
        public Task<ChatSessionEntity?> GetByToolAndCliThreadIdAsync(string toolId, string cliThreadId) => throw new NotSupportedException();
        public Task<bool> UpdateCliThreadIdAsync(string sessionId, string? cliThreadId) => throw new NotSupportedException();
        public Task<bool> UpdateWorkspaceBindingAsync(string sessionId, string? workspacePath, bool isCustomWorkspace) => throw new NotSupportedException();
        public Task<bool> UpdateSessionTitleAsync(string sessionId, string title) => throw new NotSupportedException();
        public Task<bool> UpdateCcSwitchSnapshotAsync(string sessionId, CcSwitchSessionSnapshot snapshot) => throw new NotSupportedException();
        public Task<List<ChatSessionEntity>> GetByFeishuChatKeyAsync(string feishuChatKey) => throw new NotSupportedException();
        public Task<ChatSessionEntity?> GetActiveByFeishuChatKeyAsync(string feishuChatKey) => throw new NotSupportedException();
        public Task<bool> SetActiveSessionAsync(string feishuChatKey, string sessionId) => throw new NotSupportedException();
        public Task<bool> CloseFeishuSessionAsync(string feishuChatKey, string sessionId) => throw new NotSupportedException();
        public Task<string> CreateFeishuSessionAsync(string feishuChatKey, string username, string? workspacePath = null, string? toolId = null) => throw new NotSupportedException();
    }

    private sealed class TrackingExternalCliSessionHistoryService : IExternalCliSessionHistoryService
    {
        public Task<ExternalCliHistoryResult> GetRecentHistoryAsync(string toolId, string cliThreadId, int maxCount = 20, string? workspacePath = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<ExternalCliHistoryMessage>> GetRecentMessagesAsync(string toolId, string cliThreadId, int maxCount = 20, string? workspacePath = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetCodexFinalAnswerTextAsync(string cliThreadId, string? workspacePath = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class TrackingFeishuCardKitClient : IFeishuCardKitClient
    {
        public List<(string Title, string DocumentId, string RootBlockId, string Url)> CreatedDocuments { get; } = [];
        public List<(string DocumentId, string BlockId, string Text)> AppendedTexts { get; } = [];

        public Task<FeishuCloudDocumentInfo> CreateCloudDocumentAsync(string title, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null, string? folderToken = null)
        {
            CreatedDocuments.Add((title, $"doc-{CreatedDocuments.Count + 1}", $"root-{CreatedDocuments.Count + 1}", $"https://feishu.cn/docx/doc-{CreatedDocuments.Count + 1}"));
            var created = CreatedDocuments[^1];
            return Task.FromResult(new FeishuCloudDocumentInfo
            {
                DocumentId = created.DocumentId,
                RootBlockId = created.RootBlockId,
                Url = created.Url
            });
        }

        public Task AppendCloudDocumentTextAsync(string documentId, string blockId, string text, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            AppendedTexts.Add((documentId, blockId, text));
            return Task.CompletedTask;
        }

        public Task SetCloudDocumentTenantReadableAsync(string documentId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => Task.CompletedTask;
        public Task<string> SendTextMessageAsync(string chatId, string content, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => Task.FromResult("om_1");
        public Task<string> CreateCardAsync(string initialContent, string? title = null, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<bool> UpdateCardAsync(string cardId, string content, int sequence, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> SendCardMessageAsync(string chatId, string cardId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> ReplyCardMessageAsync(string replyMessageId, string cardId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> ReplyTextMessageAsync(string replyMessageId, string content, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<FeishuDownloadedAttachment> DownloadIncomingAttachmentAsync(FeishuIncomingAttachment attachment, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<FeishuStreamingHandle> CreateStreamingHandleAsync(string chatId, string? replyMessageId, string initialContent, string? title = null, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null, FeishuStreamingCardChrome? chrome = null) => throw new NotSupportedException();
        public Task<string> SendRawCardAsync(string chatId, string cardJson, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> ReplyElementsCardAsync(string replyMessageId, FeishuNetSdk.Im.Dtos.ElementsCardV2Dto card, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> ReplyRawCardAsync(string replyMessageId, string cardJson, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<(byte[] Content, string FileName, string MimeType)> DownloadMessageResourceAsync(string messageId, string fileKey, string resourceType, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
    }
}
