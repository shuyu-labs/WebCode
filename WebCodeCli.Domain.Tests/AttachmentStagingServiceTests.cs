using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Domain.Service.Adapters;

namespace WebCodeCli.Domain.Tests;

public class AttachmentStagingServiceTests
{
    [Fact]
    public async Task StageAsync_StagesAttachmentUnderHiddenSubmissionDirectory()
    {
        var workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            "WebCodeCli.Domain.Tests",
            nameof(AttachmentStagingServiceTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            var cliExecutor = new StubCliExecutorService(workspaceRoot);
            var service = new AttachmentStagingService(cliExecutor, NullLogger<AttachmentStagingService>.Instance);

            var staged = await service.StageAsync(
                "session-123",
                "submission-123",
                [
                    new MessageDraftAttachmentInput
                    {
                        FileName = "diagram.png",
                        ContentType = "image/png",
                        Content = [0x01, 0x02, 0x03]
                    }
                ]);

            var attachment = Assert.Single(staged);
            Assert.Equal(".webcode/message-inputs/submission-123/diagram.png", attachment.Metadata.WorkspaceRelativePath);
            Assert.True(File.Exists(attachment.AbsolutePath));
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
    public async Task StageAsync_WhenDifferentSubmissionIdsNormalizeToSameDirectory_ThrowsCollisionError()
    {
        var workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            "WebCodeCli.Domain.Tests",
            nameof(AttachmentStagingServiceTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            var cliExecutor = new StubCliExecutorService(workspaceRoot);
            var service = new AttachmentStagingService(cliExecutor, NullLogger<AttachmentStagingService>.Instance);

            await service.StageAsync(
                "session-123",
                "submission:123/with?chars",
                [
                    new MessageDraftAttachmentInput
                    {
                        FileName = "diagram.png",
                        ContentType = "image/png",
                        Content = [0x01]
                    }
                ]);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StageAsync(
                "session-123",
                "submission_123_with*chars",
                [
                    new MessageDraftAttachmentInput
                    {
                        FileName = "notes.txt",
                        ContentType = "text/plain",
                        Content = [0x02]
                    }
                ]));

            Assert.Contains("collision", exception.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task ReservationMarker_WhenDifferentRawIdsRace_OnlyOneReservationSucceeds()
    {
        var method = typeof(AttachmentStagingService).GetMethod(
            "EnsureSubmissionDirectoryReservation",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var doubleSuccessObserved = false;
        var normalizedSubmissionDirectoryName = "submission_123_with_chars";

        for (var attempt = 0; attempt < 100 && !doubleSuccessObserved; attempt++)
        {
            var workspaceRoot = Path.Combine(
                Path.GetTempPath(),
                "WebCodeCli.Domain.Tests",
                nameof(AttachmentStagingServiceTests),
                Guid.NewGuid().ToString("N"));
            var submissionRoot = Path.Combine(workspaceRoot, normalizedSubmissionDirectoryName);
            Directory.CreateDirectory(submissionRoot);

            try
            {
                using var start = new ManualResetEventSlim(false);

                var firstTask = Task.Run(() => InvokeReservation(
                    method!,
                    submissionRoot,
                    "submission:123/with?chars",
                    normalizedSubmissionDirectoryName,
                    start));
                var secondTask = Task.Run(() => InvokeReservation(
                    method!,
                    submissionRoot,
                    "submission_123_with*chars",
                    normalizedSubmissionDirectoryName,
                    start));

                start.Set();
                var results = await Task.WhenAll(firstTask, secondTask);
                doubleSuccessObserved = results.All(result => result == null);
            }
            finally
            {
                if (Directory.Exists(workspaceRoot))
                {
                    Directory.Delete(workspaceRoot, recursive: true);
                }
            }
        }

        Assert.False(doubleSuccessObserved, "Expected atomic reservation to reject one concurrent submitter.");
    }

    private static Exception? InvokeReservation(
        MethodInfo method,
        string submissionRoot,
        string rawSubmissionId,
        string normalizedSubmissionDirectoryName,
        ManualResetEventSlim start)
    {
        start.Wait();

        try
        {
            method.Invoke(null, [submissionRoot, rawSubmissionId, normalizedSubmissionDirectoryName]);
            return null;
        }
        catch (TargetInvocationException ex)
        {
            return ex.InnerException ?? ex;
        }
    }

    private sealed class StubCliExecutorService(string workspaceRoot) : ICliExecutorService
    {
        public ICliToolAdapter? GetAdapter(CliToolConfig tool) => throw new NotSupportedException();
        public ICliToolAdapter? GetAdapterById(string toolId) => throw new NotSupportedException();
        public bool SupportsStreamParsing(CliToolConfig tool) => throw new NotSupportedException();
        public string? GetCliThreadId(string sessionId) => throw new NotSupportedException();
        public void SetCliThreadId(string sessionId, string threadId) => throw new NotSupportedException();
        public Task ResetSessionRuntimeAsync(string sessionId, bool clearCliThreadId = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopSessionExecutionAsync(string sessionId, string? toolId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamOutputChunk> ExecuteStreamAsync(string sessionId, string toolId, string userPrompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public bool SupportsLowInterruptionContinue(string toolId) => throw new NotSupportedException();
        public bool CanStartLowInterruptionContinue(string sessionId, string toolId) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamOutputChunk> ExecuteLowInterruptionContinueStreamAsync(string sessionId, string toolId, string? prompt = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public List<CliToolConfig> GetAvailableTools(string? username = null) => throw new NotSupportedException();
        public CliToolConfig? GetTool(string toolId, string? username = null) => throw new NotSupportedException();
        public bool ValidateTool(string toolId, string? username = null) => throw new NotSupportedException();
        public void CleanupSessionWorkspace(string sessionId) => throw new NotSupportedException();
        public void CleanupExpiredWorkspaces() => throw new NotSupportedException();
        public string GetSessionWorkspacePath(string sessionId) => workspaceRoot;
        public Task<Dictionary<string, string>> GetToolEnvironmentVariablesAsync(string toolId, string? username = null) => throw new NotSupportedException();
        public Task<CcSwitchSessionSnapshot?> SyncSessionCcSwitchSnapshotAsync(string sessionId, string? toolId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CodexThreadProviderSyncResult> SyncCodexThreadProviderAsync(string sessionId, string? toolId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SaveToolEnvironmentVariablesAsync(string toolId, Dictionary<string, string> envVars, string? username = null) => throw new NotSupportedException();
        public byte[]? GetWorkspaceFile(string sessionId, string relativePath) => throw new NotSupportedException();
        public byte[]? GetWorkspaceZip(string sessionId) => throw new NotSupportedException();
        public Task<bool> UploadFileToWorkspaceAsync(string sessionId, string fileName, byte[] fileContent, string? relativePath = null) => throw new NotSupportedException();
        public Task<bool> CreateFolderInWorkspaceAsync(string sessionId, string folderPath) => throw new NotSupportedException();
        public Task<bool> DeleteWorkspaceItemAsync(string sessionId, string relativePath, bool isDirectory) => throw new NotSupportedException();
        public Task<bool> MoveFileInWorkspaceAsync(string sessionId, string sourcePath, string targetPath) => throw new NotSupportedException();
        public Task<bool> CopyFileInWorkspaceAsync(string sessionId, string sourcePath, string targetPath) => throw new NotSupportedException();
        public Task<bool> RenameFileInWorkspaceAsync(string sessionId, string oldPath, string newName) => throw new NotSupportedException();
        public Task<int> BatchDeleteFilesAsync(string sessionId, List<string> relativePaths) => throw new NotSupportedException();
        public Task<string> InitializeSessionWorkspaceAsync(string sessionId, string? projectId = null, bool includeGit = false) => throw new NotSupportedException();
        public void RefreshWorkspaceRootCache() => throw new NotSupportedException();
    }
}
