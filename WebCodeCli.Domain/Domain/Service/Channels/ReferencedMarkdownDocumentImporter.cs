using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

public sealed class ReferencedMarkdownDocumentImporter
{
    private const string MarkdownDocumentTarget = "Markdown在线文档";

    private readonly ILogger<ReferencedMarkdownDocumentImporter> _logger;
    private readonly IReferencedMarkdownImportStateStore _stateStore;

    public ReferencedMarkdownDocumentImporter(
        ILogger<ReferencedMarkdownDocumentImporter> logger,
        IReferencedMarkdownImportStateStore stateStore)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    public async Task ImportMissingAsync(
        IFeishuCardKitClient cardKitClient,
        string chatId,
        string folderToken,
        IReadOnlyList<ReferencedMarkdownDocumentCandidate> candidates,
        string? documentAdminOpenId,
        FeishuOptions? optionsOverride,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cardKitClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderToken);
        ArgumentNullException.ThrowIfNull(candidates);

        var folderAdminGrantAttempted = false;
        string? deferredFolderAdminWarningMessage = null;

        foreach (var candidate in candidates)
        {
            try
            {
                var fingerprint = ReferencedMarkdownImportFingerprint.Compute(candidate.AbsolutePath);
                var tracked = await _stateStore.GetAsync(
                    folderToken,
                    candidate.AbsolutePath,
                    cancellationToken);
                var existing = await cardKitClient.FindCloudDocumentInFolderByTitleAsync(
                    folderToken,
                    candidate.Title,
                    cancellationToken,
                    optionsOverride);

                var document = existing;
                var successMessage = string.Empty;
                string? placementWarningMessage = null;

                if (document == null)
                {
                    (document, placementWarningMessage) = await ImportDocumentWithPlacementFallbackAsync(
                        cardKitClient,
                        folderToken,
                        candidate,
                        optionsOverride,
                        cancellationToken);
                    successMessage = $"已生成Markdown在线文档：[${candidate.Title}]({document.Url})".Replace("[$", "[", StringComparison.Ordinal);
                }
                else if (tracked != null && string.Equals(tracked.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    successMessage = $"已复用Markdown在线文档：[${candidate.Title}]({document.Url})".Replace("[$", "[", StringComparison.Ordinal);
                }
                else if (tracked != null)
                {
                    await OverwriteExistingDocumentAsync(
                        cardKitClient,
                        document,
                        candidate,
                        optionsOverride,
                        cancellationToken);
                    successMessage = $"已更新Markdown在线文档：[${candidate.Title}]({document.Url})".Replace("[$", "[", StringComparison.Ordinal);
                }
                else
                {
                    successMessage = $"已复用Markdown在线文档：[${candidate.Title}]({document.Url})".Replace("[$", "[", StringComparison.Ordinal);
                }

                await _stateStore.UpsertAsync(
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
                    cancellationToken);

                if (!folderAdminGrantAttempted && !string.IsNullOrWhiteSpace(documentAdminOpenId))
                {
                    folderAdminGrantAttempted = true;
                    try
                    {
                        await cardKitClient.GrantCloudFolderMemberFullAccessAsync(
                            folderToken,
                            documentAdminOpenId,
                            cancellationToken,
                            optionsOverride);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(
                            ex,
                            "Referenced markdown import folder admin grant failed for chat {ChatId}, title {Title}",
                            chatId,
                            candidate.Title);
                        deferredFolderAdminWarningMessage = BuildFolderAdminGrantWarningMessage(ex);
                    }
                }

                var documentAdminWarningMessage = await EnsureDocumentPermissionsAsync(
                    cardKitClient,
                    document,
                    documentAdminOpenId,
                    optionsOverride,
                    cancellationToken);

                await TrySendTextMessageAsync(cardKitClient, chatId, successMessage, cancellationToken, optionsOverride);

                if (!string.IsNullOrWhiteSpace(placementWarningMessage))
                {
                    await TrySendTextMessageAsync(cardKitClient, chatId, placementWarningMessage, cancellationToken, optionsOverride);
                }

                if (!string.IsNullOrWhiteSpace(documentAdminWarningMessage))
                {
                    await TrySendTextMessageAsync(cardKitClient, chatId, documentAdminWarningMessage, cancellationToken, optionsOverride);
                }

                if (!string.IsNullOrWhiteSpace(deferredFolderAdminWarningMessage))
                {
                    await TrySendTextMessageAsync(cardKitClient, chatId, deferredFolderAdminWarningMessage, cancellationToken, optionsOverride);
                    deferredFolderAdminWarningMessage = null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Referenced markdown import failed for chat {ChatId}, title {Title}",
                    chatId,
                    candidate.Title);
                await TrySendTextMessageAsync(
                    cardKitClient,
                    chatId,
                    BuildFailureMessage(candidate.Title, ex),
                    cancellationToken,
                    optionsOverride);
            }
        }
    }

    private async Task OverwriteExistingDocumentAsync(
        IFeishuCardKitClient cardKitClient,
        FeishuCloudDocumentInfo document,
        ReferencedMarkdownDocumentCandidate candidate,
        FeishuOptions? optionsOverride,
        CancellationToken cancellationToken)
    {
        var childBlockIds = await cardKitClient.ListCloudDocumentChildBlockIdsAsync(
            document.DocumentId,
            document.RootBlockId,
            cancellationToken,
            optionsOverride);

        if (childBlockIds.Count > 0)
        {
            await cardKitClient.DeleteCloudDocumentChildBlocksAsync(
                document.DocumentId,
                document.RootBlockId,
                0,
                childBlockIds.Count - 1,
                cancellationToken,
                optionsOverride);
        }

        var markdown = await File.ReadAllTextAsync(candidate.AbsolutePath, cancellationToken);
        JsonElement converted;
        try
        {
            converted = await cardKitClient.ConvertMarkdownToCloudDocumentBlocksAsync(
                markdown,
                cancellationToken,
                optionsOverride);
        }
        catch
        {
            await cardKitClient.AppendCloudDocumentTextAsync(
                document.DocumentId,
                document.RootBlockId,
                markdown,
                cancellationToken,
                optionsOverride);
            return;
        }

        var blocks = ExtractBlocks(converted);
        if (blocks.Count == 0)
        {
            await cardKitClient.AppendCloudDocumentTextAsync(
                document.DocumentId,
                document.RootBlockId,
                markdown,
                cancellationToken,
                optionsOverride);
            return;
        }

        try
        {
            await cardKitClient.AppendCloudDocumentBlocksAsync(
                document.DocumentId,
                document.RootBlockId,
                blocks,
                cancellationToken,
                optionsOverride);
        }
        catch
        {
            await cardKitClient.AppendCloudDocumentTextAsync(
                document.DocumentId,
                document.RootBlockId,
                markdown,
                cancellationToken,
                optionsOverride);
        }
    }

    private async Task<(FeishuCloudDocumentInfo Document, string? PlacementWarningMessage)> ImportDocumentWithPlacementFallbackAsync(
        IFeishuCardKitClient cardKitClient,
        string folderToken,
        ReferencedMarkdownDocumentCandidate candidate,
        FeishuOptions? optionsOverride,
        CancellationToken cancellationToken)
    {
        var content = await File.ReadAllBytesAsync(candidate.AbsolutePath, cancellationToken);
        var fileName = Path.GetFileName(candidate.AbsolutePath);

        try
        {
            var imported = await cardKitClient.ImportMarkdownFileAsCloudDocumentAsync(
                fileName,
                content,
                candidate.Title,
                folderToken,
                cancellationToken,
                optionsOverride);
            return (imported, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Referenced markdown import direct placement failed for title {Title}, falling back to default directory",
                candidate.Title);

            var imported = await cardKitClient.ImportMarkdownFileAsCloudDocumentAsync(
                fileName,
                content,
                candidate.Title,
                folderToken: null,
                cancellationToken,
                optionsOverride);

            return (imported, BuildPlacementWarningMessage(ex));
        }
    }

    private async Task<string?> EnsureDocumentPermissionsAsync(
        IFeishuCardKitClient cardKitClient,
        FeishuCloudDocumentInfo document,
        string? documentAdminOpenId,
        FeishuOptions? optionsOverride,
        CancellationToken cancellationToken)
    {
        await cardKitClient.SetCloudDocumentTenantReadableAsync(
            document.DocumentId,
            cancellationToken,
            optionsOverride);

        if (string.IsNullOrWhiteSpace(documentAdminOpenId))
        {
            return null;
        }

        try
        {
            await cardKitClient.GrantCloudDocumentMemberFullAccessAsync(
                document.DocumentId,
                documentAdminOpenId,
                cancellationToken,
                optionsOverride);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Referenced markdown import document admin grant failed for document {DocumentId}",
                document.DocumentId);
            return BuildDocumentAdminGrantWarningMessage(ex);
        }
    }

    private async Task TrySendTextMessageAsync(
        IFeishuCardKitClient cardKitClient,
        string chatId,
        string content,
        CancellationToken cancellationToken,
        FeishuOptions? optionsOverride)
    {
        try
        {
            await cardKitClient.SendTextMessageAsync(
                chatId,
                content,
                cancellationToken,
                optionsOverride);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Referenced markdown import notification failed for chat {ChatId}",
                chatId);
        }
    }

    private static string BuildFailureMessage(string title, Exception exception)
    {
        var scopeHint = TryExtractPermissionScopes(exception.Message);
        if (!string.IsNullOrWhiteSpace(scopeHint))
        {
            var guidance = TryExtractPermissionGuidance(exception.Message);
            return string.IsNullOrWhiteSpace(guidance)
                ? $"Markdown在线文档处理失败：{title}。飞书应用缺少文档权限，请开通 {scopeHint} 后重试。"
                : $"Markdown在线文档处理失败：{title}。飞书应用缺少文档权限，请开通 {scopeHint} 后重试。{guidance}";
        }

        if (IsDriveResourceNotFoundError(exception.Message))
        {
            return $"Markdown在线文档处理失败：{title}。飞书文档或目标文件夹资源不存在或已失效，请稍后重试；若持续出现，请检查目标文件夹是否已被删除。";
        }

        var compactReason = BuildCompactErrorReason(exception.Message);
        return string.IsNullOrWhiteSpace(compactReason)
            ? $"Markdown在线文档处理失败：{title}。请检查飞书文档权限或服务日志。"
            : $"Markdown在线文档处理失败：{title}。{compactReason}";
    }

    private static string BuildPlacementWarningMessage(Exception exception)
    {
        var scopeHint = TryExtractPermissionScopes(exception.Message);
        if (!string.IsNullOrWhiteSpace(scopeHint))
        {
            var guidance = TryExtractPermissionGuidance(exception.Message);
            return string.IsNullOrWhiteSpace(guidance)
                ? $"{MarkdownDocumentTarget}已生成，但归档到会话文档文件夹失败：飞书应用缺少文档权限，请开通 {scopeHint} 后重试。文档已保留在飞书默认目录。"
                : $"{MarkdownDocumentTarget}已生成，但归档到会话文档文件夹失败：飞书应用缺少文档权限，请开通 {scopeHint} 后重试。{guidance}文档已保留在飞书默认目录。";
        }

        if (IsDriveResourceNotFoundError(exception.Message))
        {
            return $"{MarkdownDocumentTarget}已生成，但归档到会话文档文件夹时，飞书文档或目标文件夹资源不存在或已失效。文档已保留在飞书默认目录；若持续出现，请检查目标文件夹是否已被删除。";
        }

        var compactReason = BuildCompactErrorReason(exception.Message);
        return string.IsNullOrWhiteSpace(compactReason)
            ? $"{MarkdownDocumentTarget}已生成，但归档到会话文档文件夹失败。文档已保留在飞书默认目录。"
            : $"{MarkdownDocumentTarget}已生成，但归档到会话文档文件夹失败：{compactReason}。文档已保留在飞书默认目录。";
    }

    private static string BuildDocumentAdminGrantWarningMessage(Exception exception)
    {
        var scopeHint = TryExtractPermissionScopes(exception.Message);
        if (!string.IsNullOrWhiteSpace(scopeHint))
        {
            var guidance = TryExtractPermissionGuidance(exception.Message);
            return string.IsNullOrWhiteSpace(guidance)
                ? $"{MarkdownDocumentTarget}已生成，但文档管理员权限授予失败：飞书应用缺少文档权限，请开通 {scopeHint} 后重试。"
                : $"{MarkdownDocumentTarget}已生成，但文档管理员权限授予失败：飞书应用缺少文档权限，请开通 {scopeHint} 后重试。{guidance}";
        }

        if (IsDriveResourceNotFoundError(exception.Message))
        {
            return $"{MarkdownDocumentTarget}已生成，但文档管理员权限授予失败：飞书文档资源不存在或已失效，请稍后重试；若持续出现，请检查目标文档是否已被删除。";
        }

        var compactReason = BuildCompactErrorReason(exception.Message);
        return string.IsNullOrWhiteSpace(compactReason)
            ? $"{MarkdownDocumentTarget}已生成，但文档管理员权限授予失败，请稍后重试。"
            : $"{MarkdownDocumentTarget}已生成，但文档管理员权限授予失败：{compactReason}";
    }

    private static string BuildFolderAdminGrantWarningMessage(Exception exception)
    {
        var scopeHint = TryExtractPermissionScopes(exception.Message);
        if (!string.IsNullOrWhiteSpace(scopeHint))
        {
            var guidance = TryExtractPermissionGuidance(exception.Message);
            return string.IsNullOrWhiteSpace(guidance)
                ? $"{MarkdownDocumentTarget}已生成，但会话文档文件夹管理员权限授予失败：飞书应用缺少文档权限，请开通 {scopeHint} 后重试。"
                : $"{MarkdownDocumentTarget}已生成，但会话文档文件夹管理员权限授予失败：飞书应用缺少文档权限，请开通 {scopeHint} 后重试。{guidance}";
        }

        if (IsDriveResourceNotFoundError(exception.Message))
        {
            return $"{MarkdownDocumentTarget}已生成，但会话文档文件夹管理员权限授予失败：目标文件夹资源不存在或已失效，请稍后重试；若持续出现，请检查目标文件夹是否已被删除。";
        }

        var compactReason = BuildCompactErrorReason(exception.Message);
        return string.IsNullOrWhiteSpace(compactReason)
            ? $"{MarkdownDocumentTarget}已生成，但会话文档文件夹管理员权限授予失败，请稍后重试。"
            : $"{MarkdownDocumentTarget}已生成，但会话文档文件夹管理员权限授予失败：{compactReason}";
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

        var scopes = System.Text.RegularExpressions.Regex.Matches(
                message,
                @"\b(?:docx|drive):[a-z0-9.-]+(?::[a-z0-9.-]+)*\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Select(static match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return scopes.Count == 0
            ? null
            : string.Join("、", scopes);
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
