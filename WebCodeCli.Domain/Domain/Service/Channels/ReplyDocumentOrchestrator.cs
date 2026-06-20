using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Repositories.Base.ChatSession;
using WebCodeCli.Domain.Repositories.Base.UserFeishuBotConfig;

namespace WebCodeCli.Domain.Domain.Service.Channels;

[ServiceDescription(typeof(IReplyDocumentOrchestrator), ServiceLifetime.Singleton)]
public sealed class ReplyDocumentOrchestrator : IReplyDocumentOrchestrator
{
    private const int MaxTitleLength = 180;
    private const string FailureStageDataKey = "ReplyDocumentFailureStage";
    private static readonly char[] InvalidFolderNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private const string FullReplySuffix = " - 完整回复";
    private const string FinalReplySuffix = " - 结论回复";
    private const string FullReplyLinkPrefix = "已生成完整回复文档：";
    private const string FinalReplyLinkPrefix = "已生成结论回复文档：";

    private const string AudioFullReplySuffix = " - 听完整回复";
    private const string AudioFinalReplySuffix = " - 听结论回复";
    private const string AudioFullReplyLinkPrefix = "已生成听完整回复文档：";
    private const string AudioFinalReplyLinkPrefix = "已生成听结论回复文档：";
    private const string MarkdownImportLinkPrefix = "已生成Markdown在线文档：";

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ReplyDocumentOrchestrator> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _chatLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReplyDocumentMarkdownRenderer _markdownRenderer;
    private readonly ReferencedMarkdownDocumentImporter _markdownDocumentImporter;

    public ReplyDocumentOrchestrator(
        IServiceProvider serviceProvider,
        ILogger<ReplyDocumentOrchestrator> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _markdownRenderer = new ReplyDocumentMarkdownRenderer(
            serviceProvider.GetService<ILogger<ReplyDocumentMarkdownRenderer>>()
            ?? NullLogger<ReplyDocumentMarkdownRenderer>.Instance);
        _markdownDocumentImporter = new ReferencedMarkdownDocumentImporter(
            serviceProvider.GetService<ILogger<ReferencedMarkdownDocumentImporter>>()
            ?? NullLogger<ReferencedMarkdownDocumentImporter>.Instance,
            serviceProvider.GetRequiredService<IReferencedMarkdownImportStateStore>());
    }

    public Task QueueCompletedReplyAsync(FeishuCompletedReplyDocumentRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ChatId))
        {
            throw new ArgumentException("Chat ID is required.", nameof(request));
        }

        _ = Task.Run(() => ProcessQueuedReplyAsync(request));
        return Task.CompletedTask;
    }

    private async Task ProcessQueuedReplyAsync(FeishuCompletedReplyDocumentRequest request)
    {
        var chatLock = _chatLocks.GetOrAdd(request.ChatId.Trim(), static _ => new SemaphoreSlim(1, 1));
        await chatLock.WaitAsync();
        try
        {
            await ProcessReplyCoreAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reply document orchestration failed for chat {ChatId}", request.ChatId);
        }
        finally
        {
            chatLock.Release();
        }
    }

    private async Task ProcessReplyCoreAsync(FeishuCompletedReplyDocumentRequest request)
    {
        using var scope = _serviceProvider.CreateScope();
        var configService = scope.ServiceProvider.GetRequiredService<IUserFeishuBotConfigService>();
        var userConfig = await ResolveBotConfigAsync(configService, request.Username, request.AppId);
        if (userConfig == null)
        {
            return;
        }

        var cardKitClient = scope.ServiceProvider.GetRequiredService<IFeishuCardKitClient>();
        var fullReplyContent = NormalizeDocumentBody(request.Output);
        var finalReplyContent = NormalizeDocumentBody(await ResolveFinalOnlyOutputAsync(scope.ServiceProvider, request));
        var titleQuestion = await ResolveTitleQuestionAsync(scope.ServiceProvider, request);
        var fullReplyTitle = BuildDocumentTitle(request, titleQuestion, FullReplySuffix);
        var finalReplyTitle = BuildDocumentTitle(request, titleQuestion, FinalReplySuffix);
        var audioFullReplyTitle = BuildDocumentTitle(request, titleQuestion, AudioFullReplySuffix);
        var audioFinalReplyTitle = BuildDocumentTitle(request, titleQuestion, AudioFinalReplySuffix);
        var fullReplyDocumentBody = BuildReplyDocumentBody(fullReplyContent, titleQuestion, appendUserQuestionToEnd: false);
        var finalReplyDocumentBody = BuildReplyDocumentBody(finalReplyContent, titleQuestion, appendUserQuestionToEnd: false);
        var audioFullReplyDocumentBody = BuildReplyDocumentBody(
            ListeningReplyDocumentFormatter.Format(fullReplyContent),
            titleQuestion,
            appendUserQuestionToEnd: true);
        var audioFinalReplyDocumentBody = BuildReplyDocumentBody(
            ListeningReplyDocumentFormatter.Format(finalReplyContent),
            titleQuestion,
            appendUserQuestionToEnd: true);

        if (userConfig.FullReplyDocEnabled && !string.IsNullOrWhiteSpace(fullReplyContent))
        {
            await TryCreateAndSendDocumentAsync(
                cardKitClient,
                scope.ServiceProvider,
                configService,
                request,
                fullReplyTitle,
                fullReplyDocumentBody,
                FullReplyLinkPrefix,
                userConfig.DocumentAdminOpenId);
        }

        if (userConfig.FinalReplyDocEnabled && !string.IsNullOrWhiteSpace(finalReplyContent))
        {
            await TryCreateAndSendDocumentAsync(
                cardKitClient,
                scope.ServiceProvider,
                configService,
                request,
                finalReplyTitle,
                finalReplyDocumentBody,
                FinalReplyLinkPrefix,
                userConfig.DocumentAdminOpenId);
        }

        if (userConfig.AudioFullReplyDocEnabled && !string.IsNullOrWhiteSpace(fullReplyContent))
        {
            await TryCreateAndSendDocumentAsync(
                cardKitClient,
                scope.ServiceProvider,
                configService,
                request,
                audioFullReplyTitle,
                audioFullReplyDocumentBody,
                AudioFullReplyLinkPrefix,
                userConfig.DocumentAdminOpenId);
        }

        if (userConfig.AudioFinalReplyDocEnabled && !string.IsNullOrWhiteSpace(finalReplyContent))
        {
            await TryCreateAndSendDocumentAsync(
                cardKitClient,
                scope.ServiceProvider,
                configService,
                request,
                audioFinalReplyTitle,
                audioFinalReplyDocumentBody,
                AudioFinalReplyLinkPrefix,
                userConfig.DocumentAdminOpenId);
        }

        if (userConfig.ReferencedMarkdownDocImportEnabled)
        {
            await TryImportReferencedMarkdownDocumentsAsync(
                cardKitClient,
                scope.ServiceProvider,
                configService,
                userConfig,
                request,
                fullReplyContent,
                finalReplyContent);
        }
    }

    private async Task TryCreateAndSendDocumentAsync(
        IFeishuCardKitClient cardKitClient,
        IServiceProvider serviceProvider,
        IUserFeishuBotConfigService configService,
        FeishuCompletedReplyDocumentRequest request,
        string title,
        string body,
        string messagePrefix,
        string? documentAdminOpenId)
    {
        try
        {
            var effectiveOptions = await ResolveEffectiveOptionsAsync(configService, request.Username, request.AppId);
            string? placementWarningMessage = null;
            string? documentAdminWarningMessage = null;
            string? folderAdminWarningMessage = null;
            string? folderToken = null;

            var folderName = await ResolveReplyDocumentFolderNameAsync(serviceProvider, request);
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                try
                {
                    folderToken = await cardKitClient.EnsureCloudFolderAsync(
                        folderName,
                        optionsOverride: effectiveOptions);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    MarkFailureStage(ex, ReplyDocumentFailureStage.ResolveFolder);
                    placementWarningMessage = BuildPlacementWarningMessage(messagePrefix, ex);
                    _logger.LogWarning(
                        ex,
                        "Reply document folder placement failed for chat {ChatId}, session {SessionId}, title {Title}, stage {FailureStage}",
                        request.ChatId,
                        request.SessionId,
                        title,
                        ReplyDocumentFailureStage.ResolveFolder);
                    folderToken = null;
                }

                if (!string.IsNullOrWhiteSpace(folderToken))
                {
                    if (!string.IsNullOrWhiteSpace(documentAdminOpenId))
                    {
                        try
                        {
                            await cardKitClient.GrantCloudFolderMemberFullAccessAsync(
                                folderToken,
                                documentAdminOpenId,
                                optionsOverride: effectiveOptions);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            folderAdminWarningMessage = BuildFolderAdminGrantWarningMessage(messagePrefix, ex);
                            _logger.LogWarning(
                                ex,
                                "Reply document folder admin grant failed for chat {ChatId}, session {SessionId}, title {Title}",
                                request.ChatId,
                                request.SessionId,
                                title);
                        }
                    }
                }
            }

            FeishuCloudDocumentInfo document;
            if (!string.IsNullOrWhiteSpace(folderToken))
            {
                try
                {
                    document = await cardKitClient.CreateCloudDocumentAsync(
                        TruncateTitle(title),
                        optionsOverride: effectiveOptions,
                        folderToken: folderToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    MarkFailureStage(ex, ReplyDocumentFailureStage.CreateInFolder);
                    _logger.LogWarning(
                        ex,
                        "Reply document direct folder creation failed for chat {ChatId}, session {SessionId}, title {Title}, stage {FailureStage}",
                        request.ChatId,
                        request.SessionId,
                        title,
                        ReplyDocumentFailureStage.CreateInFolder);

                    document = await cardKitClient.CreateCloudDocumentAsync(
                        TruncateTitle(title),
                        optionsOverride: effectiveOptions);

                    if (ShouldAttemptMoveFallbackAfterCreateFailure(ex))
                    {
                        try
                        {
                            await cardKitClient.MoveCloudDocumentToFolderAsync(
                                document.DocumentId,
                                folderToken,
                                optionsOverride: effectiveOptions);
                        }
                        catch (Exception moveEx) when (moveEx is not OperationCanceledException)
                        {
                            MarkFailureStage(moveEx, ReplyDocumentFailureStage.MoveToFolder);
                            placementWarningMessage = BuildPlacementWarningMessage(messagePrefix, moveEx);
                            _logger.LogWarning(
                                moveEx,
                                "Reply document folder placement failed for chat {ChatId}, session {SessionId}, title {Title}, stage {FailureStage}",
                                request.ChatId,
                                request.SessionId,
                                title,
                                ReplyDocumentFailureStage.MoveToFolder);
                        }
                    }
                    else
                    {
                        placementWarningMessage = BuildPlacementWarningMessage(messagePrefix, ex);
                    }
                }
            }
            else
            {
                document = await cardKitClient.CreateCloudDocumentAsync(
                    TruncateTitle(title),
                    optionsOverride: effectiveOptions);
            }

            await cardKitClient.AppendCloudDocumentTextAsync(
                document.DocumentId,
                document.RootBlockId,
                body,
                optionsOverride: effectiveOptions);

            await cardKitClient.SetCloudDocumentTenantReadableAsync(
                document.DocumentId,
                optionsOverride: effectiveOptions);

            if (!string.IsNullOrWhiteSpace(documentAdminOpenId))
            {
                try
                {
                    await cardKitClient.GrantCloudDocumentMemberFullAccessAsync(
                        document.DocumentId,
                        documentAdminOpenId,
                        optionsOverride: effectiveOptions);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    documentAdminWarningMessage = BuildDocumentAdminGrantWarningMessage(messagePrefix, ex);
                    _logger.LogWarning(
                        ex,
                        "Reply document admin grant failed for chat {ChatId}, session {SessionId}, title {Title}",
                        request.ChatId,
                        request.SessionId,
                        title);
                }
            }

            await cardKitClient.SendTextMessageAsync(
                request.ChatId,
                $"{messagePrefix}{document.Url}",
                optionsOverride: effectiveOptions);

            if (!string.IsNullOrWhiteSpace(placementWarningMessage))
            {
                await TrySendPlacementWarningMessageAsync(
                    cardKitClient,
                    request,
                    placementWarningMessage);
            }

            if (!string.IsNullOrWhiteSpace(documentAdminWarningMessage))
            {
                await TrySendDocumentAdminWarningMessageAsync(
                    cardKitClient,
                    request,
                    documentAdminWarningMessage);
            }

            if (!string.IsNullOrWhiteSpace(folderAdminWarningMessage))
            {
                await TrySendDocumentAdminWarningMessageAsync(
                    cardKitClient,
                    request,
                    folderAdminWarningMessage);
            }
        }
        catch (Exception ex)
        {
            var failureStage = GetFailureStage(ex)?.ToString() ?? "unknown";
            _logger.LogWarning(
                ex,
                "Reply document generation failed for chat {ChatId}, session {SessionId}, title {Title}, stage {FailureStage}",
                request.ChatId,
                request.SessionId,
                title,
                failureStage);

            await TrySendFailureMessageAsync(
                cardKitClient,
                request,
                BuildFailureMessage(messagePrefix, ex));
        }
    }

    private static string BuildFailureMessage(string messagePrefix, Exception exception)
    {
        var scopeHint = TryExtractPermissionScopes(exception.Message);
        var target = ResolveDocumentTarget(messagePrefix);
        var failureStage = GetFailureStage(exception);

        if (!string.IsNullOrWhiteSpace(scopeHint))
        {
            var permissionGuidance = TryExtractPermissionGuidance(exception.Message);
            return string.IsNullOrWhiteSpace(permissionGuidance)
                ? $"{target}生成失败：飞书应用缺少文档权限，请开通 {scopeHint} 后重试。"
                : $"{target}生成失败：飞书应用缺少文档权限，请开通 {scopeHint} 后重试。{permissionGuidance}";
        }

        if (IsDriveResourceNotFoundError(exception.Message))
        {
            return BuildDriveResourceNotFoundMessage(target, failureStage);
        }

        var compactReason = BuildCompactErrorReason(exception.Message);
        return string.IsNullOrWhiteSpace(compactReason)
            ? $"{target}生成失败，请检查飞书文档权限或服务日志。"
            : $"{target}生成失败：{compactReason}";
    }

    private static string BuildCompactErrorReason(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var normalized = message.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= 180
            ? normalized
            : normalized[..180].TrimEnd();
    }

    private static string? TryExtractPermissionScopes(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        if (!message.Contains("scope", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("permission", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("权限", StringComparison.OrdinalIgnoreCase)
            && !message.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var scopes = Regex.Matches(
                message,
                @"\b(?:docx|drive):[a-z0-9.-]+(?::[a-z0-9.-]+)*\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return scopes.Count == 0
            ? null
            : string.Join("\u3001", scopes);
    }

    private static string? TryExtractPermissionGuidance(string? message)
    {
        const string prefix = "应用尚未开通所需的应用身份权限：";

        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var startIndex = message.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
        {
            return null;
        }

        var guidance = message[startIndex..];
        var endIndex = guidance.IndexOfAny(['"', '\r', '\n']);
        if (endIndex >= 0)
        {
            guidance = guidance[..endIndex];
        }

        guidance = guidance.Trim().TrimEnd('}');
        if (!guidance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return guidance.Length == prefix.Length
            ? null
            : guidance;
    }

    private static bool IsDriveResourceNotFoundError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("Status=NotFound", StringComparison.OrdinalIgnoreCase)
               || message.Contains("\"code\":1061003", StringComparison.OrdinalIgnoreCase)
               || message.Contains("code=1061003", StringComparison.OrdinalIgnoreCase)
               || message.Contains("\"msg\":\"not found.\"", StringComparison.OrdinalIgnoreCase)
               || message.Contains("msg\":\"not found", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDriveResourceNotFoundMessage(string target, ReplyDocumentFailureStage? failureStage)
    {
        return failureStage switch
        {
            ReplyDocumentFailureStage.ResolveFolder
                => $"{target}生成失败：在定位会话文档文件夹时，飞书文件夹资源不存在或已失效，请稍后重试；若持续出现，请检查该会话文档文件夹是否已被删除。",
            ReplyDocumentFailureStage.CreateInFolder
                => $"{target}生成失败：在归档到会话文档文件夹时，飞书文档或目标文件夹资源不存在或已失效，请稍后重试；若持续出现，请检查目标文件夹是否已被删除。",
            ReplyDocumentFailureStage.MoveToFolder
                => $"{target}生成失败：在移动到会话文档文件夹时，飞书文档或目标文件夹资源不存在或已失效，请稍后重试；若持续出现，请检查目标文件夹是否已被删除。",
            _ => $"{target}生成失败：飞书文档资源不存在或已失效，请稍后重试；若持续出现，请检查目标文档或文件夹是否已被删除。"
        };
    }

    private static string BuildPlacementWarningMessage(string messagePrefix, Exception exception)
    {
        var target = ResolveDocumentTarget(messagePrefix);
        var failureStage = GetFailureStage(exception);
        var scopeHint = TryExtractPermissionScopes(exception.Message);

        if (!string.IsNullOrWhiteSpace(scopeHint))
        {
            var permissionGuidance = TryExtractPermissionGuidance(exception.Message);
            var guidanceSuffix = string.IsNullOrWhiteSpace(permissionGuidance) ? string.Empty : permissionGuidance;
            return $"{target}已生成，但归档到会话文档文件夹失败：飞书应用缺少文档权限，请开通 {scopeHint} 后重试。{guidanceSuffix}文档已保留在飞书默认目录。";
        }

        if (IsDriveResourceNotFoundError(exception.Message))
        {
            return failureStage switch
            {
                ReplyDocumentFailureStage.ResolveFolder
                    => $"{target}已生成，但在定位会话文档文件夹时，飞书文件夹资源不存在或已失效。文档已保留在飞书默认目录；若持续出现，请检查该会话文档文件夹是否已被删除。",
                ReplyDocumentFailureStage.CreateInFolder
                    => $"{target}已生成，但在归档到会话文档文件夹时，飞书文档或目标文件夹资源不存在或已失效。文档已保留在飞书默认目录；若持续出现，请检查目标文件夹是否已被删除。",
                ReplyDocumentFailureStage.MoveToFolder
                    => $"{target}已生成，但在移动到会话文档文件夹时，飞书文档或目标文件夹资源不存在或已失效。文档已保留在飞书默认目录；若持续出现，请检查目标文件夹是否已被删除。",
                _ => $"{target}已生成，但归档到会话文档文件夹时遇到资源不存在或已失效。文档已保留在飞书默认目录；若持续出现，请检查目标文档或文件夹是否已被删除。"
            };
        }

        var compactReason = BuildCompactErrorReason(exception.Message);
        return string.IsNullOrWhiteSpace(compactReason)
            ? $"{target}已生成，但归档到会话文档文件夹失败。文档已保留在飞书默认目录。"
            : $"{target}已生成，但归档到会话文档文件夹失败：{compactReason}。文档已保留在飞书默认目录。";
    }

    private static string BuildDocumentAdminGrantWarningMessage(string messagePrefix, Exception exception)
    {
        var target = ResolveDocumentTarget(messagePrefix);
        var scopeHint = TryExtractPermissionScopes(exception.Message);

        if (!string.IsNullOrWhiteSpace(scopeHint))
        {
            var permissionGuidance = TryExtractPermissionGuidance(exception.Message);
            return string.IsNullOrWhiteSpace(permissionGuidance)
                ? $"{target}已生成，但文档管理员权限授予失败：飞书应用缺少文档权限，请开通 {scopeHint} 后重试。"
                : $"{target}已生成，但文档管理员权限授予失败：飞书应用缺少文档权限，请开通 {scopeHint} 后重试。{permissionGuidance}";
        }

        if (IsDriveResourceNotFoundError(exception.Message))
        {
            return $"{target}已生成，但文档管理员权限授予失败：飞书文档资源不存在或已失效，请稍后重试；若持续出现，请检查目标文档是否已被删除。";
        }

        var compactReason = BuildCompactErrorReason(exception.Message);
        return string.IsNullOrWhiteSpace(compactReason)
            ? $"{target}已生成，但文档管理员权限授予失败，请稍后重试。"
            : $"{target}已生成，但文档管理员权限授予失败：{compactReason}";
    }

    private static string BuildFolderAdminGrantWarningMessage(string messagePrefix, Exception exception)
    {
        var target = ResolveDocumentTarget(messagePrefix);
        var scopeHint = TryExtractPermissionScopes(exception.Message);

        if (!string.IsNullOrWhiteSpace(scopeHint))
        {
            var permissionGuidance = TryExtractPermissionGuidance(exception.Message);
            return string.IsNullOrWhiteSpace(permissionGuidance)
                ? $"{target}已生成，但会话文档文件夹管理员权限授予失败：飞书应用缺少文档权限，请开通 {scopeHint} 后重试。"
                : $"{target}已生成，但会话文档文件夹管理员权限授予失败：飞书应用缺少文档权限，请开通 {scopeHint} 后重试。{permissionGuidance}";
        }

        if (IsDriveResourceNotFoundError(exception.Message))
        {
            return $"{target}已生成，但会话文档文件夹管理员权限授予失败：目标文件夹资源不存在或已失效，请稍后重试；若持续出现，请检查目标文件夹是否已被删除。";
        }

        var compactReason = BuildCompactErrorReason(exception.Message);
        return string.IsNullOrWhiteSpace(compactReason)
            ? $"{target}已生成，但会话文档文件夹管理员权限授予失败，请稍后重试。"
            : $"{target}已生成，但会话文档文件夹管理员权限授予失败：{compactReason}";
    }

    private static void MarkFailureStage(Exception exception, ReplyDocumentFailureStage failureStage)
    {
        exception.Data[FailureStageDataKey] = failureStage;
    }

    private static ReplyDocumentFailureStage? GetFailureStage(Exception exception)
    {
        if (exception.Data[FailureStageDataKey] is ReplyDocumentFailureStage failureStage)
        {
            return failureStage;
        }

        if (exception.Data[FailureStageDataKey] is string failureStageText
            && Enum.TryParse<ReplyDocumentFailureStage>(failureStageText, ignoreCase: true, out var parsedFailureStage))
        {
            return parsedFailureStage;
        }

        return null;
    }

    private static string ResolveDocumentTarget(string messagePrefix)
    {
        if (string.Equals(messagePrefix, FinalReplyLinkPrefix, StringComparison.Ordinal))
        {
            return "结论回复文档";
        }

        if (string.Equals(messagePrefix, AudioFullReplyLinkPrefix, StringComparison.Ordinal))
        {
            return "听完整回复文档";
        }

        if (string.Equals(messagePrefix, AudioFinalReplyLinkPrefix, StringComparison.Ordinal))
        {
            return "听结论回复文档";
        }

        if (string.Equals(messagePrefix, MarkdownImportLinkPrefix, StringComparison.Ordinal))
        {
            return "Markdown在线文档";
        }

        return "完整回复文档";
    }

    private enum ReplyDocumentFailureStage
    {
        ResolveFolder,
        CreateInFolder,
        MoveToFolder
    }

    private static bool ShouldAttemptMoveFallbackAfterCreateFailure(Exception exception)
    {
        var message = exception.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return true;
        }

        if (IsDriveResourceNotFoundError(message))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(TryExtractPermissionScopes(message)))
        {
            return false;
        }

        return true;
    }

    private async Task TrySendFailureMessageAsync(
        IFeishuCardKitClient cardKitClient,
        FeishuCompletedReplyDocumentRequest request,
        string message)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var configService = scope.ServiceProvider.GetRequiredService<IUserFeishuBotConfigService>();
            var effectiveOptions = await ResolveEffectiveOptionsAsync(configService, request.Username, request.AppId);
            await cardKitClient.SendTextMessageAsync(
                request.ChatId,
                message,
                optionsOverride: effectiveOptions);
        }
        catch (Exception notifyEx)
        {
            _logger.LogWarning(
                notifyEx,
                "Reply document failure notification failed for chat {ChatId}, session {SessionId}",
                request.ChatId,
                request.SessionId);
        }
    }

    private static string NormalizeDocumentBody(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        return content.Replace("\r\n", "\n").Trim();
    }

    private static string BuildDocumentTitle(
        FeishuCompletedReplyDocumentRequest request,
        string? titleQuestion,
        string suffix)
    {
        var normalizedQuestion = NormalizeQuestionForTitle(titleQuestion);
        if (string.IsNullOrWhiteSpace(normalizedQuestion))
        {
            normalizedQuestion = "未命名";
        }

        var _ = suffix;
        const string timestampFormat = "yyyy-MM-dd HH:mm:ss.fff";
        var timestamp = DateTime.Now.ToString(timestampFormat);
        var maxQuestionLength = MaxTitleLength - timestamp.Length - 1;

        var truncatedQuestion = maxQuestionLength > 0 && normalizedQuestion.Length > maxQuestionLength
            ? normalizedQuestion[..maxQuestionLength].TrimEnd()
            : normalizedQuestion;

        if (string.IsNullOrWhiteSpace(truncatedQuestion))
        {
            truncatedQuestion = "未命名";
        }

        return $"{truncatedQuestion} {timestamp}";
    }

    private static string? ResolveEffectiveCliThreadId(
        ChatSessionEntity? session,
        FeishuCompletedReplyDocumentRequest request)
    {
        var cliThreadId = !string.IsNullOrWhiteSpace(request.CliThreadId)
            ? request.CliThreadId
            : session?.CliThreadId;

        return string.IsNullOrWhiteSpace(cliThreadId)
            ? null
            : cliThreadId.Trim();
    }

    private static string? AppendThreadIdToNamedFolder(string? sessionTitle, string? cliThreadId)
    {
        if (string.IsNullOrWhiteSpace(sessionTitle))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(cliThreadId))
        {
            return sessionTitle;
        }

        return $"{sessionTitle} [{cliThreadId}]";
    }

    private async Task<string?> ResolveTitleQuestionAsync(IServiceProvider serviceProvider, FeishuCompletedReplyDocumentRequest request)
    {
        var resolvedFromHistory = await TryResolveLastRealUserMessageAsync(serviceProvider, request);
        if (!string.IsNullOrWhiteSpace(resolvedFromHistory))
        {
            return resolvedFromHistory;
        }

        return request.OriginalUserQuestion;
    }

    private async Task<string?> TryResolveLastRealUserMessageAsync(IServiceProvider serviceProvider, FeishuCompletedReplyDocumentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return null;
        }

        try
        {
            var sessionRepository = serviceProvider.GetService<IChatSessionRepository>();
            var historyService = serviceProvider.GetService<IExternalCliSessionHistoryService>();
            if (sessionRepository == null || historyService == null)
            {
                return null;
            }

            var session = await sessionRepository.GetByIdAsync(request.SessionId.Trim());
            if (session == null)
            {
                return null;
            }

            var effectiveToolId = SessionLaunchOverrideHelper.ResolveEffectiveToolId(
                session.ToolId,
                session.CcSwitchSnapshotToolId);
            if (string.IsNullOrWhiteSpace(effectiveToolId))
            {
                return null;
            }

            var cliThreadId = string.IsNullOrWhiteSpace(request.CliThreadId)
                ? session.CliThreadId?.Trim()
                : request.CliThreadId.Trim();
            if (string.IsNullOrWhiteSpace(cliThreadId))
            {
                return null;
            }

            var messages = await historyService.GetRecentMessagesAsync(
                effectiveToolId,
                cliThreadId,
                maxCount: 12,
                workspacePath: session.WorkspacePath);

            if (messages.Count == 0)
            {
                return null;
            }

            var latestUserMessage = messages
                .LastOrDefault(message =>
                    string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(message.Content)
                    && !LooksLikeControlPrompt(message.Content));

            return latestUserMessage?.Content?.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Reply document title question rollout fallback failed for session {SessionId}",
                request.SessionId);
            return null;
        }
    }

    private static bool LooksLikeControlPrompt(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var normalized = content.Trim();
        if (normalized.StartsWith("/goal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.StartsWith("/goal ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalized.Contains("使用Subagent-Driven完成plan", StringComparison.Ordinal)
               || normalized.Contains("使用Worktree技能完成Worktree", StringComparison.Ordinal);
    }

    private static string NormalizeQuestionForTitle(string? originalUserQuestion)
    {
        if (string.IsNullOrWhiteSpace(originalUserQuestion))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            originalUserQuestion
                .Replace("\r\n", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string BuildReplyDocumentBody(
        string body,
        string? userQuestion,
        bool appendUserQuestionToEnd)
    {
        var normalizedBody = NormalizeDocumentBody(body);
        var normalizedQuestion = NormalizeDocumentBody(userQuestion);
        if (string.IsNullOrWhiteSpace(normalizedQuestion))
        {
            return normalizedBody;
        }

        var questionBlock = $"## 用户内容\n\n{normalizedQuestion}";
        if (string.IsNullOrWhiteSpace(normalizedBody))
        {
            return questionBlock;
        }

        return appendUserQuestionToEnd
            ? $"{normalizedBody}\n\n{questionBlock}"
            : $"{questionBlock}\n\n---\n\n{normalizedBody}";
    }

    private static string TruncateTitle(string title)
    {
        if (title.Length <= MaxTitleLength)
        {
            return title;
        }

        return title[..MaxTitleLength].TrimEnd();
    }

    private static async Task<UserFeishuBotConfigEntity?> ResolveBotConfigAsync(
        IUserFeishuBotConfigService configService,
        string? username,
        string? appId)
    {
        if (!string.IsNullOrWhiteSpace(username))
        {
            var userConfig = await configService.GetByUsernameAsync(username.Trim());
            if (userConfig != null)
            {
                return userConfig;
            }
        }

        if (!string.IsNullOrWhiteSpace(appId))
        {
            return await configService.GetByAppIdAsync(appId.Trim());
        }

        return null;
    }

    private async Task<string?> ResolveFinalOnlyOutputAsync(IServiceProvider serviceProvider, FeishuCompletedReplyDocumentRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.FinalAnswerOutput))
        {
            return request.FinalAnswerOutput;
        }

        var fallback = await TryResolveCodexFinalAnswerFallbackAsync(serviceProvider, request);
        return string.IsNullOrWhiteSpace(fallback)
            ? request.FinalAnswerOutput
            : fallback;
    }

    private async Task<string?> TryResolveCodexFinalAnswerFallbackAsync(IServiceProvider serviceProvider, FeishuCompletedReplyDocumentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return null;
        }

        try
        {
            var sessionRepository = serviceProvider.GetService<IChatSessionRepository>();
            var session = sessionRepository == null
                ? null
                : await sessionRepository.GetByIdAsync(request.SessionId.Trim());
            if (session == null)
            {
                return null;
            }

            var effectiveToolId = SessionLaunchOverrideHelper.ResolveEffectiveToolId(
                session.ToolId,
                session.CcSwitchSnapshotToolId);
            if (!string.Equals(effectiveToolId, "codex", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var cliThreadId = string.IsNullOrWhiteSpace(request.CliThreadId)
                ? session.CliThreadId?.Trim()
                : request.CliThreadId.Trim();
            if (string.IsNullOrWhiteSpace(cliThreadId))
            {
                return null;
            }

            var historyService = serviceProvider.GetService<IExternalCliSessionHistoryService>();
            if (historyService == null)
            {
                return null;
            }

            return await historyService.GetCodexFinalAnswerTextAsync(
                cliThreadId,
                workspacePath: session.WorkspacePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Reply document final-only rollout fallback failed for session {SessionId}",
                request.SessionId);
            return null;
        }
    }

    private async Task TrySendPlacementWarningMessageAsync(
        IFeishuCardKitClient cardKitClient,
        FeishuCompletedReplyDocumentRequest request,
        string message)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var configService = scope.ServiceProvider.GetRequiredService<IUserFeishuBotConfigService>();
            var effectiveOptions = await ResolveEffectiveOptionsAsync(configService, request.Username, request.AppId);
            await cardKitClient.SendTextMessageAsync(
                request.ChatId,
                message,
                optionsOverride: effectiveOptions);
        }
        catch (Exception notifyEx)
        {
            _logger.LogWarning(
                notifyEx,
                "Reply document placement warning notification failed for chat {ChatId}, session {SessionId}",
                request.ChatId,
                request.SessionId);
        }
    }

    private async Task TrySendDocumentAdminWarningMessageAsync(
        IFeishuCardKitClient cardKitClient,
        FeishuCompletedReplyDocumentRequest request,
        string message)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var configService = scope.ServiceProvider.GetRequiredService<IUserFeishuBotConfigService>();
            var effectiveOptions = await ResolveEffectiveOptionsAsync(configService, request.Username, request.AppId);
            await cardKitClient.SendTextMessageAsync(
                request.ChatId,
                message,
                optionsOverride: effectiveOptions);
        }
        catch (Exception notifyEx)
        {
            _logger.LogWarning(
                notifyEx,
                "Reply document admin warning notification failed for chat {ChatId}, session {SessionId}",
                request.ChatId,
                request.SessionId);
        }
    }

    private async Task<string?> ResolveReplyDocumentFolderNameAsync(
        IServiceProvider serviceProvider,
        FeishuCompletedReplyDocumentRequest request)
    {
        var session = await TryGetSessionAsync(serviceProvider, request.SessionId);
        var candidate = ResolveFolderNameCandidate(session, request);
        return SanitizeFolderName(candidate);
    }

    private static string? ResolveFolderNameCandidate(
        ChatSessionEntity? session,
        FeishuCompletedReplyDocumentRequest request)
    {
        var cliThreadId = ResolveEffectiveCliThreadId(session, request);

        if (!IsUnnamedSessionTitle(session?.Title))
        {
            return AppendThreadIdToNamedFolder(session?.Title?.Trim(), cliThreadId);
        }

        if (!string.IsNullOrWhiteSpace(cliThreadId))
        {
            return cliThreadId;
        }

        return request.SessionId ?? session?.SessionId;
    }

    private static bool IsUnnamedSessionTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return true;
        }

        var normalizedTitle = title.Trim();
        return string.Equals(normalizedTitle, "未命名", StringComparison.Ordinal)
            || string.Equals(normalizedTitle, "鏈懡鍚?", StringComparison.Ordinal)
            || string.Equals(normalizedTitle, "閺堫亜鎳￠崥?", StringComparison.Ordinal);
    }

    private static string? SanitizeFolderName(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        var sanitized = folderName.Trim();
        foreach (var invalidCharacter in InvalidFolderNameCharacters)
        {
            sanitized = sanitized.Replace(invalidCharacter, '-');
        }

        sanitized = string.Join(
            " ",
            sanitized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return string.IsNullOrWhiteSpace(sanitized)
            ? null
            : sanitized;
    }

    private static async Task<ChatSessionEntity?> TryGetSessionAsync(IServiceProvider serviceProvider, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var sessionRepository = serviceProvider.GetService<IChatSessionRepository>();
        return sessionRepository == null
            ? null
            : await sessionRepository.GetByIdAsync(sessionId.Trim());
    }

    private async Task TryImportReferencedMarkdownDocumentsAsync(
        IFeishuCardKitClient cardKitClient,
        IServiceProvider serviceProvider,
        IUserFeishuBotConfigService configService,
        UserFeishuBotConfigEntity userConfig,
        FeishuCompletedReplyDocumentRequest request,
        string fullReplyContent,
        string finalReplyContent)
    {
        var session = await TryGetSessionAsync(serviceProvider, request.SessionId);
        var candidates = ExtractReferencedMarkdownCandidates(
            fullReplyContent,
            finalReplyContent,
            session?.WorkspacePath);
        if (candidates.Count == 0)
        {
            _logger.LogDebug(
                "Referenced markdown import skipped because no local markdown candidates were resolved: Session={SessionId}, WorkspacePath={WorkspacePath}",
                request.SessionId,
                session?.WorkspacePath ?? "<null>");
            return;
        }

        var folderName = await ResolveReplyDocumentFolderNameAsync(serviceProvider, request);
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        var effectiveOptions = await ResolveEffectiveOptionsAsync(configService, request.Username, request.AppId);
        string folderToken;
        try
        {
            folderToken = await cardKitClient.EnsureCloudFolderAsync(
                folderName,
                optionsOverride: effectiveOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Referenced markdown import folder resolution failed for chat {ChatId}, session {SessionId}",
                request.ChatId,
                request.SessionId);
            await TrySendFailureMessageAsync(
                cardKitClient,
                request,
                BuildFailureMessage(MarkdownImportLinkPrefix, ex));
            return;
        }

        await _markdownDocumentImporter.ImportMissingAsync(
            cardKitClient,
            request.ChatId,
            folderToken,
            candidates,
            userConfig.DocumentAdminOpenId,
            effectiveOptions,
            CancellationToken.None);
    }

    private static IReadOnlyList<ReferencedMarkdownDocumentCandidate> ExtractReferencedMarkdownCandidates(
        string fullReplyContent,
        string finalReplyContent,
        string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return [];
        }

        var results = new List<ReferencedMarkdownDocumentCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AppendCandidates(fullReplyContent);
        AppendCandidates(finalReplyContent);

        return results;

        void AppendCandidates(string sourceText)
        {
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                return;
            }

            foreach (var candidate in MarkdownReferenceExtractor.Extract(sourceText, workspacePath))
            {
                if (seen.Add(candidate.RelativePath))
                {
                    results.Add(candidate);
                }
            }
        }
    }

    private static async Task<FeishuOptions> ResolveEffectiveOptionsAsync(
        IUserFeishuBotConfigService configService,
        string? username,
        string? appId)
    {
        if (!string.IsNullOrWhiteSpace(appId))
        {
            var appOptions = await configService.GetEffectiveOptionsByAppIdAsync(appId.Trim());
            if (appOptions != null)
            {
                return appOptions;
            }
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            return await configService.GetEffectiveOptionsAsync(username.Trim());
        }

        return configService.GetSharedDefaults();
    }
}

