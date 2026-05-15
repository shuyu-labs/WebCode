using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service;

[ServiceDescription(typeof(IAttachmentStagingService), ServiceLifetime.Scoped)]
public class AttachmentStagingService : IAttachmentStagingService
{
    internal const string HiddenRootDirectoryName = ".webcode";
    internal const string MessageInputDirectoryName = "message-inputs";
    private const string SubmissionMarkerFileName = ".submission-id";

    private readonly ICliExecutorService _cliExecutorService;
    private readonly ILogger<AttachmentStagingService> _logger;

    public AttachmentStagingService(
        ICliExecutorService cliExecutorService,
        ILogger<AttachmentStagingService> logger)
    {
        _cliExecutorService = cliExecutorService;
        _logger = logger;
    }

    public async Task<List<StagedMessageAttachment>> StageAsync(
        string sessionId,
        string submissionId,
        IReadOnlyCollection<MessageDraftAttachmentInput> attachments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(submissionId);
        ArgumentNullException.ThrowIfNull(attachments);

        if (attachments.Count == 0)
        {
            return [];
        }

        var workspaceRoot = _cliExecutorService.GetSessionWorkspacePath(sessionId);
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new InvalidOperationException($"Could not resolve a workspace path for session '{sessionId}'.");
        }

        var workspaceFullPath = Path.GetFullPath(workspaceRoot);
        var hiddenRoot = Path.Combine(workspaceFullPath, HiddenRootDirectoryName);
        var messageInputsRoot = Path.Combine(hiddenRoot, MessageInputDirectoryName);
        var submissionDirectoryName = NormalizeSubmissionDirectoryName(submissionId);
        var submissionRoot = Path.Combine(messageInputsRoot, submissionDirectoryName);

        Directory.CreateDirectory(submissionRoot);
        TryHideDirectory(hiddenRoot);
        EnsureSubmissionDirectoryReservation(submissionRoot, submissionId, submissionDirectoryName);

        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stagedAttachments = new List<StagedMessageAttachment>(attachments.Count);

        foreach (var attachment in attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attachment.Content == null)
            {
                throw new InvalidOperationException(
                    $"Attachment '{attachment.FileName}' content is required.");
            }

            var displayName = string.IsNullOrWhiteSpace(attachment.FileName)
                ? "attachment"
                : attachment.FileName.Trim();
            var stagedFileName = CreateUniqueFileName(displayName, usedFileNames);
            var stagedAbsolutePath = Path.Combine(submissionRoot, stagedFileName);
            var stagedFullPath = Path.GetFullPath(stagedAbsolutePath);

            EnsurePathWithinRoot(submissionRoot, stagedFullPath, displayName);

            await File.WriteAllBytesAsync(stagedFullPath, attachment.Content, cancellationToken);

            var extension = Path.GetExtension(displayName);
            var metadata = new MessageAttachment
            {
                Id = attachment.Id,
                DisplayName = displayName,
                MimeType = string.IsNullOrWhiteSpace(attachment.ContentType)
                    ? "application/octet-stream"
                    : attachment.ContentType.Trim(),
                Extension = extension,
                SizeBytes = attachment.Content.LongLength,
                Kind = MessageSubmissionService.DetectAttachmentKind(displayName, attachment.ContentType),
                WorkspaceRelativePath = BuildWorkspaceRelativePath(submissionDirectoryName, stagedFileName),
                CreatedAt = DateTime.UtcNow
            };

            stagedAttachments.Add(new StagedMessageAttachment
            {
                InputId = attachment.Id,
                DisplayName = displayName,
                AbsolutePath = stagedFullPath,
                Metadata = metadata
            });
        }

        _logger.LogDebug(
            "Staged {Count} attachment(s) for session {SessionId} under {SubmissionRoot}",
            stagedAttachments.Count,
            sessionId,
            submissionRoot);

        return stagedAttachments;
    }

    private static string CreateUniqueFileName(string displayName, HashSet<string> usedFileNames)
    {
        var normalizedBaseName = NormalizeFileName(displayName);
        var baseName = Path.GetFileNameWithoutExtension(normalizedBaseName);
        var extension = Path.GetExtension(normalizedBaseName);
        var candidate = normalizedBaseName;
        var suffix = 2;

        while (!usedFileNames.Add(candidate))
        {
            candidate = $"{baseName}-{suffix}{extension}";
            suffix++;
        }

        return candidate;
    }

    private static string NormalizeFileName(string fileName)
    {
        var fileNameOnly = Path.GetFileName(fileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(fileNameOnly))
        {
            return "attachment";
        }

        var sanitizedChars = fileNameOnly
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
            .ToArray();
        var sanitized = new string(sanitizedChars).Trim();

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "attachment";
        }

        sanitized = sanitized.Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "attachment";
        }

        return sanitized;
    }

    internal static string NormalizeSubmissionDirectoryName(string submissionId)
    {
        return NormalizePathSegment(submissionId, "submission");
    }

    internal static string BuildSubmissionRootRelativePath(string submissionId)
    {
        return $"{HiddenRootDirectoryName}/{MessageInputDirectoryName}/{NormalizeSubmissionDirectoryName(submissionId)}";
    }

    private static string NormalizePathSegment(string segment, string fallback)
    {
        var sanitizedChars = segment
            .Trim()
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == '/' || ch == '\\' ? '_' : ch)
            .ToArray();
        var sanitized = new string(sanitizedChars).Trim('.', ' ');

        return string.IsNullOrWhiteSpace(sanitized)
            ? fallback
            : sanitized;
    }

    private static string BuildWorkspaceRelativePath(string submissionDirectoryName, string stagedFileName)
    {
        return $"{BuildSubmissionRootRelativePath(submissionDirectoryName)}/{stagedFileName}";
    }

    private static void EnsureSubmissionDirectoryReservation(
        string submissionRoot,
        string rawSubmissionId,
        string normalizedSubmissionDirectoryName)
    {
        var markerPath = Path.Combine(submissionRoot, SubmissionMarkerFileName);
        try
        {
            using var stream = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(rawSubmissionId);
            writer.Flush();
            return;
        }
        catch (IOException)
        {
        }

        var existingSubmissionId = TryReadReservationMarker(markerPath);
        if (existingSubmissionId != null)
        {
            if (!string.Equals(existingSubmissionId, rawSubmissionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Submission id normalization collision detected: '{rawSubmissionId}' maps to '{normalizedSubmissionDirectoryName}', which is already reserved for a different submission.");
            }

            return;
        }

        if (Directory.EnumerateFileSystemEntries(submissionRoot).Any(entry => !PathsEqual(entry, markerPath)))
        {
            throw new InvalidOperationException(
                $"Submission id normalization collision detected: '{rawSubmissionId}' maps to '{normalizedSubmissionDirectoryName}', but the staging directory already exists without a matching reservation marker.");
        }

        throw new InvalidOperationException(
            $"Submission id normalization collision detected: '{rawSubmissionId}' maps to '{normalizedSubmissionDirectoryName}', but the reservation marker could not be claimed safely.");
    }

    private static string? TryReadReservationMarker(string markerPath)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!File.Exists(markerPath))
            {
                return null;
            }

            try
            {
                return File.ReadAllText(markerPath);
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(10);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(10);
            }
        }

        return null;
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            comparison);
    }

    private static void EnsurePathWithinRoot(string rootPath, string candidatePath, string displayName)
    {
        var normalizedRoot = EnsureTrailingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!candidatePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Attachment '{displayName}' resolved outside the staging root.");
        }
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        return path[^1] == Path.DirectorySeparatorChar || path[^1] == Path.AltDirectorySeparatorChar
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static void TryHideDirectory(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !Directory.Exists(path))
            {
                return;
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Hidden) == 0)
            {
                File.SetAttributes(path, attributes | FileAttributes.Hidden);
            }
        }
        catch
        {
        }
    }
}
