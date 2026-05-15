using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service.Adapters;

namespace WebCodeCli.Domain.Domain.Service;

[ServiceDescription(typeof(IMessageSubmissionService), ServiceLifetime.Scoped)]
public class MessageSubmissionService : IMessageSubmissionService
{
    private const int MaxAttachmentCount = 10;
    private const long MaxAttachmentSizeBytes = 100L * 1024 * 1024;

    private static readonly HashSet<string> TextLikeMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json",
        "application/xml",
        "application/javascript",
        "application/x-javascript",
        "application/x-sh",
        "application/x-httpd-php",
        "application/x-yaml",
        "application/yaml",
        "text/csv",
        "text/markdown"
    };

    private static readonly HashSet<string> TextLikeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".xml", ".yaml", ".yml",
        ".cs", ".js", ".jsx", ".ts", ".tsx", ".css", ".scss", ".less",
        ".html", ".htm", ".sql", ".py", ".java", ".go", ".rs", ".c",
        ".h", ".hpp", ".cpp", ".sh", ".ps1", ".bat", ".cmd", ".log",
        ".csv", ".config", ".ini", ".toml"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".svg"
    };

    private static readonly HashSet<string> OfficeMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/msword",
        "application/vnd.ms-excel",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    };

    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx"
    };

    private readonly ICliExecutorService _cliExecutorService;
    private readonly ICliAdapterFactory _cliAdapterFactory;
    private readonly IAttachmentStagingService _attachmentStagingService;
    private readonly ILogger<MessageSubmissionService> _logger;

    public MessageSubmissionService(
        ICliExecutorService cliExecutorService,
        ICliAdapterFactory cliAdapterFactory,
        IAttachmentStagingService attachmentStagingService,
        ILogger<MessageSubmissionService> logger)
    {
        _cliExecutorService = cliExecutorService;
        _cliAdapterFactory = cliAdapterFactory;
        _attachmentStagingService = attachmentStagingService;
        _logger = logger;
    }

    public async Task<PreparedMessageSubmission> PrepareAsync(
        MessageDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.ToolId);

        var trimmedText = draft.Text?.Trim() ?? string.Empty;
        var attachments = draft.Attachments ?? [];

        if (string.IsNullOrWhiteSpace(trimmedText) && attachments.Count == 0)
        {
            throw new InvalidOperationException("Message text or attachments are required.");
        }

        if (string.IsNullOrWhiteSpace(trimmedText) && attachments.Count > 0)
        {
            throw new InvalidOperationException("Message text is required when attachments are included.");
        }

        if (attachments.Count > MaxAttachmentCount)
        {
            throw new InvalidOperationException($"A maximum of {MaxAttachmentCount} attachments is supported per submission.");
        }

        ValidateAttachments(attachments);

        var tool = _cliExecutorService.GetTool(draft.ToolId, draft.SubmittedBy);
        if (tool == null)
        {
            throw new InvalidOperationException($"CLI tool '{draft.ToolId}' is not available.");
        }

        var workspacePath = await _cliExecutorService.InitializeSessionWorkspaceAsync(draft.SessionId);
        var sessionContext = new CliSessionContext
        {
            SessionId = draft.SessionId,
            CliThreadId = _cliExecutorService.GetCliThreadId(draft.SessionId),
            WorkingDirectory = workspacePath
        };

        List<StagedMessageAttachment> stagedAttachments = [];
        if (attachments.Count > 0)
        {
            stagedAttachments = await _attachmentStagingService.StageAsync(
                draft.SessionId,
                draft.DraftId,
                attachments,
                cancellationToken);
        }

        var capabilities = ResolveAttachmentCapabilities(tool);
        var nativeAttachments = new List<CliExecutionAttachment>();
        var referenceAttachments = new List<CliExecutionAttachment>();

        var remainingNativeSlots = capabilities.SupportsNativeAttachments
            ? capabilities.SupportsMultipleNativeAttachments ? int.MaxValue : 1
            : 0;

        foreach (var stagedAttachment in stagedAttachments)
        {
            var executionAttachment = new CliExecutionAttachment
            {
                DisplayName = stagedAttachment.Metadata.DisplayName,
                Kind = stagedAttachment.Metadata.Kind,
                WorkspaceRelativePath = stagedAttachment.Metadata.WorkspaceRelativePath,
                AbsolutePath = stagedAttachment.AbsolutePath
            };

            var canUseNative =
                capabilities.SupportsNativeAttachments &&
                remainingNativeSlots > 0 &&
                capabilities.NativeKinds.Contains(stagedAttachment.Metadata.Kind);

            if (canUseNative)
            {
                nativeAttachments.Add(executionAttachment);
                if (remainingNativeSlots != int.MaxValue)
                {
                    remainingNativeSlots--;
                }
                continue;
            }

            if (!capabilities.AllowsReferenceFallback)
            {
                throw new InvalidOperationException(
                    $"Attachment '{stagedAttachment.Metadata.DisplayName}' is not supported by tool '{draft.ToolId}'.");
            }

            referenceAttachments.Add(executionAttachment);
        }

        var warnings = new List<MessageSubmissionWarning>();
        if (nativeAttachments.Count > 0 && referenceAttachments.Count > 0)
        {
            warnings.Add(new MessageSubmissionWarning
            {
                Code = "partial-downgrade",
                Message = "Some attachments will be passed natively while others will be referenced from the workspace."
            });
        }

        var normalizedAttachments = stagedAttachments.Select(staged => staged.Metadata).ToList();
        var stagingRootRelativePath = AttachmentStagingService.BuildSubmissionRootRelativePath(draft.DraftId);

        var prepared = new PreparedMessageSubmission
        {
            SessionId = draft.SessionId,
            ToolId = draft.ToolId,
            Text = trimmedText,
            Attachments = normalizedAttachments,
            StagingRootRelativePath = stagingRootRelativePath,
            UserMessage = new ChatMessage
            {
                Role = "user",
                Content = trimmedText,
                CliToolId = draft.ToolId,
                CreatedAt = DateTime.UtcNow,
                IsCompleted = true,
                Attachments = normalizedAttachments
            },
            ExecutionRequest = new CliExecutionRequest
            {
                SessionId = draft.SessionId,
                ToolId = draft.ToolId,
                PromptText = trimmedText,
                SessionContext = sessionContext,
                NativeAttachments = nativeAttachments,
                ReferenceAttachments = referenceAttachments,
                Warnings = warnings.ToList()
            },
            Warnings = warnings
        };

        _logger.LogDebug(
            "Prepared message submission {DraftId} for session {SessionId} with {NativeCount} native and {ReferenceCount} reference attachment(s)",
            draft.DraftId,
            draft.SessionId,
            nativeAttachments.Count,
            referenceAttachments.Count);

        return prepared;
    }

    internal static MessageAttachmentKind DetectAttachmentKind(string fileName, string contentType)
    {
        var normalizedContentType = (contentType ?? string.Empty).Trim();
        var extension = Path.GetExtension(fileName ?? string.Empty);

        if (normalizedContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || ImageExtensions.Contains(extension))
        {
            return MessageAttachmentKind.Image;
        }

        if (string.Equals(normalizedContentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return MessageAttachmentKind.Pdf;
        }

        if (normalizedContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || TextLikeMimeTypes.Contains(normalizedContentType)
            || TextLikeExtensions.Contains(extension))
        {
            return MessageAttachmentKind.Text;
        }

        if (OfficeMimeTypes.Contains(normalizedContentType)
            || OfficeExtensions.Contains(extension))
        {
            return MessageAttachmentKind.Office;
        }

        throw new InvalidOperationException(
            $"Attachment '{fileName}' is not supported. Supported types include images, text/code, PDFs, and office documents.");
    }

    private static void ValidateAttachments(IReadOnlyCollection<MessageDraftAttachmentInput> attachments)
    {
        foreach (var attachment in attachments)
        {
            if (attachment.Content == null)
            {
                throw new InvalidOperationException(
                    $"Attachment '{attachment.FileName}' content is required.");
            }

            if (attachment.Content.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Attachment '{attachment.FileName}' is empty and cannot be submitted.");
            }

            if (attachment.Content.LongLength > MaxAttachmentSizeBytes)
            {
                throw new InvalidOperationException(
                    $"Attachment '{attachment.FileName}' exceeds the maximum size of {MaxAttachmentSizeBytes / (1024 * 1024)} MB.");
            }

            _ = DetectAttachmentKind(attachment.FileName, attachment.ContentType);
        }
    }

    private CliAttachmentCapabilities ResolveAttachmentCapabilities(CliToolConfig tool)
    {
        var adapter = _cliAdapterFactory.GetAdapter(tool);
        if (adapter != null)
        {
            return adapter.GetAttachmentCapabilities(tool);
        }

        return CliAttachmentCapabilities.ReferenceOnly();
    }
}
