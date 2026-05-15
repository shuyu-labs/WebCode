using AntSK.Domain.Repositories.Base;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Model;
using WebCodeCli.Domain.Repositories.Base;
using WebCodeCli.Domain.Repositories.Base.ChatSession;

namespace WebCodeCli.Domain.Tests;

public class SessionHistoryManagerTests
{
    [Fact]
    public async Task SaveSessionImmediateAsync_CreatesNewSessionInFreshSqliteDatabase()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "SessionHistoryManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var dbPath = Path.Combine(testRoot, "WebCodeCli.db");
        var scope = CreateSqliteScope(dbPath);
        var originalSessionScope = Repository<ChatSessionEntity>.SqlScope;
        var originalMessageScope = Repository<ChatMessageEntity>.SqlScope;
        var originalAttachmentScope = Repository<ChatMessageAttachmentEntity>.SqlScope;

        Repository<ChatSessionEntity>.SqlScope = scope;
        Repository<ChatMessageEntity>.SqlScope = scope;
        Repository<ChatMessageAttachmentEntity>.SqlScope = scope;
        scope.InitializeChatSessionTables();

        try
        {
            var manager = new SessionHistoryManager(
                new ChatSessionRepository(),
                new ChatMessageRepository(),
                new ChatMessageAttachmentRepository(),
                new StubUserContextService(),
                NullLogger<SessionHistoryManager>.Instance);

            var session = new SessionHistory
            {
                SessionId = "fresh-sqlite-session",
                Title = "Fresh sqlite session",
                WorkspacePath = testRoot,
                ToolId = "codex",
                Messages = []
            };

            await manager.SaveSessionImmediateAsync(session);
            manager.ClearCache();

            var reloaded = await manager.GetSessionAsync("fresh-sqlite-session");

            Assert.NotNull(reloaded);
            Assert.Equal("fresh-sqlite-session", reloaded!.SessionId);
            Assert.Equal(testRoot, reloaded.WorkspacePath);
            Assert.Empty(reloaded.Messages);
        }
        finally
        {
            Repository<ChatSessionEntity>.SqlScope = originalSessionScope;
            Repository<ChatMessageEntity>.SqlScope = originalMessageScope;
            Repository<ChatMessageAttachmentEntity>.SqlScope = originalAttachmentScope;
            scope.Dispose();
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SaveSessionImmediateAsync_WhenConcurrentManagersSaveSameSession_DoesNotOverlapDeletePhase()
    {
        const string sessionId = "shared-session";
        var sessionRepository = new InMemoryChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "default",
                Title = "Shared session",
                ToolId = "codex",
                WorkspacePath = @"D:\repo\superpowers",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        ]);
        var messageRepository = new InMemoryChatMessageRepository();
        var attachmentRepository = new BlockingInMemoryChatMessageAttachmentRepository();
        var firstManager = CreateManager(sessionRepository, messageRepository, attachmentRepository);
        var secondManager = CreateManager(sessionRepository, messageRepository, attachmentRepository);

        var firstSaveTask = firstManager.SaveSessionImmediateAsync(CreateSession(sessionId, "First title", "first message"));
        await attachmentRepository.WaitForFirstDeleteAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var secondSaveTask = secondManager.SaveSessionImmediateAsync(CreateSession(sessionId, "Second title", "second message"));
        await Task.Delay(200);
        Assert.False(attachmentRepository.ConcurrentDeleteObserved);
        attachmentRepository.ReleaseFirstDelete();

        await Task.WhenAll(firstSaveTask, secondSaveTask);

        firstManager.ClearCache();
        var reloaded = await firstManager.GetSessionAsync(sessionId);

        Assert.False(attachmentRepository.ConcurrentDeleteObserved);
        Assert.NotNull(reloaded);
        Assert.Equal("Second title", reloaded!.Title);
        var message = Assert.Single(reloaded.Messages);
        Assert.Equal("second message", message.Content);
    }

    [Fact]
    public async Task SaveSessionImmediateAsync_RoundTripsMessageAttachments()
    {
        var manager = CreateManager();
        var session = new SessionHistory
        {
            SessionId = "session-attachments",
            Title = "Attachment session",
            WorkspacePath = @"D:\VSWorkshop\WebCode\artifacts\session-attachments",
            ToolId = "codex",
            Messages =
            [
                new ChatMessage
                {
                    Id = "msg-user-1",
                    Role = "user",
                    Content = "Review these files",
                    Attachments =
                    [
                        new MessageAttachment
                        {
                            Id = "att-image-1",
                            DisplayName = "diagram.png",
                            MimeType = "image/png",
                            Extension = ".png",
                            SizeBytes = 12,
                            Kind = MessageAttachmentKind.Image,
                            WorkspaceRelativePath = ".webcode/message-inputs/submission-1/diagram.png"
                        }
                    ]
                }
            ]
        };

        await manager.SaveSessionImmediateAsync(session);
        manager.ClearCache();

        var reloaded = await manager.GetSessionAsync("session-attachments");

        var userMessage = Assert.Single(reloaded!.Messages);
        Assert.Equal("msg-user-1", userMessage.Id);
        var attachment = Assert.Single(userMessage.Attachments);
        Assert.Equal("att-image-1", attachment.Id);
        Assert.Equal(".webcode/message-inputs/submission-1/diagram.png", attachment.WorkspaceRelativePath);
    }

    [Fact]
    public async Task GetSessionAsync_WhenStoredMessageIdIsBlank_UsesStableLegacyIdAcrossLoads()
    {
        var sessionRepository = new InMemoryChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = "session-legacy-message-id",
                Username = "default",
                Title = "Legacy message id",
                ToolId = "codex",
                WorkspacePath = @"D:\repo\superpowers",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        ]);
        var messageRepository = new InMemoryChatMessageRepository(
        [
            new ChatMessageEntity
            {
                Id = 42,
                MessageId = string.Empty,
                SessionId = "session-legacy-message-id",
                Username = "default",
                Role = "user",
                Content = "legacy message",
                CreatedAt = DateTime.UtcNow
            }
        ]);
        var manager = CreateManager(sessionRepository, messageRepository);

        var firstLoad = await manager.GetSessionAsync("session-legacy-message-id");
        manager.ClearCache();
        var secondLoad = await manager.GetSessionAsync("session-legacy-message-id");

        var firstMessage = Assert.Single(firstLoad!.Messages);
        var secondMessage = Assert.Single(secondLoad!.Messages);
        Assert.Equal("legacy-msg-42", firstMessage.Id);
        Assert.Equal(firstMessage.Id, secondMessage.Id);
    }

    [Fact]
    public async Task SaveSessionImmediateAsync_RoundTripsAttachmentOrderWhenCreatedAtMatches()
    {
        var manager = CreateManager();
        var createdAt = new DateTime(2026, 05, 14, 12, 0, 0, DateTimeKind.Utc);
        var session = new SessionHistory
        {
            SessionId = "session-attachment-order",
            Title = "Attachment order",
            WorkspacePath = @"D:\VSWorkshop\WebCode\artifacts\session-attachment-order",
            ToolId = "codex",
            Messages =
            [
                new ChatMessage
                {
                    Id = "msg-user-order",
                    Role = "user",
                    Content = "Review these files in order",
                    Attachments =
                    [
                        new MessageAttachment
                        {
                            Id = "att-a",
                            DisplayName = "a.txt",
                            MimeType = "text/plain",
                            Extension = ".txt",
                            SizeBytes = 1,
                            Kind = MessageAttachmentKind.Text,
                            WorkspaceRelativePath = ".webcode/message-inputs/submission-1/a.txt",
                            CreatedAt = createdAt
                        },
                        new MessageAttachment
                        {
                            Id = "att-b",
                            DisplayName = "b.txt",
                            MimeType = "text/plain",
                            Extension = ".txt",
                            SizeBytes = 1,
                            Kind = MessageAttachmentKind.Text,
                            WorkspaceRelativePath = ".webcode/message-inputs/submission-1/b.txt",
                            CreatedAt = createdAt
                        }
                    ]
                }
            ]
        };

        await manager.SaveSessionImmediateAsync(session);
        manager.ClearCache();

        var reloaded = await manager.GetSessionAsync("session-attachment-order");

        var message = Assert.Single(reloaded!.Messages);
        Assert.Collection(
            message.Attachments,
            attachment => Assert.Equal("att-a", attachment.Id),
            attachment => Assert.Equal("att-b", attachment.Id));
    }

    [Fact]
    public async Task DeleteSessionAsync_RemovesSessionMessagesAndAttachments()
    {
        var sessionRepository = new InMemoryChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = "session-delete-cleanup",
                Username = "default",
                Title = "Delete cleanup",
                ToolId = "codex",
                WorkspacePath = @"D:\repo\superpowers",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        ]);
        var messageRepository = new InMemoryChatMessageRepository(
        [
            new ChatMessageEntity
            {
                Id = 7,
                MessageId = "msg-delete-1",
                SessionId = "session-delete-cleanup",
                Username = "default",
                Role = "user",
                Content = "delete me",
                CreatedAt = DateTime.UtcNow
            }
        ]);
        var attachmentRepository = new InMemoryChatMessageAttachmentRepository(
        [
            new ChatMessageAttachmentEntity
            {
                Id = 11,
                MessageId = "msg-delete-1",
                SessionId = "session-delete-cleanup",
                Username = "default",
                AttachmentId = "att-delete-1",
                DisplayName = "delete.txt",
                MimeType = "text/plain",
                Extension = ".txt",
                SizeBytes = 3,
                Kind = MessageAttachmentKind.Text.ToString(),
                WorkspaceRelativePath = ".webcode/message-inputs/delete.txt",
                CreatedAt = DateTime.UtcNow
            }
        ]);
        var manager = CreateManager(sessionRepository, messageRepository, attachmentRepository);

        await manager.DeleteSessionAsync("session-delete-cleanup");

        var session = await manager.GetSessionAsync("session-delete-cleanup");
        var remainingMessages = await messageRepository.GetBySessionIdAndUsernameAsync("session-delete-cleanup", "default");
        var remainingAttachments = await attachmentRepository.GetBySessionIdAndUsernameAsync("session-delete-cleanup", "default");

        Assert.Null(session);
        Assert.Empty(remainingMessages);
        Assert.Empty(remainingAttachments);
    }

    [Fact]
    public async Task SaveSessionImmediateAsync_RoundTripsToolLaunchOverrides()
    {
        var sessionRepository = new InMemoryChatSessionRepository();
        var messageRepository = new InMemoryChatMessageRepository();
        var manager = new SessionHistoryManager(
            sessionRepository,
            messageRepository,
            new InMemoryChatMessageAttachmentRepository(),
            new StubUserContextService(),
            NullLogger<SessionHistoryManager>.Instance);

        var session = new SessionHistory
        {
            SessionId = "session-launch-overrides-roundtrip",
            Title = "Launch overrides",
            WorkspacePath = @"D:\repo\superpowers",
            ToolId = "codex",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = "continue",
                    CreatedAt = DateTime.UtcNow
                }
            ],
            ToolLaunchOverrides = new Dictionary<string, SessionToolLaunchOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new() { Model = "gpt-5.4", ReasoningEffort = "high", UsePersistentProcess = true },
                ["claude-code"] = new() { Model = "sonnet" }
            }
        };

        await manager.SaveSessionImmediateAsync(session);
        manager.ClearCache();

        var storedEntity = await sessionRepository.GetByIdAndUsernameAsync(session.SessionId, "default");
        var reloadedSession = await manager.GetSessionAsync(session.SessionId);

        Assert.NotNull(storedEntity);
        Assert.Contains("\"codex\"", storedEntity!.ToolLaunchOverridesJson, StringComparison.Ordinal);
        Assert.Contains("\"reasoningEffort\":\"high\"", storedEntity.ToolLaunchOverridesJson, StringComparison.Ordinal);
        Assert.Contains("\"usePersistentProcess\":true", storedEntity.ToolLaunchOverridesJson, StringComparison.Ordinal);

        Assert.NotNull(reloadedSession);
        Assert.Equal("gpt-5.4", reloadedSession!.ToolLaunchOverrides["codex"].Model);
        Assert.Equal("high", reloadedSession.ToolLaunchOverrides["codex"].ReasoningEffort);
        Assert.True(reloadedSession.ToolLaunchOverrides["codex"].UsePersistentProcess);
        Assert.Equal("sonnet", reloadedSession.ToolLaunchOverrides["claude-code"].Model);
    }

    [Fact]
    public async Task SaveSessionImmediateAsync_WhenOverridesEmpty_ClearsToolLaunchOverridesJson()
    {
        var sessionRepository = new InMemoryChatSessionRepository();
        var manager = new SessionHistoryManager(
            sessionRepository,
            new InMemoryChatMessageRepository(),
            new InMemoryChatMessageAttachmentRepository(),
            new StubUserContextService(),
            NullLogger<SessionHistoryManager>.Instance);

        var session = new SessionHistory
        {
            SessionId = "session-empty-overrides",
            Title = "Launch overrides",
            WorkspacePath = @"D:\repo\superpowers",
            ToolId = "codex",
            Messages = [],
            ToolLaunchOverrides = new Dictionary<string, SessionToolLaunchOverride>(StringComparer.OrdinalIgnoreCase)
        };

        await manager.SaveSessionImmediateAsync(session);

        var storedEntity = await sessionRepository.GetByIdAndUsernameAsync(session.SessionId, "default");

        Assert.NotNull(storedEntity);
        Assert.Null(storedEntity!.ToolLaunchOverridesJson);
    }

    [Fact]
    public async Task GetSessionAsync_WhenStoredJsonIsInvalid_ReturnsEmptyOverrides()
    {
        var sessionRepository = new InMemoryChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = "session-invalid-launch-overrides-json",
                Username = "default",
                Title = "Launch overrides",
                ToolId = "codex",
                WorkspacePath = @"D:\repo\superpowers",
                ToolLaunchOverridesJson = "{invalid json",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        ]);

        var manager = new SessionHistoryManager(
            sessionRepository,
            new InMemoryChatMessageRepository(),
            new InMemoryChatMessageAttachmentRepository(),
            new StubUserContextService(),
            NullLogger<SessionHistoryManager>.Instance);

        var session = await manager.GetSessionAsync("session-invalid-launch-overrides-json");

        Assert.NotNull(session);
        Assert.Empty(session!.ToolLaunchOverrides);
    }

    private static SessionHistoryManager CreateManager(
        InMemoryChatSessionRepository? sessionRepository = null,
        InMemoryChatMessageRepository? messageRepository = null,
        InMemoryChatMessageAttachmentRepository? attachmentRepository = null)
    {
        return new SessionHistoryManager(
            sessionRepository ?? new InMemoryChatSessionRepository(),
            messageRepository ?? new InMemoryChatMessageRepository(),
            attachmentRepository ?? new InMemoryChatMessageAttachmentRepository(),
            new StubUserContextService(),
            NullLogger<SessionHistoryManager>.Instance);
    }

    private static SqlSugarScope CreateSqliteScope(string dbPath)
    {
        return new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = $"Data Source={dbPath}",
            DbType = SqlSugar.DbType.Sqlite,
            InitKeyType = InitKeyType.Attribute,
            IsAutoCloseConnection = true
        });
    }

    private static SessionHistory CreateSession(string sessionId, string title, string content)
    {
        return new SessionHistory
        {
            SessionId = sessionId,
            Title = title,
            WorkspacePath = @"D:\repo\superpowers",
            ToolId = "codex",
            Messages =
            [
                new ChatMessage
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Role = "user",
                    Content = content
                }
            ]
        };
    }

    private sealed class StubUserContextService : IUserContextService
    {
        private string _username = "default";

        public string GetCurrentUsername() => _username;

        public string GetCurrentRole() => "user";

        public bool IsAuthenticated() => true;

        public void SetCurrentUsername(string username)
        {
            _username = string.IsNullOrWhiteSpace(username) ? "default" : username.Trim();
        }
    }

    private sealed class InMemoryChatSessionRepository(IEnumerable<ChatSessionEntity>? initialSessions = null) : IChatSessionRepository
    {
        private readonly List<ChatSessionEntity> _sessions = initialSessions?.ToList() ?? [];
        private readonly object _gate = new();

        public SqlSugarScope GetDB() => throw new NotSupportedException();
        public List<ChatSessionEntity> GetList() => [.. _sessions];
        public Task<List<ChatSessionEntity>> GetListAsync() => Task.FromResult(GetList());
        public List<ChatSessionEntity> GetList(Expression<Func<ChatSessionEntity, bool>> whereExpression) => _sessions.AsQueryable().Where(whereExpression).ToList();
        public Task<List<ChatSessionEntity>> GetListAsync(Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(GetList(whereExpression));
        public int Count(Expression<Func<ChatSessionEntity, bool>> whereExpression) => _sessions.AsQueryable().Count(whereExpression);
        public Task<int> CountAsync(Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(Count(whereExpression));
        public PageList<ChatSessionEntity> GetPageList(Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public PageList<P> GetPageList<P>(Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public Task<PageList<ChatSessionEntity>> GetPageListAsync(Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public Task<PageList<P>> GetPageListAsync<P>(Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public PageList<ChatSessionEntity> GetPageList(Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<ChatSessionEntity>> GetPageListAsync(Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public PageList<P> GetPageList<P>(Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<P>> GetPageListAsync<P>(Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public PageList<ChatSessionEntity> GetPageList(List<IConditionalModel> conditionalList, PageModel page) => throw new NotSupportedException();
        public Task<PageList<ChatSessionEntity>> GetPageListAsync(List<IConditionalModel> conditionalList, PageModel page) => throw new NotSupportedException();
        public PageList<ChatSessionEntity> GetPageList(List<IConditionalModel> conditionalList, PageModel page, Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<ChatSessionEntity>> GetPageListAsync(List<IConditionalModel> conditionalList, PageModel page, Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public ChatSessionEntity GetById(dynamic id) => _sessions.First(x => string.Equals(x.SessionId, id?.ToString(), StringComparison.OrdinalIgnoreCase));
        public Task<ChatSessionEntity> GetByIdAsync(dynamic id) => Task.FromResult(GetById(id));
        public ChatSessionEntity GetSingle(Expression<Func<ChatSessionEntity, bool>> whereExpression) => _sessions.AsQueryable().Single(whereExpression);
        public Task<ChatSessionEntity> GetSingleAsync(Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(GetSingle(whereExpression));
        public ChatSessionEntity GetFirst(Expression<Func<ChatSessionEntity, bool>> whereExpression) => _sessions.AsQueryable().First(whereExpression);
        public Task<ChatSessionEntity> GetFirstAsync(Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(GetFirst(whereExpression));
        public bool Insert(ChatSessionEntity obj)
        {
            lock (_gate)
            {
                _sessions.Add(obj);
                return true;
            }
        }
        public Task<bool> InsertAsync(ChatSessionEntity obj) => Task.FromResult(Insert(obj));
        public bool InsertRange(List<ChatSessionEntity> objs) { _sessions.AddRange(objs); return true; }
        public Task<bool> InsertRangeAsync(List<ChatSessionEntity> objs) => Task.FromResult(InsertRange(objs));
        public int InsertReturnIdentity(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<int> InsertReturnIdentityAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public long InsertReturnBigIdentity(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<long> InsertReturnBigIdentityAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public bool DeleteByIds(dynamic[] ids) => throw new NotSupportedException();
        public Task<bool> DeleteByIdsAsync(dynamic[] ids) => throw new NotSupportedException();
        public bool Delete(dynamic id) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(dynamic id) => throw new NotSupportedException();
        public bool Delete(ChatSessionEntity obj) => _sessions.Remove(obj);
        public Task<bool> DeleteAsync(ChatSessionEntity obj) => Task.FromResult(Delete(obj));
        public bool Delete(Expression<Func<ChatSessionEntity, bool>> whereExpression) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Expression<Func<ChatSessionEntity, bool>> whereExpression) => throw new NotSupportedException();
        public bool Update(ChatSessionEntity obj)
        {
            lock (_gate)
            {
                var existing = _sessions.FirstOrDefault(x => string.Equals(x.SessionId, obj.SessionId, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    return false;
                }

                var index = _sessions.IndexOf(existing);
                _sessions[index] = obj;
                return true;
            }
        }

        public Task<bool> UpdateAsync(ChatSessionEntity obj) => Task.FromResult(Update(obj));
        public bool UpdateRange(List<ChatSessionEntity> objs) => throw new NotSupportedException();
        public Task<bool> UpdateRangeAsync(List<ChatSessionEntity> objs) => throw new NotSupportedException();
        public bool InsertOrUpdate(ChatSessionEntity obj)
        {
            var existing = _sessions.FirstOrDefault(x => string.Equals(x.SessionId, obj.SessionId, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                _sessions.Add(obj);
                return true;
            }

            var index = _sessions.IndexOf(existing);
            _sessions[index] = obj;
            return true;
        }

        public Task<bool> InsertOrUpdateAsync(ChatSessionEntity obj) => Task.FromResult(InsertOrUpdate(obj));
        public bool IsAny(Expression<Func<ChatSessionEntity, bool>> whereExpression) => _sessions.AsQueryable().Any(whereExpression);
        public Task<bool> IsAnyAsync(Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(IsAny(whereExpression));
        public Task<List<ChatSessionEntity>> GetByUsernameAsync(string username) => Task.FromResult(_sessions.Where(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)).ToList());
        public Task<ChatSessionEntity?> GetByIdAndUsernameAsync(string sessionId, string username)
        {
            lock (_gate)
            {
                return Task.FromResult(_sessions.FirstOrDefault(x =>
                    string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)));
            }
        }
        public Task<bool> DeleteByIdAndUsernameAsync(string sessionId, string username) => Task.FromResult(_sessions.RemoveAll(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)) > 0);
        public Task<List<ChatSessionEntity>> GetByUsernameOrderByUpdatedAtAsync(string username) => Task.FromResult(_sessions.Where(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.UpdatedAt).ToList());
        public Task<ChatSessionEntity?> GetByUsernameToolAndCliThreadIdAsync(string username, string toolId, string cliThreadId) => Task.FromResult(_sessions.FirstOrDefault(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase) && string.Equals(x.ToolId, toolId, StringComparison.OrdinalIgnoreCase) && string.Equals(x.CliThreadId, cliThreadId, StringComparison.OrdinalIgnoreCase)));
        public Task<ChatSessionEntity?> GetByToolAndCliThreadIdAsync(string toolId, string cliThreadId) => Task.FromResult(_sessions.FirstOrDefault(x => string.Equals(x.ToolId, toolId, StringComparison.OrdinalIgnoreCase) && string.Equals(x.CliThreadId, cliThreadId, StringComparison.OrdinalIgnoreCase)));
        public Task<bool> UpdateCliThreadIdAsync(string sessionId, string? cliThreadId)
        {
            var session = _sessions.FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            if (session == null)
            {
                return Task.FromResult(false);
            }

            session.CliThreadId = cliThreadId;
            session.UpdatedAt = DateTime.Now;
            return Task.FromResult(true);
        }

        public Task<bool> UpdateWorkspaceBindingAsync(string sessionId, string? workspacePath, bool isCustomWorkspace) => Task.FromResult(true);
        public Task<bool> UpdateSessionTitleAsync(string sessionId, string title) => Task.FromResult(true);
        public Task<bool> UpdateCcSwitchSnapshotAsync(string sessionId, CcSwitchSessionSnapshot snapshot) => Task.FromResult(true);
        public Task<List<ChatSessionEntity>> GetByFeishuChatKeyAsync(string feishuChatKey) => Task.FromResult(new List<ChatSessionEntity>());
        public Task<ChatSessionEntity?> GetActiveByFeishuChatKeyAsync(string feishuChatKey) => Task.FromResult<ChatSessionEntity?>(null);
        public Task<bool> SetActiveSessionAsync(string feishuChatKey, string sessionId) => Task.FromResult(true);
        public Task<bool> CloseFeishuSessionAsync(string feishuChatKey, string sessionId) => Task.FromResult(true);
        public Task<string> CreateFeishuSessionAsync(string feishuChatKey, string username, string? workspacePath = null, string? toolId = null) => Task.FromResult(Guid.NewGuid().ToString("N"));
    }

    private sealed class InMemoryChatMessageRepository(IEnumerable<ChatMessageEntity>? initialMessages = null) : IChatMessageRepository
    {
        private readonly List<ChatMessageEntity> _messages = initialMessages?.ToList() ?? [];
        private readonly object _gate = new();

        public SqlSugarScope GetDB() => throw new NotSupportedException();
        public List<ChatMessageEntity> GetList() => [.. _messages];
        public Task<List<ChatMessageEntity>> GetListAsync() => Task.FromResult(GetList());
        public List<ChatMessageEntity> GetList(Expression<Func<ChatMessageEntity, bool>> whereExpression) => _messages.AsQueryable().Where(whereExpression).ToList();
        public Task<List<ChatMessageEntity>> GetListAsync(Expression<Func<ChatMessageEntity, bool>> whereExpression) => Task.FromResult(GetList(whereExpression));
        public int Count(Expression<Func<ChatMessageEntity, bool>> whereExpression) => _messages.AsQueryable().Count(whereExpression);
        public Task<int> CountAsync(Expression<Func<ChatMessageEntity, bool>> whereExpression) => Task.FromResult(Count(whereExpression));
        public PageList<ChatMessageEntity> GetPageList(Expression<Func<ChatMessageEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public PageList<P> GetPageList<P>(Expression<Func<ChatMessageEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public Task<PageList<ChatMessageEntity>> GetPageListAsync(Expression<Func<ChatMessageEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public Task<PageList<P>> GetPageListAsync<P>(Expression<Func<ChatMessageEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public PageList<ChatMessageEntity> GetPageList(Expression<Func<ChatMessageEntity, bool>> whereExpression, PageModel page, Expression<Func<ChatMessageEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<ChatMessageEntity>> GetPageListAsync(Expression<Func<ChatMessageEntity, bool>> whereExpression, PageModel page, Expression<Func<ChatMessageEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public PageList<P> GetPageList<P>(Expression<Func<ChatMessageEntity, bool>> whereExpression, PageModel page, Expression<Func<ChatMessageEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<P>> GetPageListAsync<P>(Expression<Func<ChatMessageEntity, bool>> whereExpression, PageModel page, Expression<Func<ChatMessageEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public PageList<ChatMessageEntity> GetPageList(List<IConditionalModel> conditionalList, PageModel page) => throw new NotSupportedException();
        public Task<PageList<ChatMessageEntity>> GetPageListAsync(List<IConditionalModel> conditionalList, PageModel page) => throw new NotSupportedException();
        public PageList<ChatMessageEntity> GetPageList(List<IConditionalModel> conditionalList, PageModel page, Expression<Func<ChatMessageEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<ChatMessageEntity>> GetPageListAsync(List<IConditionalModel> conditionalList, PageModel page, Expression<Func<ChatMessageEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public ChatMessageEntity GetById(dynamic id) => throw new NotSupportedException();
        public Task<ChatMessageEntity> GetByIdAsync(dynamic id) => throw new NotSupportedException();
        public ChatMessageEntity GetSingle(Expression<Func<ChatMessageEntity, bool>> whereExpression) => _messages.AsQueryable().Single(whereExpression);
        public Task<ChatMessageEntity> GetSingleAsync(Expression<Func<ChatMessageEntity, bool>> whereExpression) => Task.FromResult(GetSingle(whereExpression));
        public ChatMessageEntity GetFirst(Expression<Func<ChatMessageEntity, bool>> whereExpression) => _messages.AsQueryable().First(whereExpression);
        public Task<ChatMessageEntity> GetFirstAsync(Expression<Func<ChatMessageEntity, bool>> whereExpression) => Task.FromResult(GetFirst(whereExpression));
        public bool Insert(ChatMessageEntity obj) { _messages.Add(obj); return true; }
        public Task<bool> InsertAsync(ChatMessageEntity obj) => Task.FromResult(Insert(obj));
        public bool InsertRange(List<ChatMessageEntity> objs) { _messages.AddRange(objs); return true; }
        public Task<bool> InsertRangeAsync(List<ChatMessageEntity> objs) => Task.FromResult(InsertRange(objs));
        public int InsertReturnIdentity(ChatMessageEntity obj) => throw new NotSupportedException();
        public Task<int> InsertReturnIdentityAsync(ChatMessageEntity obj) => throw new NotSupportedException();
        public long InsertReturnBigIdentity(ChatMessageEntity obj) => throw new NotSupportedException();
        public Task<long> InsertReturnBigIdentityAsync(ChatMessageEntity obj) => throw new NotSupportedException();
        public bool DeleteByIds(dynamic[] ids) => throw new NotSupportedException();
        public Task<bool> DeleteByIdsAsync(dynamic[] ids) => throw new NotSupportedException();
        public bool Delete(dynamic id) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(dynamic id) => throw new NotSupportedException();
        public bool Delete(ChatMessageEntity obj) => _messages.Remove(obj);
        public Task<bool> DeleteAsync(ChatMessageEntity obj) => Task.FromResult(Delete(obj));
        public bool Delete(Expression<Func<ChatMessageEntity, bool>> whereExpression) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Expression<Func<ChatMessageEntity, bool>> whereExpression) => throw new NotSupportedException();
        public bool Update(ChatMessageEntity obj) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(ChatMessageEntity obj) => throw new NotSupportedException();
        public bool UpdateRange(List<ChatMessageEntity> objs) => throw new NotSupportedException();
        public Task<bool> UpdateRangeAsync(List<ChatMessageEntity> objs) => throw new NotSupportedException();
        public bool InsertOrUpdate(ChatMessageEntity obj) => throw new NotSupportedException();
        public Task<bool> InsertOrUpdateAsync(ChatMessageEntity obj) => throw new NotSupportedException();
        public bool IsAny(Expression<Func<ChatMessageEntity, bool>> whereExpression) => _messages.AsQueryable().Any(whereExpression);
        public Task<bool> IsAnyAsync(Expression<Func<ChatMessageEntity, bool>> whereExpression) => Task.FromResult(IsAny(whereExpression));
        public Task<List<ChatMessageEntity>> GetBySessionIdAsync(string sessionId) => Task.FromResult(_messages.Where(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)).ToList());
        public Task<List<ChatMessageEntity>> GetBySessionIdAndUsernameAsync(string sessionId, string username)
        {
            lock (_gate)
            {
                return Task.FromResult(_messages
                    .Where(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase))
                    .ToList());
            }
        }
        public Task<bool> DeleteBySessionIdAsync(string sessionId)
        {
            _messages.RemoveAll(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(true);
        }

        public Task<bool> DeleteBySessionIdAndUsernameAsync(string sessionId, string username)
        {
            lock (_gate)
            {
                _messages.RemoveAll(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
                return Task.FromResult(true);
            }
        }

        public Task<bool> InsertMessagesAsync(List<ChatMessageEntity> messages)
        {
            lock (_gate)
            {
                _messages.AddRange(messages);
                return Task.FromResult(true);
            }
        }
    }

    private class InMemoryChatMessageAttachmentRepository(IEnumerable<ChatMessageAttachmentEntity>? initialAttachments = null) : IChatMessageAttachmentRepository
    {
        private readonly List<ChatMessageAttachmentEntity> _attachments = initialAttachments?.ToList() ?? [];
        private int _nextId = (initialAttachments?.Select(x => x.Id).DefaultIfEmpty(0).Max() ?? 0) + 1;
        private readonly object _gate = new();

        public SqlSugarScope GetDB() => throw new NotSupportedException();
        public List<ChatMessageAttachmentEntity> GetList() => [.. _attachments];
        public Task<List<ChatMessageAttachmentEntity>> GetListAsync() => Task.FromResult(GetList());
        public List<ChatMessageAttachmentEntity> GetList(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression) => _attachments.AsQueryable().Where(whereExpression).ToList();
        public Task<List<ChatMessageAttachmentEntity>> GetListAsync(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression) => Task.FromResult(GetList(whereExpression));
        public int Count(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression) => _attachments.AsQueryable().Count(whereExpression);
        public Task<int> CountAsync(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression) => Task.FromResult(Count(whereExpression));
        public PageList<ChatMessageAttachmentEntity> GetPageList(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public PageList<P> GetPageList<P>(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public Task<PageList<ChatMessageAttachmentEntity>> GetPageListAsync(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public Task<PageList<P>> GetPageListAsync<P>(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public PageList<ChatMessageAttachmentEntity> GetPageList(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression, PageModel page, Expression<Func<ChatMessageAttachmentEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<ChatMessageAttachmentEntity>> GetPageListAsync(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression, PageModel page, Expression<Func<ChatMessageAttachmentEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public PageList<P> GetPageList<P>(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression, PageModel page, Expression<Func<ChatMessageAttachmentEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<P>> GetPageListAsync<P>(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression, PageModel page, Expression<Func<ChatMessageAttachmentEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public PageList<ChatMessageAttachmentEntity> GetPageList(List<IConditionalModel> conditionalList, PageModel page) => throw new NotSupportedException();
        public Task<PageList<ChatMessageAttachmentEntity>> GetPageListAsync(List<IConditionalModel> conditionalList, PageModel page) => throw new NotSupportedException();
        public PageList<ChatMessageAttachmentEntity> GetPageList(List<IConditionalModel> conditionalList, PageModel page, Expression<Func<ChatMessageAttachmentEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<ChatMessageAttachmentEntity>> GetPageListAsync(List<IConditionalModel> conditionalList, PageModel page, Expression<Func<ChatMessageAttachmentEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public ChatMessageAttachmentEntity GetById(dynamic id) => throw new NotSupportedException();
        public Task<ChatMessageAttachmentEntity> GetByIdAsync(dynamic id) => throw new NotSupportedException();
        public ChatMessageAttachmentEntity GetSingle(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression) => _attachments.AsQueryable().Single(whereExpression);
        public Task<ChatMessageAttachmentEntity> GetSingleAsync(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression) => Task.FromResult(GetSingle(whereExpression));
        public ChatMessageAttachmentEntity GetFirst(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression) => _attachments.AsQueryable().First(whereExpression);
        public Task<ChatMessageAttachmentEntity> GetFirstAsync(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression) => Task.FromResult(GetFirst(whereExpression));
        public bool Insert(ChatMessageAttachmentEntity obj) { _attachments.Add(obj); return true; }
        public Task<bool> InsertAsync(ChatMessageAttachmentEntity obj) => Task.FromResult(Insert(obj));
        public bool InsertRange(List<ChatMessageAttachmentEntity> objs) { _attachments.AddRange(objs); return true; }
        public Task<bool> InsertRangeAsync(List<ChatMessageAttachmentEntity> objs) => Task.FromResult(InsertRange(objs));
        public int InsertReturnIdentity(ChatMessageAttachmentEntity obj) => throw new NotSupportedException();
        public Task<int> InsertReturnIdentityAsync(ChatMessageAttachmentEntity obj) => throw new NotSupportedException();
        public long InsertReturnBigIdentity(ChatMessageAttachmentEntity obj) => throw new NotSupportedException();
        public Task<long> InsertReturnBigIdentityAsync(ChatMessageAttachmentEntity obj) => throw new NotSupportedException();
        public bool DeleteByIds(dynamic[] ids) => throw new NotSupportedException();
        public Task<bool> DeleteByIdsAsync(dynamic[] ids) => throw new NotSupportedException();
        public bool Delete(dynamic id) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(dynamic id) => throw new NotSupportedException();
        public bool Delete(ChatMessageAttachmentEntity obj) => _attachments.Remove(obj);
        public Task<bool> DeleteAsync(ChatMessageAttachmentEntity obj) => Task.FromResult(Delete(obj));
        public bool Delete(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression) => throw new NotSupportedException();
        public bool Update(ChatMessageAttachmentEntity obj) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(ChatMessageAttachmentEntity obj) => throw new NotSupportedException();
        public bool UpdateRange(List<ChatMessageAttachmentEntity> objs) => throw new NotSupportedException();
        public Task<bool> UpdateRangeAsync(List<ChatMessageAttachmentEntity> objs) => throw new NotSupportedException();
        public bool InsertOrUpdate(ChatMessageAttachmentEntity obj) => throw new NotSupportedException();
        public Task<bool> InsertOrUpdateAsync(ChatMessageAttachmentEntity obj) => throw new NotSupportedException();
        public bool IsAny(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression) => _attachments.AsQueryable().Any(whereExpression);
        public Task<bool> IsAnyAsync(Expression<Func<ChatMessageAttachmentEntity, bool>> whereExpression) => Task.FromResult(IsAny(whereExpression));
        public Task<List<ChatMessageAttachmentEntity>> GetBySessionIdAndUsernameAsync(string sessionId, string username)
        {
            lock (_gate)
            {
                return Task.FromResult(_attachments
                    .Where(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.CreatedAt)
                    .ThenBy(x => x.Id)
                    .ToList());
            }
        }

        public Task<bool> DeleteBySessionIdAndUsernameAsync(string sessionId, string username)
        {
            lock (_gate)
            {
                _attachments.RemoveAll(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
                return Task.FromResult(true);
            }
        }

        public Task<bool> InsertAttachmentsAsync(List<ChatMessageAttachmentEntity> attachments)
        {
            lock (_gate)
            {
                foreach (var attachment in attachments)
                {
                    if (attachment.Id <= 0)
                    {
                        attachment.Id = _nextId++;
                    }
                }

                _attachments.AddRange(attachments);
                return Task.FromResult(true);
            }
        }
    }

    private sealed class BlockingInMemoryChatMessageAttachmentRepository : InMemoryChatMessageAttachmentRepository, IChatMessageAttachmentRepository
    {
        private readonly TaskCompletionSource<bool> _firstDeleteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _allowFirstDeleteToContinue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _deleteCalls;
        private int _concurrentDeleteObserved;

        public bool ConcurrentDeleteObserved => Volatile.Read(ref _concurrentDeleteObserved) == 1;

        public Task WaitForFirstDeleteAsync() => _firstDeleteEntered.Task;

        public void ReleaseFirstDelete()
        {
            _allowFirstDeleteToContinue.TrySetResult(true);
        }

        public new async Task<bool> DeleteBySessionIdAndUsernameAsync(string sessionId, string username)
        {
            var deleteCallNumber = Interlocked.Increment(ref _deleteCalls);
            if (deleteCallNumber == 1)
            {
                _firstDeleteEntered.TrySetResult(true);
                await _allowFirstDeleteToContinue.Task;
            }
            else if (!_allowFirstDeleteToContinue.Task.IsCompleted)
            {
                Interlocked.Exchange(ref _concurrentDeleteObserved, 1);
            }

            return await base.DeleteBySessionIdAndUsernameAsync(sessionId, username);
        }
    }
}
