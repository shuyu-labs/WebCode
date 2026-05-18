using System.Collections.Concurrent;
using System.Text.Json;
using FeishuNetSdk.Im.Events;
using FeishuNetSdk.Im.Dtos;
using FeishuNetSdk.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Repositories.Base.ChatSession;

namespace WebCodeCli.Domain.Domain.Service.Channels;

/// <summary>
/// 飞书消息事件处理器
/// 处理 im.message.receive_v1 事件
/// 实现 FeishuNetSdk 的 IEventHandler 接口以自动接收事件
/// </summary>
public class FeishuMessageHandler : IEventHandler<EventV2Dto<ImMessageReceiveV1EventBodyDto>, ImMessageReceiveV1EventBodyDto>, IDisposable
{
    private static readonly FeishuIncomingAttachmentParser AttachmentParser = new();
    private readonly FeishuOptions _options;
    private readonly ILogger<FeishuMessageHandler> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly FeishuCommandService _commandService;
    private readonly FeishuHelpCardBuilder _cardBuilder;
    private readonly IFeishuCardKitClient _cardKit;
    private readonly ICliExecutorService _cliExecutor;
    private readonly IFeishuChannelService _feishuChannel;
    private readonly FeishuCardActionService _cardActionService;

    /// <summary>
    /// 静态消息收到事件（解决 SDK 创建不同实例的问题）
    /// </summary>
    public static event Func<FeishuIncomingMessage, Task>? MessageReceived;

    /// <summary>
    /// 已处理的消息 ID（去重）
    /// Key: MessageId, Value: 处理时间
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTime> ProcessedMessages = new();

    /// <summary>
    /// 定时清理器（修复内存泄漏问题）
    /// </summary>
    private readonly System.Threading.Timer _cleanupTimer;
    private bool _disposed = false;

    public FeishuMessageHandler(
        IOptions<FeishuOptions> options,
        ILogger<FeishuMessageHandler> logger,
        IServiceProvider serviceProvider,
        FeishuCommandService commandService,
        FeishuHelpCardBuilder cardBuilder,
        IFeishuCardKitClient cardKit,
        ICliExecutorService cliExecutor,
        IFeishuChannelService feishuChannel,
        FeishuCardActionService cardActionService)
    {
        _options = options.Value;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _commandService = commandService;
        _cardBuilder = cardBuilder;
        _cardKit = cardKit;
        _cliExecutor = cliExecutor;
        _feishuChannel = feishuChannel;
        _cardActionService = cardActionService;

        // 启动定时清理器（每 5 分钟清理一次过期消息）
        _cleanupTimer = new System.Threading.Timer(
            _ => CleanupOldMessages(),
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// 实现 IEventHandler 接口 - SDK 自动调用此方法
    /// </summary>
    /// <param name="input">飞书事件 DTO</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task ExecuteAsync(EventV2Dto<ImMessageReceiveV1EventBodyDto> input, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("🔥 [Feishu] 收到事件: EventId={EventId}, EventType={EventType}", input.EventId, input.Header?.EventType);
        try
        {
            await HandleMessageReceiveAsync(input);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 [Feishu] 处理消息异常");
        }
    }

    /// <summary>
    /// 处理收到的消息事件（内部方法）
    /// </summary>
    /// <param name="input">飞书事件 DTO</param>
    private async Task HandleMessageReceiveAsync(EventV2Dto<ImMessageReceiveV1EventBodyDto> input)
    {
        var appId = input.Header?.AppId;
        var eventDto = input.Event;
        var message = eventDto.Message;
        _logger.LogInformation("🔥 [Feishu] 消息详情: ChatId={ChatId}, ChatType={ChatType}, MessageType={MessageType}, Content={Content}",
            message.ChatId, message.ChatType, message.MessageType, message.Content);

        // 去重检查 - 使用 MessageId 而非 EventId，原子操作避免并发重复处理
        if (!ProcessedMessages.TryAdd(message.MessageId, DateTime.UtcNow))
        {
            _logger.LogDebug("Duplicate message ignored: {MessageId}", message.MessageId);
            return;
        }

        // 清理过期的消息记录（超过 15 分钟的记录）
        CleanupOldMessages();

        // 解析消息内容
        var content = ParseMessageContent(message.Content, message.MessageType);
        var attachments = AttachmentParser.Parse(message.MessageType, message.Content);

        // 获取发送者信息（优先使用 union_id，便于跨应用稳定绑定）
        var senderId = eventDto.Sender.SenderId.UnionId
            ?? eventDto.Sender.SenderId.OpenId
            ?? eventDto.Sender.SenderId.UserId
            ?? string.Empty;

        // 检测是否 @ 了机器人
        var isBotMentioned = await CheckBotMentionAsync(message, appId);

        // 群聊过滤：只有 @ 机器人才处理
        if (message.ChatType == "group" && !isBotMentioned)
        {
            _logger.LogDebug("Group message without @mention ignored: {ChatId}", message.ChatId);
            return;
        }

        // 移除 @ 提及占位符（Feishu 使用 @_user_1 风格的占位符）
        if (isBotMentioned && message.Mentions?.Length > 0)
        {
            foreach (var mention in message.Mentions)
            {
                var pattern = System.Text.RegularExpressions.Regex.Escape(mention.Key);
                content = System.Text.RegularExpressions.Regex.Replace(content, pattern, "").Trim();
            }
        }

        var trimmedContent = content.Trim();
        using var bindingScope = _serviceProvider.CreateScope();
        var bindingService = bindingScope.ServiceProvider.GetRequiredService<IFeishuUserBindingService>();

        if (TryParseBindCommand(trimmedContent, out var webUsername))
        {
            await HandleBindCommandAsync(message.ChatId, message.MessageId, senderId, webUsername, appId);
            return;
        }

        if (IsUnbindCommand(trimmedContent))
        {
            await HandleUnbindCommandAsync(message.MessageId, senderId, appId);
            return;
        }

        var boundWebUsername = await bindingService.GetBoundWebUsernameAsync(senderId);
        if (string.IsNullOrWhiteSpace(boundWebUsername))
        {
            var cardJson = _cardBuilder.BuildBindWebUserCard((await bindingService.GetBindableWebUsernamesAsync(appId)).ToArray());
            var effectiveOptions = await ResolveEffectiveOptionsAsync(null, appId);
            await _cardKit.ReplyRawCardAsync(message.MessageId, cardJson, optionsOverride: effectiveOptions);
            return;
        }

        // 检测 feishuhelp 命令（飞书会自动去掉斜杠，所以不检测斜杠）
        if (!string.IsNullOrEmpty(trimmedContent) &&
            (trimmedContent.StartsWith("feishuhelp", StringComparison.OrdinalIgnoreCase) ||
             trimmedContent.StartsWith("/feishuhelp", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("🔥 [Feishu] 检测到 feishuhelp 命令!");
            var keyword = trimmedContent.StartsWith("/", StringComparison.Ordinal)
                ? trimmedContent.Substring("/feishuhelp".Length).Trim()
                : trimmedContent.Substring("feishuhelp".Length).Trim();
            EnqueueMessageWork(
                "feishuhelp",
                message.MessageId,
                () => HandleFeishuHelpAsync(message.ChatId, message.MessageId, keyword, boundWebUsername, appId));
            return;
        }

        if (!string.IsNullOrEmpty(trimmedContent) &&
            (trimmedContent.StartsWith("feishusessions", StringComparison.OrdinalIgnoreCase) ||
             trimmedContent.StartsWith("/feishusessions", StringComparison.OrdinalIgnoreCase) ||
             trimmedContent.StartsWith("feishusession", StringComparison.OrdinalIgnoreCase) ||
             trimmedContent.StartsWith("/feishusession", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("🔥 [Feishu] 检测到 feishusessions 命令!");
            EnqueueMessageWork(
                "feishusessions",
                message.MessageId,
                () => HandleSessionsCommandAsync(message.ChatId, message.MessageId, boundWebUsername, appId));
            return;
        }

        if (!string.IsNullOrEmpty(trimmedContent) &&
            (trimmedContent.StartsWith("feishuprojects", StringComparison.OrdinalIgnoreCase) ||
             trimmedContent.StartsWith("/feishuprojects", StringComparison.OrdinalIgnoreCase) ||
             trimmedContent.StartsWith("feishuproject", StringComparison.OrdinalIgnoreCase) ||
             trimmedContent.StartsWith("/feishuproject", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("🔥 [Feishu] 检测到 feishuprojects 命令!");
            EnqueueMessageWork(
                "feishuprojects",
                message.MessageId,
                () => HandleProjectsCommandAsync(message.ChatId, message.MessageId, senderId, boundWebUsername, appId));
            return;
        }

        var incomingMessage = new FeishuIncomingMessage
        {
            MessageId = message.MessageId,
            ChatId = message.ChatId,
            ChatType = message.ChatType,
            AppId = appId,
            Content = content,
            RawContent = message.Content,
            MessageType = message.MessageType,
            Attachments = attachments.ToList(),
            SenderId = senderId,
            SenderName = boundWebUsername,
            ChatName = message.ChatType == "p2p" ? boundWebUsername : message.ChatId,
            IsBotMentioned = isBotMentioned,
            EventId = message.MessageId
        };

        _logger.LogInformation(
            "🔥 [Feishu] 消息处理完成: [{ChatType}] {ChatId} from {SenderId}: {Content}",
            message.ChatType, message.ChatId, senderId,
            content.Length > 50 ? $"{content[..50]}..." : content);

        // 转发消息给频道服务处理
        try
        {
            _logger.LogInformation("🔥 [Feishu] 转发消息给 FeishuChannelService 处理");
            await _feishuChannel.HandleIncomingMessageAsync(incomingMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔥 [Feishu] 消息转发处理失败");
        }
    }

    /// <summary>
    /// 解析消息内容
    /// </summary>
    /// <param name="rawContent">原始 JSON 内容</param>
    /// <param name="msgType">消息类型</param>
    /// <returns>解析后的文本内容</returns>
    private static string ParseMessageContent(string rawContent, string msgType)
    {
        if (string.IsNullOrEmpty(rawContent))
            return string.Empty;

        try
        {
            var json = JsonDocument.Parse(rawContent);
            var root = json.RootElement;

            return msgType switch
            {
                "text" => root.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "",
                "post" => FeishuPromptNormalizer.Normalize(rawContent),
                _ => rawContent
            };
        }
        catch
        {
            // JSON 解析失败，返回原始内容
            return rawContent;
        }
    }

    /// <summary>
    /// 检查是否 @ 了机器人
    /// </summary>
    /// <param name="message">消息对象</param>
    /// <returns>是否 @ 了机器人</returns>
    private async Task<bool> CheckBotMentionAsync(ImMessageReceiveV1EventBodyDto.EventMessage message, string? appId)
    {
        if (message.ChatType == "p2p")
        {
            return true;
        }

        if (message.Mentions == null || message.Mentions.Length == 0)
        {
            return false;
        }

        var effectiveOptions = await ResolveEffectiveOptionsAsync(null, appId);

        foreach (var mention in message.Mentions)
        {
            if (mention.Key == "@_all")
            {
                return true;
            }

            if (mention.Id?.OpenId == _options.AppId)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(effectiveOptions.DefaultCardTitle)
                && string.Equals(mention.Name, effectiveOptions.DefaultCardTitle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 清理过期的消息记录
    /// 防止内存无限增长
    /// </summary>
    private void CleanupOldMessages()
    {
        var expireTime = DateTime.UtcNow.AddMinutes(-15);
        var keysToRemove = ProcessedMessages
            .Where(kvp => kvp.Value < expireTime)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            ProcessedMessages.TryRemove(key, out _);
        }

        // 额外保护：如果记录数超过 500，删除最旧的记录
        if (ProcessedMessages.Count > 500)
        {
            var oldestKeys = ProcessedMessages
                .OrderBy(kvp => kvp.Value)
                .Take(100)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in oldestKeys)
            {
                ProcessedMessages.TryRemove(key, out _);
            }
        }
    }

    private void EnqueueMessageWork(string operationName, string messageId, Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [Feishu] 后台处理失败: Operation={Operation}, MessageId={MessageId}", operationName, messageId);
            }
        });

        _logger.LogInformation("🔥 [Feishu] 已转后台处理: Operation={Operation}, MessageId={MessageId}", operationName, messageId);
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _cleanupTimer?.Dispose();
            _disposed = true;
        }
    }

    private string GetSessionWorkspaceDisplay(string sessionId)
    {
        try
        {
            return _cliExecutor.GetSessionWorkspacePath(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Feishu] 获取会话工作区失败，降级显示: {SessionId}", sessionId);
            return "(工作区未初始化或已失效)";
        }
    }

    private static bool TryParseBindCommand(string content, out string username)
    {
        username = string.Empty;
        var prefixes = new[] { "绑定 ", "/绑定 ", "bind ", "/bind " };
        foreach (var prefix in prefixes)
        {
            if (content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                username = content[prefix.Length..].Trim();
                return !string.IsNullOrWhiteSpace(username);
            }
        }

        return false;
    }

    private static bool IsUnbindCommand(string content)
    {
        return string.Equals(content, "解绑", StringComparison.OrdinalIgnoreCase)
            || string.Equals(content, "/解绑", StringComparison.OrdinalIgnoreCase)
            || string.Equals(content, "unbind", StringComparison.OrdinalIgnoreCase)
            || string.Equals(content, "/unbind", StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandleBindCommandAsync(string chatId, string replyToMessageId, string feishuUserId, string webUsername, string? appId)
    {
        using var scope = _serviceProvider.CreateScope();
        var bindingService = scope.ServiceProvider.GetRequiredService<IFeishuUserBindingService>();
        var chatSessionRepository = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();

        var result = await bindingService.BindAsync(feishuUserId, webUsername, appId);
        if (!result.Success)
        {
            await _feishuChannel.ReplyMessageAsync(replyToMessageId, $"❌ 绑定失败：{result.ErrorMessage}", result.WebUsername, appId);
            return;
        }

        var chatKey = chatId.ToLowerInvariant();
        var legacySessions = await chatSessionRepository.GetByFeishuChatKeyAsync(chatKey);
        foreach (var session in legacySessions.Where(x => !string.Equals(x.Username, result.WebUsername, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            await chatSessionRepository.DeleteAsync(session);
        }

        await _feishuChannel.ReplyMessageAsync(
            replyToMessageId,
            $"✅ 已绑定 Web 用户：{result.WebUsername}\n现在你发送的消息、会话、目录和项目都将与 Web 端共享。",
            result.WebUsername,
            appId);
    }

    private async Task HandleUnbindCommandAsync(string replyToMessageId, string feishuUserId, string? appId)
    {
        using var scope = _serviceProvider.CreateScope();
        var bindingService = scope.ServiceProvider.GetRequiredService<IFeishuUserBindingService>();
        var boundUsername = await bindingService.GetBoundWebUsernameAsync(feishuUserId);
        var success = await bindingService.UnbindAsync(feishuUserId);
        await _feishuChannel.ReplyMessageAsync(
            replyToMessageId,
            success ? "✅ 已解绑 Web 用户。" : "⚠️ 当前未绑定 Web 用户。",
            boundUsername,
            appId);
    }

    /// <summary>
    /// 处理 /feishuhelp 命令
    /// </summary>
    private async Task HandleFeishuHelpAsync(string chatId, string replyToMessageId, string keyword, string? webUsername, string? appId)
    {
        _logger.LogInformation("🔥 [FeishuHelp] 收到帮助请求: ChatId={ChatId}, ReplyToMessageId={ReplyToMessageId}, Keyword={Keyword}",
            chatId, replyToMessageId, keyword);

        try
        {
            var toolId = _feishuChannel.ResolveToolId(chatId);
            _logger.LogInformation("🔥 [FeishuHelp] 当前聊天解析工具: ToolId={ToolId}", toolId);

            // 自动刷新命令列表，确保获取最新的技能和插件
            _logger.LogInformation("🔥 [FeishuHelp] 开始刷新命令列表...");
            await _commandService.RefreshCommandsAsync(toolId);
            _logger.LogInformation("✅ [FeishuHelp] 命令列表刷新完成");

            List<FeishuCommandCategory> categories;
            ElementsCardV2Dto card;

            _logger.LogInformation("🔥 [FeishuHelp] 开始获取命令列表...");
            if (string.IsNullOrEmpty(keyword))
            {
                categories = await _commandService.GetCategorizedCommandsAsync(toolId);
                _logger.LogInformation("🔥 [FeishuHelp] 获取到 {Count} 个分组", categories.Count);
                var showGoalQuickActionButtons = ResolveShowGoalQuickActionButtons(chatId, webUsername, toolId);
                var showSuperpowersQuickActions = ResolveShowSuperpowersQuickActions(chatId, webUsername, toolId);
                card = _cardBuilder.BuildCommandListCardV2(
                    categories,
                    replyTtsEnabled: await GetReplyTtsEnabledAsync(chatId, webUsername),
                    showGoalQuickActionButtons: showGoalQuickActionButtons,
                    showSuperpowersQuickActions: showSuperpowersQuickActions);
            }
            else
            {
                categories = await _commandService.FilterCommandsAsync(keyword, toolId);
                _logger.LogInformation("🔥 [FeishuHelp] 过滤后获取到 {Count} 个分组", categories.Count);
                var showGoalQuickActionButtons = ResolveShowGoalQuickActionButtons(chatId, webUsername, toolId);
                var showSuperpowersQuickActions = ResolveShowSuperpowersQuickActions(chatId, webUsername, toolId);
                card = _cardBuilder.BuildFilteredCardV2(
                    categories,
                    keyword,
                    showGoalQuickActionButtons,
                    showSuperpowersQuickActions);
            }

            _logger.LogDebug("🔥 [FeishuHelp] 帮助卡片DTO内容: {Card}", JsonSerializer.Serialize(card));
            _logger.LogInformation("🔥 [FeishuHelp] 开始调用 ReplyElementsCardAsync...");
            var effectiveOptions = await ResolveEffectiveOptionsAsync(webUsername, appId);
            var messageId = await _cardKit.ReplyElementsCardAsync(replyToMessageId, card, optionsOverride: effectiveOptions);
            _logger.LogInformation("✅ [FeishuHelp] 帮助卡片已发送, MessageId={MessageId}", messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [FeishuHelp] 发送帮助卡片失败");

            try
            {
                _logger.LogInformation("🔥 [FeishuHelp] 尝试发送降级提示...");
                await _feishuChannel.ReplyMessageAsync(replyToMessageId, "❌ 帮助卡片发送失败，请稍后重试。", webUsername, appId);
            }
            catch (Exception innerEx)
            {
                _logger.LogError(innerEx, "❌ [FeishuHelp] 发送降级提示也失败了");
            }
        }
    }

    /// <summary>
    /// 处理/sessions命令，返回会话管理卡片
    /// </summary>
    private async Task HandleSessionsCommandAsync(string chatId, string replyToMessageId, string webUsername, string? appId)
    {
        try
        {
            var card = await _cardActionService.BuildSessionManagerCardAsync(chatId, null, webUsername);
            var effectiveOptions = await ResolveEffectiveOptionsAsync(webUsername, appId);
            var messageId = await _cardKit.ReplyElementsCardAsync(replyToMessageId, card, optionsOverride: effectiveOptions);
            _logger.LogInformation("✅ [Feishu] 会话管理卡片已发送, MessageId={MessageId}", messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理sessions命令失败");
            await _feishuChannel.ReplyMessageAsync(replyToMessageId, "❌ 会话管理功能暂时不可用，请稍后重试。", webUsername, appId);
        }
    }

    /// <summary>
    /// 处理 /feishuprojects 命令，返回项目管理卡片
    /// </summary>
    private async Task HandleProjectsCommandAsync(string chatId, string replyToMessageId, string operatorUserId, string? webUsername, string? appId)
    {
        try
        {
            var card = await _cardActionService.BuildProjectManagerCardAsync(chatId, operatorUserId);
            var effectiveOptions = await ResolveEffectiveOptionsAsync(webUsername, appId);
            var messageId = await _cardKit.ReplyElementsCardAsync(replyToMessageId, card, optionsOverride: effectiveOptions);
            _logger.LogInformation("✅ [Feishu] 项目管理卡片已发送, MessageId={MessageId}", messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理 feishuprojects 命令失败");
            await _feishuChannel.ReplyMessageAsync(replyToMessageId, "❌ 项目管理功能暂时不可用，请稍后重试。", webUsername, appId);
        }
    }

    private async Task<FeishuOptions> ResolveEffectiveOptionsAsync(string? username, string? appId)
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

        return await userFeishuBotConfigService.GetEffectiveOptionsAsync(username);
    }

    private async Task<bool> GetReplyTtsEnabledAsync(string chatId, string? username)
    {
        var resolvedUsername = string.IsNullOrWhiteSpace(username)
            ? _feishuChannel.GetSessionUsername(chatId)
            : username;
        if (string.IsNullOrWhiteSpace(resolvedUsername))
        {
            return false;
        }

        using var scope = _serviceProvider.CreateScope();
        var userFeishuBotConfigService = scope.ServiceProvider.GetRequiredService<IUserFeishuBotConfigService>();
        var config = await userFeishuBotConfigService.GetByUsernameAsync(resolvedUsername);
        return config?.ReplyTtsEnabled == true;
    }

    private bool ResolveShowGoalQuickActionButtons(string chatId, string? username, string? toolId)
    {
        var chatKey = chatId.ToLowerInvariant();
        var resolvedUsername = string.IsNullOrWhiteSpace(username)
            ? _feishuChannel.GetSessionUsername(chatKey)
            : username;
        var sessionId = _feishuChannel.GetCurrentSession(chatKey, resolvedUsername);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
        var session = repo.GetByIdAsync(sessionId).GetAwaiter().GetResult();
        return GoalQuickActionVisibilityHelper.ShouldShowButtons(session, _cliExecutor, toolId);
    }

    private bool ResolveShowSuperpowersQuickActions(string chatId, string? username, string? toolId)
    {
        var chatKey = chatId.ToLowerInvariant();
        var resolvedUsername = string.IsNullOrWhiteSpace(username)
            ? _feishuChannel.GetSessionUsername(chatKey)
            : username;
        var sessionId = _feishuChannel.GetCurrentSession(chatKey, resolvedUsername);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return true;
        }

        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
        var session = repo.GetByIdAsync(sessionId).GetAwaiter().GetResult();
        return !IsGoalRuntimeSession(session);
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

    private async Task<List<ChatSessionEntity>> GetChatSessionEntitiesAsync(string chatKey, string username)
    {
        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();

        var sessions = await repo.GetByUsernameOrderByUpdatedAtAsync(username);
        return sessions
            .Where(s => string.Equals(s.FeishuChatKey, chatKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();
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
}
