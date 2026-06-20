using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Domain.Service.Adapters;
using WebCodeCli.Domain.Repositories.Base.ChatSession;

namespace WebCodeCli.Domain.Domain.Service.Channels;

/// <summary>
/// 椋炰功娓犻亾鏈嶅姟瀹炵幇
/// 璐熻矗澶勭悊椋炰功娑堟伅鍙戦€併€佹帴鏀跺拰娴佸紡鍥炲
/// 涓?CliExecutorService 闆嗘垚瀹炵幇 AI 鍔╂墜鍔熻兘
/// </summary>
[ServiceDescription(typeof(IFeishuChannelService), ServiceLifetime.Singleton)]
public class FeishuChannelService : BackgroundService, IFeishuChannelService
{
    private static readonly FeishuIncomingAttachmentParser IncomingAttachmentParser = new();
    private readonly FeishuOptions _options;
    private readonly ILogger<FeishuChannelService> _logger;
    private readonly IFeishuCardKitClient _cardKit;
    private readonly IServiceProvider _serviceProvider;
    private FeishuMessageHandler? _messageHandler;
    private readonly ICliExecutorService _cliExecutor;
    private readonly IChatSessionService _chatSessionService;
    private readonly IFeishuAttachmentDraftService _attachmentDraftService;
    private readonly IReplyDocumentOrchestrator? _replyDocumentOrchestrator;

    private bool _isRunning = false;

    // 椋炰功娓犻亾榛樿鍥為€€宸ュ叿 ID锛堟渶缁堣繕浼氭鏌ラ厤缃拰瀹為檯鍙敤宸ュ叿锛?
    private const string FallbackToolId = "claude-code";

    // Event de-duplication cache keyed by Feishu event id.
    private readonly ConcurrentDictionary<string, DateTime> _processedEventIds = new();
    // Event ids older than this window are evicted from the cache.
    private const int EventCacheExpirationMinutes = 10;
    private readonly Dictionary<string, ActiveSessionExecution> _activeExecutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _activeExecutionsLock = new();
    private const string SupersededExecutionMessage = "当前回复已停止：同一会话收到了新的补充消息，请查看新卡片继续结果。";
    private const int StreamingStatusPulseIntervalMs = 900;
    private static readonly TimeSpan StreamingStatusPulseQuietWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExternalHistoryBackfillInterval = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// 鏈嶅姟鏄惁杩愯涓?
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// 鑾峰彇鑱婂ぉ鐨勫綋鍓嶆椿璺冧細璇滻D
    /// </summary>
    /// <param name="chatKey">鑱婂ぉ閿紙鏍煎紡锛歠eishu:{AppId}:{ChatId}锛?/param>
    /// <returns>褰撳墠浼氳瘽ID锛屽鏋滀笉瀛樺湪鍒欒繑鍥瀗ull</returns>
    public string? GetCurrentSession(string chatKey, string? username = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
        var bindingService = scope.ServiceProvider.GetRequiredService<IFeishuUserBindingService>();
        var sessions = GetValidFeishuSessions(repo, bindingService, chatKey, username);
        var activeSession = sessions.FirstOrDefault(s => s.IsFeishuActive && s.FeishuChatKey == chatKey);
        if (activeSession != null)
        {
            return activeSession.SessionId;
        }

        var latestSession = sessions.OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
        if (latestSession == null || string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        return SwitchCurrentSession(chatKey, latestSession.SessionId, username)
            ? latestSession.SessionId
            : null;
    }

    /// <summary>
    /// 鑾峰彇浼氳瘽鐨勬渶鍚庢椿璺冩椂闂?
    /// </summary>
    /// <param name="sessionId">浼氳瘽ID</param>
    /// <returns>鏈€鍚庢椿璺冩椂闂达紝濡傛灉浼氳瘽涓嶅瓨鍦ㄥ垯杩斿洖null</returns>
    public DateTime? GetSessionLastActiveTime(string sessionId)
    {
        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
        var session = repo.GetByIdAsync(sessionId).GetAwaiter().GetResult();
        return session?.UpdatedAt;
    }

    /// <summary>
    /// 鑾峰彇鑱婂ぉ鐨勬墍鏈変細璇滻D鍒楄〃
    /// </summary>
    /// <param name="chatKey">鑱婂ぉ閿?/param>
    /// <returns>浼氳瘽ID鍒楄〃</returns>
    public List<string> GetChatSessions(string chatKey, string? username = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
        var bindingService = scope.ServiceProvider.GetRequiredService<IFeishuUserBindingService>();
        var sessions = GetValidFeishuSessions(repo, bindingService, chatKey, username);
        return sessions.Select(s => s.SessionId).ToList();
    }

    /// <summary>
    /// 鍒囨崲鑱婂ぉ鐨勫綋鍓嶆椿璺冧細璇?
    /// </summary>
    /// <param name="chatKey">鑱婂ぉ閿?/param>
    /// <param name="sessionId">瑕佸垏鎹㈠埌鐨勪細璇滻D</param>
    /// <returns>鏄惁鍒囨崲鎴愬姛</returns>
    public bool SwitchCurrentSession(string chatKey, string sessionId, string? username = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();

        if (string.IsNullOrWhiteSpace(username))
        {
            return repo.SetActiveSessionAsync(chatKey, sessionId).GetAwaiter().GetResult();
        }

        var targetSession = repo.GetByIdAndUsernameAsync(sessionId, username).GetAwaiter().GetResult();
        if (targetSession == null)
        {
            return false;
        }

        var userChatSessions = repo.GetListAsync(x => x.Username == username && x.FeishuChatKey == chatKey)
            .GetAwaiter().GetResult();
        foreach (var session in userChatSessions.Where(x => x.IsFeishuActive))
        {
            session.IsFeishuActive = false;
            session.UpdatedAt = DateTime.Now;
            repo.UpdateAsync(session).GetAwaiter().GetResult();
        }

        targetSession.FeishuChatKey = chatKey;
        targetSession.IsFeishuActive = true;
        targetSession.UpdatedAt = DateTime.Now;
        repo.UpdateAsync(targetSession).GetAwaiter().GetResult();
        return true;
    }

    /// <summary>
    /// 鍏抽棴鎸囧畾浼氳瘽
    /// </summary>
    /// <param name="chatKey">鑱婂ぉ閿?/param>
    /// <param name="sessionId">瑕佸叧闂殑浼氳瘽ID</param>
    /// <returns>鏄惁鍏抽棴鎴愬姛</returns>
    public bool CloseSession(string chatKey, string sessionId, string? username = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();

        bool success;
        if (string.IsNullOrWhiteSpace(username))
        {
            success = repo.CloseFeishuSessionAsync(chatKey, sessionId).GetAwaiter().GetResult();
        }
        else
        {
            var targetSession = repo.GetByIdAndUsernameAsync(sessionId, username).GetAwaiter().GetResult();
            if (targetSession == null)
            {
                return false;
            }

            var wasActive = targetSession.IsFeishuActive && string.Equals(targetSession.FeishuChatKey, chatKey, StringComparison.OrdinalIgnoreCase);
            success = repo.DeleteAsync(targetSession).GetAwaiter().GetResult();
            if (success && wasActive)
            {
                var latestSession = repo.GetByUsernameOrderByUpdatedAtAsync(username).GetAwaiter().GetResult().FirstOrDefault();
                if (latestSession != null)
                {
                    SwitchCurrentSession(chatKey, latestSession.SessionId, username);
                }
            }
        }

        if (success)
        {
            _cliExecutor.CleanupSessionWorkspace(sessionId);
            _logger.LogInformation("Closed session {SessionId} for chat {ChatKey}, user={User}", sessionId, chatKey, username ?? string.Empty);
        }
        return success;
    }

    public bool IsSessionExecutionActive(string sessionId)
    {
        lock (_activeExecutionsLock)
        {
            return _activeExecutions.ContainsKey(sessionId);
        }
    }

    public bool StopSessionExecution(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (_activeExecutionsLock)
        {
            if (!_activeExecutions.TryGetValue(sessionId, out var execution))
            {
                return false;
            }

            execution.RequestStop();
            return true;
        }
    }

    public void PauseSessionStatusPulse(string sessionId, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || duration <= TimeSpan.Zero)
        {
            return;
        }

        lock (_activeExecutionsLock)
        {
            if (_activeExecutions.TryGetValue(sessionId, out var execution))
            {
                execution.PausePulse(duration);
            }
        }
    }

    public FeishuChannelService(
        IOptions<FeishuOptions> options,
        ILogger<FeishuChannelService> logger,
        IFeishuCardKitClient cardKit,
        IServiceProvider serviceProvider,
        ICliExecutorService cliExecutor,
        IChatSessionService chatSessionService,
        IReplyDocumentOrchestrator? replyDocumentOrchestrator = null,
        IFeishuAttachmentDraftService? attachmentDraftService = null)
    {
        _options = options.Value;
        _logger = logger;
        _cardKit = cardKit;
        _serviceProvider = serviceProvider;
        _cliExecutor = cliExecutor;
        _chatSessionService = chatSessionService;
        _attachmentDraftService = attachmentDraftService
            ?? serviceProvider.GetService<IFeishuAttachmentDraftService>()
            ?? new FeishuAttachmentDraftService();
        _replyDocumentOrchestrator = replyDocumentOrchestrator;
    }

    /// <summary>
    /// 鍚庡彴鏈嶅姟涓绘墽琛屾柟娉?
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Feishu channel is disabled");
            return;
        }

        _logger.LogInformation("Starting Feishu channel service...");

        // 涓嶅啀璁㈤槄闈欐€佷簨浠讹紝閫氳繃 HandleIncomingMessageAsync 鏂规硶澶勭悊娑堟伅锛堥伩鍏嶉噸澶嶅鐞嗭級

        _isRunning = true;

        _logger.LogInformation("Feishu channel service started (AppId: {AppId})", _options.AppId);

        // 淇濇寔杩愯锛岀瓑寰呭彇娑堜俊鍙?
        // WebSocket 杩炴帴鐢卞閮ㄧ殑 FeishuNetSdk.WebSocket 鏈嶅姟绠＄悊
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Feishu channel service cancellation requested");
        }
    }


    /// <summary>
    /// 鍋滄鏈嶅姟
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Feishu channel service...");
        _isRunning = false;
        await base.StopAsync(cancellationToken);
        _logger.LogInformation("Feishu channel service stopped");
    }

    /// <summary>
    /// 澶勭悊鏀跺埌鐨勬秷鎭?
    /// </summary>
    private async Task OnMessageReceivedAsync(FeishuIncomingMessage message)
    {
        try
        {
            _logger.LogInformation(
                "[FeishuChannel] 收到消息事件: MessageId={MessageId}, ChatId={ChatId}, Content={Content}",
                message.MessageId,
                message.ChatId,
                message.Content);

            var normalizedPrompt = FeishuPromptNormalizer.Normalize(message.Content);
            string sessionId;
            try
            {
                sessionId = GetCurrentSession(message);
            }
            catch (InvalidOperationException ex)
            {
                await ReplyMessageAsync(message.MessageId, $"鈿狅笍 {ex.Message}", message.SenderName, message.AppId);
                return;
            }

            var toolId = ResolveToolId(message.ChatId, message.SenderName);
            var effectiveOptions = await ResolveEffectiveOptionsAsync(message.SenderName, message.ChatId, message.AppId);

            if (await TryHandleInlinePostSubmissionAsync(
                    sessionId,
                    toolId,
                    message,
                    normalizedPrompt,
                    effectiveOptions))
            {
                return;
            }

            if (IsAttachmentMessageType(message.MessageType))
            {
                await HandleAttachmentMessageAsync(
                    sessionId,
                    toolId,
                    message,
                    normalizedPrompt,
                    effectiveOptions);
                return;
            }
            await ExecuteStreamingSubmissionAsync(
                sessionId,
                message.ChatId,
                toolId,
                normalizedPrompt,
                new ChatMessage
                {
                    Role = "user",
                    Content = normalizedPrompt,
                    CliToolId = toolId,
                    CreatedAt = DateTime.UtcNow
                },
                message.MessageId,
                username: message.SenderName,
                appId: message.AppId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[FeishuChannel] 处理消息失败: {MessageId}",
                message.MessageId);
        }
    }

    public async Task ExecutePreparedSubmissionAsync(
        PreparedMessageSubmission submission,
        string chatId,
        string? replyToMessageId = null,
        string? username = null,
        string? appId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);

        var userMessage = submission.UserMessage ?? new ChatMessage
        {
            Role = "user",
            Content = submission.Text,
            CliToolId = submission.ToolId,
            CreatedAt = DateTime.UtcNow,
            IsCompleted = true,
            Attachments = submission.Attachments
        };

        await ExecuteStreamingSubmissionAsync(
            submission.SessionId,
            chatId,
            submission.ToolId,
            submission.Text,
            userMessage,
            replyToMessageId,
            submission.ExecutionRequest,
            username,
            appId,
            cancellationToken);
    }

    private async Task ExecuteStreamingSubmissionAsync(
        string sessionId,
        string chatId,
        string toolId,
        string promptText,
        ChatMessage userMessage,
        string? completionReplyToMessageId,
        CliExecutionRequest? executionRequest = null,
        string? username = null,
        string? appId = null,
        CancellationToken cancellationToken = default)
    {
        _chatSessionService.AddMessage(sessionId, userMessage);

        var (streamingChrome, baseStatusMarkdown) = await BuildStreamingCardChromeAsync(chatId, sessionId, username);
        TryAttachSuperpowersQuickActions(streamingChrome, sessionId, toolId, showStopAction: true);

        var effectiveOptions = await ResolveEffectiveOptionsAsync(username, chatId, appId);
        var handle = await CreateStreamingHandleWithOverflowFallbackAsync(
            chatId,
            null,
            effectiveOptions.ThinkingMessage,
            effectiveOptions,
            streamingChrome,
            cancellationToken);

        _logger.LogInformation(
            "[FeishuChannel] 流式句柄已创建: CardId={CardId}",
            handle.CardId);

        var activeExecution = new ActiveSessionExecution(
            sessionId,
            completionReplyToMessageId ?? handle.MessageId,
            handle,
            streamingChrome,
            baseStatusMarkdown,
            effectiveOptions.ThinkingMessage);
        var cardSession = new FeishuStreamingCardSession(
            activeExecution.Handle,
            (_, latestContent, token) => TryCreateReplacementStreamingHandleAsync(
                chatId,
                null,
                latestContent,
                activeExecution.Chrome,
                effectiveOptions,
                token),
            activeExecution.ReplaceHandle,
            (stoppedHandle, latestContent, token) => TryFinishReplacementStreamingCardAsync(
                stoppedHandle,
                activeExecution,
                latestContent,
                token),
            deferReplacementUntilNextForegroundUpdate: IsGoalRuntimeSession(TryGetSessionEntity(sessionId)));
        activeExecution.PausePulseForOverflowCard(StreamingStatusPulseQuietWindow);
        using var backgroundUpdatesCts = new CancellationTokenSource();
        var statusPulseTask = RunStreamingStatusPulseAsync(activeExecution, cardSession, backgroundUpdatesCts.Token);
        var externalHistoryBackfillTask = RunExternalHistoryBackfillAsync(
            sessionId,
            toolId,
            promptText,
            effectiveOptions.ThinkingMessage,
            () => activeExecution.GetLatestRenderedContent(),
            content =>
            {
                if (activeExecution.Handle.AreCardUpdatesStopped)
                {
                    return;
                }

                activeExecution.SetLatestRenderedContent(content);
                activeExecution.PausePulseForOverflowCard(StreamingStatusPulseQuietWindow);
            },
            content => cardSession.UpdateAsync(
                content,
                activeExecution.UpdateCancellationTokenSource.Token,
                allowPendingReplacementActivation: false),
            backgroundUpdatesCts.Token);
        var previousExecution = RegisterActiveExecution(sessionId, activeExecution);
        if (previousExecution != null)
        {
            await SupersedeExecutionAsync(previousExecution, completionReplyToMessageId ?? handle.MessageId);
        }

        try
        {
            await ExecuteCliAndStreamAsync(
                sessionId,
                chatId,
                toolId,
                promptText,
                completionReplyToMessageId,
                effectiveOptions.ThinkingMessage,
                activeExecution,
                cardSession,
                backgroundUpdatesCts,
                statusPulseTask,
                externalHistoryBackfillTask,
                username,
                appId,
                executionRequest,
                activeExecution.ExecutionCancellationTokenSource.Token);
        }
        finally
        {
            CancelBackgroundUpdates(backgroundUpdatesCts);
            activeExecution.CancelUpdateWork();
            await AwaitStatusPulseAsync(statusPulseTask);
            await AwaitBackgroundTaskAsync(externalHistoryBackfillTask);
            UnregisterActiveExecution(sessionId, activeExecution);
            activeExecution.Dispose();
        }
    }

    private async Task UpdateOpenAttachmentDraftAsync(
        FeishuAttachmentDraftState draft,
        FeishuIncomingMessage message,
        string normalizedPrompt,
        FeishuOptions effectiveOptions,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(normalizedPrompt))
        {
            _attachmentDraftService.UpdateText(message.AppId, message.ChatId, message.SenderId, normalizedPrompt);
        }

        foreach (var incomingAttachment in message.Attachments)
        {
            var stagedAttachment = await StageIncomingAttachmentForDraftAsync(draft, incomingAttachment, effectiveOptions, cancellationToken);
            _attachmentDraftService.AddStagedAttachment(message.AppId, message.ChatId, message.SenderId, stagedAttachment);
        }

        var updatedDraft = _attachmentDraftService.GetDraft(message.AppId, message.ChatId, message.SenderId);
        if (updatedDraft == null)
        {
            return;
        }

        await SendAttachmentDraftCardAsync(updatedDraft, message.ChatId, effectiveOptions, cancellationToken);
    }

    private async Task<MessageAttachment> StageIncomingAttachmentForDraftAsync(
        FeishuAttachmentDraftState draft,
        FeishuIncomingAttachment incomingAttachment,
        FeishuOptions effectiveOptions,
        CancellationToken cancellationToken)
    {
        var downloadedAttachment = await _cardKit.DownloadIncomingAttachmentAsync(
            incomingAttachment,
            cancellationToken,
            effectiveOptions);

        var workspacePath = _cliExecutor.GetSessionWorkspacePath(draft.SessionId);
        var workspaceRoot = Path.GetFullPath(workspacePath);
        var stagingRelativeRoot = AttachmentStagingService.BuildSubmissionRootRelativePath(draft.DraftId);
        var stagingFullRoot = Path.Combine(
            workspaceRoot,
            stagingRelativeRoot.Replace("/", Path.DirectorySeparatorChar.ToString()));
        Directory.CreateDirectory(stagingFullRoot);

        var markerPath = Path.Combine(stagingFullRoot, ".submission-id");
        if (!File.Exists(markerPath))
        {
            await File.WriteAllTextAsync(markerPath, draft.DraftId, cancellationToken);
        }

        var stagedFileName = CreateUniqueDraftAttachmentFileName(
            stagingFullRoot,
            downloadedAttachment.DisplayName,
            incomingAttachment.AttachmentKey);
        var stagedFullPath = Path.Combine(stagingFullRoot, stagedFileName);
        await File.WriteAllBytesAsync(stagedFullPath, downloadedAttachment.Content, cancellationToken);

        return new MessageAttachment
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = downloadedAttachment.DisplayName,
            MimeType = downloadedAttachment.MimeType,
            Extension = Path.GetExtension(downloadedAttachment.DisplayName),
            SizeBytes = downloadedAttachment.SizeBytes,
            Kind = MessageSubmissionService.DetectAttachmentKind(downloadedAttachment.DisplayName, downloadedAttachment.MimeType),
            WorkspaceRelativePath = $"{stagingRelativeRoot}/{stagedFileName}",
            CreatedAt = DateTime.UtcNow
        };
    }

    private async Task SendAttachmentDraftCardAsync(
        FeishuAttachmentDraftState draft,
        string chatId,
        FeishuOptions effectiveOptions,
        CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var cardBuilder = scope.ServiceProvider.GetService<FeishuAttachmentDraftCardBuilder>();
        if (cardBuilder == null)
        {
            return;
        }

        var card = cardBuilder.BuildCard(draft);
        var cardJson = JsonSerializer.Serialize(card);
        await _cardKit.SendRawCardAsync(
            chatId,
            cardJson,
            cancellationToken,
            effectiveOptions);
    }

    private static string CreateUniqueDraftAttachmentFileName(
        string stagingRoot,
        string displayName,
        string attachmentKey)
    {
        var preferredFileName = string.IsNullOrWhiteSpace(displayName)
            ? attachmentKey
            : displayName;
        var sanitizedName = SanitizeDraftAttachmentFileName(preferredFileName);
        var baseName = Path.GetFileNameWithoutExtension(sanitizedName);
        var extension = Path.GetExtension(sanitizedName);
        var candidate = sanitizedName;
        var suffix = 2;

        while (File.Exists(Path.Combine(stagingRoot, candidate)))
        {
            candidate = $"{baseName}-{suffix}{extension}";
            suffix++;
        }

        return candidate;
    }

    private static string SanitizeDraftAttachmentFileName(string fileName)
    {
        var fileNameOnly = Path.GetFileName(fileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(fileNameOnly))
        {
            return "attachment";
        }

        var sanitizedChars = fileNameOnly
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
            .ToArray();
        var sanitized = new string(sanitizedChars).Trim().Trim('.', ' ');

        return string.IsNullOrWhiteSpace(sanitized) ? "attachment" : sanitized;
    }

    /// <summary>
    /// 澶勭悊鏀跺埌鐨勬秷鎭紙鐢?FeishuMessageHandler 璋冪敤锛?
    /// </summary>
    /// <param name="message">鏀跺埌鐨勬秷鎭?/param>
    public async Task HandleIncomingMessageAsync(FeishuIncomingMessage message)
    {
        // 浜嬩欢鍘婚噸妫€鏌?
        if (!string.IsNullOrEmpty(message.EventId))
        {
            // 娓呯悊杩囨湡鐨別vent_id
            CleanupExpiredEventIds();

            if (_processedEventIds.TryAdd(message.EventId, DateTime.UtcNow))
            {
                _logger.LogDebug("处理新事件: EventId={EventId}", message.EventId);
            }
            else
            {
                _logger.LogInformation("跳过重复事件: EventId={EventId}", message.EventId);
                return;
            }
        }

        _logger.LogInformation("[FeishuChannel] HandleIncomingMessageAsync 被调用");
        await OnMessageReceivedAsync(message);
    }

    private async Task HandleAttachmentMessageAsync(
        string sessionId,
        string toolId,
        FeishuIncomingMessage message,
        string normalizedPrompt,
        FeishuOptions effectiveOptions)
    {
        var attachment = await StageIncomingAttachmentAsync(sessionId, message, effectiveOptions);
        if (attachment == null)
        {
            _logger.LogWarning(
                "[FeishuChannel] 无法处理附件消息: MessageId={MessageId}, MessageType={MessageType}",
                message.MessageId,
                message.MessageType);
            await ReplyMessageAsync(message.MessageId, "⚠️ 附件解析失败，请重新发送。", message.SenderName, message.AppId);
            return;
        }

        var defaultInstruction = ShouldAppendAttachmentPromptText(
            message,
            normalizedPrompt,
            new IncomingAttachmentDescriptor(attachment.ResourceType, attachment.FileKey))
            ? normalizedPrompt
            : string.Empty;
        var cardJson = FeishuAttachmentSubmissionCardHelper.BuildSubmissionCardJson(
            sessionId,
            message.ChatId,
            toolId,
            attachment.ResourceType,
            attachment.FileName,
            attachment.AbsolutePath,
            attachment.MimeType,
            defaultInstruction);

        await _cardKit.ReplyRawCardAsync(
            message.MessageId,
            cardJson,
            optionsOverride: effectiveOptions);

        _logger.LogInformation(
            "[FeishuChannel] 已回复附件待提交卡片: MessageId={MessageId}, SessionId={SessionId}, ResourceType={ResourceType}, FilePath={FilePath}",
            message.MessageId,
            sessionId,
            attachment.ResourceType,
            attachment.AbsolutePath);
    }

    private async Task<bool> TryHandleInlinePostSubmissionAsync(
        string sessionId,
        string toolId,
        FeishuIncomingMessage message,
        string normalizedPrompt,
        FeishuOptions effectiveOptions,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(message.MessageType, "post", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var attachments = ResolveIncomingAttachments(message);
        if (attachments.Count == 0)
        {
            return false;
        }

        var promptText = ExtractInlineAttachmentPromptText(normalizedPrompt);
        if (string.IsNullOrWhiteSpace(promptText))
        {
            return false;
        }

        var attachmentInputs = await DownloadIncomingAttachmentsAsDraftInputsAsync(
            message.MessageId,
            attachments,
            effectiveOptions,
            cancellationToken);
        var preparedSubmission = await PrepareMessageSubmissionAsync(
            new MessageDraft
            {
                SessionId = sessionId,
                ToolId = toolId,
                Channel = MessageSubmissionChannel.Feishu,
                Text = promptText,
                Attachments = attachmentInputs,
                SubmittedBy = message.SenderName
            },
            cancellationToken);

        await ExecutePreparedSubmissionAsync(
            preparedSubmission,
            message.ChatId,
            message.MessageId,
            message.SenderName,
            message.AppId,
            cancellationToken);

        _logger.LogInformation(
            "[FeishuChannel] 已将富文本消息中的 {AttachmentCount} 个附件与文本一并直传给 CLI: MessageId={MessageId}, SessionId={SessionId}",
            attachmentInputs.Count,
            message.MessageId,
            sessionId);

        return true;
    }

    /// <summary>
    /// 娓呯悊杩囨湡鐨勪簨浠禝D缂撳瓨
    /// </summary>
    private void CleanupExpiredEventIds()
    {
        var expirationTime = DateTime.UtcNow.AddMinutes(-EventCacheExpirationMinutes);
        var expiredIds = _processedEventIds
            .Where(kv => kv.Value < expirationTime)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in expiredIds)
        {
            _processedEventIds.TryRemove(id, out _);
        }

        if (expiredIds.Count > 0)
        {
            _logger.LogDebug("娓呯悊浜?{Count} 涓繃鏈熺殑浜嬩欢ID", expiredIds.Count);
        }
    }

    private ActiveSessionExecution? RegisterActiveExecution(string sessionId, ActiveSessionExecution execution)
    {
        lock (_activeExecutionsLock)
        {
            _activeExecutions.TryGetValue(sessionId, out var previousExecution);
            _activeExecutions[sessionId] = execution;
            return previousExecution;
        }
    }

    private void UnregisterActiveExecution(string sessionId, ActiveSessionExecution execution)
    {
        lock (_activeExecutionsLock)
        {
            if (_activeExecutions.TryGetValue(sessionId, out var currentExecution) &&
                currentExecution.OperationId == execution.OperationId)
            {
                _activeExecutions.Remove(sessionId);
            }
        }
    }

    private async Task SupersedeExecutionAsync(ActiveSessionExecution execution, string newMessageId)
    {
        execution.MarkSuperseded();
        execution.CancelUpdateWork();
        execution.SetStoppedStatus();

        try
        {
            execution.ExecutionCancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await execution.Handle.FinishAsync(BuildSupersededCardContent(execution));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "缁撴潫琚柊娑堟伅鎺ョ鐨勬棫鍗＄墖澶辫触: Session={SessionId}, PreviousMessageId={PreviousMessageId}, NewMessageId={NewMessageId}",
                execution.SessionId,
                execution.MessageId,
                newMessageId);
        }
    }

    /// <summary>
    /// 鑾峰彇褰撳墠浼氳瘽
    /// 濡傛灉鑱婂ぉ娌℃湁娲诲姩浼氳瘽锛屽垯鎶涘嚭寮傚父鎻愮ず鐢ㄦ埛鎵嬪姩鍒涘缓
    /// </summary>
    /// <param name="message">椋炰功 incoming 娑堟伅</param>
    /// <returns>浼氳瘽ID</returns>
    /// <exception cref="InvalidOperationException">濡傛灉娌℃湁褰撳墠浼氳瘽鍒欐姏鍑?/exception>
    private string GetCurrentSession(FeishuIncomingMessage message)
    {
        var chatKey = message.ChatId.ToLowerInvariant();
        var username = message.SenderName;
        _logger.LogInformation("[会话匹配] 消息 ChatId={ChatId}, ChatKey={ChatKey}, User={User}",
            message.ChatId, chatKey, username);

        var currentSessionId = GetCurrentSession(chatKey, username);
        if (!string.IsNullOrWhiteSpace(currentSessionId))
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
            var currentSession = repo.GetByIdAsync(currentSessionId).GetAwaiter().GetResult();
            if (currentSession != null)
            {
                currentSession.UpdatedAt = DateTime.UtcNow;
                repo.UpdateAsync(currentSession).GetAwaiter().GetResult();
            }

            return currentSessionId;
        }

        throw new InvalidOperationException("当前没有可用会话，请先发送 /feishusessions 创建或选择会话。");
    }

    /// <summary>
    /// 鍒涘缓鏂颁細璇?
    /// </summary>
    /// <param name="message">椋炰功 incoming 娑堟伅</param>
    /// <param name="customWorkspacePath">鑷畾涔夊伐浣滃尯璺緞锛堝彲閫夛級</param>
    /// <returns>鏂颁細璇滻D</returns>
    public string CreateNewSession(FeishuIncomingMessage message, string? customWorkspacePath = null, string? toolId = null)
    {
        var chatKey = message.ChatId.ToLowerInvariant();
        var username = string.IsNullOrWhiteSpace(message.SenderName) ? "unknown" : message.SenderName;
        var resolvedToolId = NormalizeToolId(toolId) ?? ResolveToolId(chatKey, username);

        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
        var newSessionId = repo.CreateFeishuSessionAsync(
            chatKey,
            username,
            null,
            resolvedToolId).GetAwaiter().GetResult();

        var session = repo.GetByIdAsync(newSessionId).GetAwaiter().GetResult();
        if (session == null)
        {
            throw new InvalidOperationException($"鍒涘缓椋炰功浼氳瘽鍚庢湭鎵惧埌浼氳瘽璁板綍: {newSessionId}");
        }

        if (!string.IsNullOrWhiteSpace(customWorkspacePath))
        {
            var sessionDirectoryService = scope.ServiceProvider.GetRequiredService<ISessionDirectoryService>();
            sessionDirectoryService.SetSessionWorkspaceAsync(newSessionId, username, customWorkspacePath, true)
                .GetAwaiter().GetResult();
            session = repo.GetByIdAsync(newSessionId).GetAwaiter().GetResult();
            if (session == null)
            {
                throw new InvalidOperationException($"璁剧疆椋炰功浼氳瘽宸ヤ綔鍖哄悗鏈壘鍒颁細璇濊褰? {newSessionId}");
            }
            session.ToolId = resolvedToolId;
            session.UpdatedAt = DateTime.Now;
            repo.UpdateAsync(session).GetAwaiter().GetResult();
        }
        else
        {
            var workspacePath = _cliExecutor.InitializeSessionWorkspaceAsync(newSessionId).GetAwaiter().GetResult();
            session.WorkspacePath = workspacePath;
            session.IsCustomWorkspace = false;
            session.ToolId = resolvedToolId;
            session.UpdatedAt = DateTime.Now;
            repo.UpdateAsync(session).GetAwaiter().GetResult();
        }

        _logger.LogInformation(
            "Created new session: {SessionId} for chat: {ChatId} (user: {UserName}, tool: {ToolId}, workspace: {WorkspacePath})",
            newSessionId,
            message.ChatId,
            username,
            resolvedToolId,
            customWorkspacePath ?? session.WorkspacePath ?? string.Empty);
        return newSessionId;
    }

    public string? GetSessionUsername(string chatKey)
    {
        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
        var bindingService = scope.ServiceProvider.GetRequiredService<IFeishuUserBindingService>();
        var session = GetValidFeishuSessions(repo, bindingService, chatKey).FirstOrDefault();
        return session?.Username;
    }

    public string ResolveToolId(string chatKey, string? username = null)
    {
        var normalizedChatKey = chatKey.ToLowerInvariant();

        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
        var bindingService = scope.ServiceProvider.GetRequiredService<IFeishuUserBindingService>();
        var sessions = GetValidFeishuSessions(repo, bindingService, normalizedChatKey, username);

        var activeToolId = sessions
            .Where(s => s.IsFeishuActive)
            .Select(s => NormalizeToolId(s.ToolId))
            .FirstOrDefault(IsConfiguredToolAvailable);
        if (!string.IsNullOrWhiteSpace(activeToolId))
        {
            return activeToolId;
        }

        var historicalToolId = sessions
            .Select(s => NormalizeToolId(s.ToolId))
            .FirstOrDefault(IsConfiguredToolAvailable);
        if (!string.IsNullOrWhiteSpace(historicalToolId))
        {
            return historicalToolId;
        }

        return ResolveDefaultToolId();
    }

    private List<ChatSessionEntity> GetValidFeishuSessions(
        IChatSessionRepository repo,
        IFeishuUserBindingService bindingService,
        string chatKey,
        string? username = null)
    {
        CleanupUnboundFeishuSessions(repo, bindingService);

        List<ChatSessionEntity> sessions;
        if (string.IsNullOrWhiteSpace(username))
        {
            sessions = repo.GetByFeishuChatKeyAsync(chatKey).GetAwaiter().GetResult();
        }
        else
        {
            sessions = repo.GetByUsernameOrderByUpdatedAtAsync(username).GetAwaiter().GetResult();
        }

        return sessions.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    private void CleanupUnboundFeishuSessions(IChatSessionRepository repo, IFeishuUserBindingService bindingService)
    {
        var boundWebUsernames = bindingService.GetAllBoundWebUsernamesAsync().GetAwaiter().GetResult();
        var feishuSessions = repo.GetListAsync(x => x.FeishuChatKey != null).GetAwaiter().GetResult();
        var invalidSessions = feishuSessions
            .Where(session => !boundWebUsernames.Contains(session.Username))
            .ToList();

        foreach (var invalidSession in invalidSessions)
        {
            repo.DeleteAsync(invalidSession).GetAwaiter().GetResult();
            _logger.LogInformation("娓呯悊鏈粦瀹?Web 鐢ㄦ埛鐨勯涔︽棫浼氳瘽: {SessionId}, User={User}, ChatKey={ChatKey}", invalidSession.SessionId, invalidSession.Username, invalidSession.FeishuChatKey ?? string.Empty);
        }
    }

    /// <summary>
    /// 鎵ц CLI 宸ュ叿骞舵祦寮忔洿鏂板崱鐗?
    /// </summary>
    private async Task ExecuteCliAndStreamAsync(
        string sessionId,
        string chatId,
        string toolId,
        string userPrompt,
        string? completionReplyToMessageId,
        string thinkingMessage,
        ActiveSessionExecution activeExecution,
        FeishuStreamingCardSession cardSession,
        CancellationTokenSource backgroundUpdatesCts,
        Task statusPulseTask,
        Task externalHistoryBackfillTask,
        string? username = null,
        string? appId = null,
        CliExecutionRequest? executionRequest = null,
        CancellationToken cancellationToken = default)
    {
        var outputBuilder = new StringBuilder();
        var assistantMessageBuilder = new StringBuilder();
        var turnAssistantMessageBuilder = new StringBuilder();
        var finalAnswerMessageBuilder = new StringBuilder();
        var jsonlBuffer = new StringBuilder(); // JSONL 缂撳啿鍖猴紝澶勭悊涓嶅畬鏁寸殑琛?
        var hasStructuredTodoList = false;
        var latestRenderedContent = thinkingMessage;
        var cardDisconnected = false;
        var resolvedToolId = NormalizeToolId(toolId) ?? ResolveDefaultToolId();
        var tool = _cliExecutor.GetTool(resolvedToolId);

        if (tool == null)
        {
            activeExecution.CancelUpdateWork();
            activeExecution.SetErrorStatus();
            await cardSession.FinishAsync(FeishuStreamingErrorFormatter.AppendError(
                latestRenderedContent,
                $"未找到 CLI 工具 '{resolvedToolId}'，请在配置中添加该工具。"));
            _logger.LogWarning("CLI tool not found: {ToolId}", resolvedToolId);
            return;
        }

        // 鑾峰彇閫傞厤鍣紙鐢ㄤ簬瑙ｆ瀽 JSONL 杈撳嚭锛?
        var adapter = _cliExecutor.GetAdapter(tool);
        var useAdapter = adapter != null && _cliExecutor.SupportsStreamParsing(tool);

        _logger.LogDebug(
            "Executing CLI tool: {ToolId} for session: {SessionId}, UseAdapter: {UseAdapter}",
            tool.Id,
            sessionId,
            useAdapter);

        TryAttachSuperpowersQuickActions(activeExecution.Chrome, sessionId, tool.Id, showStopAction: true);
        try
        {
            var stream = executionRequest == null
                ? _cliExecutor.ExecuteStreamAsync(sessionId, tool.Id, userPrompt, cancellationToken)
                : _cliExecutor.ExecuteStreamAsync(executionRequest, cancellationToken);

            await foreach (var chunk in stream)
            {
                if (chunk.IsError)
                {
                    if (activeExecution.IsSuperseded && cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation(
                            "CLI execution superseded by newer message: Session={SessionId}, MessageId={MessageId}",
                            sessionId,
                            completionReplyToMessageId ?? activeExecution.Handle.MessageId);
                        return;
                    }

                    _logger.LogError(
                        "CLI execution error: {Error}",
                        chunk.ErrorMessage ?? "Unknown error");
                    activeExecution.CancelUpdateWork();
                    activeExecution.SetErrorStatus();
                    await cardSession.FinishAsync(FeishuStreamingErrorFormatter.AppendError(
                        latestRenderedContent,
                        chunk.ErrorMessage ?? "执行失败"));
                    return;
                }

                if (chunk.IsTurnBoundary)
                {
                    if (cardDisconnected)
                    {
                        continue;
                    }

                    if (_replyDocumentOrchestrator != null && turnAssistantMessageBuilder.Length > 0)
                    {
                        try
                        {
                            await _replyDocumentOrchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyDocumentRequest
                            {
                                ChatId = chatId,
                                SessionId = sessionId,
                                CliThreadId = ResolveCliThreadId(sessionId),
                                OriginalUserQuestion = userPrompt,
                                Username = username,
                                AppId = appId,
                                Output = turnAssistantMessageBuilder.ToString().Trim(),
                                FinalAnswerOutput = finalAnswerMessageBuilder.ToString().Trim()
                            });
                        }
                        catch (Exception ttsQueueEx)
                        {
                            _logger.LogWarning(
                                ttsQueueEx,
                                "Failed to queue reply document at turn boundary: Session={SessionId}, MessageId={MessageId}",
                                sessionId,
                                completionReplyToMessageId ?? activeExecution.Handle.MessageId);
                        }
                    }

                    var handoffSucceeded = await TryRotateGoalRuntimeTurnCardAsync(
                        sessionId,
                        chatId,
                        tool.Id,
                        activeExecution,
                        cardSession,
                        username,
                        appId,
                        cancellationToken);
                    if (!handoffSucceeded)
                    {
                        var disconnectedContent = await TryHandleStreamingCardDisconnectAsync(activeExecution, latestRenderedContent, cancellationToken);
                        if (disconnectedContent != null)
                        {
                            cardDisconnected = true;
                            latestRenderedContent = disconnectedContent;
                        }

                        continue;
                    }

                    outputBuilder.Clear();
                    assistantMessageBuilder.Clear();
                    turnAssistantMessageBuilder.Clear();
                    finalAnswerMessageBuilder.Clear();
                    jsonlBuffer.Clear();
                    hasStructuredTodoList = false;
                    latestRenderedContent = thinkingMessage;
                    activeExecution.SetLatestRenderedContent(thinkingMessage);
                    activeExecution.Chrome.LatestToolCallMarkdown = null;
                    activeExecution.PausePulseForOverflowCard(StreamingStatusPulseQuietWindow);
                    continue;
                }

                // 绱Н鍘熷杈撳嚭鍐呭
                outputBuilder.Append(chunk.Content);

                // 濡傛灉浣跨敤閫傞厤鍣紝瑙ｆ瀽 JSONL 骞舵彁鍙栧姪鎵嬫秷鎭?
                string displayContent;
                if (useAdapter)
                {
                    // 瑙ｆ瀽 JSONL 琛屽苟鎻愬彇鍔╂墜娑堟伅锛堜娇鐢ㄧ紦鍐插尯澶勭悊涓嶅畬鏁寸殑琛岋級
                    hasStructuredTodoList |= ProcessJsonlChunk(
                        chunk.Content,
                        sessionId,
                        adapter!,
                        assistantMessageBuilder,
                        turnAssistantMessageBuilder,
                        finalAnswerMessageBuilder,
                        jsonlBuffer,
                        activeExecution.Chrome);
                    displayContent = assistantMessageBuilder.ToString();

                    // 濡傛灉娌℃湁鍔╂墜娑堟伅锛屾樉绀?鎬濊€冧腑"
                    if (string.IsNullOrWhiteSpace(displayContent))
                    {
                        var latestKnownContent = activeExecution.GetLatestRenderedContent();
                        displayContent = ExtractFallbackOutput(outputBuilder.ToString(), adapter!)
                            ?? (ShouldProbeExternalHistory(latestKnownContent, thinkingMessage)
                                ? thinkingMessage
                                : latestKnownContent);
                    }
                }
                else
                {
                    // 涓嶄娇鐢ㄩ€傞厤鍣ㄦ椂锛岃繃婊ょ郴缁熸秷鎭?
                    displayContent = FormatMarkdownOutput(outputBuilder.ToString());
                }

                if (!cardDisconnected)
                {
                    // 娴佸紡鏇存柊鍗＄墖锛堣妭娴佸湪 handle 鍐呴儴澶勭悊锛?
                    activeExecution.SetLatestRenderedContent(displayContent);
                    latestRenderedContent = displayContent;
                    activeExecution.PausePulseForOverflowCard(StreamingStatusPulseQuietWindow);
                    var updateSucceeded = await cardSession.UpdateAsync(
                        displayContent,
                        cancellationToken,
                        allowPendingReplacementActivation: true);
                    var disconnectedContent = updateSucceeded
                        ? null
                        : await TryHandleStreamingCardDisconnectAsync(activeExecution, latestRenderedContent, cancellationToken);
                    if (disconnectedContent != null)
                    {
                        cardDisconnected = true;
                        latestRenderedContent = disconnectedContent;
                    }
                }

                _logger.LogDebug(
                    "Streamed chunk: {ContentPreview}...",
                    chunk.Content.Length > 50 ? chunk.Content[..50] : chunk.Content);

                // 濡傛灉瀹屾垚锛岃烦鍑哄惊鐜?
                if (chunk.IsCompleted)
                {
                    break;
                }
            }

            if (useAdapter && jsonlBuffer.Length > 0)
            {
                hasStructuredTodoList |= ProcessJsonlLine(
                    jsonlBuffer.ToString(),
                    sessionId,
                    adapter!,
                    assistantMessageBuilder,
                    turnAssistantMessageBuilder,
                    finalAnswerMessageBuilder,
                    activeExecution.Chrome);
                jsonlBuffer.Clear();
            }

            if (activeExecution.IsSuperseded && cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // 瀹屾垚娴佸紡鍥炲
            string finalOutput;
            if (useAdapter)
            {
                // 浣跨敤閫傞厤鍣ㄦ椂锛屼娇鐢ㄦ彁鍙栫殑鍔╂墜娑堟伅
                finalOutput = assistantMessageBuilder.ToString().Trim();
                if (string.IsNullOrWhiteSpace(finalOutput))
                {
                    finalOutput = ExtractFallbackOutput(outputBuilder.ToString(), adapter!) ?? "无输出";
                }

                finalOutput = await ResolveExternalHistoryFallbackOutputAsync(
                    sessionId,
                    tool.Id,
                    userPrompt,
                    thinkingMessage,
                    finalOutput,
                    cancellationToken) ?? "无输出";
            }
            else
            {
                // 涓嶄娇鐢ㄩ€傞厤鍣ㄦ椂锛岃繃婊ょ郴缁熸秷鎭?
                finalOutput = FormatMarkdownOutput(outputBuilder.ToString());
            }

            var finalAnswerOutput = finalAnswerMessageBuilder.ToString().Trim();

            if (!cardDisconnected)
            {
                var disconnectedContent = await TryHandleStreamingCardDisconnectAsync(activeExecution, latestRenderedContent, cancellationToken);
                if (disconnectedContent != null)
                {
                    cardDisconnected = true;
                    latestRenderedContent = disconnectedContent;
                }
            }

            CancelBackgroundUpdates(backgroundUpdatesCts);
            await AwaitStatusPulseAsync(statusPulseTask);
            await AwaitBackgroundTaskAsync(externalHistoryBackfillTask);
            if (!cardDisconnected && !cancellationToken.IsCancellationRequested && !activeExecution.Handle.AreCardUpdatesStopped)
            {
                var completionPresentation = await BuildCompletionPresentationAsync(
                    sessionId,
                    tool.Id,
                    activeExecution.BaseStatusMarkdown);
                activeExecution.SetLatestRenderedContent(finalOutput);
                latestRenderedContent = finalOutput;
                activeExecution.Chrome.StatusMarkdown = completionPresentation.StatusMarkdown;
                SetTopChipGroupsEnabled(activeExecution.Chrome, true);
                TryAttachSuperpowersQuickActions(activeExecution.Chrome, sessionId, tool.Id);
                var finishSucceeded = await cardSession.FinishAsync(finalOutput);
                if (!finishSucceeded)
                {
                    var disconnectedContent = await TryHandleStreamingCardDisconnectAsync(activeExecution, latestRenderedContent, cancellationToken);
                    if (disconnectedContent != null)
                    {
                        cardDisconnected = true;
                        latestRenderedContent = disconnectedContent;
                    }
                }
                // NOTE: Keep the explicit completion text notification for Feishu users.
                try
                {
                    if (!cardDisconnected && string.IsNullOrWhiteSpace(completionReplyToMessageId))
                    {
                        await SendMessageAsync(
                            chatId,
                            completionPresentation.NotificationText,
                            username,
                            appId);
                    }
                    else if (!cardDisconnected)
                    {
                        await ReplyMessageAsync(
                            completionReplyToMessageId,
                            completionPresentation.NotificationText,
                            username,
                            appId);
                    }
                }
                catch (Exception notificationEx)
                {
                    _logger.LogWarning(
                        notificationEx,
                        "鍙戦€佹祦寮忓畬鎴愭枃鏈€氱煡澶辫触: MessageId={MessageId}",
                        completionReplyToMessageId ?? activeExecution.Handle.MessageId);
                }
            }
            else
            {
                _logger.LogInformation(
                    cancellationToken.IsCancellationRequested || activeExecution.Handle.AreCardUpdatesStopped
                        ? "Feishu card updates stopped mid-stream; skipped final card completion update: Session={SessionId}, MessageId={MessageId}"
                        : "Feishu card completed without final card update: Session={SessionId}, MessageId={MessageId}",
                    sessionId,
                    completionReplyToMessageId ?? activeExecution.Handle.MessageId);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                // 娣诲姞鍔╂墜鍥炲鍒颁細璇?
                _chatSessionService.AddMessage(sessionId, new ChatMessage
                {
                    Role = "assistant",
                    Content = finalOutput,
                    CliToolId = tool.Id,
                    IsCompleted = true,
                    CreatedAt = DateTime.UtcNow
                });

                // 鏇存柊浼氳瘽鏈€鍚庢椿鍔ㄦ椂闂?
                using var scope = _serviceProvider.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
                var session = await repo.GetByIdAsync(sessionId);
                if (session != null)
                {
                    session.ToolId = tool.Id;
                    session.UpdatedAt = DateTime.UtcNow;
                    await repo.UpdateAsync(session);
                }

                if (!cardDisconnected && _replyDocumentOrchestrator != null)
                {
                    try
                    {
                        await _replyDocumentOrchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyDocumentRequest
                        {
                            ChatId = chatId,
                            SessionId = sessionId,
                            CliThreadId = ResolveCliThreadId(sessionId),
                            OriginalUserQuestion = userPrompt,
                            Username = username,
                            AppId = appId,
                            Output = finalOutput,
                            FinalAnswerOutput = finalAnswerOutput
                        });
                    }
                    catch (Exception ttsQueueEx)
                    {
                        _logger.LogWarning(
                            ttsQueueEx,
                            "Failed to queue reply document after Feishu completion: Session={SessionId}, MessageId={MessageId}",
                            sessionId,
                            completionReplyToMessageId ?? activeExecution.Handle.MessageId);
                    }
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "CLI execution completed for message: {MessageId}, session: {SessionId}",
                    completionReplyToMessageId ?? activeExecution.Handle.MessageId,
                    sessionId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                activeExecution.Handle.AreCardUpdatesStopped
                    ? "CLI execution stopped by user: Session={SessionId}, MessageId={MessageId}"
                    : "CLI execution cancelled because a newer message took over: Session={SessionId}, MessageId={MessageId}",
                sessionId,
                completionReplyToMessageId ?? activeExecution.Handle.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLI execution failed for message: {MessageId}", completionReplyToMessageId ?? activeExecution.Handle.MessageId);
            activeExecution.CancelUpdateWork();
            activeExecution.SetErrorStatus();
            await cardSession.FinishAsync(FeishuStreamingErrorFormatter.AppendError(
                latestRenderedContent,
                ex.Message));
        }
    }

    private async Task<StagedIncomingAttachment?> StageIncomingAttachmentAsync(
        string sessionId,
        FeishuIncomingMessage message,
        FeishuOptions effectiveOptions)
    {
        if (!IsAttachmentMessageType(message.MessageType))
        {
            return null;
        }

        var attachment = TryParseIncomingAttachment(message);
        if (attachment == null)
        {
            _logger.LogWarning(
                "[FeishuChannel] 无法从入站附件消息解析资源信息: MessageId={MessageId}, MessageType={MessageType}, RawContent={RawContent}",
                message.MessageId,
                message.MessageType,
                message.RawContent);
            return null;
        }

        var download = await _cardKit.DownloadMessageResourceAsync(
            message.MessageId,
            attachment.FileKey,
            attachment.ResourceType,
            optionsOverride: effectiveOptions);

        var relativeDirectory = Path.Combine(".webcode", "feishu-inputs");
        var sanitizedFileName = SanitizeFileName(download.FileName);
        if (string.IsNullOrWhiteSpace(sanitizedFileName))
        {
            sanitizedFileName = $"{attachment.ResourceType}-{attachment.FileKey}";
        }

        var stagedFileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{sanitizedFileName}";
        var uploaded = await _cliExecutor.UploadFileToWorkspaceAsync(
            sessionId,
            stagedFileName,
            download.Content,
            relativeDirectory);

        if (!uploaded)
        {
            throw new InvalidOperationException($"Failed to stage Feishu attachment into workspace for message {message.MessageId}.");
        }

        var workspacePath = _cliExecutor.GetSessionWorkspacePath(sessionId);
        var absolutePath = Path.Combine(workspacePath, relativeDirectory, stagedFileName);

        return new StagedIncomingAttachment(
            attachment.ResourceType,
            attachment.FileKey,
            sanitizedFileName,
            download.MimeType,
            absolutePath);
    }

    private static bool IsAttachmentMessageType(string? messageType)
    {
        return string.Equals(messageType, "image", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(messageType, "file", StringComparison.OrdinalIgnoreCase);
    }

    private static List<FeishuIncomingAttachment> ResolveIncomingAttachments(FeishuIncomingMessage message)
    {
        if (message.Attachments.Count > 0)
        {
            return message.Attachments;
        }

        var rawPayload = !string.IsNullOrWhiteSpace(message.RawContent)
            ? message.RawContent
            : message.Content;
        return IncomingAttachmentParser.Parse(message.MessageType, rawPayload).ToList();
    }

    private static IncomingAttachmentDescriptor? TryParseIncomingAttachment(FeishuIncomingMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.RawContent))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(message.RawContent);
            var root = document.RootElement;

            if (string.Equals(message.MessageType, "image", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("image_key", out var imageKeyElement))
                {
                    return null;
                }

                var imageKey = imageKeyElement.GetString();
                if (string.IsNullOrWhiteSpace(imageKey))
                {
                    return null;
                }

                return new IncomingAttachmentDescriptor("image", imageKey);
            }

            if (string.Equals(message.MessageType, "file", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("file_key", out var fileKeyElement))
                {
                    return null;
                }

                var fileKey = fileKeyElement.GetString();
                if (string.IsNullOrWhiteSpace(fileKey))
                {
                    return null;
                }

                return new IncomingAttachmentDescriptor("file", fileKey);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return sanitized.Trim();
    }

    private static bool ShouldAppendAttachmentPromptText(
        FeishuIncomingMessage message,
        string normalizedPrompt,
        IncomingAttachmentDescriptor attachment)
    {
        if (string.IsNullOrWhiteSpace(normalizedPrompt))
        {
            return false;
        }

        var trimmedPrompt = normalizedPrompt.Trim();
        var trimmedRawContent = message.RawContent?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(trimmedRawContent) &&
            string.Equals(trimmedPrompt, trimmedRawContent, StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmedPrompt.StartsWith("{", StringComparison.Ordinal) &&
            trimmedPrompt.Contains(attachment.FileKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private string? ResolveCliThreadId(string sessionId)
    {
        var cliThreadId = _cliExecutor.GetCliThreadId(sessionId)?.Trim();
        if (!string.IsNullOrWhiteSpace(cliThreadId))
        {
            return cliThreadId;
        }

        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
        return repo.GetByIdAsync(sessionId).GetAwaiter().GetResult()?.CliThreadId?.Trim();
    }

    private static string ExtractInlineAttachmentPromptText(string normalizedPrompt)
    {
        if (string.IsNullOrWhiteSpace(normalizedPrompt))
        {
            return string.Empty;
        }

        var normalizedNewLines = normalizedPrompt
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalizedNewLines.Split('\n');
        var builder = new StringBuilder();
        var previousLineWasBlank = false;

        foreach (var rawLine in lines)
        {
            var cleanedLine = rawLine
                .Replace("[image]", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("[media]", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();

            if (cleanedLine.Length == 0)
            {
                if (builder.Length == 0 || previousLineWasBlank)
                {
                    continue;
                }

                builder.AppendLine();
                previousLineWasBlank = true;
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(cleanedLine);
            previousLineWasBlank = false;
        }

        return builder.ToString().Trim();
    }

    private async Task<List<MessageDraftAttachmentInput>> DownloadIncomingAttachmentsAsDraftInputsAsync(
        string messageId,
        IReadOnlyList<FeishuIncomingAttachment> attachments,
        FeishuOptions effectiveOptions,
        CancellationToken cancellationToken)
    {
        var inputs = new List<MessageDraftAttachmentInput>(attachments.Count);

        foreach (var attachment in attachments)
        {
            var (content, fileName, mimeType) = await _cardKit.DownloadMessageResourceAsync(
                messageId,
                attachment.AttachmentKey,
                string.Equals(attachment.MessageType, "file", StringComparison.OrdinalIgnoreCase) ? "file" : "image",
                cancellationToken,
                effectiveOptions);

            inputs.Add(new MessageDraftAttachmentInput
            {
                FileName = string.IsNullOrWhiteSpace(fileName)
                    ? attachment.AttachmentKey
                    : fileName,
                ContentType = string.IsNullOrWhiteSpace(mimeType)
                    ? attachment.MimeType
                    : mimeType,
                Content = content
            });
        }

        return inputs;
    }

    private async Task<PreparedMessageSubmission> PrepareMessageSubmissionAsync(
        MessageDraft draft,
        CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var scopedProvider = scope.ServiceProvider;
        var messageSubmissionService = scopedProvider.GetService<IMessageSubmissionService>();
        if (messageSubmissionService != null)
        {
            return await messageSubmissionService.PrepareAsync(draft, cancellationToken);
        }

        var attachmentStagingService = scopedProvider.GetService<IAttachmentStagingService>()
            ?? new AttachmentStagingService(_cliExecutor, NullLogger<AttachmentStagingService>.Instance);
        var cliAdapterFactory = scopedProvider.GetService<ICliAdapterFactory>()
            ?? new CliAdapterFactory();

        var fallbackMessageSubmissionService = new MessageSubmissionService(
            _cliExecutor,
            cliAdapterFactory,
            attachmentStagingService,
            NullLogger<MessageSubmissionService>.Instance);

        return await fallbackMessageSubmissionService.PrepareAsync(draft, cancellationToken);
    }

    private sealed record IncomingAttachmentDescriptor(string ResourceType, string FileKey);

    private sealed record StagedIncomingAttachment(
        string ResourceType,
        string FileKey,
        string FileName,
        string MimeType,
        string AbsolutePath);

    private static string BuildSupersededCardContent(ActiveSessionExecution execution)
    {
        var latestContent = execution.GetLatestRenderedContent()?.Trim();
        if (string.IsNullOrWhiteSpace(latestContent) ||
            string.Equals(latestContent, execution.InitialContent.Trim(), StringComparison.Ordinal))
        {
            return SupersededExecutionMessage;
        }

        if (latestContent.Contains(SupersededExecutionMessage, StringComparison.Ordinal))
        {
            return latestContent;
        }

        return $"{latestContent}\n\n{SupersededExecutionMessage}";
    }

    private static async Task TryFinishReplacementStreamingCardAsync(
        FeishuStreamingHandle stoppedHandle,
        ActiveSessionExecution execution,
        string latestContent,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var previousStatusMarkdown = execution.Chrome.StatusMarkdown;
        execution.SetStoppedStatus();
        try
        {
            await stoppedHandle.FinishAsync(
                FeishuStreamingReplacementFormatter.BuildTransferredContent(latestContent));
        }
        finally
        {
            execution.Chrome.StatusMarkdown = previousStatusMarkdown;
        }
    }

    private async Task<bool> TryRotateGoalRuntimeTurnCardAsync(
        string sessionId,
        string chatId,
        string toolId,
        ActiveSessionExecution activeExecution,
        FeishuStreamingCardSession cardSession,
        string? username,
        string? appId,
        CancellationToken cancellationToken)
    {
        if (!IsGoalRuntimeSession(TryGetSessionEntity(sessionId)))
        {
            return true;
        }

        var previousHandle = activeExecution.Handle;
        var handoffContent = activeExecution.GetLatestRenderedContent();
        var currentChrome = activeExecution.Chrome;
        var previousStatusMarkdown = currentChrome.StatusMarkdown;
        var effectiveOptions = await ResolveEffectiveOptionsAsync(username, chatId, appId);
        ApplyChromeForTurnHandoff(currentChrome, activeExecution.BaseStatusMarkdown);

        try
        {
            await previousHandle.FinishAsync(handoffContent);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Finishing previous goal-runtime turn card failed: Session={SessionId}, CardId={CardId}", sessionId, previousHandle.CardId);
        }

        currentChrome.StatusMarkdown = previousStatusMarkdown;
        TryAttachSuperpowersQuickActions(currentChrome, sessionId, toolId, showStopAction: true);

        FeishuStreamingHandle nextHandle;
        try
        {
            nextHandle = await CreateStreamingHandleWithOverflowFallbackAsync(
                chatId,
                null,
                effectiveOptions.ThinkingMessage,
                effectiveOptions,
                currentChrome,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Creating next goal-runtime turn card failed: Session={SessionId}, ChatId={ChatId}", sessionId, chatId);
            currentChrome.StatusMarkdown = previousStatusMarkdown;
            return false;
        }

        activeExecution.ReplaceHandle(nextHandle);
        await cardSession.SwitchHandleAsync(nextHandle, resetReplacementCount: true, cancellationToken);
        return true;
    }

    private ChatSessionEntity? TryGetSessionEntity(string sessionId)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
            return repo.GetByIdAsync(sessionId).GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyChromeForTurnHandoff(
        FeishuStreamingCardChrome chrome,
        string baseStatusMarkdown)
    {
        chrome.StatusMarkdown = GoalRuntimeCompletionStateFormatter.WithGoalContinuingState(baseStatusMarkdown);
        SetTopChipGroupsEnabled(chrome, true);
        chrome.BottomPrompt = null;
        chrome.AdditionalBottomPrompts.Clear();
        chrome.BottomActions.Clear();
    }

    /// <summary>
    /// 鏍煎紡鍖?Markdown 杈撳嚭
    /// 閫傜敤浜庨涔﹀崱鐗囨樉绀?
    /// </summary>
    private static string FormatMarkdownOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "无输出";

        // 杩囨护绯荤粺閽╁瓙娑堟伅锛圕laude Code CLI 鐨勫唴閮ㄨ皟璇曚俊鎭級
        // 杩欎簺 JSON 鏍煎紡鐨勬秷鎭寘鍚?hook_started銆乭ook_response銆丼essionStart 绛?
        // 鍙傝€冪綉椤电鐨?JSONL 閫傞厤鍣ㄨ繃婊ら€昏緫
        var lines = output.Split('\n');
        var filteredLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            // 璺宠繃绌鸿
            if (string.IsNullOrWhiteSpace(trimmedLine))
            {
                filteredLines.Add(line);
                continue;
            }

            // 璺宠繃鎵€鏈夌郴缁熸秷鎭紙涓庣綉椤电琛屼负涓€鑷达級
            // 绯荤粺娑堟伅鏍煎紡: {"type":"system",...}
            // 鍖呮嫭: init銆乭ook_started銆乭ook_response銆乭ook_event 绛?
            if (trimmedLine.StartsWith("{") && trimmedLine.Contains("\"type\":\"system\""))
            {
                // 杩囨护鎵€鏈?type 涓?system 鐨?JSON 娑堟伅
                continue;
            }

            filteredLines.Add(line);
        }

        var formatted = string.Join('\n', filteredLines);

        // 绉婚櫎杩囧鐨勭┖琛岋紙鏈€澶氫繚鐣欒繛缁?2 涓┖琛岋級
        formatted = System.Text.RegularExpressions.Regex.Replace(
            formatted,
            @"\n{3,}",
            "\n\n");

        // 闄愬埗鏈€澶ч暱搴︼紙椋炰功鍗＄墖鏈夊唴瀹归檺鍒讹級
        const int maxLength = 10000;
        if (formatted.Length > maxLength)
        {
            formatted = formatted[..maxLength] + "\n\n... (鍐呭杩囬暱锛屽凡鎴柇)";
        }

        return formatted.Trim();
    }

    private string ResolveDefaultToolId()
    {
        var configured = NormalizeToolId(_options.DefaultToolId);
        if (IsConfiguredToolAvailable(configured))
        {
            return configured!;
        }

        foreach (var candidate in new[] { FallbackToolId, "codex", "opencode" })
        {
            var normalized = NormalizeToolId(candidate);
            if (IsConfiguredToolAvailable(normalized))
            {
                return normalized!;
            }
        }

        return _cliExecutor.GetAvailableTools().FirstOrDefault()?.Id ?? FallbackToolId;
    }

    private bool IsConfiguredToolAvailable(string? toolId)
    {
        var normalized = NormalizeToolId(toolId);
        return !string.IsNullOrWhiteSpace(normalized) && _cliExecutor.GetTool(normalized) != null;
    }

    private static string? NormalizeToolId(string? toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return null;
        }

        if (toolId.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            return "claude-code";
        }

        if (toolId.Equals("opencode-cli", StringComparison.OrdinalIgnoreCase))
        {
            return "opencode";
        }

        return toolId;
    }

    private async Task RunExternalHistoryBackfillAsync(
        string sessionId,
        string toolId,
        string? expectedUserPrompt,
        string thinkingMessage,
        Func<string> contentAccessor,
        Action<string> contentUpdater,
        Func<string, Task> contentRenderer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(ExternalHistoryBackfillInterval, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var currentContent = contentAccessor();
                if (!ShouldProbeExternalHistory(currentContent, thinkingMessage))
                {
                    continue;
                }

                var historyContent = await TryGetLatestAssistantMessageFromExternalHistoryAsync(
                    sessionId,
                    toolId,
                    expectedUserPrompt,
                    cancellationToken);

                if (string.IsNullOrWhiteSpace(historyContent))
                {
                    continue;
                }

                var trimmedHistoryContent = historyContent.Trim();
                if (string.Equals(trimmedHistoryContent, currentContent?.Trim(), StringComparison.Ordinal))
                {
                    continue;
                }

                contentUpdater(trimmedHistoryContent);
                await contentRenderer(trimmedHistoryContent);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "External CLI history backfill failed: SessionId={SessionId}, ToolId={ToolId}", sessionId, toolId);
        }
    }

    private async Task<string?> ResolveExternalHistoryFallbackOutputAsync(
        string sessionId,
        string toolId,
        string? expectedUserPrompt,
        string thinkingMessage,
        string? currentOutput,
        CancellationToken cancellationToken)
    {
        if (!ShouldProbeExternalHistory(currentOutput, thinkingMessage))
        {
            return currentOutput?.Trim();
        }

        var historyContent = await TryGetLatestAssistantMessageFromExternalHistoryAsync(
            sessionId,
            toolId,
            expectedUserPrompt,
            cancellationToken);

        return string.IsNullOrWhiteSpace(historyContent)
            ? currentOutput?.Trim()
            : historyContent.Trim();
    }

    private async Task<string?> TryGetLatestAssistantMessageFromExternalHistoryAsync(
        string sessionId,
        string toolId,
        string? expectedUserPrompt,
        CancellationToken cancellationToken)
    {
        var cliThreadId = _cliExecutor.GetCliThreadId(sessionId);
        if (string.IsNullOrWhiteSpace(cliThreadId))
        {
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var historyService = scope.ServiceProvider.GetService<IExternalCliSessionHistoryService>();
        if (historyService == null)
        {
            return null;
        }

        var workspacePath = TryGetSessionWorkspacePath(sessionId);
        var messages = await historyService.GetRecentMessagesAsync(
            NormalizeToolId(toolId) ?? ResolveDefaultToolId(),
            cliThreadId,
            maxCount: 8,
            workspacePath: workspacePath,
            cancellationToken: cancellationToken);

        if (messages.Count == 0)
        {
            return null;
        }

        var lastMessage = messages[^1];
        if (!string.Equals(lastMessage.Role, "assistant", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(lastMessage.Content))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(expectedUserPrompt))
        {
            var previousUserMessage = messages
                .Take(messages.Count - 1)
                .LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));

            if (previousUserMessage == null ||
                !MatchesExpectedPrompt(previousUserMessage.Content, expectedUserPrompt))
            {
                return null;
            }
        }

        return lastMessage.Content.Trim();
    }

    private static bool ShouldProbeExternalHistory(string? currentContent, string thinkingMessage)
    {
        if (string.IsNullOrWhiteSpace(currentContent))
        {
            return true;
        }

        var trimmedContent = currentContent.Trim();
        return string.Equals(trimmedContent, thinkingMessage?.Trim(), StringComparison.Ordinal)
               || string.Equals(trimmedContent, "无输出", StringComparison.Ordinal);
    }

    private static bool MatchesExpectedPrompt(string? historyPrompt, string? expectedPrompt)
    {
        return string.Equals(
            NormalizeComparableText(historyPrompt),
            NormalizeComparableText(expectedPrompt),
            StringComparison.Ordinal);
    }

    private static string NormalizeComparableText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string? ExtractFallbackOutput(string fullOutput, ICliToolAdapter adapter)
    {
        if (string.IsNullOrWhiteSpace(fullOutput))
        {
            return null;
        }

        string? lastUsefulContent = null;
        var sawStructuredOutput = false;
        var lines = fullOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var isStructuredLine = line.StartsWith("{", StringComparison.Ordinal)
                                   || line.StartsWith("[", StringComparison.Ordinal);
            if (isStructuredLine)
            {
                sawStructuredOutput = true;
            }

            var outputEvent = adapter.ParseOutputLine(line);
            if (outputEvent == null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(outputEvent.Content))
            {
                continue;
            }

            var assistantMessage = adapter.ExtractAssistantMessage(outputEvent);
            if (!string.IsNullOrWhiteSpace(assistantMessage))
            {
                lastUsefulContent = assistantMessage.Trim();
                continue;
            }

            if (ShouldUseStructuredFallbackOutput(outputEvent))
            {
                lastUsefulContent = outputEvent.Content.Trim();
            }
        }

        if (!sawStructuredOutput)
        {
            return FormatMarkdownOutput(fullOutput);
        }

        return lastUsefulContent;
    }

    private static bool ShouldUseStructuredFallbackOutput(CliOutputEvent outputEvent)
    {
        if (string.IsNullOrWhiteSpace(outputEvent.Content))
        {
            return false;
        }

        if (outputEvent.EventType is "result" or "error" or "raw" or "assistant" or "assistant:message" or "stream_event" or "turn.failed")
        {
            return true;
        }

        return outputEvent.EventType is "item.updated" or "item.completed"
               && outputEvent.ItemType is "todo_list" or "file_change";
    }

    private async Task<(FeishuStreamingCardChrome Chrome, string BaseStatusMarkdown)> BuildStreamingCardChromeAsync(string chatId, string sessionId, string? username)
    {
        var chatKey = chatId.ToLowerInvariant();
        var sessions = GetChatSessionEntities(chatKey, username);
        var currentSession = sessions.FirstOrDefault(session =>
            string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));

        var workspaceName = TryGetSessionWorkspaceDirectoryName(sessionId)
            ?? ExtractWorkspaceDirectoryName(currentSession?.WorkspacePath)
            ?? "当前会话";
        var sessionLabel = GetSessionDisplayLabel(currentSession);
        var toolLabel = GetToolDisplayName(currentSession?.ToolId);
        var baseStatusMarkdown = BuildSessionStatusMarkdown(
            $"当前会话：**{workspaceName}** · {sessionLabel} · {toolLabel}",
            currentSession);

        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = FeishuStreamingStatusFormatter.WithRunningState(baseStatusMarkdown, 0)
        };
        chrome.OverflowOptions.AddRange(await BuildStreamingStatusOverflowOptionsAsync(chatKey, sessionId, currentSession, currentSession?.ToolId));
        chrome.TopChipGroups.AddRange(await BuildStreamingTopChipGroupsAsync(chatKey, sessionId, currentSession, currentSession?.ToolId, isEnabled: false));

        foreach (var session in sessions
                     .Where(session => !string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                     .Take(5))
        {
            chrome.OverflowOptions.Add(new FeishuStreamingCardOverflowOption
            {
                Text = BuildSessionOptionText(session),
                Value = new
                {
                    action = "switch_session",
                    session_id = session.SessionId,
                    chat_key = chatKey
                }
            });
        }

        chrome.OverflowOptions.Add(new FeishuStreamingCardOverflowOption
        {
            Text = "模型/会话管理...",
            Value = new
            {
                action = "open_session_manager",
                chat_key = chatKey,
                send_as_new_card = true
            }
        });

        return (chrome, baseStatusMarkdown);
    }

    private async Task<List<FeishuStreamingCardTopChipGroup>> BuildStreamingTopChipGroupsAsync(
        string chatKey,
        string sessionId,
        ChatSessionEntity? session,
        string? toolId,
        bool isEnabled)
    {
        var effectiveToolId = SessionLaunchOverrideHelper.ResolveEffectiveToolId(toolId, session?.CcSwitchSnapshotToolId);
        if (!SessionLaunchOverrideHelper.SupportsLaunchOverrides(effectiveToolId))
        {
            return [];
        }

        var groups = new List<FeishuStreamingCardTopChipGroup>();
        var launchState = await FeishuStreamingLaunchStateResolver.ResolveAsync(session, effectiveToolId);

        if (string.Equals(effectiveToolId, "codex", StringComparison.OrdinalIgnoreCase))
        {
            groups.Add(new FeishuStreamingCardTopChipGroup
            {
                Kind = "switch_hint",
                IsEnabled = isEnabled,
                SummaryMarkdown = "模型/思考等级在流式回复完成后可切换，会话随时可切换，但手机端飞书可能需点多次才能成功"
            });

            groups.Add(new FeishuStreamingCardTopChipGroup
            {
                Kind = "reasoning_effort",
                IsEnabled = isEnabled,
                Items =
                [
                    BuildReasoningChip("low", launchState.ReasoningEffort, isEnabled, sessionId, chatKey, effectiveToolId),
                    BuildReasoningChip("medium", launchState.ReasoningEffort, isEnabled, sessionId, chatKey, effectiveToolId),
                    BuildReasoningChip("high", launchState.ReasoningEffort, isEnabled, sessionId, chatKey, effectiveToolId),
                    BuildReasoningChip("xhigh", launchState.ReasoningEffort, isEnabled, sessionId, chatKey, effectiveToolId)
                ]
            });
        }

        return groups;
    }

    private async Task<List<FeishuStreamingCardOverflowOption>> BuildStreamingStatusOverflowOptionsAsync(
        string chatKey,
        string sessionId,
        ChatSessionEntity? session,
        string? toolId)
    {
        var effectiveToolId = SessionLaunchOverrideHelper.ResolveEffectiveToolId(toolId, session?.CcSwitchSnapshotToolId);
        if (!SessionLaunchOverrideHelper.SupportsLaunchOverrides(effectiveToolId))
        {
            return [];
        }

        var launchState = await FeishuStreamingLaunchStateResolver.ResolveAsync(session, effectiveToolId);
        var modelOptions = await LoadStreamingModelOptionsAsync(
            effectiveToolId,
            session?.CcSwitchProviderId,
            launchState.Model);

        return modelOptions
            .Where(option => !string.IsNullOrWhiteSpace(option.Id))
            .Select(option => new FeishuStreamingCardOverflowOption
            {
                Text = $"模型：{option.DisplayName}",
                Value = new
                {
                    action = "switch_streaming_card_model",
                    session_id = sessionId,
                    chat_key = chatKey,
                    tool_id = effectiveToolId,
                    model = option.Id
                }
            })
            .ToList();
    }

    private async Task<List<CcSwitchModelOption>> LoadStreamingModelOptionsAsync(string toolId, string? providerId, string? currentModel)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var ccSwitchService = scope.ServiceProvider.GetService<ICcSwitchService>();
            if (ccSwitchService == null)
            {
                return MergeStreamingModelOptions([], currentModel);
            }

            var catalog = await ccSwitchService.GetModelCatalogAsync(toolId, providerId);
            return MergeStreamingModelOptions(catalog.Models, currentModel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "飞书流式卡片读取模型列表失败: ToolId={ToolId}, ProviderId={ProviderId}", toolId, providerId);
            return MergeStreamingModelOptions([], currentModel);
        }
    }

    private static List<CcSwitchModelOption> MergeStreamingModelOptions(IEnumerable<CcSwitchModelOption> options, string? currentModel)
    {
        var merged = new List<CcSwitchModelOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var option in options)
        {
            if (option == null || string.IsNullOrWhiteSpace(option.Id))
            {
                continue;
            }

            var id = option.Id.Trim();
            if (!seen.Add(id))
            {
                continue;
            }

            merged.Add(new CcSwitchModelOption
            {
                Id = id,
                DisplayName = string.IsNullOrWhiteSpace(option.DisplayName) ? id : option.DisplayName.Trim()
            });
        }

        if (!string.IsNullOrWhiteSpace(currentModel) && seen.Add(currentModel.Trim()))
        {
            merged.Insert(0, new CcSwitchModelOption
            {
                Id = currentModel.Trim(),
                DisplayName = currentModel.Trim()
            });
        }

        return merged;
    }

    private static FeishuStreamingCardTopChipItem BuildReasoningChip(
        string value,
        string? currentValue,
        bool isEnabled,
        string sessionId,
        string chatKey,
        string toolId)
    {
        return new FeishuStreamingCardTopChipItem
        {
            Text = value,
            IsActive = string.Equals(value, currentValue, StringComparison.OrdinalIgnoreCase),
            IsEnabled = isEnabled,
            PreferredWidthPx = CalculateReasoningChipWidthPx(value),
            Value = new
            {
                action = "switch_streaming_card_reasoning_effort",
                session_id = sessionId,
                chat_key = chatKey,
                tool_id = toolId,
                reasoning_effort = value
            }
        };
    }

    private static int CalculateModelChipWidthPx(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return 180;
        }

        var trimmed = modelName.Trim();
        var effectiveLength = trimmed.Length;
        var uppercaseCount = trimmed.Count(char.IsUpper);
        var punctuationCount = trimmed.Count(ch => ch is '-' or '_' or '.' or '/');
        var digitCount = trimmed.Count(char.IsDigit);

        var width = 72
            + (effectiveLength * 7)
            + (uppercaseCount * 2)
            + punctuationCount
            + digitCount;

        var scaledWidth = (int)Math.Ceiling(width * 1.0);
        return Math.Clamp(scaledWidth, 180, 720);
    }

    private static int CalculateReasoningChipWidthPx(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return 96;
        }

        var trimmed = label.Trim();
        var uppercaseCount = trimmed.Count(char.IsUpper);
        var punctuationCount = trimmed.Count(ch => ch is '-' or '_' or '.' or '/');
        var digitCount = trimmed.Count(char.IsDigit);
        var width = 24
            + (trimmed.Length * 9)
            + (uppercaseCount * 2)
            + punctuationCount
            + digitCount;
        var scaledWidth = (int)Math.Ceiling(width * 1.0);
        return Math.Clamp(scaledWidth, 96, 440);
    }

    private static void SetTopChipGroupsEnabled(FeishuStreamingCardChrome chrome, bool isEnabled)
    {
        foreach (var group in chrome.TopChipGroups)
        {
            group.IsEnabled = isEnabled;

            foreach (var item in group.Items)
            {
                item.IsEnabled = isEnabled;
            }
        }
    }

    private void TryAttachSuperpowersQuickActions(
        FeishuStreamingCardChrome chrome,
        string sessionId,
        string toolId,
        bool showStopAction = false)
    {
        string? chatKey = null;
        ChatSessionEntity? session = null;
        using (var scope = _serviceProvider.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
            session = repo.GetByIdAsync(sessionId).GetAwaiter().GetResult();
            chatKey = session?.FeishuChatKey;
        }

        if (string.IsNullOrWhiteSpace(chatKey))
        {
            return;
        }

        var normalizedToolId = NormalizeToolId(toolId) ?? toolId;
        var isGoalRuntimeSession = IsGoalRuntimeSession(session);
        var capabilityState = ResolveSuperpowersCapabilityState(sessionId, normalizedToolId);
        var showGoalQuickActionButtons = GoalQuickActionVisibilityHelper.ShouldShowButtons(
            session,
            _cliExecutor,
            normalizedToolId);
        chrome.BottomPrompt = isGoalRuntimeSession
            ? null
            : SuperpowersQuickActionCardHelper.CreateBottomPrompt(
                sessionId,
                chatKey,
                normalizedToolId,
                capabilityState);
        chrome.AdditionalBottomPrompts.Clear();
        var goalCapabilityState = ResolveGoalCapabilityState(sessionId, normalizedToolId);
        var goalPrompt = GoalQuickActionCardHelper.CreateBottomPrompt(
            sessionId,
            chatKey,
            normalizedToolId,
            goalCapabilityState);
        if (goalPrompt != null)
        {
            chrome.AdditionalBottomPrompts.Add(goalPrompt);
        }
        chrome.BottomActions.Clear();
        chrome.BottomActions.AddRange(GoalQuickActionCardHelper.CreateBottomActions(
            sessionId,
            chatKey,
            normalizedToolId,
            goalCapabilityState,
            showGoalQuickActionButtons,
            isGoalRuntimeSession));
        if (!isGoalRuntimeSession)
        {
            chrome.BottomActions.AddRange(SuperpowersQuickActionCardHelper.CreateBottomActions(
                sessionId,
                chatKey,
                normalizedToolId,
                showPlanActions: ShouldShowSuperpowersPlanActions(sessionId),
                capabilityState: capabilityState,
                showStopAction: showStopAction));
            chrome.StatusMarkdown = SuperpowersQuickActionCardHelper.MergeCapabilityStatusMarkdown(
                chrome.StatusMarkdown,
                capabilityState);
        }
        chrome.StatusMarkdown = GoalQuickActionCardHelper.MergeCapabilityStatusMarkdown(
            chrome.StatusMarkdown,
            goalCapabilityState);
    }

    private bool ShouldShowSuperpowersPlanActions(string sessionId)
    {
        return SuperpowersQuickActionCardHelper.ShouldShowPlanActions(
            _chatSessionService.GetMessages(sessionId).Select(static message => message?.Content),
            HasSuperpowersPlanFiles(sessionId));
    }

    private bool HasSuperpowersPlanFiles(string sessionId)
    {
        var workspacePath = TryGetSessionWorkspacePath(sessionId);
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return false;
        }

        var planDirectory = Path.Combine(workspacePath, "docs", "superpowers", "plans");
        try
        {
            return Directory.Exists(planDirectory)
                   && Directory.EnumerateFiles(planDirectory, "*.md", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "检查 Superpowers 计划文件失败: SessionId={SessionId}", sessionId);
            return false;
        }
    }

    private string? TryGetSessionWorkspacePath(string sessionId)
    {
        try
        {
            return _cliExecutor.GetSessionWorkspacePath(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "从 CLI 运行时读取工作区失败: SessionId={SessionId}", sessionId);
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
            return repo.GetByIdAsync(sessionId).GetAwaiter().GetResult()?.WorkspacePath;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "从会话仓储读取工作区失败: SessionId={SessionId}", sessionId);
            return null;
        }
    }

    private SuperpowersCapabilitySnapshot? ResolveSuperpowersCapabilityState(string sessionId, string toolId)
    {
        using var scope = _serviceProvider.CreateScope();
        var capabilityService = scope.ServiceProvider.GetService<ISuperpowersCapabilityService>();
        var repo = scope.ServiceProvider.GetService<IChatSessionRepository>();
        if (capabilityService == null)
        {
            return null;
        }

        var session = repo?.GetByIdAsync(sessionId).GetAwaiter().GetResult();
        var normalizedToolId = NormalizeToolId(session?.CcSwitchSnapshotToolId ?? toolId) ?? toolId;

        return capabilityService.GetStateAsync(new SuperpowersCapabilityContext
        {
            ToolId = normalizedToolId,
            ProviderId = session?.CcSwitchProviderId,
            WorkspacePath = session?.WorkspacePath
        }).GetAwaiter().GetResult();
    }

    private GoalCapabilitySnapshot? ResolveGoalCapabilityState(string sessionId, string toolId)
    {
        using var scope = _serviceProvider.CreateScope();
        var capabilityService = scope.ServiceProvider.GetService<IGoalCapabilityService>();
        var repo = scope.ServiceProvider.GetService<IChatSessionRepository>();
        if (capabilityService == null)
        {
            return null;
        }

        var session = repo?.GetByIdAsync(sessionId).GetAwaiter().GetResult();
        var normalizedToolId = NormalizeToolId(session?.CcSwitchSnapshotToolId ?? toolId) ?? toolId;

        return capabilityService.GetStateAsync(new GoalCapabilityContext
        {
            ToolId = normalizedToolId,
            ProviderId = session?.CcSwitchProviderId,
            WorkspacePath = session?.WorkspacePath
        }).GetAwaiter().GetResult();
    }

    private static bool SessionContainsSuperpowers(IEnumerable<ChatMessage> messages)
    {
        return messages.Any(message =>
            !string.IsNullOrWhiteSpace(message?.Content)
            && message.Content.Contains("superpowers", StringComparison.OrdinalIgnoreCase));
    }

    private async Task RunStreamingStatusPulseAsync(
        ActiveSessionExecution execution,
        FeishuStreamingCardSession cardSession,
        CancellationToken loopCancellationToken)
    {
        try
        {
            while (!loopCancellationToken.IsCancellationRequested)
            {
                await Task.Delay(StreamingStatusPulseIntervalMs, loopCancellationToken);

                if (loopCancellationToken.IsCancellationRequested
                    || execution.UpdateCancellationTokenSource.IsCancellationRequested
                    || execution.IsSuperseded
                    || execution.Handle.AreCardUpdatesStopped)
                {
                    break;
                }

                if (execution.IsPulsePaused())
                {
                    continue;
                }

                execution.AdvanceRunningStatus();
                await cardSession.UpdateAsync(
                    execution.GetLatestRenderedContent(),
                    execution.UpdateCancellationTokenSource.Token,
                    allowPendingReplacementActivation: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void CancelBackgroundUpdates(CancellationTokenSource backgroundUpdatesCts)
    {
        try
        {
            backgroundUpdatesCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task AwaitStatusPulseAsync(Task statusPulseTask)
    {
        try
        {
            await statusPulseTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task AwaitBackgroundTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<string?> TryHandleStreamingCardDisconnectAsync(
        ActiveSessionExecution activeExecution,
        string latestRenderedContent,
        CancellationToken cancellationToken)
    {
        if (!activeExecution.Handle.AreCardUpdatesStopped)
        {
            return null;
        }

        activeExecution.CancelUpdateWork();
        if (cancellationToken.IsCancellationRequested)
        {
            return latestRenderedContent;
        }

        activeExecution.SetErrorStatus();

        var disconnectedContent = FeishuStreamingErrorFormatter.AppendError(
            latestRenderedContent,
            "飞书流式更新断连，已停止继续推送卡片。");
        activeExecution.SetLatestRenderedContent(disconnectedContent);

        try
        {
            await activeExecution.Handle.FinishAsync(disconnectedContent);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Finishing disconnected Feishu card failed: Session={SessionId}, MessageId={MessageId}",
                activeExecution.SessionId,
                activeExecution.MessageId);
        }

        return disconnectedContent;
    }

    private async Task<FeishuStreamingHandle?> TryCreateReplacementStreamingHandleAsync(
        string chatId,
        string? replyMessageId,
        string latestRenderedContent,
        FeishuStreamingCardChrome chrome,
        FeishuOptions effectiveOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CreateStreamingHandleWithOverflowFallbackAsync(
                chatId,
                replyMessageId,
                latestRenderedContent,
                effectiveOptions,
                chrome,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create replacement Feishu streaming card for chat {ChatId}", chatId);
            return null;
        }
    }

    private async Task<FeishuStreamingHandle> CreateStreamingHandleWithOverflowFallbackAsync(
        string chatId,
        string? replyMessageId,
        string initialContent,
        FeishuOptions effectiveOptions,
        FeishuStreamingCardChrome chrome,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _cardKit.CreateStreamingHandleAsync(
                chatId,
                replyMessageId,
                initialContent,
                effectiveOptions.DefaultCardTitle,
                cancellationToken,
                effectiveOptions,
                chrome);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("code: 200860", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                ex,
                "Streaming card creation overflowed; switching to plain-text fallback stream (chatId={ChatId}, replyMessageId={ReplyMessageId})",
                chatId,
                replyMessageId ?? "<none>");
            return await FeishuTextStreamingFallbackHandleFactory.CreateAsync(
                _cardKit,
                chatId,
                replyMessageId,
                initialContent,
                effectiveOptions,
                cancellationToken);
        }
    }

    private List<ChatSessionEntity> GetChatSessionEntities(string chatKey, string? username)
    {
        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
        var bindingService = scope.ServiceProvider.GetRequiredService<IFeishuUserBindingService>();

        return GetValidFeishuSessions(repo, bindingService, chatKey, username)
            .Where(session => string.Equals(session.FeishuChatKey, chatKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(session => session.UpdatedAt)
            .ToList();
    }

    private static string BuildSessionOptionText(ChatSessionEntity session)
    {
        var workspaceName = ExtractWorkspaceDirectoryName(session.WorkspacePath) ?? "未命名会话";
        var sessionLabel = GetSessionDisplayLabel(session);
        var goalRuntimePrefix = IsGoalRuntimeSession(session) ? "🎯 " : string.Empty;
        return $"{goalRuntimePrefix}{workspaceName} · {sessionLabel} · {GetToolDisplayName(session.ToolId)}";
    }

    private async Task<CompletionPresentation> BuildCompletionPresentationAsync(
        string sessionId,
        string toolId,
        string baseStatusMarkdown,
        string? fallbackWorkspacePath = null)
    {
        ChatSessionEntity? session;
        using (var scope = _serviceProvider.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
            session = await repo.GetByIdAsync(sessionId);
        }

        var goal = await TryGetGoalRuntimeGoalAsync(sessionId, toolId, session);
        return new CompletionPresentation(
            BuildCompletionStatusMarkdown(baseStatusMarkdown, session, goal),
            BuildCompletionNotificationText(sessionId, session, goal, fallbackWorkspacePath));
    }

    private async Task<AppServerGoalSnapshot?> TryGetGoalRuntimeGoalAsync(
        string sessionId,
        string toolId,
        ChatSessionEntity? session)
    {
        if (!IsGoalRuntimeSession(session))
        {
            return null;
        }

        try
        {
            return await _cliExecutor.TryGetGoalRuntimeGoalAsync(sessionId, toolId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取 Goal runtime 完成态失败: Session={SessionId}", sessionId);
            return null;
        }
    }

    private string BuildCompletionNotificationText(
        string sessionId,
        ChatSessionEntity? session,
        AppServerGoalSnapshot? goal,
        string? fallbackWorkspacePath = null)
    {
        var workspaceName = TryGetSessionWorkspaceDirectoryName(sessionId)
            ?? ExtractWorkspaceDirectoryName(session?.WorkspacePath)
            ?? ExtractWorkspaceDirectoryName(fallbackWorkspacePath)
            ?? "当前会话";
        var sessionLabel = GetSessionDisplayLabel(session);

        return BuildSessionStatusMarkdown(
            $"当前会话：{workspaceName}  {sessionLabel}\n{BuildCompletionSummaryLine(session, goal)}",
            session);
    }

    private static string BuildCompletionStatusMarkdown(
        string baseStatusMarkdown,
        ChatSessionEntity? session,
        AppServerGoalSnapshot? goal)
    {
        if (!IsGoalRuntimeSession(session))
        {
            return FeishuStreamingStatusFormatter.WithCompletedState(baseStatusMarkdown);
        }

        return NormalizeGoalRuntimeStatus(goal?.Status) switch
        {
            "active" => GoalRuntimeCompletionStateFormatter.WithGoalContinuingState(baseStatusMarkdown),
            "paused" => GoalRuntimeCompletionStateFormatter.WithGoalPausedState(baseStatusMarkdown),
            "complete" => FeishuStreamingStatusFormatter.WithCompletedState(baseStatusMarkdown),
            _ => GoalRuntimeCompletionStateFormatter.WithTurnFinishedState(baseStatusMarkdown)
        };
    }

    private static string BuildCompletionSummaryLine(ChatSessionEntity? session, AppServerGoalSnapshot? goal)
    {
        if (!IsGoalRuntimeSession(session))
        {
            return "已完成";
        }

        return NormalizeGoalRuntimeStatus(goal?.Status) switch
        {
            "active" => "本轮执行已结束，Goal 仍在运行",
            "paused" => "Goal 已暂停",
            "complete" => "Goal 已完成",
            "budgetlimited" => "Goal 已达到预算上限",
            _ => "本轮执行已结束"
        };
    }

    private static string? NormalizeGoalRuntimeStatus(string? status)
        => status?.Trim().ToLowerInvariant();

    private sealed record CompletionPresentation(string StatusMarkdown, string NotificationText);

    private static string GetSessionDisplayLabel(ChatSessionEntity? session)
    {
        if (!string.IsNullOrWhiteSpace(session?.Title))
        {
            return session.Title.Trim();
        }

        return ShortSessionId(session?.SessionId ?? string.Empty);
    }

    private static string ShortSessionId(string sessionId)
    {
        return string.IsNullOrWhiteSpace(sessionId)
            ? "-"
            : sessionId.Length <= 8
                ? sessionId
                : sessionId[..8];
    }

    private static string GetToolDisplayName(string? toolId)
    {
        return NormalizeToolId(toolId) switch
        {
            "claude-code" => "Claude Code",
            "codex" => "Codex",
            "opencode" => "OpenCode",
            _ => "未设置"
        };
    }

    private static string BuildSessionStatusMarkdown(string baseMarkdown, ChatSessionEntity? session)
    {
        var launchOverrideSummary = BuildSessionLaunchOverrideSummary(session);
        return string.IsNullOrWhiteSpace(launchOverrideSummary)
            ? baseMarkdown
            : $"{baseMarkdown}\n{launchOverrideSummary}";
    }

    private static string? BuildSessionLaunchOverrideSummary(ChatSessionEntity? session)
    {
        if (session == null)
        {
            return null;
        }

        var effectiveToolId = SessionLaunchOverrideHelper.ResolveEffectiveToolId(
            session.ToolId,
            session.CcSwitchSnapshotToolId);
        if (!SessionLaunchOverrideHelper.SupportsLaunchOverrides(effectiveToolId))
        {
            return null;
        }

        var launchOverride = SessionLaunchOverrideHelper.GetEffectiveOverride(
            SessionLaunchOverrideHelper.Deserialize(session.ToolLaunchOverridesJson),
            effectiveToolId,
            session.ToolId,
            session.CcSwitchSnapshotToolId);
        if (launchOverride == null)
        {
            return null;
        }

        var summaryLines = new List<string>();
        if (launchOverride.UseGoalRuntime == true)
        {
            summaryLines.Add("🎯 **Goal持续会话**");
        }

        if (!string.IsNullOrWhiteSpace(launchOverride.Model))
        {
            summaryLines.Add($"🤖 模型: `{launchOverride.Model}`");
        }

        if (string.Equals(effectiveToolId, "codex", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(launchOverride.ReasoningEffort))
        {
            summaryLines.Add($"🧠 思考: `{launchOverride.ReasoningEffort}`");
        }

        return summaryLines.Count == 0 ? null : string.Join("\n", summaryLines);
    }

    private static bool IsGoalRuntimeSession(ChatSessionEntity? session)
    {
        if (session == null)
        {
            return false;
        }

        var effectiveToolId = SessionLaunchOverrideHelper.ResolveEffectiveToolId(
            session.ToolId,
            session.CcSwitchSnapshotToolId);
        if (!SessionLaunchOverrideHelper.SupportsLaunchOverrides(effectiveToolId))
        {
            return false;
        }

        var launchOverride = SessionLaunchOverrideHelper.GetEffectiveOverride(
            SessionLaunchOverrideHelper.Deserialize(session.ToolLaunchOverridesJson),
            effectiveToolId,
            session.ToolId,
            session.CcSwitchSnapshotToolId);
        return launchOverride?.UseGoalRuntime == true;
    }

    private string? TryGetSessionWorkspaceDirectoryName(string sessionId)
    {
        try
        {
            return ExtractWorkspaceDirectoryName(_cliExecutor.GetSessionWorkspacePath(sessionId));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "浠?CLI 缂撳瓨鑾峰彇椋炰功浼氳瘽鐩綍鍚嶅け璐ワ紝灏濊瘯浠撳偍鍥為€€: {SessionId}", sessionId);
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
            var session = repo.GetByIdAsync(sessionId).GetAwaiter().GetResult();
            return ExtractWorkspaceDirectoryName(session?.WorkspacePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "浠庝粨鍌ㄥ洖閫€椋炰功浼氳瘽鐩綍鍚嶅け璐? {SessionId}", sessionId);
            return null;
        }
    }

    private static string? ExtractWorkspaceDirectoryName(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return null;
        }

        var trimmedPath = workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(trimmedPath))
        {
            return null;
        }

        var directoryName = Path.GetFileName(trimmedPath);
        return string.IsNullOrWhiteSpace(directoryName) ? null : directoryName;
    }

    /// <summary>
    /// 鍙戦€佹枃鏈秷鎭?    /// </summary>
    public async Task<string> SendMessageAsync(string chatId, string content, string? username = null, string? appId = null)
    {
        _logger.LogDebug("Sending message to chat {ChatId}: {Content}", chatId, content);
        var effectiveOptions = await ResolveEffectiveOptionsAsync(username, chatId, appId);
        var messageId = await _cardKit.SendTextMessageAsync(chatId, content, optionsOverride: effectiveOptions);
        _logger.LogDebug("Text message sent: MessageId={MessageId}", messageId);
        return messageId;
    }

    /// <summary>
    /// 鍥炲娑堟伅
    /// </summary>
    public async Task<string> ReplyMessageAsync(string messageId, string content, string? username = null, string? appId = null)
    {
        _logger.LogDebug("Replying to message {MessageId}: {Content}", messageId, content);
        var effectiveOptions = await ResolveEffectiveOptionsAsync(username, appId: appId);
        var replyMessageId = await _cardKit.ReplyTextMessageAsync(messageId, content, optionsOverride: effectiveOptions);
        _logger.LogDebug("Text reply sent: MessageId={ReplyMessageId}", replyMessageId);
        return replyMessageId;
    }

    /// <summary>
    /// 鍙戦€佹祦寮忔秷鎭紙鏍稿績鏂规硶锛?
    /// 绔嬪嵆鍒涘缓鍗＄墖骞跺彂閫侊紝杩斿洖娴佸紡鍙ユ焺鐢ㄤ簬鍚庣画鏇存柊
    /// </summary>
    public async Task<FeishuStreamingHandle> SendStreamingMessageAsync(
        string chatId,
        string initialContent,
        string? replyToMessageId = null,
        string? username = null,
        string? appId = null)
    {
        _logger.LogDebug(
            "Creating streaming message for chat {ChatId} (reply to: {ReplyMessageId})",
            chatId,
            replyToMessageId ?? "none");
        var effectiveOptions = await ResolveEffectiveOptionsAsync(username, chatId, appId);

        // 閫氳繃 CardKit 瀹㈡埛绔垱寤烘祦寮忓彞鏌?
        var handle = await _cardKit.CreateStreamingHandleAsync(
            chatId,
            replyToMessageId,
            initialContent,
            effectiveOptions.DefaultCardTitle,
            optionsOverride: effectiveOptions);

        _logger.LogDebug(
            "Streaming handle created: CardId={CardId}, MessageId={MessageId}",
            handle.CardId,
            handle.MessageId);

        return handle;
    }

    private async Task<FeishuOptions> ResolveEffectiveOptionsAsync(string? username = null, string? chatId = null, string? appId = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var userFeishuBotConfigService = scope.ServiceProvider.GetRequiredService<IUserFeishuBotConfigService>();

        if (!string.IsNullOrWhiteSpace(appId))
        {
            var appOptions = await userFeishuBotConfigService.GetEffectiveOptionsByAppIdAsync(appId);
            if (appOptions != null)
            {
                return appOptions;
            }
        }

        var resolvedUsername = username;

        if (string.IsNullOrWhiteSpace(resolvedUsername) && !string.IsNullOrWhiteSpace(chatId))
        {
            resolvedUsername = GetSessionUsername(chatId.ToLowerInvariant());
        }

        if (string.IsNullOrWhiteSpace(resolvedUsername))
        {
            var userContext = scope.ServiceProvider.GetRequiredService<IUserContextService>();
            if (userContext.IsAuthenticated())
            {
                resolvedUsername = userContext.GetCurrentUsername();
            }
        }

        return await userFeishuBotConfigService.GetEffectiveOptionsAsync(resolvedUsername);
    }

    /// <summary>
    /// 澶勭悊 JSONL 杈撳嚭鍧楋紝瑙ｆ瀽骞舵彁鍙栧姪鎵嬫秷鎭?
    /// 浣跨敤缂撳啿鍖哄鐞嗚法 chunk 鐨勪笉瀹屾暣 JSON 琛?
    /// </summary>
    private bool ProcessJsonlChunk(
        string content,
        string sessionId,
        ICliToolAdapter adapter,
        StringBuilder assistantMessageBuilder,
        StringBuilder turnAssistantMessageBuilder,
        StringBuilder finalAnswerMessageBuilder,
        StringBuilder jsonlBuffer,
        FeishuStreamingCardChrome? chrome)
    {
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        // 灏嗘柊鍐呭娣诲姞鍒扮紦鍐插尯
        jsonlBuffer.Append(content);
        var hasStructuredTodoList = false;

        // 澶勭悊缂撳啿鍖轰腑鐨勫畬鏁磋
        while (true)
        {
            var bufferContent = jsonlBuffer.ToString();
            var newlineIndex = bufferContent.IndexOf('\n');

            // 濡傛灉娌℃湁瀹屾暣鐨勮锛岀瓑寰呮洿澶氭暟鎹?
            if (newlineIndex < 0)
            {
                break;
            }

            // 鎻愬彇瀹屾暣鐨勮
            var line = bufferContent.Substring(0, newlineIndex).TrimEnd('\r');
            jsonlBuffer.Remove(0, newlineIndex + 1);

            // 澶勭悊杩欎竴琛?
            hasStructuredTodoList |= ProcessJsonlLine(
                line,
                sessionId,
                adapter,
                assistantMessageBuilder,
                turnAssistantMessageBuilder,
                finalAnswerMessageBuilder,
                chrome);
        }

        return hasStructuredTodoList;
    }

    /// <summary>
    /// 澶勭悊鍗曡 JSONL
    /// </summary>
    private bool ProcessJsonlLine(
        string line,
        string sessionId,
        ICliToolAdapter adapter,
        StringBuilder assistantMessageBuilder,
        StringBuilder turnAssistantMessageBuilder,
        StringBuilder finalAnswerMessageBuilder,
        FeishuStreamingCardChrome? chrome)
    {
        var trimmedLine = line.Trim();
        if (string.IsNullOrWhiteSpace(trimmedLine))
        {
            return false;
        }

        try
        {
            // 浣跨敤閫傞厤鍣ㄨВ鏋愯緭鍑鸿
            var outputEvent = adapter.ParseOutputLine(trimmedLine);
            if (outputEvent == null)
            {
                return false;
            }

            var cliThreadId = adapter.ExtractSessionId(outputEvent);
            if (!string.IsNullOrWhiteSpace(cliThreadId))
            {
                var existingThreadId = _cliExecutor.GetCliThreadId(sessionId);
                if (!string.Equals(existingThreadId, cliThreadId, StringComparison.Ordinal))
                {
                    _cliExecutor.SetCliThreadId(sessionId, cliThreadId);
                }
            }

            UpdateLastToolSummary(chrome, outputEvent);

            // 鎻愬彇鍔╂墜娑堟伅
            var assistantMessage = adapter.ExtractAssistantMessage(outputEvent);
            if (!string.IsNullOrEmpty(assistantMessage))
            {
                assistantMessageBuilder.Append(assistantMessage);
                turnAssistantMessageBuilder.Append(assistantMessage);
                if (string.Equals(outputEvent.AssistantPhase, "final_answer", StringComparison.Ordinal))
                {
                    finalAnswerMessageBuilder.Append(assistantMessage);
                }
            }

            return LowInterruptionContinueHelper.HasStructuredTodoList(outputEvent);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse JSONL line: {Line}", trimmedLine.Length > 100 ? trimmedLine[..100] : trimmedLine);
            return false;
        }
    }

    private static void UpdateLastToolSummary(FeishuStreamingCardChrome? chrome, CliOutputEvent outputEvent)
    {
        if (chrome == null)
        {
            return;
        }

        var markdown = FeishuStreamingToolSummaryFormatter.BuildLatestToolCallMarkdown(outputEvent);
        if (!string.IsNullOrWhiteSpace(markdown))
        {
            chrome.LatestToolCallMarkdown = markdown;
        }
    }

    private sealed class ActiveSessionExecution : IDisposable
    {
        private readonly object _contentLock = new();
        private int _superseded;
        private int _runningFrame;
        private FeishuStreamingHandle _handle;
        private string _latestRenderedContent;

        public ActiveSessionExecution(
            string sessionId,
            string messageId,
            FeishuStreamingHandle handle,
            FeishuStreamingCardChrome chrome,
            string baseStatusMarkdown,
            string initialContent)
        {
            SessionId = sessionId;
            MessageId = messageId;
            _handle = handle;
            Chrome = chrome;
            BaseStatusMarkdown = baseStatusMarkdown;
            InitialContent = initialContent;
            _latestRenderedContent = initialContent;
            ExecutionCancellationTokenSource = new CancellationTokenSource();
            UpdateCancellationTokenSource = new CancellationTokenSource();
            OperationId = Guid.NewGuid();
            PulseGate = new FeishuStreamingStatusPulseGate();
        }

        public Guid OperationId { get; }

        public string SessionId { get; }

        public string MessageId { get; }

        public FeishuStreamingHandle Handle => Volatile.Read(ref _handle);

        public FeishuStreamingCardChrome Chrome { get; }

        public string BaseStatusMarkdown { get; }

        public string InitialContent { get; }

        public CancellationTokenSource ExecutionCancellationTokenSource { get; }

        public CancellationTokenSource UpdateCancellationTokenSource { get; }

        public FeishuStreamingStatusPulseGate PulseGate { get; }

        public bool IsSuperseded => Volatile.Read(ref _superseded) == 1;

        public bool IsPulsePaused() => PulseGate.IsPaused();

        public void ReplaceHandle(FeishuStreamingHandle handle)
        {
            ArgumentNullException.ThrowIfNull(handle);
            Volatile.Write(ref _handle, handle);
        }

        public void SetLatestRenderedContent(string content)
        {
            lock (_contentLock)
            {
                _latestRenderedContent = content;
            }
        }

        public string GetLatestRenderedContent()
        {
            lock (_contentLock)
            {
                return _latestRenderedContent;
            }
        }

        public void AdvanceRunningStatus()
        {
            Chrome.StatusMarkdown = FeishuStreamingStatusFormatter.WithRunningState(
                BaseStatusMarkdown,
                Interlocked.Increment(ref _runningFrame));
        }

        public void PausePulse(TimeSpan duration)
        {
            PulseGate.PauseFor(duration);
        }

        public void PausePulseForOverflowCard(TimeSpan duration)
        {
            if (Chrome.OverflowOptions.Count > 0)
            {
                PausePulse(duration);
            }
        }

        public void SetCompletedStatus()
        {
            Chrome.StatusMarkdown = FeishuStreamingStatusFormatter.WithCompletedState(BaseStatusMarkdown);
        }

        public void SetStoppedStatus()
        {
            Chrome.StatusMarkdown = FeishuStreamingStatusFormatter.WithStoppedState(BaseStatusMarkdown);
        }

        public void SetErrorStatus()
        {
            Chrome.StatusMarkdown = FeishuStreamingStatusFormatter.WithErrorState(BaseStatusMarkdown);
        }

        public void MarkSuperseded()
        {
            Interlocked.Exchange(ref _superseded, 1);
        }

        public void RequestStop()
        {
            MarkSuperseded();
            SetStoppedStatus();
            CancelUpdateWork();

            try
            {
                ExecutionCancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void CancelUpdateWork(bool stopCardUpdates = true)
        {
            try
            {
                UpdateCancellationTokenSource.Cancel();
                if (stopCardUpdates)
                {
                    Handle.StopCardUpdates();
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            ExecutionCancellationTokenSource.Dispose();
            UpdateCancellationTokenSource.Dispose();
        }
    }
}

