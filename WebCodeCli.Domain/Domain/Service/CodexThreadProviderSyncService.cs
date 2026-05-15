using System.Data.SQLite;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service;

[ServiceDescription(typeof(ICodexThreadProviderSyncService), ServiceLifetime.Singleton)]
public sealed class CodexThreadProviderSyncService : ICodexThreadProviderSyncService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly ILogger<CodexThreadProviderSyncService> _logger;

    public CodexThreadProviderSyncService(ILogger<CodexThreadProviderSyncService> logger)
    {
        _logger = logger;
    }

    public async Task<CodexThreadProviderSyncResult> SyncThreadProviderAsync(
        CodexThreadProviderSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionWorkspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetProviderId);

        var sessionWorkspacePath = Path.GetFullPath(request.SessionWorkspacePath);
        var threadId = request.ThreadId.Trim();
        var targetProviderId = request.TargetProviderId.Trim();
        var targetCodexRoot = Path.Combine(sessionWorkspacePath, ".codex");
        Directory.CreateDirectory(targetCodexRoot);

        var sourceCodexRoot = ResolveSourceCodexConfigDirectory();
        var result = new CodexThreadProviderSyncResult
        {
            SessionWorkspacePath = sessionWorkspacePath,
            ThreadId = threadId,
            TargetProviderId = targetProviderId,
            SourceCodexHome = sourceCodexRoot
        };

        var sourceMatches = await FindRolloutFilesAsync(sourceCodexRoot, threadId, cancellationToken);
        var seededFromSource = await SeedMissingRolloutFilesAsync(
            sourceCodexRoot,
            targetCodexRoot,
            sourceMatches,
            threadId,
            cancellationToken);
        result.SeededFromSource = seededFromSource;

        var localMatches = await FindRolloutFilesAsync(targetCodexRoot, threadId, cancellationToken);
        if (localMatches.Count == 0)
        {
            throw new InvalidOperationException($"未在本地 Codex 快照中找到线程 {threadId} 的 rollout 文件。");
        }

        foreach (var rolloutPath in localMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rewriteOutcome = await TryRewriteRolloutFileAsync(
                rolloutPath,
                targetCodexRoot,
                threadId,
                targetProviderId,
                cancellationToken);
            switch (rewriteOutcome)
            {
                case RolloutRewriteOutcome.Updated:
                    result.UpdatedRolloutFiles.Add(rolloutPath);
                    break;
                case RolloutRewriteOutcome.Locked:
                    result.SkippedRolloutFiles.Add(rolloutPath);
                    break;
            }
        }

        result.SqliteResult = await UpdateThreadProviderInSqliteAsync(sessionWorkspacePath, threadId, targetProviderId, cancellationToken);
        result.HasWarnings = result.SkippedRolloutFiles.Count > 0
            || result.SqliteResult.Missing
            || result.SqliteResult.Locked
            || !result.SqliteResult.Updated;
        result.Message = BuildResultMessage(result);

        if (result.UpdatedRolloutFiles.Count == 0 && !result.SqliteResult.Updated && !result.HasWarnings)
        {
            result.Message = $"Codex thread {threadId} 已经是当前 provider {targetProviderId}。";
        }

        return result;
    }

    private async Task<List<string>> FindRolloutFilesAsync(string codexRoot, string threadId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(codexRoot) || !Directory.Exists(codexRoot))
        {
            return [];
        }

        var candidatePaths = new List<string>();
        foreach (var rootDirectoryName in new[] { "sessions", "archived_sessions" })
        {
            var rootDirectory = Path.Combine(codexRoot, rootDirectoryName);
            if (!Directory.Exists(rootDirectory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(rootDirectory, $"*{threadId}*.jsonl", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidatePaths.Add(path);
            }
        }

        if (candidatePaths.Count == 0)
        {
            foreach (var rootDirectoryName in new[] { "sessions", "archived_sessions" })
            {
                var rootDirectory = Path.Combine(codexRoot, rootDirectoryName);
                if (!Directory.Exists(rootDirectory))
                {
                    continue;
                }

                foreach (var path in Directory.EnumerateFiles(rootDirectory, "*.jsonl", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    candidatePaths.Add(path);
                }
            }
        }

        var matchedPaths = new List<string>();
        foreach (var candidatePath in candidatePaths.Distinct(GetPathComparer()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await RolloutFileBelongsToThreadAsync(candidatePath, threadId, cancellationToken))
            {
                matchedPaths.Add(candidatePath);
            }
        }

        return matchedPaths;
    }

    private async Task<bool> SeedMissingRolloutFilesAsync(
        string sourceCodexRoot,
        string targetCodexRoot,
        IReadOnlyCollection<string> sourceMatches,
        string threadId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceCodexRoot)
            || string.IsNullOrWhiteSpace(targetCodexRoot)
            || !Directory.Exists(sourceCodexRoot)
            || Path.GetFullPath(sourceCodexRoot).Equals(Path.GetFullPath(targetCodexRoot), GetPathComparison()))
        {
            return false;
        }

        var seeded = false;
        foreach (var sourcePath in sourceMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceCodexRoot, sourcePath);
            var targetPath = Path.Combine(targetCodexRoot, relativePath);
            if (File.Exists(targetPath))
            {
                continue;
            }

            try
            {
                var content = await File.ReadAllTextAsync(sourcePath, cancellationToken);
                var targetDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                await File.WriteAllTextAsync(targetPath, content, cancellationToken);
                seeded = true;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Seed Codex rollout file failed: {SourcePath}", sourcePath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Seed Codex rollout file denied: {SourcePath}", sourcePath);
            }
        }

        return seeded;
    }

    private async Task<RolloutRewriteOutcome> TryRewriteRolloutFileAsync(
        string rolloutPath,
        string targetCodexRoot,
        string threadId,
        string targetProviderId,
        CancellationToken cancellationToken)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(rolloutPath, cancellationToken);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Read rollout file failed: {RolloutPath}", rolloutPath);
            return RolloutRewriteOutcome.Locked;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Read rollout file denied: {RolloutPath}", rolloutPath);
            return RolloutRewriteOutcome.Locked;
        }

        var firstLineSplit = SplitFirstLine(content);
        if (string.IsNullOrWhiteSpace(firstLineSplit.FirstLine))
        {
            return RolloutRewriteOutcome.Ignored;
        }

        if (!TryGetRolloutThreadId(firstLineSplit.FirstLine, out var matchedThreadId)
            || !string.Equals(matchedThreadId, threadId, StringComparison.OrdinalIgnoreCase))
        {
            return RolloutRewriteOutcome.Ignored;
        }

        var updatedFirstLine = RewriteRolloutProviderId(firstLineSplit.FirstLine, targetProviderId);
        if (string.Equals(updatedFirstLine, firstLineSplit.FirstLine, StringComparison.Ordinal))
        {
            return RolloutRewriteOutcome.Ignored;
        }

        var updatedContent = string.Concat(updatedFirstLine, firstLineSplit.LineBreak, firstLineSplit.Remainder);
        try
        {
            if (!await TryBackupRolloutFileAsync(rolloutPath, targetCodexRoot, content, cancellationToken))
            {
                return RolloutRewriteOutcome.Locked;
            }

            await File.WriteAllTextAsync(rolloutPath, updatedContent, cancellationToken);
            return RolloutRewriteOutcome.Updated;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Write rollout file failed: {RolloutPath}", rolloutPath);
            return RolloutRewriteOutcome.Locked;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Write rollout file denied: {RolloutPath}", rolloutPath);
            return RolloutRewriteOutcome.Locked;
        }
    }

    private async Task<bool> TryBackupRolloutFileAsync(
        string rolloutPath,
        string targetCodexRoot,
        string content,
        CancellationToken cancellationToken)
    {
        string relativePath;
        try
        {
            relativePath = Path.GetRelativePath(targetCodexRoot, rolloutPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Resolve rollout backup path failed: {RolloutPath}", rolloutPath);
            return false;
        }

        if (relativePath.StartsWith("..", GetPathComparison())
            || Path.IsPathRooted(relativePath))
        {
            _logger.LogWarning("Skip rollout backup because path escapes session-local Codex root: {RolloutPath}", rolloutPath);
            return false;
        }

        var backupPath = Path.Combine(targetCodexRoot, "rollout-backups", relativePath);

        try
        {
            var backupDirectory = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrWhiteSpace(backupDirectory))
            {
                Directory.CreateDirectory(backupDirectory);
            }

            await File.WriteAllTextAsync(backupPath, content, cancellationToken);
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Write rollout backup failed: {BackupPath}", backupPath);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Write rollout backup denied: {BackupPath}", backupPath);
            return false;
        }
    }

    private static async Task<bool> RolloutFileBelongsToThreadAsync(
        string rolloutPath,
        string threadId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(rolloutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var firstLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return false;
            }

            return TryGetRolloutThreadId(firstLine, out var matchedThreadId)
                && string.Equals(matchedThreadId, threadId, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<CodexThreadProviderSyncSqliteResult> UpdateThreadProviderInSqliteAsync(
        string sessionWorkspacePath,
        string threadId,
        string targetProviderId,
        CancellationToken cancellationToken)
    {
        var databasePath = Path.Combine(sessionWorkspacePath, ".codex", "state_5.sqlite");
        var result = new CodexThreadProviderSyncSqliteResult
        {
            DatabasePath = databasePath
        };

        if (!File.Exists(databasePath))
        {
            result.Missing = true;
            result.Message = "state_5.sqlite 不存在，已跳过 sqlite 同步。";
            return result;
        }

        result.Exists = true;

        try
        {
            var connectionString = new SQLiteConnectionStringBuilder
            {
                DataSource = databasePath,
                Version = 3,
                FailIfMissing = true,
                Pooling = false
            }.ToString();

            using var connection = new SQLiteConnection(connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE threads SET model_provider = @targetProvider WHERE id = @threadId";
            command.Parameters.AddWithValue("@targetProvider", targetProviderId);
            command.Parameters.AddWithValue("@threadId", threadId);
            result.RowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
            result.Updated = result.RowsAffected > 0;
            result.Message = result.Updated
                ? $"sqlite 中的线程 {threadId} 已更新为 provider {targetProviderId}。"
                : $"sqlite 中未找到线程 {threadId}，未更新任何行。";
            return result;
        }
        catch (SQLiteException ex) when (IsDatabaseLocked(ex))
        {
            _logger.LogWarning(ex, "SQLite database locked: {DatabasePath}", databasePath);
            result.Locked = true;
            result.Message = "state_5.sqlite 当前被占用，已跳过 sqlite 同步。";
            return result;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "SQLite file read/write failed: {DatabasePath}", databasePath);
            result.Locked = true;
            result.Message = "state_5.sqlite 当前被占用，已跳过 sqlite 同步。";
            return result;
        }
    }

    private static string BuildResultMessage(CodexThreadProviderSyncResult result)
    {
        var rolloutCount = result.UpdatedRolloutFiles.Count;
        var sqliteMessage = result.SqliteResult.Message;

        if (result.HasWarnings)
        {
            var warningParts = new List<string>();
            if (result.SkippedRolloutFiles.Count > 0)
            {
                warningParts.Add($"{result.SkippedRolloutFiles.Count} 个 rollout 文件被跳过");
            }

            if (!string.IsNullOrWhiteSpace(sqliteMessage))
            {
                warningParts.Add(sqliteMessage);
            }

            if (result.SeededFromSource)
            {
                warningParts.Add("已先从 source 同步缺失的本地 rollout 文件");
            }

            var warningText = warningParts.Count == 0
                ? "存在部分同步项被跳过。"
                : string.Join("；", warningParts);

            return $"Codex thread {result.ThreadId} 已同步到 provider {result.TargetProviderId}，但存在部分跳过项：{warningText} 已更新 {rolloutCount} 个 rollout 文件。";
        }

        return result.SqliteResult.Updated
            ? $"Codex thread {result.ThreadId} 已同步到 provider {result.TargetProviderId}，已更新 {rolloutCount} 个 rollout 文件并更新 sqlite。"
            : $"Codex thread {result.ThreadId} 已同步到 provider {result.TargetProviderId}，已更新 {rolloutCount} 个 rollout 文件。";
    }

    private static bool TryGetRolloutThreadId(string firstLine, out string? threadId)
    {
        threadId = null;
        try
        {
            using var document = JsonDocument.Parse(firstLine);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeProperty)
                || !string.Equals(typeProperty.GetString(), "session_meta", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!root.TryGetProperty("payload", out var payloadProperty)
                || payloadProperty.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!payloadProperty.TryGetProperty("id", out var idProperty))
            {
                return false;
            }

            threadId = idProperty.GetString();
            return !string.IsNullOrWhiteSpace(threadId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string RewriteRolloutProviderId(string firstLine, string targetProviderId)
    {
        var node = JsonNode.Parse(firstLine)?.AsObject();
        if (node == null)
        {
            return firstLine;
        }

        if (node["payload"] is JsonObject payloadObject)
        {
            payloadObject["model_provider"] = targetProviderId;
        }

        return node.ToJsonString(JsonOptions);
    }

    private static (string FirstLine, string LineBreak, string Remainder) SplitFirstLine(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var newlineIndex = content.IndexOf('\n');
        if (newlineIndex < 0)
        {
            return (content, string.Empty, string.Empty);
        }

        var lineBreakStart = newlineIndex > 0 && content[newlineIndex - 1] == '\r'
            ? newlineIndex - 1
            : newlineIndex;
        var firstLine = content[..lineBreakStart];
        var lineBreak = content[lineBreakStart..(newlineIndex + 1)];
        var remainder = newlineIndex + 1 < content.Length
            ? content[(newlineIndex + 1)..]
            : string.Empty;

        return (firstLine, lineBreak, remainder);
    }

    private static StringComparison GetPathComparison()
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool IsDatabaseLocked(SQLiteException exception)
        => exception.ResultCode is SQLiteErrorCode.Busy or SQLiteErrorCode.Locked;

    private static string ResolveSourceCodexConfigDirectory()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            return NormalizeCodexConfigDirectory(codexHome);
        }

        var homePath = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(homePath))
        {
            homePath = Environment.GetEnvironmentVariable("USERPROFILE");
        }

        if (string.IsNullOrWhiteSpace(homePath))
        {
            homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return string.IsNullOrWhiteSpace(homePath)
            ? string.Empty
            : NormalizeCodexConfigDirectory(homePath);
    }

    private static string NormalizeCodexConfigDirectory(string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return string.Empty;
        }

        var trimmedPath = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(trimmedPath), ".codex", StringComparison.OrdinalIgnoreCase)
            ? trimmedPath
            : Path.Combine(trimmedPath, ".codex");
    }

    private enum RolloutRewriteOutcome
    {
        Ignored = 0,
        Updated = 1,
        Locked = 2
    }
}
