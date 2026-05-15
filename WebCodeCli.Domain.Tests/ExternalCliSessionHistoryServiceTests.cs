using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Domain.Tests;

public class ExternalCliSessionHistoryServiceTests
{
    [Fact]
    public async Task GetRecentMessagesAsync_ForCodex_ReadsUserAndAssistantMessagesFromRolloutFile()
    {
        using var sandbox = new HistoryTestSandbox();
        var rolloutPath = sandbox.WriteFile(
            Path.Combine("codex", "rollout-codex-thread-1.jsonl"),
            """
            {"timestamp":"2026-03-23T01:00:00Z","type":"session_meta","payload":{"id":"codex-thread-1","cwd":"D:\\repo"}}
            {"timestamp":"2026-03-23T01:00:01Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"用户提问"}]}}
            {"timestamp":"2026-03-23T01:00:02Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"第一行"},{"type":"output_text","text":"第二行"}]}}
            {"timestamp":"2026-03-23T01:00:03Z","type":"response_item","payload":{"type":"message","role":"developer","content":[{"type":"input_text","text":"忽略我"}]}}
            """);

        var service = new TestExternalCliSessionHistoryService(
            codexSessionsRootPath: Path.GetDirectoryName(rolloutPath)!);

        var messages = await service.GetRecentMessagesAsync("codex", "codex-thread-1");

        Assert.Collection(
            messages,
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Equal("用户提问", message.Content);
            },
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("第一行\n第二行", message.Content);
            });
    }

    [Fact]
    public async Task GetRecentMessagesAsync_ForCodex_CanReadRolloutFileWhileItIsOpenedForWriting()
    {
        using var sandbox = new HistoryTestSandbox();
        var rolloutPath = sandbox.WriteFile(
            Path.Combine("codex", "rollout-codex-thread-locked.jsonl"),
            """
            {"timestamp":"2026-03-23T01:00:00Z","type":"session_meta","payload":{"id":"codex-thread-locked","cwd":"D:\\repo"}}
            {"timestamp":"2026-03-23T01:00:01Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"用户提问"}]}}
            {"timestamp":"2026-03-23T01:00:02Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"助手回复"}]}}
            """);

        using var lockStream = new FileStream(
            rolloutPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);

        var service = new TestExternalCliSessionHistoryService(
            codexSessionsRootPath: Path.GetDirectoryName(rolloutPath)!);

        var messages = await service.GetRecentMessagesAsync("codex", "codex-thread-locked");

        Assert.Collection(
            messages,
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Equal("用户提问", message.Content);
            },
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("助手回复", message.Content);
            });
    }

    [Fact]
    public async Task GetRecentMessagesAsync_ForCodex_PrefersWorkspaceScopedRolloutOverGlobalLegacyRollout()
    {
        using var sandbox = new HistoryTestSandbox();
        const string threadId = "codex-thread-resumed";
        var globalRolloutPath = sandbox.WriteFile(
            Path.Combine("global-codex", "2026", "04", "23", $"rollout-2026-04-23T17-19-27-{threadId}.jsonl"),
            """
            {"timestamp":"2026-03-23T01:00:00Z","type":"session_meta","payload":{"id":"codex-thread-resumed","cwd":"D:\\legacy"}}
            {"timestamp":"2026-03-23T01:00:01Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"legacy user"}]}}
            {"timestamp":"2026-03-23T01:00:02Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"legacy assistant"}]}}
            """);
        var workspacePath = sandbox.CreateDirectory("workspace");
        sandbox.WriteFile(
            Path.Combine("workspace", ".codex", "sessions", "2026", "04", "28", $"rollout-2026-04-28T09-12-00-{threadId}.jsonl"),
            """
            {"timestamp":"2026-04-28T09:12:00Z","type":"session_meta","payload":{"id":"codex-thread-resumed","cwd":"D:\\workspace"}}
            {"timestamp":"2026-04-28T09:12:01Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"workspace user"}]}}
            {"timestamp":"2026-04-28T09:12:02Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"workspace assistant"}]}}
            """);

        var service = new TestExternalCliSessionHistoryService(
            codexSessionsRootPath: Path.GetDirectoryName(globalRolloutPath)!);

        var history = await service.GetRecentHistoryAsync("codex", threadId, workspacePath: workspacePath);

        Assert.Collection(
            history.Messages,
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Equal("workspace user", message.Content);
            },
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("workspace assistant", message.Content);
            });
        Assert.Contains($@"workspace\.codex\sessions\2026\04\28\rollout-2026-04-28T09-12-00-{threadId}.jsonl", history.SourcePath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRecentMessagesAsync_ForCodex_LogsResolvedWorkspaceRolloutDiagnostics()
    {
        using var sandbox = new HistoryTestSandbox();
        const string threadId = "codex-thread-diagnostic";
        var globalRolloutPath = sandbox.WriteFile(
            Path.Combine("global-codex", "sessions", "2026", "04", "23", $"rollout-2026-04-23T17-19-27-{threadId}.jsonl"),
            """
            {"timestamp":"2026-03-23T01:00:00Z","type":"session_meta","payload":{"id":"codex-thread-diagnostic","cwd":"D:\\legacy","model_provider":"global-provider"}}
            {"timestamp":"2026-03-23T01:00:01Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"global assistant"}]}}
            """);
        var workspacePath = sandbox.CreateDirectory("workspace");
        var workspaceRolloutPath = sandbox.WriteFile(
            Path.Combine("workspace", ".codex", "sessions", "2026", "04", "28", $"rollout-2026-04-28T09-12-00-{threadId}.jsonl"),
            """
            {"timestamp":"2026-04-28T09:12:00Z","type":"session_meta","payload":{"id":"codex-thread-diagnostic","cwd":"D:\\workspace","model_provider":"project-provider"}}
            {"timestamp":"2026-04-28T09:12:01Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"workspace assistant"}]}}
            """);
        var logger = new RecordingLogger<ExternalCliSessionHistoryService>();
        var service = new TestExternalCliSessionHistoryService(
            codexSessionsRootPath: Path.GetDirectoryName(globalRolloutPath)!,
            logger: logger);

        var messages = await service.GetRecentMessagesAsync("codex", threadId, workspacePath: workspacePath);

        Assert.Collection(
            messages,
            message => Assert.Equal("workspace assistant", message.Content));

        var logText = string.Join("\n", logger.Entries.Select(entry => entry.Message));
        Assert.Contains("[CodexHistory] Start resolving rollout", logText);
        Assert.Contains("[CodexHistory] Rollout resolved", logText);
        Assert.Contains("Scope=workspace", logText);
        Assert.Contains("MatchKind=filename", logText);
        Assert.Contains(workspaceRolloutPath, logText);
        Assert.Contains("FirstLineThreadId=codex-thread-diagnostic", logText);
        Assert.Contains("FirstLineModelProvider=project-provider", logText);
    }

    [Fact]
    public async Task GetRecentMessagesAsync_ForCodex_ReadsArchivedWorkspaceRolloutWhenActiveHistoryWasArchived()
    {
        using var sandbox = new HistoryTestSandbox();
        const string threadId = "codex-thread-archived";
        var workspacePath = sandbox.CreateDirectory("workspace");

        sandbox.WriteFile(
            Path.Combine("workspace", ".codex", "archived_sessions", "2026", "04", "28", $"rollout-2026-04-28T09-12-00-{threadId}.jsonl"),
            """
            {"timestamp":"2026-04-28T09:12:00Z","type":"session_meta","payload":{"id":"codex-thread-archived","cwd":"D:\\workspace"}}
            {"timestamp":"2026-04-28T09:12:01Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"archived user"}]}}
            {"timestamp":"2026-04-28T09:12:02Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"archived assistant"}]}}
            """);

        var service = new TestExternalCliSessionHistoryService();

        var messages = await service.GetRecentMessagesAsync("codex", threadId, workspacePath: workspacePath);

        Assert.Collection(
            messages,
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Equal("archived user", message.Content);
            },
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("archived assistant", message.Content);
            });
    }

    [Fact]
    public async Task GetRecentMessagesAsync_ForClaudeCode_ReadsMessagesFromTranscriptFile()
    {
        using var sandbox = new HistoryTestSandbox();
        var projectsRoot = sandbox.CreateDirectory(Path.Combine("claude", "projects"));
        var transcriptPath = sandbox.WriteFile(
            Path.Combine("claude", "projects", "sample-project", "claude-session-1.jsonl"),
            """
            {"type":"progress","timestamp":"2026-03-23T01:00:00Z","sessionId":"claude-session-1","cwd":"D:\\repo"}
            {"type":"user","timestamp":"2026-03-23T01:00:01Z","sessionId":"claude-session-1","message":{"role":"user","content":"用户问题"}}
            {"type":"assistant","timestamp":"2026-03-23T01:00:02Z","sessionId":"claude-session-1","message":{"type":"message","role":"assistant","content":[{"type":"thinking","thinking":"跳过思考"},{"type":"text","text":"助手回复"}]}}
            {"type":"assistant","timestamp":"2026-03-23T01:00:03Z","sessionId":"claude-session-1","message":{"type":"message","role":"assistant","content":[{"type":"tool_use","name":"Read"}]}}
            """);

        sandbox.WriteFile(
            Path.Combine("claude", "projects", "sample-project", "sessions-index.json"),
            $$"""
            {
              "version": 1,
              "entries": [
                {
                  "sessionId": "claude-session-1",
                  "fullPath": "{{transcriptPath.Replace("\\", "\\\\")}}",
                  "projectPath": "D:\\repo",
                  "modified": "2026-03-23T01:00:03Z"
                }
              ]
            }
            """);

        var service = new TestExternalCliSessionHistoryService(
            claudeProjectsRootPath: projectsRoot);

        var messages = await service.GetRecentMessagesAsync("claude-code", "claude-session-1");

        Assert.Collection(
            messages,
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Equal("用户问题", message.Content);
            },
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("助手回复", message.Content);
            });
    }

    [Fact]
    public async Task GetRecentMessagesAsync_ForClaudeCode_CanReadTranscriptWhenSessionsIndexFileIsOpenedForWriting()
    {
        using var sandbox = new HistoryTestSandbox();
        var projectsRoot = sandbox.CreateDirectory(Path.Combine("claude", "projects"));
        var transcriptPath = sandbox.WriteFile(
            Path.Combine("claude", "projects", "sample-project", "claude-session-lock.jsonl"),
            """
            {"type":"user","timestamp":"2026-03-23T01:00:01Z","sessionId":"claude-session-lock","message":{"role":"user","content":"用户问题"}}
            {"type":"assistant","timestamp":"2026-03-23T01:00:02Z","sessionId":"claude-session-lock","message":{"type":"message","role":"assistant","content":[{"type":"text","text":"助手回复"}]}}
            """);

        var indexPath = sandbox.WriteFile(
            Path.Combine("claude", "projects", "sample-project", "sessions-index.json"),
            $$"""
            {
              "version": 1,
              "entries": [
                {
                  "sessionId": "claude-session-lock",
                  "fullPath": "{{transcriptPath.Replace("\\", "\\\\")}}",
                  "projectPath": "D:\\repo",
                  "modified": "2026-03-23T01:00:03Z"
                }
              ]
            }
            """);

        using var lockStream = new FileStream(
            indexPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);

        var service = new TestExternalCliSessionHistoryService(
            claudeProjectsRootPath: projectsRoot);

        var messages = await service.GetRecentMessagesAsync("claude-code", "claude-session-lock");

        Assert.Collection(
            messages,
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Equal("用户问题", message.Content);
            },
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("助手回复", message.Content);
            });
    }

    [Fact]
    public async Task GetRecentMessagesAsync_ForOpenCode_ParsesExportedSessionJson()
    {
        const string exportedJson =
            """
            {
              "info": {
                "id": "ses_test_1"
              },
              "messages": [
                {
                  "info": {
                    "role": "user",
                    "time": {
                      "created": 1768196424497
                    }
                  },
                  "parts": [
                    {
                      "type": "text",
                      "text": "你好"
                    }
                  ]
                },
                {
                  "info": {
                    "role": "assistant",
                    "time": {
                      "created": 1768196424512
                    }
                  },
                  "parts": [
                    {
                      "type": "reasoning",
                      "text": "跳过思考"
                    },
                    {
                      "type": "text",
                      "text": "世界"
                    }
                  ]
                }
              ]
            }
            """;

        var service = new TestExternalCliSessionHistoryService(
            processHandler: (fileName, arguments, _) =>
            {
                Assert.Equal("opencode", fileName);
                Assert.Contains("export ses_test_1", arguments, StringComparison.Ordinal);
                return Task.FromResult((0, exportedJson, string.Empty));
            });

        var messages = await service.GetRecentMessagesAsync("opencode", "ses_test_1");

        Assert.Collection(
            messages,
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Equal("你好", message.Content);
            },
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("世界", message.Content);
            });
    }

    private sealed class TestExternalCliSessionHistoryService : ExternalCliSessionHistoryService
    {
        private readonly string? _codexConfigRootPath;
        private readonly string? _claudeProjectsRootPath;
        private readonly Func<string, string, CancellationToken, Task<(int ExitCode, string Stdout, string Stderr)>>? _processHandler;

        public TestExternalCliSessionHistoryService(
            string? codexSessionsRootPath = null,
            string? claudeProjectsRootPath = null,
            Func<string, string, CancellationToken, Task<(int ExitCode, string Stdout, string Stderr)>>? processHandler = null,
            ILogger<ExternalCliSessionHistoryService>? logger = null)
            : base(logger ?? NullLogger<ExternalCliSessionHistoryService>.Instance)
        {
            _codexConfigRootPath = codexSessionsRootPath;
            _claudeProjectsRootPath = claudeProjectsRootPath;
            _processHandler = processHandler;
        }

        protected override string? GetCodexConfigRootPath() => _codexConfigRootPath;

        protected override string? GetClaudeProjectsRootPath() => _claudeProjectsRootPath;

        protected override Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
            string fileName,
            string arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (_processHandler == null)
            {
                throw new NotSupportedException("Test process handler was not configured.");
            }

            return _processHandler(fileName, arguments, cancellationToken);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class HistoryTestSandbox : IDisposable
    {
        public HistoryTestSandbox()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "WebCodeCli.Domain.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string CreateDirectory(string relativePath)
        {
            var fullPath = Path.Combine(RootPath, relativePath);
            Directory.CreateDirectory(fullPath);
            return fullPath;
        }

        public string WriteFile(string relativePath, string content)
        {
            var fullPath = Path.Combine(RootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content.Replace("\r\n", "\n"));
            return fullPath;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
