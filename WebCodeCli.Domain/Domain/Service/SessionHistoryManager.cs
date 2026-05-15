using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlSugar;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Exceptions;
using WebCodeCli.Domain.Repositories.Base.ChatSession;

namespace WebCodeCli.Domain.Domain.Service;

/// <summary>
/// 会话历史管理器 - 使用 SQLite 后端存储
/// </summary>
[ServiceDescription(typeof(ISessionHistoryManager), ServiceLifetime.Scoped)]
public class SessionHistoryManager : ISessionHistoryManager, IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SessionSaveLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim AnonymousSessionSaveLock = new(1, 1);

    private readonly IChatSessionRepository _sessionRepository;
    private readonly IChatMessageRepository _messageRepository;
    private readonly IChatMessageAttachmentRepository _messageAttachmentRepository;
    private readonly IUserContextService _userContextService;
    private readonly ILogger<SessionHistoryManager> _logger;
    
    private const int MaxMessagesPerSession = 1000;
    private const int MaxTitleLength = 100;
    private const int SaveDebounceMs = 500;
    
    // 防抖定时器
    private System.Threading.Timer? _saveTimer;
    private readonly object _saveLock = new object();
    private bool _hasPendingSave = false;
    private SessionHistory? _pendingSession = null;
    
    // 会话缓存
    private readonly ConcurrentDictionary<string, SessionHistory> _sessionCache = new();
    private List<SessionHistory>? _allSessionsCache = null;
    private DateTime _cacheTimestamp = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromSeconds(10);

    public SessionHistoryManager(
        IChatSessionRepository sessionRepository,
        IChatMessageRepository messageRepository,
        IChatMessageAttachmentRepository messageAttachmentRepository,
        IUserContextService userContextService,
        ILogger<SessionHistoryManager> logger)
    {
        _sessionRepository = sessionRepository;
        _messageRepository = messageRepository;
        _messageAttachmentRepository = messageAttachmentRepository;
        _userContextService = userContextService;
        _logger = logger;
    }

    /// <summary>
    /// 加载所有会话
    /// </summary>
    public async Task<List<SessionHistory>> LoadSessionsAsync()
    {
        var startTime = DateTime.Now;

        try
        {

            // 检查缓存是否有效
            if (_allSessionsCache != null && 
                DateTime.Now - _cacheTimestamp < _cacheExpiration)
            {
                _logger.LogDebug("从缓存加载会话列表，共 {Count} 个会话", _allSessionsCache.Count);
                return _allSessionsCache;
            }

            var username = _userContextService.GetCurrentUsername();
            var sessionEntities = await _sessionRepository.GetByUsernameOrderByUpdatedAtAsync(username);
            
            var sessions = new List<SessionHistory>();
            foreach (var entity in sessionEntities)
            {
                var messages = await _messageRepository.GetBySessionIdAndUsernameAsync(entity.SessionId, username);
                var attachments = await _messageAttachmentRepository.GetBySessionIdAndUsernameAsync(entity.SessionId, username);
                sessions.Add(MapToSessionHistory(entity, messages, attachments));
            }

            // 更新缓存
            _allSessionsCache = sessions;
            _cacheTimestamp = DateTime.Now;
            
            // 更新会话缓存
            _sessionCache.Clear();
            foreach (var session in sessions)
            {
                _sessionCache[session.SessionId] = session;
            }

            var loadTime = (DateTime.Now - startTime).TotalMilliseconds;
            _logger.LogInformation("成功加载 {Count} 个会话，耗时: {Time:F2}ms", sessions.Count, loadTime);
            
            return sessions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载会话列表失败");
            return new List<SessionHistory>();
        }
    }

    /// <summary>
    /// 保存会话（带防抖）
    /// </summary>
    public Task SaveSessionAsync(SessionHistory session)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        _logger.LogDebug("收到保存会话请求: {SessionId}, 消息数: {Count}", session.SessionId, session.Messages?.Count ?? 0);

        // 限制消息数量
        TrimMessages(session);

        // 更新时间戳
        session.UpdatedAt = DateTime.Now;

        lock (_saveLock)
        {
            _hasPendingSave = true;
            _pendingSession = session;
            
            _logger.LogDebug("设置防抖定时器，{Ms}ms 后保存", SaveDebounceMs);
            
            // 重置定时器
            _saveTimer?.Dispose();
            _saveTimer = new System.Threading.Timer(async _ =>
            {
                await ExecuteSaveAsync();
            }, null, SaveDebounceMs, Timeout.Infinite);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 执行实际的保存操作
    /// </summary>
    private async Task ExecuteSaveAsync()
    {
        SessionHistory? sessionToSave = null;
        
        lock (_saveLock)
        {
            if (!_hasPendingSave || _pendingSession == null)
            {
                _logger.LogDebug("没有待保存的会话，跳过");
                return;
            }
            
            sessionToSave = _pendingSession;
            _hasPendingSave = false;
            _pendingSession = null;
        }

        if (sessionToSave != null)
        {
            _logger.LogDebug("执行实际保存: {SessionId}", sessionToSave.SessionId);
            await SaveSessionImmediateAsync(sessionToSave);
        }
    }

    /// <summary>
    /// 立即保存会话
    /// </summary>
    public async Task SaveSessionImmediateAsync(SessionHistory session)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        var sessionSaveLock = GetSessionSaveLock(session.SessionId);
        await sessionSaveLock.WaitAsync();
        using var sessionSaveLockHandle = new SemaphoreReleaser(sessionSaveLock);

        try
        {
            var startTime = DateTime.Now;
            // 限制消息数量
            TrimMessages(session);

            // 更新时间戳
            session.UpdatedAt = DateTime.Now;

            var username = _userContextService.GetCurrentUsername();

            var existingSession = await _sessionRepository.GetByIdAndUsernameAsync(session.SessionId, username);
            MergeManagedToolSnapshot(session, existingSession);
            
            // 保存会话实体
            await ExecuteWithTransactionIfAvailableAsync(async () =>
            {
                var sessionEntity = MapToSessionEntity(session, username);
            var sessionSuccess = existingSession == null
                ? await _sessionRepository.InsertAsync(sessionEntity)
                : await _sessionRepository.UpdateAsync(sessionEntity);

            if (!sessionSuccess)
            {
                _logger.LogWarning("会话 {SessionId} 保存失败", session.SessionId);
                throw new InvalidOperationException("保存会话失败，请稍后重试");
            }

            // 删除旧消息并保存新消息
            await _messageAttachmentRepository.DeleteBySessionIdAndUsernameAsync(session.SessionId, username);
            await _messageRepository.DeleteBySessionIdAndUsernameAsync(session.SessionId, username);
            
            if (session.Messages != null && session.Messages.Count > 0)
            {
                var messageEntities = new List<ChatMessageEntity>(session.Messages.Count);
                var attachmentEntities = new List<ChatMessageAttachmentEntity>();

                foreach (var message in session.Messages)
                {
                    var messageEntity = MapToMessageEntity(message, session.SessionId, username);
                    message.Id = messageEntity.MessageId;
                    messageEntities.Add(messageEntity);

                    if (message.Attachments.Count == 0)
                    {
                        continue;
                    }

                    attachmentEntities.AddRange(message.Attachments.Select(attachment =>
                        MapToAttachmentEntity(attachment, messageEntity.MessageId, session.SessionId, username)));
                }

                var messagesInserted = await _messageRepository.InsertMessagesAsync(messageEntities);
                if (!messagesInserted)
                {
                    throw new InvalidOperationException("淇濆瓨浼氳瘽娑堟伅澶辫触锛岃绋嶅悗閲嶈瘯");
                }

                var attachmentsInserted = await _messageAttachmentRepository.InsertAttachmentsAsync(attachmentEntities);
                if (!attachmentsInserted)
                {
                    throw new InvalidOperationException("淇濆瓨浼氳瘽闄勪欢澶辫触锛岃绋嶅悗閲嶈瘯");
                }
            }

            // 更新缓存
            });

            _sessionCache[session.SessionId] = session;
            
            // 更新全局缓存中的会话
            if (_allSessionsCache != null)
            {
                var existingIndex = _allSessionsCache.FindIndex(s => s.SessionId == session.SessionId);
                if (existingIndex >= 0)
                {
                    _allSessionsCache[existingIndex] = session;
                }
                else
                {
                    _allSessionsCache.Insert(0, session);
                }
            }
            
            var saveTime = (DateTime.Now - startTime).TotalMilliseconds;
            _logger.LogDebug("会话 {SessionId} 保存成功，耗时: {Time:F2}ms", session.SessionId, saveTime);
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not QuotaExceededException)
        {
            _logger.LogError(ex, "保存会话 {SessionId} 失败", session.SessionId);
            throw new InvalidOperationException($"保存会话失败: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 删除会话
    /// </summary>
    public async Task DeleteSessionAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("会话ID不能为空", nameof(sessionId));
        }

        try
        {
            var username = _userContextService.GetCurrentUsername();

            try
            {
                await ExecuteWithTransactionIfAvailableAsync(async () =>
                {
                    await _messageAttachmentRepository.DeleteBySessionIdAndUsernameAsync(sessionId, username);
                    await _messageRepository.DeleteBySessionIdAndUsernameAsync(sessionId, username);

                    var success = await _sessionRepository.DeleteByIdAndUsernameAsync(sessionId, username);
                    if (!success)
                    {
                        throw new SessionDeleteNotFoundException(sessionId);
                    }
                });
            }
            catch (SessionDeleteNotFoundException)
            {
                _logger.LogWarning("会话 {SessionId} 不存在或删除失败", sessionId);
                return;
            }

            _sessionCache.TryRemove(sessionId, out _);

            if (_allSessionsCache != null)
            {
                _allSessionsCache.RemoveAll(s => s.SessionId == sessionId);
            }

            _logger.LogInformation("会话 {SessionId} 已删除", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除会话 {SessionId} 失败", sessionId);
            throw;
        }
    }

    /// <summary>
    /// 获取单个会话
    /// </summary>
    public async Task<SessionHistory?> GetSessionAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        try
        {
            // 先检查缓存
            if (_sessionCache.TryGetValue(sessionId, out var cachedSession))
            {
                return cachedSession;
            }

            var username = _userContextService.GetCurrentUsername();
            var sessionEntity = await _sessionRepository.GetByIdAndUsernameAsync(sessionId, username);
            
            if (sessionEntity == null)
            {
                return null;
            }

            var messages = await _messageRepository.GetBySessionIdAndUsernameAsync(sessionId, username);
            var attachments = await _messageAttachmentRepository.GetBySessionIdAndUsernameAsync(sessionId, username);
            var session = MapToSessionHistory(sessionEntity, messages, attachments);

            _sessionCache[sessionId] = session;

            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取会话 {SessionId} 失败", sessionId);
            return null;
        }
    }

    /// <summary>
    /// 验证工作区路径
    /// </summary>
    public bool ValidateWorkspacePath(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return false;
        }

        try
        {
            return Directory.Exists(workspacePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "验证工作区路径失败: {Path}", workspacePath);
            return false;
        }
    }

    /// <summary>
    /// 更新所有会话的工作区有效性状态（不删除失效会话，仅更新状态）
    /// </summary>
    public async Task<int> CleanupInvalidSessionsAsync()
    {
        try
        {
            var sessions = await LoadSessionsAsync();
            var invalidCount = 0;

            foreach (var session in sessions)
            {
                var wasValid = session.IsWorkspaceValid;
                session.IsWorkspaceValid = ValidateWorkspacePath(session.WorkspacePath);

                if (!session.IsWorkspaceValid)
                {
                    invalidCount++;
                    if (wasValid)
                    {
                        // 状态从有效变为失效时记录日志
                        _logger.LogInformation(
                            "工作区失效: {SessionId} - {Title}, 工作区路径: {Path}, 最后更新时间: {UpdatedAt}",
                            session.SessionId,
                            session.Title,
                            session.WorkspacePath,
                            session.UpdatedAt);
                    }
                }
            }

            if (invalidCount > 0)
            {
                _logger.LogInformation("检测到 {Count} 个工作区失效的会话，数据已保留", invalidCount);
            }

            return invalidCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新会话工作区状态失败");
            return 0;
        }
    }

    /// <summary>
    /// 生成会话标题
    /// </summary>
    public string GenerateSessionTitle(string firstMessage)
    {
        if (string.IsNullOrWhiteSpace(firstMessage))
        {
            return "新会话";
        }

        // 移除多余的空白字符
        var title = firstMessage.Trim();
        
        // 替换换行符为空格
        title = title.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
        
        // 合并多个空格为一个
        while (title.Contains("  "))
        {
            title = title.Replace("  ", " ");
        }

        // 截断标题
        if (title.Length > 30)
        {
            title = title.Substring(0, 30) + "...";
        }

        return title;
    }

    /// <summary>
    /// 限制消息数量
    /// </summary>
    public void TrimMessages(SessionHistory session)
    {
        if (session == null || session.Messages == null)
        {
            return;
        }

        if (session.Messages.Count > MaxMessagesPerSession)
        {
            var messagesToRemove = session.Messages.Count - MaxMessagesPerSession;
            session.Messages.RemoveRange(0, messagesToRemove);
            
            _logger.LogInformation(
                "会话 {SessionId} 消息数量超限，已删除最早的 {Count} 条消息",
                session.SessionId,
                messagesToRemove);
        }
    }

    /// <summary>
    /// 清除缓存（用于强制刷新）
    /// </summary>
    public void ClearCache()
    {
        _sessionCache.Clear();
        _allSessionsCache = null;
        _cacheTimestamp = DateTime.MinValue;
        _logger.LogDebug("会话缓存已清除");
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // 确保保存所有待处理的会话
        lock (_saveLock)
        {
            if (_hasPendingSave && _pendingSession != null)
            {
                // 立即保存
                var sessionToSave = _pendingSession;
                _hasPendingSave = false;
                _pendingSession = null;
                
                // 注意：这里不能使用 await，因为在 lock 中
                _ = SaveSessionImmediateAsync(sessionToSave);
            }
        }

        // 停止定时器
        if (_saveTimer != null)
        {
            await _saveTimer.DisposeAsync();
            _saveTimer = null;
        }

        GC.SuppressFinalize(this);
    }

    #region 映射方法

    private SessionHistory MapToSessionHistory(
        ChatSessionEntity entity,
        List<ChatMessageEntity> messageEntities,
        List<ChatMessageAttachmentEntity> attachmentEntities)
    {
        var attachmentsByMessageId = attachmentEntities
            .GroupBy(x => x.MessageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(MapToAttachment).ToList(),
                StringComparer.OrdinalIgnoreCase);

        return new SessionHistory
        {
            SessionId = entity.SessionId,
            Title = entity.Title ?? "新会话",
            WorkspacePath = entity.WorkspacePath ?? string.Empty,
            ToolId = entity.ToolId ?? string.Empty,
            CliThreadId = entity.CliThreadId,
            UsesCcSwitchSnapshot = entity.UsesCcSwitchSnapshot,
            CcSwitchSnapshotToolId = entity.CcSwitchSnapshotToolId,
            CcSwitchProviderId = entity.CcSwitchProviderId,
            CcSwitchProviderName = entity.CcSwitchProviderName,
            CcSwitchProviderCategory = entity.CcSwitchProviderCategory,
            CcSwitchLiveConfigPath = entity.CcSwitchLiveConfigPath,
            CcSwitchSnapshotRelativePath = entity.CcSwitchSnapshotRelativePath,
            CcSwitchSnapshotSyncedAt = entity.CcSwitchSnapshotSyncedAt,
            ToolLaunchOverrides = SessionLaunchOverrideHelper.Deserialize(entity.ToolLaunchOverridesJson),
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            IsWorkspaceValid = entity.IsWorkspaceValid,
            IsCustomWorkspace = entity.IsCustomWorkspace,
            ProjectId = entity.ProjectId,
            Messages = messageEntities.Select(m =>
            {
                var logicalMessageId = ResolveLogicalMessageId(m);
                return new ChatMessage
                {
                    Id = logicalMessageId,
                    Role = m.Role,
                    Content = m.Content ?? string.Empty,
                    CreatedAt = m.CreatedAt,
                    Attachments = attachmentsByMessageId.TryGetValue(logicalMessageId, out var attachments)
                        ? attachments
                        : new List<MessageAttachment>()
                };
            }).ToList()
        };
    }

    private ChatSessionEntity MapToSessionEntity(SessionHistory session, string username)
    {
        return new ChatSessionEntity
        {
            SessionId = session.SessionId,
            Username = username,
            Title = session.Title,
            WorkspacePath = session.WorkspacePath,
            ToolId = session.ToolId,
            CliThreadId = session.CliThreadId,
            UsesCcSwitchSnapshot = session.UsesCcSwitchSnapshot,
            CcSwitchSnapshotToolId = session.CcSwitchSnapshotToolId,
            CcSwitchProviderId = session.CcSwitchProviderId,
            CcSwitchProviderName = session.CcSwitchProviderName,
            CcSwitchProviderCategory = session.CcSwitchProviderCategory,
            CcSwitchLiveConfigPath = session.CcSwitchLiveConfigPath,
            CcSwitchSnapshotRelativePath = session.CcSwitchSnapshotRelativePath,
            CcSwitchSnapshotSyncedAt = session.CcSwitchSnapshotSyncedAt,
            ToolLaunchOverridesJson = SessionLaunchOverrideHelper.Serialize(session.ToolLaunchOverrides),
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt,
            IsWorkspaceValid = session.IsWorkspaceValid,
            IsCustomWorkspace = session.IsCustomWorkspace,
            ProjectId = session.ProjectId
        };
    }

    private ChatMessageEntity MapToMessageEntity(ChatMessage message, string sessionId, string username)
    {
        return new ChatMessageEntity
        {
            MessageId = string.IsNullOrWhiteSpace(message.Id) ? Guid.NewGuid().ToString("N") : message.Id,
            SessionId = sessionId,
            Username = username,
            Role = message.Role,
            Content = message.Content,
            CreatedAt = message.CreatedAt
        };
    }

    private static ChatMessageAttachmentEntity MapToAttachmentEntity(
        MessageAttachment attachment,
        string messageId,
        string sessionId,
        string username)
    {
        return new ChatMessageAttachmentEntity
        {
            MessageId = messageId,
            SessionId = sessionId,
            Username = username,
            AttachmentId = attachment.Id,
            DisplayName = attachment.DisplayName,
            MimeType = attachment.MimeType,
            Extension = attachment.Extension,
            SizeBytes = attachment.SizeBytes,
            Kind = attachment.Kind.ToString(),
            WorkspaceRelativePath = attachment.WorkspaceRelativePath,
            CreatedAt = attachment.CreatedAt
        };
    }

    private static MessageAttachment MapToAttachment(ChatMessageAttachmentEntity attachmentEntity)
    {
        var kind = Enum.TryParse<MessageAttachmentKind>(attachmentEntity.Kind, true, out var parsedKind)
            ? parsedKind
            : MessageAttachmentKind.Text;

        return new MessageAttachment
        {
            Id = attachmentEntity.AttachmentId,
            DisplayName = attachmentEntity.DisplayName,
            MimeType = attachmentEntity.MimeType,
            Extension = attachmentEntity.Extension,
            SizeBytes = attachmentEntity.SizeBytes,
            Kind = kind,
            WorkspaceRelativePath = attachmentEntity.WorkspaceRelativePath,
            CreatedAt = attachmentEntity.CreatedAt
        };
    }

    private async Task ExecuteWithTransactionIfAvailableAsync(Func<Task> action)
    {
        var db = TryGetTransactionDb();
        if (db == null)
        {
            await action();
            return;
        }

        await db.Ado.BeginTranAsync();
        try
        {
            await action();
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }
    }

    private SqlSugarScope? TryGetTransactionDb()
    {
        try
        {
            return _sessionRepository.GetDB();
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static SemaphoreSlim GetSessionSaveLock(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return AnonymousSessionSaveLock;
        }

        return SessionSaveLocks.GetOrAdd(sessionId.Trim(), static _ => new SemaphoreSlim(1, 1));
    }

    private static string ResolveLogicalMessageId(ChatMessageEntity messageEntity)
    {
        return string.IsNullOrWhiteSpace(messageEntity.MessageId)
            ? $"legacy-msg-{messageEntity.Id}"
            : messageEntity.MessageId;
    }

    private readonly struct SemaphoreReleaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;

        public SemaphoreReleaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            _semaphore.Release();
        }
    }

    private sealed class SessionDeleteNotFoundException(string sessionId)
        : InvalidOperationException($"Session '{sessionId}' was not found during delete.");

    private static void MergeManagedToolSnapshot(SessionHistory session, ChatSessionEntity? existingEntity)
    {
        if (existingEntity == null || !existingEntity.UsesCcSwitchSnapshot)
        {
            return;
        }

        var currentToolId = NormalizeSnapshotToolId(session.ToolId);
        var snapshotToolId = NormalizeSnapshotToolId(existingEntity.CcSwitchSnapshotToolId);
        if (!string.IsNullOrWhiteSpace(currentToolId) &&
            !string.IsNullOrWhiteSpace(snapshotToolId) &&
            !string.Equals(currentToolId, snapshotToolId, StringComparison.OrdinalIgnoreCase))
        {
            session.UsesCcSwitchSnapshot = false;
            session.CcSwitchSnapshotToolId = null;
            session.CcSwitchProviderId = null;
            session.CcSwitchProviderName = null;
            session.CcSwitchProviderCategory = null;
            session.CcSwitchLiveConfigPath = null;
            session.CcSwitchSnapshotRelativePath = null;
            session.CcSwitchSnapshotSyncedAt = null;
            return;
        }

        if (session.UsesCcSwitchSnapshot)
        {
            return;
        }

        session.UsesCcSwitchSnapshot = existingEntity.UsesCcSwitchSnapshot;
        session.CcSwitchSnapshotToolId = existingEntity.CcSwitchSnapshotToolId;
        session.CcSwitchProviderId = existingEntity.CcSwitchProviderId;
        session.CcSwitchProviderName = existingEntity.CcSwitchProviderName;
        session.CcSwitchProviderCategory = existingEntity.CcSwitchProviderCategory;
        session.CcSwitchLiveConfigPath = existingEntity.CcSwitchLiveConfigPath;
        session.CcSwitchSnapshotRelativePath = existingEntity.CcSwitchSnapshotRelativePath;
        session.CcSwitchSnapshotSyncedAt = existingEntity.CcSwitchSnapshotSyncedAt;
    }

    private static string NormalizeSnapshotToolId(string? toolId)
    {
        return toolId?.Trim().ToLowerInvariant() switch
        {
            "claude" => "claude-code",
            "opencode-cli" => "opencode",
            var value => value ?? string.Empty
        };
    }

    #endregion
}
