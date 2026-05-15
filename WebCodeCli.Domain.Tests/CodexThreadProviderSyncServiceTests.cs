using Microsoft.Extensions.Logging.Abstractions;
using System.Data.SQLite;
using System.Text.Json;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Domain.Tests;

public class CodexThreadProviderSyncServiceTests
{
    [Fact]
    public async Task SyncThreadProviderAsync_RewritesOnlyMatchingLocalThreadRollout()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(workspaceRoot, "workspace");
        var codexRoot = Path.Combine(workspacePath, ".codex");
        var sessionsRoot = Path.Combine(codexRoot, "sessions", "2026", "04", "30");
        var archivedRoot = Path.Combine(codexRoot, "archived_sessions", "2026", "04", "30");
        var matchingPath = Path.Combine(sessionsRoot, "rollout-2026-04-30T10-00-00-thread-a.jsonl");
        var archivedMatchingPath = Path.Combine(archivedRoot, "rollout-2026-04-30T10-00-01-thread-a.jsonl");
        var falseCandidatePath = Path.Combine(sessionsRoot, "rollout-2026-04-30T10-00-02-thread-a-fake.jsonl");

        Directory.CreateDirectory(sessionsRoot);
        Directory.CreateDirectory(archivedRoot);
        await File.WriteAllTextAsync(
            matchingPath,
            """
            {"type":"session_meta","payload":{"id":"thread-a","cwd":"D:\\repo","model_provider":"old-provider"}}
            {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hello"}]}}
            """);
        await File.WriteAllTextAsync(
            archivedMatchingPath,
            """
            {"type":"session_meta","payload":{"id":"thread-a","cwd":"D:\\repo","model_provider":"old-provider"}}
            {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"archived"}]}}
            """);
        await File.WriteAllTextAsync(
            falseCandidatePath,
            """
            {"type":"session_meta","payload":{"id":"thread-b","cwd":"D:\\repo","model_provider":"old-provider"}}
            {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ignore"}]}}
            """);
        CreateStateDatabase(Path.Combine(workspacePath, ".codex", "state_5.sqlite"), "thread-a", "old-provider");

        try
        {
            var service = new CodexThreadProviderSyncService(NullLogger<CodexThreadProviderSyncService>.Instance);

            var result = await service.SyncThreadProviderAsync(new CodexThreadProviderSyncRequest
            {
                SessionWorkspacePath = workspacePath,
                ThreadId = "thread-a",
                TargetProviderId = "new-provider"
            });

            Assert.False(result.SeededFromSource);
            Assert.False(result.HasWarnings);
            Assert.Contains(result.UpdatedRolloutFiles, path => string.Equals(path, matchingPath, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.UpdatedRolloutFiles, path => string.Equals(path, archivedMatchingPath, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("new-provider", ReadProviderIdFromFirstLine(matchingPath));
            Assert.Equal("new-provider", ReadProviderIdFromFirstLine(archivedMatchingPath));
            Assert.Equal("old-provider", ReadProviderIdFromFirstLine(falseCandidatePath));
            Assert.False(result.SkippedRolloutFiles.Any());
            Assert.True(result.SqliteResult.Updated);
            Assert.Equal("new-provider", ReadProviderIdFromSqlite(Path.Combine(workspacePath, ".codex", "state_5.sqlite"), "thread-a"));
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SyncThreadProviderAsync_BacksUpMatchingRolloutBeforeRewrite()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(workspaceRoot, "workspace");
        var codexRoot = Path.Combine(workspacePath, ".codex");
        var sessionsRoot = Path.Combine(codexRoot, "sessions", "2026", "04", "30");
        var matchingPath = Path.Combine(sessionsRoot, "rollout-2026-04-30T10-00-00-thread-a.jsonl");
        var falseCandidatePath = Path.Combine(sessionsRoot, "rollout-2026-04-30T10-00-01-thread-b.jsonl");
        var expectedBackupPath = Path.Combine(codexRoot, "rollout-backups", "sessions", "2026", "04", "30", "rollout-2026-04-30T10-00-00-thread-a.jsonl");
        var unexpectedBackupPath = Path.Combine(codexRoot, "rollout-backups", "sessions", "2026", "04", "30", "rollout-2026-04-30T10-00-01-thread-b.jsonl");
        const string originalMatchingContent =
            """
            {"type":"session_meta","payload":{"id":"thread-a","cwd":"D:\\repo","model_provider":"old-provider"}}
            {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hello"}]}}
            """;

        Directory.CreateDirectory(sessionsRoot);
        await File.WriteAllTextAsync(matchingPath, originalMatchingContent);
        await File.WriteAllTextAsync(
            falseCandidatePath,
            """
            {"type":"session_meta","payload":{"id":"thread-b","cwd":"D:\\repo","model_provider":"other-provider"}}
            {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ignore"}]}}
            """);
        CreateStateDatabase(Path.Combine(workspacePath, ".codex", "state_5.sqlite"), "thread-a", "old-provider");

        try
        {
            var service = new CodexThreadProviderSyncService(NullLogger<CodexThreadProviderSyncService>.Instance);

            await service.SyncThreadProviderAsync(new CodexThreadProviderSyncRequest
            {
                SessionWorkspacePath = workspacePath,
                ThreadId = "thread-a",
                TargetProviderId = "new-provider"
            });

            Assert.True(File.Exists(expectedBackupPath));
            Assert.Equal(
                originalMatchingContent.ReplaceLineEndings("\n"),
                (await File.ReadAllTextAsync(expectedBackupPath)).ReplaceLineEndings("\n"));
            Assert.False(File.Exists(unexpectedBackupPath));
            Assert.Equal("new-provider", ReadProviderIdFromFirstLine(matchingPath));
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SyncThreadProviderAsync_SeedsLocalSnapshotFromSourceWhenLocalThreadIsMissing()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(workspaceRoot, "source-home");
        var targetRoot = Path.Combine(workspaceRoot, "workspace");
        var sourceCodexRoot = Path.Combine(sourceRoot, ".codex");
        var sourceSessionsRoot = Path.Combine(sourceCodexRoot, "sessions", "2026", "04", "30");
        var sourceMatchPath = Path.Combine(sourceSessionsRoot, "rollout-2026-04-30T10-00-00-thread-a.jsonl");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");

        Directory.CreateDirectory(sourceSessionsRoot);
        await File.WriteAllTextAsync(
            sourceMatchPath,
            """
            {"type":"session_meta","payload":{"id":"thread-a","cwd":"D:\\repo","model_provider":"old-provider"}}
            {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hello"}]}}
            """);
        CreateStateDatabase(Path.Combine(targetRoot, ".codex", "state_5.sqlite"), "thread-a", "old-provider");

        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", sourceRoot);
            Environment.SetEnvironmentVariable("HOME", sourceRoot);
            Environment.SetEnvironmentVariable("USERPROFILE", sourceRoot);

            var service = new CodexThreadProviderSyncService(NullLogger<CodexThreadProviderSyncService>.Instance);

            var result = await service.SyncThreadProviderAsync(new CodexThreadProviderSyncRequest
            {
                SessionWorkspacePath = targetRoot,
                ThreadId = "thread-a",
                TargetProviderId = "new-provider"
            });

            var localMatchPath = Path.Combine(targetRoot, ".codex", "sessions", "2026", "04", "30", "rollout-2026-04-30T10-00-00-thread-a.jsonl");

            Assert.True(result.SeededFromSource);
            Assert.False(result.HasWarnings);
            Assert.True(File.Exists(localMatchPath));
            Assert.Equal("new-provider", ReadProviderIdFromFirstLine(localMatchPath));
            Assert.Equal("old-provider", ReadProviderIdFromFirstLine(sourceMatchPath));
            Assert.Equal("new-provider", ReadProviderIdFromSqlite(Path.Combine(targetRoot, ".codex", "state_5.sqlite"), "thread-a"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);

            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SyncThreadProviderAsync_WhenStateDatabaseMissing_ReturnsPartialSuccess()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(workspaceRoot, "workspace");
        var codexRoot = Path.Combine(workspacePath, ".codex");
        var sessionsRoot = Path.Combine(codexRoot, "sessions", "2026", "04", "30");
        var matchingPath = Path.Combine(sessionsRoot, "rollout-2026-04-30T10-00-00-thread-a.jsonl");

        Directory.CreateDirectory(sessionsRoot);
        await File.WriteAllTextAsync(
            matchingPath,
            """
            {"type":"session_meta","payload":{"id":"thread-a","cwd":"D:\\repo","model_provider":"old-provider"}}
            {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hello"}]}}
            """);

        try
        {
            var service = new CodexThreadProviderSyncService(NullLogger<CodexThreadProviderSyncService>.Instance);

            var result = await service.SyncThreadProviderAsync(new CodexThreadProviderSyncRequest
            {
                SessionWorkspacePath = workspacePath,
                ThreadId = "thread-a",
                TargetProviderId = "new-provider"
            });

            Assert.True(result.HasWarnings);
            Assert.True(result.SqliteResult.Missing);
            Assert.Equal("new-provider", ReadProviderIdFromFirstLine(matchingPath));
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
        }
    }

    private static string ReadProviderIdFromFirstLine(string rolloutPath)
    {
        var firstLine = File.ReadLines(rolloutPath).First(line => !string.IsNullOrWhiteSpace(line));
        using var document = JsonDocument.Parse(firstLine);
        var payload = document.RootElement.GetProperty("payload");
        return payload.GetProperty("model_provider").GetString() ?? string.Empty;
    }

    private static string ReadProviderIdFromSqlite(string databasePath, string threadId)
    {
        using var connection = new SQLiteConnection($"Data Source={databasePath};Version=3;Read Only=True;FailIfMissing=True;Pooling=False;");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT model_provider FROM threads WHERE id = @threadId LIMIT 1";
        command.Parameters.AddWithValue("@threadId", threadId);
        var result = command.ExecuteScalar();
        return result?.ToString() ?? string.Empty;
    }

    private static void CreateStateDatabase(string databasePath, string threadId, string providerId)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }

        using var connection = new SQLiteConnection($"Data Source={databasePath};Version=3;Pooling=False;");
        connection.Open();

        using (var createCommand = connection.CreateCommand())
        {
            createCommand.CommandText = """
            CREATE TABLE threads (
                id TEXT PRIMARY KEY,
                rollout_path TEXT,
                model_provider TEXT,
                cwd TEXT,
                archived INTEGER NOT NULL DEFAULT 0
            );
            """;
            createCommand.ExecuteNonQuery();
        }

        using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.CommandText = """
            INSERT INTO threads (id, rollout_path, model_provider, cwd, archived)
            VALUES (@id, @rolloutPath, @providerId, @cwd, 0);
            """;
            insertCommand.Parameters.AddWithValue("@id", threadId);
            insertCommand.Parameters.AddWithValue("@rolloutPath", "rollout");
            insertCommand.Parameters.AddWithValue("@providerId", providerId);
            insertCommand.Parameters.AddWithValue("@cwd", @"D:\repo");
            insertCommand.ExecuteNonQuery();
        }
    }
}
