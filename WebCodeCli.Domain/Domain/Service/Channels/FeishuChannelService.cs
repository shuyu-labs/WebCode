using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    private readonly FeishuOptions _options;
    private readonly ILogger<FeishuChannelService> _logger;
    private readonly IFeishuCardKitClient _cardKit;
    private readonly IServiceProvider _serviceProvider;
    private FeishuMessageHandler? _messageHandler;
    private readonly ICliExecutorService _cliExecutor;
    private readonly IChatSessionService _chatSessionService;
    private readonly IReplyTtsOrchestrator? _replyTtsOrchestrator;

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
        IReplyTtsOrchestrator? replyTtsOrchestrator = null)
    {
        _options = options.Value;
        _logger = logger;
        _cardKit = cardKit;
        _serviceProvider = serviceProvider;
        _cliExecutor = cliExecutor;
        _chatSessionService = chatSessionService;
        _replyTtsOrchestrator = replyTtsOrchestrator;
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
                "馃敟 [FeishuChannel] 鏀跺埌娑堟伅浜嬩欢: MessageId={MessageId}, ChatId={ChatId}, Content={Content}",
                message.MessageId,
                message.ChatId,
                message.Content);

            string sessionId;
            try
            {
                // 鑾峰彇褰撳墠浼氳瘽锛堟棤浼氳瘽鏃舵姏鍑哄紓甯革級
                sessionId = GetCurrentSession(message);
            }
            catch (InvalidOperationException ex)
            {
                // 娌℃湁浼氳瘽鏃舵彁绀虹敤鎴?
                await ReplyMessageAsync(message.MessageId, $"鈿狅笍 {ex.Message}", message.SenderName, message.AppId);
                return;
            }

            var toolId = ResolveToolId(message.ChatId, message.SenderName);
            var normalizedPrompt = FeishuPromptNormalizer.Normalize(message.Content);

            // 娣诲姞鐢ㄦ埛娑堟伅鍒颁細璇?
            _chatSessionService.AddMessage(sessionId, new ChatMessage
            {
                Role = "user",
                Content = normalizedPrompt,
                CliToolId = toolId,
                CreatedAt = DateTime.UtcNow
            });

            var (streamingChrome, baseStatusMarkdown) = await BuildStreamingCardChromeAsync(message.ChatId, sessionId, message.SenderName);

            // Create the streaming reply and show the thinking state immediately.
            var effectiveOptions = await ResolveEffectiveOptionsAsync(message.SenderName, message.ChatId, message.AppId);
            var handle = await _cardKit.CreateStreamingHandleAsync(
                message.ChatId,
                null,
                effectiveOptions.ThinkingMessage,
                effectiveOptions.DefaultCardTitle,
                optionsOverride: effectiveOptions,
                chrome: streamingChrome);

            _logger.LogInformation(
                "馃敟 [FeishuChannel] 娴佸紡鍙ユ焺宸插垱寤? CardId={CardId}",
                handle.CardId);

            var activeExecution = new ActiveSessionExecution(
                sessionId,
                message.MessageId,
                handle,
                streamingChrome,
                baseStatusMarkdown,
                effectiveOptions.ThinkingMessage);
            activeExecution.PausePulseForOverflowCard(StreamingStatusPulseQuietWindow);
            var statusPulseTask = RunStreamingStatusPulseAsync(activeExecution);
            var previousExecution = RegisterActiveExecution(sessionId, activeExecution);
            if (previousExecution != null)
            {
                await SupersedeExecutionAsync(previousExecution, message.MessageId);
            }

            var cliPrompt = normalizedPrompt;

            try
            {
                // Execute the CLI tool and stream updates into the card.
                await ExecuteCliAndStreamAsync(
                    handle,
                    sessionId,
                    message.ChatId,
                    toolId,
                    cliPrompt,
                    message.MessageId,
                    effectiveOptions.ThinkingMessage,
                    activeExecution,
                    message.SenderName,
                    message.AppId,
                    activeExecution.CancellationTokenSource.Token);
            }
            finally
            {
                activeExecution.CancelPulse();
                await AwaitStatusPulseAsync(statusPulseTask);
                UnregisterActiveExecution(sessionId, activeExecution);
                activeExecution.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "馃敟 [FeishuChannel] 澶勭悊娑堟伅澶辫触: {MessageId}",
                message.MessageId);
        }
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
                _logger.LogDebug("澶勭悊鏂颁簨浠? EventId={EventId}", message.EventId);
            }
            else
            {
                _logger.LogInformation("璺宠繃閲嶅浜嬩欢: EventId={EventId}", message.EventId);
                return;
            }
        }

        _logger.LogInformation("馃敟 [FeishuChannel] HandleIncomingMessageAsync 琚皟鐢?");
        await OnMessageReceivedAsync(message);
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
        execution.CancelPulse();
        execution.SetStoppedStatus();

        try
        {
            execution.CancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await execution.Handle.FinishAsync(SupersededExecutionMessage);
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
        _logger.LogInformation("馃攳 [浼氳瘽鍖归厤] 娑堟伅ChatId={ChatId}, ChatKey={ChatKey}, User={User}",
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
        FeishuStreamingHandle handle,
        string sessionId,
        string chatId,
        string toolId,
        string userPrompt,
        string messageId,
        string thinkingMessage,
        ActiveSessionExecution activeExecution,
        string? username = null,
        string? appId = null,
        CancellationToken cancellationToken = default)
    {
        var outputBuilder = new StringBuilder();
        var assistantMessageBuilder = new StringBuilder();
        var jsonlBuffer = new StringBuilder(); // JSONL 缂撳啿鍖猴紝澶勭悊涓嶅畬鏁寸殑琛?
        var hasStructuredTodoList = false;
        var latestRenderedContent = thinkingMessage;
        var resolvedToolId = NormalizeToolId(toolId) ?? ResolveDefaultToolId();
        var tool = _cliExecutor.GetTool(resolvedToolId);

        if (tool == null)
        {
            activeExecution.CancelPulse();
            activeExecution.SetErrorStatus();
            await handle.FinishAsync(FeishuStreamingErrorFormatter.AppendError(
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

        TryAttachSuperpowersQuickActions(activeExecution.Chrome, sessionId, tool.Id);
        try
        {
            // 鎵ц CLI 宸ュ叿骞舵祦寮忓鐞嗚緭鍑?
            await foreach (var chunk in _cliExecutor.ExecuteStreamAsync(sessionId, tool.Id, userPrompt, cancellationToken))
            {
                if (chunk.IsError)
                {
                    if (activeExecution.IsSuperseded && cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation(
                            "CLI execution superseded by newer message: Session={SessionId}, MessageId={MessageId}",
                            sessionId,
                            messageId);
                        return;
                    }

                    _logger.LogError(
                        "CLI execution error: {Error}",
                        chunk.ErrorMessage ?? "Unknown error");
                    activeExecution.CancelPulse();
                    activeExecution.SetErrorStatus();
                    await handle.FinishAsync(FeishuStreamingErrorFormatter.AppendError(
                        latestRenderedContent,
                        chunk.ErrorMessage ?? "执行失败"));
                    return;
                }

                // 绱Н鍘熷杈撳嚭鍐呭
                outputBuilder.Append(chunk.Content);

                // 濡傛灉浣跨敤閫傞厤鍣紝瑙ｆ瀽 JSONL 骞舵彁鍙栧姪鎵嬫秷鎭?
                string displayContent;
                if (useAdapter)
                {
                    // 瑙ｆ瀽 JSONL 琛屽苟鎻愬彇鍔╂墜娑堟伅锛堜娇鐢ㄧ紦鍐插尯澶勭悊涓嶅畬鏁寸殑琛岋級
                    hasStructuredTodoList |= ProcessJsonlChunk(chunk.Content, sessionId, adapter!, assistantMessageBuilder, jsonlBuffer);
                    displayContent = assistantMessageBuilder.ToString();

                    // 濡傛灉娌℃湁鍔╂墜娑堟伅锛屾樉绀?鎬濊€冧腑"
                    if (string.IsNullOrWhiteSpace(displayContent))
                    {
                        displayContent = thinkingMessage;
                    }
                }
                else
                {
                    // 涓嶄娇鐢ㄩ€傞厤鍣ㄦ椂锛岃繃婊ょ郴缁熸秷鎭?
                    displayContent = FormatMarkdownOutput(outputBuilder.ToString());
                }

                // 娴佸紡鏇存柊鍗＄墖锛堣妭娴佸湪 handle 鍐呴儴澶勭悊锛?
                activeExecution.SetLatestRenderedContent(displayContent);
                latestRenderedContent = displayContent;
                activeExecution.PausePulseForOverflowCard(StreamingStatusPulseQuietWindow);
                await handle.UpdateAsync(displayContent);

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
                hasStructuredTodoList |= ProcessJsonlLine(jsonlBuffer.ToString(), sessionId, adapter!, assistantMessageBuilder);
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
            }
            else
            {
                // 涓嶄娇鐢ㄩ€傞厤鍣ㄦ椂锛岃繃婊ょ郴缁熸秷鎭?
                finalOutput = FormatMarkdownOutput(outputBuilder.ToString());
            }

            activeExecution.SetLatestRenderedContent(finalOutput);
            activeExecution.CancelPulse();
            activeExecution.SetCompletedStatus();
            SetTopChipGroupsEnabled(activeExecution.Chrome, true);
            TryAttachSuperpowersQuickActions(activeExecution.Chrome, sessionId, tool.Id);
            // NOTE: Keep the explicit completion text notification for Feishu users.
            try
            {
                await ReplyMessageAsync(
                    messageId,
                    BuildCompletionNotificationText(sessionId),
                    username,
                    appId);
            }
            catch (Exception notificationEx)
            {
                _logger.LogWarning(notificationEx, "鍙戦€佹祦寮忓畬鎴愭枃鏈€氱煡澶辫触: MessageId={MessageId}", messageId);
            }
            await handle.FinishAsync(finalOutput);

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

            if (_replyTtsOrchestrator != null)
            {
                try
                {
                    await _replyTtsOrchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyTtsRequest
                    {
                        ChatId = chatId,
                        Username = username,
                        AppId = appId,
                        Output = finalOutput
                    });
                }
                catch (Exception ttsQueueEx)
                {
                    _logger.LogWarning(
                        ttsQueueEx,
                        "Failed to queue reply TTS after Feishu completion: Session={SessionId}, MessageId={MessageId}",
                        sessionId,
                        messageId);
                }
            }

            _logger.LogInformation(
                "CLI execution completed for message: {MessageId}, session: {SessionId}",
                messageId,
                sessionId);
        }
        catch (OperationCanceledException) when (activeExecution.IsSuperseded && cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "CLI execution cancelled because a newer message took over: Session={SessionId}, MessageId={MessageId}",
                sessionId,
                messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CLI execution failed for message: {MessageId}", messageId);
            activeExecution.CancelPulse();
            activeExecution.SetErrorStatus();
            await handle.FinishAsync(FeishuStreamingErrorFormatter.AppendError(
                latestRenderedContent,
                ex.Message));
        }
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

    private static string? ExtractFallbackOutput(string fullOutput, ICliToolAdapter adapter)
    {
        if (string.IsNullOrWhiteSpace(fullOutput))
        {
            return null;
        }

        string? lastUsefulContent = null;
        var lines = fullOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var outputEvent = adapter.ParseOutputLine(line);
            if (outputEvent == null || string.IsNullOrWhiteSpace(outputEvent.Content))
            {
                continue;
            }

            if (outputEvent.EventType is "result" or "error" or "raw" or "assistant" or "assistant:message" or "stream_event")
            {
                lastUsefulContent = outputEvent.Content.Trim();
            }
        }

        return lastUsefulContent;
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
        string toolId)
    {
        string? chatKey = null;
        using (var scope = _serviceProvider.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
            var session = repo.GetByIdAsync(sessionId).GetAwaiter().GetResult();
            chatKey = session?.FeishuChatKey;
        }

        if (string.IsNullOrWhiteSpace(chatKey))
        {
            return;
        }

        var normalizedToolId = NormalizeToolId(toolId) ?? toolId;
        var capabilityState = ResolveSuperpowersCapabilityState(sessionId, normalizedToolId);
        chrome.BottomPrompt = SuperpowersQuickActionCardHelper.CreateBottomPrompt(
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
        chrome.BottomActions.AddRange(SuperpowersQuickActionCardHelper.CreateBottomActions(
            sessionId,
            chatKey,
            normalizedToolId,
            showPlanActions: ShouldShowSuperpowersPlanActions(sessionId),
            capabilityState));
        chrome.StatusMarkdown = SuperpowersQuickActionCardHelper.MergeCapabilityStatusMarkdown(
            chrome.StatusMarkdown,
            capabilityState);
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

    private async Task RunStreamingStatusPulseAsync(ActiveSessionExecution execution)
    {
        try
        {
            while (!execution.CancellationTokenSource.IsCancellationRequested)
            {
                await Task.Delay(StreamingStatusPulseIntervalMs, execution.CancellationTokenSource.Token);

                if (execution.CancellationTokenSource.IsCancellationRequested || execution.IsSuperseded)
                {
                    break;
                }

                if (execution.IsPulsePaused())
                {
                    continue;
                }

                execution.AdvanceRunningStatus();
                await execution.Handle.UpdateAsync(execution.GetLatestRenderedContent());
            }
        }
        catch (OperationCanceledException)
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
        return $"{workspaceName} · {sessionLabel} · {GetToolDisplayName(session.ToolId)}";
    }

    private string BuildCompletionNotificationText(string sessionId, string? fallbackWorkspacePath = null)
    {
        ChatSessionEntity? session;
        using (var scope = _serviceProvider.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
            session = repo.GetByIdAsync(sessionId).GetAwaiter().GetResult();
        }

        var workspaceName = TryGetSessionWorkspaceDirectoryName(sessionId)
            ?? ExtractWorkspaceDirectoryName(session?.WorkspacePath)
            ?? ExtractWorkspaceDirectoryName(fallbackWorkspacePath)
            ?? "当前会话";
        var sessionLabel = GetSessionDisplayLabel(session);

        return BuildSessionStatusMarkdown(
            $"当前会话：{workspaceName}  {sessionLabel}\n已完成",
            session);
    }

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
    private bool ProcessJsonlChunk(string content, string sessionId, ICliToolAdapter adapter, StringBuilder assistantMessageBuilder, StringBuilder jsonlBuffer)
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
            hasStructuredTodoList |= ProcessJsonlLine(line, sessionId, adapter, assistantMessageBuilder);
        }

        return hasStructuredTodoList;
    }

    /// <summary>
    /// 澶勭悊鍗曡 JSONL
    /// </summary>
    private bool ProcessJsonlLine(string line, string sessionId, ICliToolAdapter adapter, StringBuilder assistantMessageBuilder)
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

            // 鎻愬彇鍔╂墜娑堟伅
            var assistantMessage = adapter.ExtractAssistantMessage(outputEvent);
            if (!string.IsNullOrEmpty(assistantMessage))
            {
                assistantMessageBuilder.Append(assistantMessage);
            }

            return LowInterruptionContinueHelper.HasStructuredTodoList(outputEvent);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse JSONL line: {Line}", trimmedLine.Length > 100 ? trimmedLine[..100] : trimmedLine);
            return false;
        }
    }

    private sealed class ActiveSessionExecution : IDisposable
    {
        private readonly object _contentLock = new();
        private int _superseded;
        private int _runningFrame;
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
            Handle = handle;
            Chrome = chrome;
            BaseStatusMarkdown = baseStatusMarkdown;
            _latestRenderedContent = initialContent;
            CancellationTokenSource = new CancellationTokenSource();
            OperationId = Guid.NewGuid();
            PulseGate = new FeishuStreamingStatusPulseGate();
        }

        public Guid OperationId { get; }

        public string SessionId { get; }

        public string MessageId { get; }

        public FeishuStreamingHandle Handle { get; }

        public FeishuStreamingCardChrome Chrome { get; }

        public string BaseStatusMarkdown { get; }

        public CancellationTokenSource CancellationTokenSource { get; }

        public FeishuStreamingStatusPulseGate PulseGate { get; }

        public bool IsSuperseded => Volatile.Read(ref _superseded) == 1;

        public bool IsPulsePaused() => PulseGate.IsPaused();

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

        public void CancelPulse()
        {
            try
            {
                CancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            CancellationTokenSource.Dispose();
        }
    }
}

