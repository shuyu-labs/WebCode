using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlSugar;
using System.Diagnostics;
using System.Reflection;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Domain.Service.Adapters;
using WebCodeCli.Domain.Model;
using WebCodeCli.Domain.Repositories.Base.ChatSession;

namespace WebCodeCli.Domain.Tests;

public class CliExecutorServiceTests
{
    [Fact]
    public void GetCliThreadId_WhenPersistedThreadIdMissing_RecoversImportedCodexThreadIdFromTitle()
    {
        const string sessionId = "session-imported";
        const string cliThreadId = "019d1338-0c3f-7eb3-ae2b-e4617eb7d24e";

        var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    Title = $"[Codex] {cliThreadId}",
                    WorkspacePath = @"D:\VSWorkshop\OpenDify",
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);
        var serviceProvider = new NullServiceProvider(
            repository,
            new StubSessionOutputService());

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            Options.Create(new CliToolsOption
            {
                TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
                Tools = []
            }),
            NullLogger<PersistentProcessManager>.Instance,
            serviceProvider,
            new StubChatSessionService(),
            new StubCliAdapterFactory(),
            new StubCcSwitchService());

        var resolvedThreadId = service.GetCliThreadId(sessionId);

        Assert.Equal(cliThreadId, resolvedThreadId);
        Assert.Equal(cliThreadId, repository.LastUpdatedCliThreadId);
        Assert.Equal(cliThreadId, repository.GetById(sessionId).CliThreadId);
    }

    [Fact]
    public void GetCliThreadId_WhenImportedTitleIsNotARealThreadId_DoesNotRecoverWrongValue()
    {
        const string sessionId = "session-imported-friendly-title";

        var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    Title = "[Codex] 修复 OpenDify 登录问题",
                    WorkspacePath = @"D:\VSWorkshop\OpenDify",
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);
        var serviceProvider = new NullServiceProvider(
            repository,
            new StubSessionOutputService());

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            Options.Create(new CliToolsOption
            {
                TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
                Tools = []
            }),
            NullLogger<PersistentProcessManager>.Instance,
            serviceProvider,
            new StubChatSessionService(),
            new StubCliAdapterFactory(),
            new StubCcSwitchService());

        var resolvedThreadId = service.GetCliThreadId(sessionId);

        Assert.Null(resolvedThreadId);
        Assert.Null(repository.LastUpdatedCliThreadId);
    }

    [Fact]
    public async Task SyncCodexThreadProviderAsync_WhenCliThreadIdIsRecovered_PassesResolvedThreadAndActiveProviderToSyncService()
    {
        const string sessionId = "session-sync-thread-provider";
        const string cliThreadId = "019d1338-0c3f-7eb3-ae2b-e4617eb7d24e";

        var workspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(workspaceRoot, "workspace");
        var liveConfigPath = Path.Combine(workspaceRoot, "codex-live-config.toml");
        Directory.CreateDirectory(workspacePath);
        await File.WriteAllTextAsync(liveConfigPath, "model_provider = \"provider-new\"\n");

        try
        {
            var repository = new StubChatSessionRepository(
                [
                    new ChatSessionEntity
                    {
                        SessionId = sessionId,
                        Username = "luhaiyan",
                        ToolId = "codex",
                        Title = $"[Codex] {cliThreadId}",
                        WorkspacePath = workspacePath,
                        CreatedAt = DateTime.Now,
                        UpdatedAt = DateTime.Now
                    }
                ]);
            var threadSyncService = new RecordingCodexThreadProviderSyncService();
            var ccSwitchService = new StubCcSwitchService(
                new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
                {
                    ["codex"] = new CcSwitchToolStatus
                    {
                        ToolId = "codex",
                        ToolName = "Codex",
                        IsManaged = true,
                        IsDetected = true,
                        HasDatabase = true,
                        HasSettingsFile = true,
                        HasLiveConfig = true,
                        IsLaunchReady = true,
                        ConfigDirectory = Path.Combine(workspaceRoot, "cc-switch"),
                        LiveConfigPath = liveConfigPath,
                        ActiveProviderId = "provider-new",
                        ActiveProviderName = "Provider New",
                        ActiveProviderCategory = "custom",
                        StatusMessage = "ready"
                    }
                });
            var serviceProvider = new NullServiceProvider(repository, new StubSessionOutputService(), threadSyncService);

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
                    Tools = []
                }),
                NullLogger<PersistentProcessManager>.Instance,
                serviceProvider,
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                ccSwitchService);

            var result = await service.SyncCodexThreadProviderAsync(sessionId);

            var request = Assert.Single(threadSyncService.Requests);
            Assert.Equal(workspacePath, request.SessionWorkspacePath);
            Assert.Equal(cliThreadId, request.ThreadId);
            Assert.Equal("provider-new", request.TargetProviderId);
            Assert.False(result.HasWarnings);
            Assert.True(File.Exists(Path.Combine(workspacePath, ".codex", "config.toml")));
            Assert.Equal(cliThreadId, repository.LastUpdatedCliThreadId);
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
    public async Task SyncCodexThreadProviderAsync_WhenCliThreadIdCannotBeResolved_FailsFast()
    {
        const string sessionId = "session-sync-thread-provider-missing";

        var workspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(workspaceRoot, "workspace");
        var liveConfigPath = Path.Combine(workspaceRoot, "codex-live-config.toml");
        Directory.CreateDirectory(workspacePath);
        await File.WriteAllTextAsync(liveConfigPath, "model_provider = \"provider-new\"\n");

        try
        {
            var repository = new StubChatSessionRepository(
                [
                    new ChatSessionEntity
                    {
                        SessionId = sessionId,
                        Username = "luhaiyan",
                        ToolId = "codex",
                        Title = "new session",
                        WorkspacePath = workspacePath,
                        CreatedAt = DateTime.Now,
                        UpdatedAt = DateTime.Now
                    }
                ]);
            var threadSyncService = new RecordingCodexThreadProviderSyncService();
            var ccSwitchService = new StubCcSwitchService(
                new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
                {
                    ["codex"] = new CcSwitchToolStatus
                    {
                        ToolId = "codex",
                        ToolName = "Codex",
                        IsManaged = true,
                        IsDetected = true,
                        HasDatabase = true,
                        HasSettingsFile = true,
                        HasLiveConfig = true,
                        IsLaunchReady = true,
                        ConfigDirectory = Path.Combine(workspaceRoot, "cc-switch"),
                        LiveConfigPath = liveConfigPath,
                        ActiveProviderId = "provider-new",
                        ActiveProviderName = "Provider New",
                        ActiveProviderCategory = "custom",
                        StatusMessage = "ready"
                    }
                });
            var serviceProvider = new NullServiceProvider(repository, new StubSessionOutputService(), threadSyncService);

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
                    Tools = []
                }),
                NullLogger<PersistentProcessManager>.Instance,
                serviceProvider,
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                ccSwitchService);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncCodexThreadProviderAsync(sessionId));

            Assert.Contains("CLI thread", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(threadSyncService.Requests);
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
    public void CodexAdapter_BuildLowInterruptionArguments_UsesResumeFullAutoWithoutPrompt()
    {
        var adapter = new CodexAdapter();
        var tool = new CliToolConfig
        {
            Id = "codex",
            Name = "Codex",
            Command = "codex",
            Enabled = true
        };
        var context = new CliSessionContext
        {
            SessionId = "session-low-interruption",
            CliThreadId = "thread-123",
            WorkingDirectory = Path.GetTempPath()
        };

        var arguments = adapter.BuildLowInterruptionArguments(tool, context);

        Assert.Equal("exec resume --skip-git-repo-check --json --full-auto thread-123", arguments);
        Assert.DoesNotContain("{prompt}", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("keep going", arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClaudeCodeAdapter_BuildArguments_ReplacesModelArgumentWithSessionOverride()
    {
        var adapter = new ClaudeCodeAdapter();
        var tool = new CliToolConfig
        {
            Id = "claude-code",
            Name = "Claude Code",
            Command = "claude",
            ArgumentTemplate = "-p --model old-model {session} \"{prompt}\"",
            Enabled = true
        };
        var context = new CliSessionContext
        {
            SessionId = "session-claude-model",
            WorkingDirectory = Path.GetTempPath(),
            LaunchModelOverride = "claude-sonnet-4"
        };

        var arguments = adapter.BuildArguments(tool, "hello", context);

        Assert.Contains("--model \"claude-sonnet-4\"", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("old-model", arguments, StringComparison.Ordinal);
        Assert.Equal(1, arguments.Split("--model", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void OpenCodeAdapter_BuildArguments_ReplacesModelArgumentWithSessionOverride()
    {
        var adapter = new OpenCodeAdapter();
        var tool = new CliToolConfig
        {
            Id = "opencode",
            Name = "OpenCode",
            Command = "opencode",
            ArgumentTemplate = "run --model old/provider-model {session} \"{prompt}\" --format json",
            Enabled = true
        };
        var context = new CliSessionContext
        {
            SessionId = "session-opencode-model",
            WorkingDirectory = Path.GetTempPath(),
            LaunchModelOverride = "openai/gpt-5.4"
        };

        var arguments = adapter.BuildArguments(tool, "hello", context);

        Assert.Contains("--model \"openai/gpt-5.4\"", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("old/provider-model", arguments, StringComparison.Ordinal);
        Assert.Equal(1, arguments.Split("--model", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void SupportsLowInterruptionContinue_WhenAdapterProvidesNativeDefaults_ReturnsTrue()
    {
        var tool = new CliToolConfig
        {
            Id = "codex",
            Name = "Codex",
            Command = "codex",
            Enabled = true
        };

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            Options.Create(new CliToolsOption
            {
                TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
                Tools = [tool]
            }),
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(new CodexAdapter()),
            new StubCcSwitchService());

        Assert.True(service.SupportsLowInterruptionContinue(tool.Id));
    }

    [Fact]
    public void CanStartLowInterruptionContinue_WhenThreadIdMissing_ReturnsFalse()
    {
        var tool = new CliToolConfig
        {
            Id = "codex",
            Name = "Codex",
            Command = "codex",
            Enabled = true
        };

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            Options.Create(new CliToolsOption
            {
                TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
                Tools = [tool]
            }),
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(new CodexAdapter()),
            new StubCcSwitchService());

        Assert.False(service.CanStartLowInterruptionContinue("session-without-thread", tool.Id));
    }

    [Fact]
    public void CanStartLowInterruptionContinue_WhenThreadIdExistsAndToolSupported_ReturnsTrue()
    {
        var tool = new CliToolConfig
        {
            Id = "codex",
            Name = "Codex",
            Command = "codex",
            Enabled = true
        };

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            Options.Create(new CliToolsOption
            {
                TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
                Tools = [tool]
            }),
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(new CodexAdapter()),
            new StubCcSwitchService());

        service.SetCliThreadId("session-with-thread", "thread-123");

        Assert.True(service.CanStartLowInterruptionContinue("session-with-thread", tool.Id));
    }

    [Fact]
    public async Task ExecuteLowInterruptionContinueStreamAsync_WhenThreadIdMissing_ReturnsCompletedErrorChunk()
    {
        var tool = new CliToolConfig
        {
            Id = "codex",
            Name = "Codex",
            Command = "codex",
            Enabled = true
        };

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            Options.Create(new CliToolsOption
            {
                TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
                Tools = [tool]
            }),
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(new CodexAdapter()),
            new StubCcSwitchService());

        var chunks = new List<StreamOutputChunk>();
        await foreach (var chunk in service.ExecuteLowInterruptionContinueStreamAsync("session-without-thread", tool.Id))
        {
            chunks.Add(chunk);
        }

        var failureChunk = Assert.Single(chunks.Where(chunk => chunk.IsError && chunk.IsCompleted));
        Assert.Contains("thread/session id", failureChunk.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteLowInterruptionContinueStreamAsync_UsesLowInterruptionAdapterPath()
    {
        var tool = new CliToolConfig
        {
            Id = "recording-low-interruption-tool",
            Name = "Recording Low Interruption Tool",
            Command = "powershell.exe",
            Enabled = true
        };
        var adapter = new RecordingLowInterruptionAdapter();

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            Options.Create(new CliToolsOption
            {
                TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
                Tools = [tool]
            }),
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(adapter),
            new StubCcSwitchService());

        service.SetCliThreadId("session-low-interruption", "thread-123");

        var chunks = new List<StreamOutputChunk>();
        await foreach (var chunk in service.ExecuteLowInterruptionContinueStreamAsync("session-low-interruption", tool.Id))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(1, adapter.BuildLowInterruptionArgumentsCallCount);
        Assert.Equal(0, adapter.BuildArgumentsCallCount);
        Assert.Equal("thread-123", adapter.LastLowInterruptionContext?.CliThreadId);
        Assert.Contains(chunks, chunk => !chunk.IsError);
    }

    [Fact]
    public async Task ExecuteLowInterruptionContinueStreamAsync_ForCodex_WritesContinuationPromptToStandardInput()
    {
        var tool = new CliToolConfig
        {
            Id = "codex-like-stdin",
            Name = "Codex-like stdin",
            Command = "powershell.exe",
            Enabled = true
        };
        var adapter = new RecordingCodexLowInterruptionInputAdapter();

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            Options.Create(new CliToolsOption
            {
                TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
                Tools = [tool]
            }),
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(adapter),
            new StubCcSwitchService());

        service.SetCliThreadId("session-low-interruption-stdin", "thread-stdin");

        var chunks = new List<StreamOutputChunk>();
        await foreach (var chunk in service.ExecuteLowInterruptionContinueStreamAsync("session-low-interruption-stdin", tool.Id))
        {
            chunks.Add(chunk);
        }

        Assert.Contains(
            chunks,
            chunk => string.Equals(
                chunk.Content?.Trim(),
                LowInterruptionContinueDefaults.DefaultPrompt,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteLowInterruptionContinueStreamAsync_ForCodex_UsesProvidedPromptOverride()
    {
        var tool = new CliToolConfig
        {
            Id = "codex-like-stdin",
            Name = "Codex-like stdin",
            Command = "powershell.exe",
            Enabled = true
        };
        var adapter = new RecordingCodexLowInterruptionInputAdapter();

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            Options.Create(new CliToolsOption
            {
                TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
                Tools = [tool]
            }),
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(adapter),
            new StubCcSwitchService());

        service.SetCliThreadId("session-low-interruption-stdin", "thread-stdin");

        var chunks = new List<StreamOutputChunk>();
        await foreach (var chunk in service.ExecuteLowInterruptionContinueStreamAsync(
                           "session-low-interruption-stdin",
                           tool.Id,
                           "finish the remaining backlog items",
                           default))
        {
            chunks.Add(chunk);
        }

        Assert.Contains(
            chunks,
            chunk => string.Equals(
                chunk.Content?.Trim(),
                "finish the remaining backlog items",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResetSessionRuntimeAsync_ClearsCachedAndPersistedThreadIdsWithoutRemovingWorkspace()
    {
        const string sessionId = "session-reset-runtime";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, sessionId);
        var keepFilePath = Path.Combine(workspacePath, "keep.txt");
        Directory.CreateDirectory(workspacePath);
        await File.WriteAllTextAsync(keepFilePath, "keep");

        try
        {
            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    CliThreadId = "thread-123",
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);
            var sessionOutputService = new StubSessionOutputService
            {
                State = new OutputPanelState
                {
                    SessionId = sessionId,
                    ActiveThreadId = "thread-123"
                }
            };

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools = []
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, sessionOutputService),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                new StubCcSwitchService());

            service.SetCliThreadId(sessionId, "thread-123");

            await service.ResetSessionRuntimeAsync(sessionId);

            Assert.Null(service.GetCliThreadId(sessionId));
            Assert.Null(repository.GetById(sessionId).CliThreadId);
            Assert.Null(repository.LastUpdatedCliThreadId);
            Assert.Equal(string.Empty, sessionOutputService.State?.ActiveThreadId);
            Assert.Equal(1, sessionOutputService.SaveCallCount);
            Assert.True(File.Exists(keepFilePath));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResetSessionRuntimeAsync_CleansUpOnlyTargetSessionPersistentProcess()
    {
        const string sessionA = "session-reset-a";
        const string sessionB = "session-reset-b";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));

        var tool = new CliToolConfig
        {
            Id = "persistent-reset-tool",
            Name = "Persistent Reset Tool",
            Command = "powershell.exe",
            PersistentModeArguments = "-NoProfile -Command \"$line = [Console]::In.ReadLine(); Write-Output 'persisted-ready'; Start-Sleep -Seconds 30\"",
            UsePersistentProcess = true,
            TimeoutSeconds = 6,
            Enabled = true
        };

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            Options.Create(new CliToolsOption
            {
                TempWorkspaceRoot = tempRoot,
                Tools = [tool]
            }),
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(),
            new StubCcSwitchService());

        try
        {
            var sessionAChunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionA, tool.Id, "hello-a"))
            {
                sessionAChunks.Add(chunk);
            }

            var sessionBChunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionB, tool.Id, "hello-b"))
            {
                sessionBChunks.Add(chunk);
            }

            Assert.Contains(sessionAChunks, chunk => chunk.Content.Contains("persisted-ready", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(sessionBChunks, chunk => chunk.Content.Contains("persisted-ready", StringComparison.OrdinalIgnoreCase));

            var processManager = typeof(CliExecutorService)
                .GetField("_processManager", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service)!;
            var processMap = typeof(PersistentProcessManager)
                .GetField("_processes", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(processManager)!;

            Assert.Equal(2, (int)processMap.GetType().GetProperty("Count")!.GetValue(processMap)!);

            await service.ResetSessionRuntimeAsync(sessionA);

            Assert.Equal(1, (int)processMap.GetType().GetProperty("Count")!.GetValue(processMap)!);

            var remainingKeys = (IEnumerable<string>)processMap.GetType().GetProperty("Keys")!.GetValue(processMap)!;
            Assert.Single(remainingKeys);
            Assert.Contains(sessionB, remainingKeys.Single(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            service.CleanupSessionWorkspace(sessionA);
            service.CleanupSessionWorkspace(sessionB);

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CleanupSessionWorkspace_WhenCustomWorkspaceMetadataWasDeleted_PreservesDirectoryContents()
    {
        const string sessionId = "session-custom-workspace";
        const string username = "luhaiyan";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var customWorkspacePath = Path.Combine(tempRoot, "project-a");
        var preservedFilePath = Path.Combine(customWorkspacePath, "keep.txt");

        Directory.CreateDirectory(customWorkspacePath);
        await File.WriteAllTextAsync(preservedFilePath, "keep");

        try
        {
            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = username,
                    WorkspacePath = customWorkspacePath,
                    IsCustomWorkspace = true,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools = []
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                new StubCcSwitchService());

            var resolvedWorkspacePath = service.GetSessionWorkspacePath(sessionId);
            Assert.Equal(customWorkspacePath, resolvedWorkspacePath);

            await repository.DeleteByIdAndUsernameAsync(sessionId, username);

            service.CleanupSessionWorkspace(sessionId);

            Assert.True(Directory.Exists(customWorkspacePath));
            Assert.True(File.Exists(preservedFilePath));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CleanupSessionWorkspace_WhenExpectedTempWorkspaceMetadataWasDeleted_StillDeletesTempDirectory()
    {
        const string sessionId = "session-temp-workspace";
        const string username = "luhaiyan";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = username,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools = []
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                new StubCcSwitchService());

            var workspacePath = await service.InitializeSessionWorkspaceAsync(sessionId);
            await File.WriteAllTextAsync(Path.Combine(workspacePath, "temp.txt"), "delete");

            await repository.DeleteByIdAndUsernameAsync(sessionId, username);

            service.CleanupSessionWorkspace(sessionId);

            Assert.False(Directory.Exists(workspacePath));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenProcessTimesOut_ReturnsTimeoutChunkInsteadOfThrowing()
    {
        var tool = new CliToolConfig
        {
            Id = "timeout-tool",
            Name = "Timeout Tool",
            Command = "powershell.exe",
            ArgumentTemplate = "-NoProfile -Command \"Start-Sleep -Seconds 3\"",
            TimeoutSeconds = 1,
            Enabled = true
        };

        var options = Options.Create(new CliToolsOption
        {
            TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
            Tools = [tool]
        });

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            options,
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(),
            new StubCcSwitchService());

        var chunks = new List<StreamOutputChunk>();
        await foreach (var chunk in service.ExecuteStreamAsync("session-timeout", tool.Id, "ignored"))
        {
            chunks.Add(chunk);
        }

        var timeoutChunk = Assert.Single(chunks.Where(c => c.IsError && c.IsCompleted));
        Assert.Contains("执行超时", timeoutChunk.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenPersistentProcessStopsOutputButKeepsRunning_CompletesWithoutTimeoutError()
    {
        const string sessionId = "session-persistent-idle";

        var tool = new CliToolConfig
        {
            Id = "persistent-idle-tool",
            Name = "Persistent Idle Tool",
            Command = "powershell.exe",
            PersistentModeArguments = "-NoProfile -Command \"$line = [Console]::In.ReadLine(); Write-Output 'persisted-done'; Start-Sleep -Seconds 10\"",
            UsePersistentProcess = true,
            TimeoutSeconds = 4,
            Enabled = true
        };

        var options = Options.Create(new CliToolsOption
        {
            TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
            Tools = [tool]
        });

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            options,
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(),
            new StubCcSwitchService());

        try
        {
            var chunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, tool.Id, "ignored"))
            {
                chunks.Add(chunk);
            }

            Assert.DoesNotContain(chunks, c => c.IsError && c.IsCompleted);
            Assert.Contains(chunks, c => c.Content.Contains("persisted-done", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(chunks, c => c.IsCompleted && !c.IsError);
        }
        finally
        {
            service.CleanupSessionWorkspace(sessionId);
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenOneTimeCodexTurnCompletedArrives_CompletesBeforeTimeoutOrProcessExit()
    {
        const string sessionId = "session-onetime-codex-turn-completed";
        const string cliThreadId = "thread-onetime-turn-completed";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var scriptPath = Path.Combine(tempRoot, "codex-onetime-semantic-completed.cmd");

        Directory.CreateDirectory(tempRoot);
        await File.WriteAllTextAsync(
            scriptPath,
            """
            @echo off
            echo {"type":"thread.started","thread_id":"thread-onetime-turn-completed"}
            echo {"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}
            powershell.exe -NoProfile -Command "Start-Sleep -Seconds 10"
            """);

        var tool = new CliToolConfig
        {
            Id = "codex-onetime-semantic-completed",
            Name = "Codex One-Time Semantic Completed",
            Command = "cmd.exe",
            ArgumentTemplate = $"/d /c \"{scriptPath}\"",
            TimeoutSeconds = 2,
            Enabled = true
        };

        var options = Options.Create(new CliToolsOption
        {
            TempWorkspaceRoot = tempRoot,
            Tools = [tool]
        });

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            options,
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(new TestCodexLikeCommandAdapter("codex-onetime-semantic-completed")),
            new StubCcSwitchService());

        try
        {
            var chunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, tool.Id, "ignored"))
            {
                chunks.Add(chunk);
            }

            Assert.DoesNotContain(chunks, c => c.IsError && c.IsCompleted);
            Assert.Contains(
                chunks,
                c => !string.IsNullOrEmpty(c.Content) && c.Content.Contains("\"turn.completed\"", StringComparison.Ordinal));
            Assert.Contains(chunks, c => c.IsCompleted && !c.IsError);
            Assert.Equal(cliThreadId, service.GetCliThreadId(sessionId));
        }
        finally
        {
            service.CleanupSessionWorkspace(sessionId);
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenOneTimeCodexTurnCompletedArrives_RequestsGracefulExitBeforeForcedKill()
    {
        const string sessionId = "session-onetime-codex-graceful-exit";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var scriptPath = Path.Combine(tempRoot, "codex-onetime-graceful-exit.cmd");

        Directory.CreateDirectory(tempRoot);
        await File.WriteAllTextAsync(
            scriptPath,
            """
            @echo off
            echo {"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}
            powershell.exe -NoProfile -Command "Start-Sleep -Seconds 10"
            """);

        var tool = new CliToolConfig
        {
            Id = "codex-onetime-graceful-exit",
            Name = "Codex One-Time Graceful Exit",
            Command = "cmd.exe",
            ArgumentTemplate = $"/d /c \"{scriptPath}\"",
            TimeoutSeconds = 2,
            Enabled = true
        };

        var options = Options.Create(new CliToolsOption
        {
            TempWorkspaceRoot = tempRoot,
            Tools = [tool]
        });

        var service = new TrackingGracefulExitCliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            options,
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(new TestCodexLikeCommandAdapter("codex-onetime-graceful-exit")),
            new StubCcSwitchService());

        try
        {
            var chunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, tool.Id, "ignored"))
            {
                chunks.Add(chunk);
            }

            Assert.Equal(1, service.GracefulExitAttemptCount);
            Assert.DoesNotContain(chunks, c => c.IsError && c.IsCompleted);
            Assert.Contains(chunks, c => c.IsCompleted && !c.IsError);
        }
        finally
        {
            service.CleanupSessionWorkspace(sessionId);
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenOneTimeCodexTurnFailedArrives_ReturnsFailureWithoutTimeout()
    {
        const string sessionId = "session-onetime-codex-turn-failed";
        const string upstreamError = "one-time retry exhausted";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var scriptPath = Path.Combine(tempRoot, "codex-onetime-semantic-failed.cmd");

        Directory.CreateDirectory(tempRoot);
        await File.WriteAllTextAsync(
            scriptPath,
            """
            @echo off
            echo {"type":"turn.failed","error":{"message":"one-time retry exhausted","code":500}}
            powershell.exe -NoProfile -Command "Start-Sleep -Seconds 10"
            """);

        var tool = new CliToolConfig
        {
            Id = "codex-onetime-semantic-failed",
            Name = "Codex One-Time Semantic Failed",
            Command = "cmd.exe",
            ArgumentTemplate = $"/d /c \"{scriptPath}\"",
            TimeoutSeconds = 2,
            Enabled = true
        };

        var options = Options.Create(new CliToolsOption
        {
            TempWorkspaceRoot = tempRoot,
            Tools = [tool]
        });

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            options,
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(new TestCodexLikeCommandAdapter("codex-onetime-semantic-failed")),
            new StubCcSwitchService());

        try
        {
            var chunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, tool.Id, "ignored"))
            {
                chunks.Add(chunk);
            }

            Assert.Contains(
                chunks,
                c => !string.IsNullOrEmpty(c.Content) && c.Content.Contains("\"turn.failed\"", StringComparison.Ordinal));
            var failureChunk = Assert.Single(chunks, chunk => chunk.IsError && chunk.IsCompleted);
            Assert.Contains(upstreamError, failureChunk.ErrorMessage);
            Assert.DoesNotContain("执行超时", failureChunk.ErrorMessage, StringComparison.Ordinal);
        }
        finally
        {
            service.CleanupSessionWorkspace(sessionId);
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenCodexTurnCompletedArrives_CompletesBeforeIdleFallbackOrTimeout()
    {
        const string sessionId = "session-codex-turn-completed";
        const string cliThreadId = "thread-turn-completed";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var scriptPath = Path.Combine(tempRoot, "codex-semantic-completed.cmd");

        Directory.CreateDirectory(tempRoot);
        await File.WriteAllTextAsync(
            scriptPath,
            """
            @echo off
            set /p INPUT=
            echo {"type":"thread.started","thread_id":"thread-turn-completed"}
            echo {"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}
            powershell.exe -NoProfile -Command "Start-Sleep -Seconds 10"
            """);

        var tool = new CliToolConfig
        {
            Id = "codex-semantic-completed",
            Name = "Codex Semantic Completed",
            Command = "cmd.exe",
            PersistentModeArguments = $"/d /c \"{scriptPath}\"",
            UsePersistentProcess = true,
            TimeoutSeconds = 5,
            Enabled = true
        };

        var options = Options.Create(new CliToolsOption
        {
            TempWorkspaceRoot = tempRoot,
            Tools = [tool]
        });

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            options,
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(new TestCodexLikeAdapter("codex-semantic-completed")),
            new StubCcSwitchService());

        try
        {
            var chunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, tool.Id, "ignored"))
            {
                chunks.Add(chunk);
            }

            Assert.DoesNotContain(chunks, c => c.IsError && c.IsCompleted);
            Assert.Contains(
                chunks,
                c => !string.IsNullOrEmpty(c.Content) && c.Content.Contains("\"turn.completed\"", StringComparison.Ordinal));
            Assert.Contains(chunks, c => c.IsCompleted && !c.IsError);
            Assert.Equal(cliThreadId, service.GetCliThreadId(sessionId));
        }
        finally
        {
            service.CleanupSessionWorkspace(sessionId);
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenCodexTurnFailedArrives_ReturnsFailureWithoutWaitingForTimeout()
    {
        const string sessionId = "session-codex-turn-failed";
        const string upstreamError = "network retry exhausted";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var scriptPath = Path.Combine(tempRoot, "codex-semantic-failed.cmd");

        Directory.CreateDirectory(tempRoot);
        await File.WriteAllTextAsync(
            scriptPath,
            """
            @echo off
            set /p INPUT=
            echo {"type":"turn.failed","error":{"message":"network retry exhausted","code":500}}
            powershell.exe -NoProfile -Command "Start-Sleep -Seconds 10"
            """);

        var tool = new CliToolConfig
        {
            Id = "codex-semantic-failed",
            Name = "Codex Semantic Failed",
            Command = "cmd.exe",
            PersistentModeArguments = $"/d /c \"{scriptPath}\"",
            UsePersistentProcess = true,
            TimeoutSeconds = 1,
            Enabled = true
        };

        var options = Options.Create(new CliToolsOption
        {
            TempWorkspaceRoot = tempRoot,
            Tools = [tool]
        });

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            options,
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(new TestCodexLikeAdapter("codex-semantic-failed")),
            new StubCcSwitchService());

        try
        {
            var chunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, tool.Id, "ignored"))
            {
                chunks.Add(chunk);
            }

            Assert.Contains(
                chunks,
                c => !string.IsNullOrEmpty(c.Content) && c.Content.Contains("\"turn.failed\"", StringComparison.Ordinal));
            var failureChunk = Assert.Single(chunks, chunk => chunk.IsError && chunk.IsCompleted);
            Assert.Contains(upstreamError, failureChunk.ErrorMessage);
            Assert.DoesNotContain("执行已取消或超时", failureChunk.ErrorMessage, StringComparison.Ordinal);
        }
        finally
        {
            service.CleanupSessionWorkspace(sessionId);
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenAdapterProvidesDetailedFailure_ReturnsUpstreamErrorMessage()
    {
        const string upstreamError = "unexpected status 402 Payment Required";

        var tool = new CliToolConfig
        {
            Id = "adapter-error-tool",
            Name = "Adapter Error Tool",
            Command = "powershell.exe",
            ArgumentTemplate = "-NoProfile -Command \"Write-Output 'RETRY: Reconnecting... 1/5'; Write-Output 'ERROR: unexpected status 402 Payment Required'; exit 1\"",
            TimeoutSeconds = 10,
            Enabled = true
        };

        var options = Options.Create(new CliToolsOption
        {
            TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
            Tools = [tool]
        });

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            options,
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(new StubErrorAdapter()),
            new StubCcSwitchService());

        var chunks = new List<StreamOutputChunk>();
        await foreach (var chunk in service.ExecuteStreamAsync("session-adapter-error", tool.Id, "ignored"))
        {
            chunks.Add(chunk);
        }

        var failureChunk = Assert.Single(chunks.Where(c => c.IsError && c.IsCompleted));
        Assert.Contains(upstreamError, failureChunk.ErrorMessage);
        Assert.DoesNotContain("执行失败（退出码", failureChunk.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenCcSwitchManagedToolNotReady_ReturnsBlockingMessage()
    {
        var tool = new CliToolConfig
        {
            Id = "codex",
            Name = "Codex",
            Command = "codex",
            ArgumentTemplate = "exec {prompt}",
            TimeoutSeconds = 10,
            Enabled = true
        };

        var options = Options.Create(new CliToolsOption
        {
            TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
            Tools = [tool]
        });

        var ccSwitchService = new StubCcSwitchService(new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = new()
            {
                ToolId = "codex",
                ToolName = "Codex",
                IsManaged = true,
                IsLaunchReady = false,
                StatusMessage = "Codex 当前未在 cc-switch 中激活 Provider。"
            }
        });

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            options,
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(),
            ccSwitchService);

        var chunks = new List<StreamOutputChunk>();
        await foreach (var chunk in service.ExecuteStreamAsync("session-cc-switch-blocked", tool.Id, "ignored"))
        {
            chunks.Add(chunk);
        }

        var failureChunk = Assert.Single(chunks.Where(c => c.IsError && c.IsCompleted));
        Assert.Equal("Codex 当前未在 cc-switch 中激活 Provider。", failureChunk.ErrorMessage);
    }

    [Fact]
    public async Task SyncSessionCcSwitchSnapshotAsync_WhenManagedSessionExists_CopiesLiveConfigAndPersistsSnapshot()
    {
        const string sessionId = "session-sync-snapshot";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, "workspace");
        var liveConfigDirectory = Path.Combine(tempRoot, "live");
        var liveConfigPath = Path.Combine(liveConfigDirectory, "config.toml");
        const string liveConfigContent = "model = \"gpt-5.4\"\nprovider = \"provider-a\"\n";

        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(liveConfigDirectory);
        await File.WriteAllTextAsync(liveConfigPath, liveConfigContent);

        try
        {
            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools = []
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                new StubCcSwitchService(new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
                {
                    ["codex"] = new()
                    {
                        ToolId = "codex",
                        ToolName = "Codex",
                        IsManaged = true,
                        IsLaunchReady = true,
                        ActiveProviderId = "provider-a",
                        ActiveProviderName = "Provider A",
                        ActiveProviderCategory = "custom",
                        LiveConfigPath = liveConfigPath,
                        StatusMessage = "Codex 已由 cc-switch 管理并可直接启动。"
                    }
                }));

            var snapshot = await service.SyncSessionCcSwitchSnapshotAsync(sessionId);
            var snapshotPath = Path.Combine(workspacePath, ".codex", "config.toml");

            Assert.NotNull(snapshot);
            Assert.True(File.Exists(snapshotPath));
            Assert.Equal(
                liveConfigContent.ReplaceLineEndings("\n"),
                (await File.ReadAllTextAsync(snapshotPath)).ReplaceLineEndings("\n"));

            var persistedSession = repository.GetById(sessionId);
            Assert.True(persistedSession.UsesCcSwitchSnapshot);
            Assert.Equal("codex", persistedSession.CcSwitchSnapshotToolId);
            Assert.Equal("provider-a", persistedSession.CcSwitchProviderId);
            Assert.Equal("Provider A", persistedSession.CcSwitchProviderName);
            Assert.Equal("custom", persistedSession.CcSwitchProviderCategory);
            Assert.Equal(liveConfigPath, persistedSession.CcSwitchLiveConfigPath);
            Assert.Equal(Path.Combine(".codex", "config.toml"), persistedSession.CcSwitchSnapshotRelativePath);
            Assert.NotNull(persistedSession.CcSwitchSnapshotSyncedAt);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SyncSessionCcSwitchSnapshotAsync_WhenCodexLiveConfigContainsClaudeMcp_RemovesItFromProjectScopedConfig()
    {
        const string sessionId = "session-sync-strip-claude-mcp";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, "workspace");
        var liveConfigDirectory = Path.Combine(tempRoot, "live");
        var liveConfigPath = Path.Combine(liveConfigDirectory, "config.toml");
        const string liveConfigContent = """
provider = "provider-a"
requires_openai_auth = true

[mcp_servers.claude]
type = "stdio"
command = "claude"
args = ["mcp", "serve"]
""";

        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(liveConfigDirectory);
        await File.WriteAllTextAsync(liveConfigPath, liveConfigContent);

        try
        {
            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools = []
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                new StubCcSwitchService(new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
                {
                    ["codex"] = new()
                    {
                        ToolId = "codex",
                        ToolName = "Codex",
                        IsManaged = true,
                        IsLaunchReady = true,
                        ActiveProviderId = "provider-a",
                        ActiveProviderName = "Provider A",
                        ActiveProviderCategory = "custom",
                        LiveConfigPath = liveConfigPath,
                        StatusMessage = "Codex 已由 cc-switch 管理并可直接启动。"
                    }
                }));

            await service.SyncSessionCcSwitchSnapshotAsync(sessionId);

            var projectConfigPath = Path.Combine(workspacePath, ".codex", "config.toml");
            var baseConfigPath = Path.Combine(workspacePath, ".codex", "config.webcode.base.toml");
            var projectConfigContent = await File.ReadAllTextAsync(projectConfigPath);
            var baseConfigContent = await File.ReadAllTextAsync(baseConfigPath);

            Assert.DoesNotContain("[mcp_servers.claude]", projectConfigContent, StringComparison.Ordinal);
            Assert.DoesNotContain("command = \"claude\"", projectConfigContent, StringComparison.Ordinal);
            Assert.Contains("provider = \"provider-a\"", projectConfigContent, StringComparison.Ordinal);
            Assert.DoesNotContain("[mcp_servers.claude]", baseConfigContent, StringComparison.Ordinal);
            Assert.DoesNotContain("command = \"claude\"", baseConfigContent, StringComparison.Ordinal);
            Assert.Contains("provider = \"provider-a\"", baseConfigContent, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SyncSessionCcSwitchSnapshotAsync_WhenLiveConfigDirectoryContainsAuth_SyncsThatAuthIntoProjectSnapshot()
    {
        const string sessionId = "session-sync-colocated-auth";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, "workspace");
        var liveConfigDirectory = Path.Combine(tempRoot, "live", ".codex");
        var liveConfigPath = Path.Combine(liveConfigDirectory, "config.toml");
        var liveAuthPath = Path.Combine(liveConfigDirectory, "auth.json");
        var unrelatedHomePath = Path.Combine(tempRoot, "unrelated-home");
        const string liveConfigContent = "provider = \"provider-a\"\nrequires_openai_auth = true\n";
        const string liveAuthContent = "{\n  \"OPENAI_API_KEY\": \"sk-colocated-provider\"\n}\n";
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");

        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(liveConfigDirectory);
        Directory.CreateDirectory(unrelatedHomePath);
        await File.WriteAllTextAsync(liveConfigPath, liveConfigContent);
        await File.WriteAllTextAsync(liveAuthPath, liveAuthContent);

        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", null);
            Environment.SetEnvironmentVariable("HOME", unrelatedHomePath);
            Environment.SetEnvironmentVariable("USERPROFILE", unrelatedHomePath);

            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools = []
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                new StubCcSwitchService(new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
                {
                    ["codex"] = new()
                    {
                        ToolId = "codex",
                        ToolName = "Codex",
                        IsManaged = true,
                        IsLaunchReady = true,
                        ActiveProviderId = "provider-a",
                        ActiveProviderName = "Provider A",
                        ActiveProviderCategory = "custom",
                        LiveConfigPath = liveConfigPath,
                        StatusMessage = "Codex 已由 cc-switch 管理并可直接启动。"
                    }
                }));

            await service.SyncSessionCcSwitchSnapshotAsync(sessionId);

            var snapshotAuthPath = Path.Combine(workspacePath, ".codex", "auth.json");
            Assert.True(File.Exists(snapshotAuthPath));
            Assert.Equal(liveAuthContent, await File.ReadAllTextAsync(snapshotAuthPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SyncSessionCcSwitchSnapshotAsync_WhenExplicitlyResynced_RefreshesPinnedProvider()
    {
        const string sessionId = "session-resync-snapshot";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, "workspace");
        var liveConfigDirectory = Path.Combine(tempRoot, "live");
        var liveConfigPath = Path.Combine(liveConfigDirectory, "config.toml");
        var sourceHomePath = Path.Combine(tempRoot, "source-home");
        var sourceAuthPath = Path.Combine(sourceHomePath, ".codex", "auth.json");
        const string firstConfigContent = "provider = \"provider-a\"\n";
        const string secondConfigContent = "provider = \"provider-b\"\n";
        const string firstAuthContent = "{\n  \"OPENAI_API_KEY\": \"sk-provider-a\"\n}\n";
        const string secondAuthContent = "{\n  \"OPENAI_API_KEY\": \"sk-provider-b\"\n}\n";
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(liveConfigDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceAuthPath)!);
        await File.WriteAllTextAsync(liveConfigPath, firstConfigContent);
        await File.WriteAllTextAsync(sourceAuthPath, firstAuthContent);

        try
        {
            Environment.SetEnvironmentVariable("HOME", sourceHomePath);
            Environment.SetEnvironmentVariable("USERPROFILE", sourceHomePath);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(sourceHomePath, ".codex"));

            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var ccSwitchService = new StubCcSwitchService(new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new()
                {
                    ToolId = "codex",
                    ToolName = "Codex",
                    IsManaged = true,
                    IsLaunchReady = true,
                    ActiveProviderId = "provider-a",
                    ActiveProviderName = "Provider A",
                    ActiveProviderCategory = "custom",
                    LiveConfigPath = liveConfigPath,
                    StatusMessage = "Codex 已由 cc-switch 管理并可直接启动。"
                }
            });

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools = []
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                ccSwitchService);

            await service.SyncSessionCcSwitchSnapshotAsync(sessionId);

            await File.WriteAllTextAsync(liveConfigPath, secondConfigContent);
            await File.WriteAllTextAsync(sourceAuthPath, secondAuthContent);
            ccSwitchService.SetToolStatus("codex", new CcSwitchToolStatus
            {
                ToolId = "codex",
                ToolName = "Codex",
                IsManaged = true,
                IsLaunchReady = true,
                ActiveProviderId = "provider-b",
                ActiveProviderName = "Provider B",
                ActiveProviderCategory = "official",
                LiveConfigPath = liveConfigPath,
                StatusMessage = "Codex 已由 cc-switch 管理并可直接启动。"
            });

            var refreshedSnapshot = await service.SyncSessionCcSwitchSnapshotAsync(sessionId);
            var snapshotPath = Path.Combine(workspacePath, ".codex", "config.toml");
            var snapshotAuthPath = Path.Combine(workspacePath, ".codex", "auth.json");

            Assert.NotNull(refreshedSnapshot);
            Assert.Equal("Provider B", refreshedSnapshot.ProviderName);
            Assert.Equal(
                secondConfigContent.ReplaceLineEndings("\n"),
                (await File.ReadAllTextAsync(snapshotPath)).ReplaceLineEndings("\n"));
            Assert.Equal(secondAuthContent, await File.ReadAllTextAsync(snapshotAuthPath));

            var persistedSession = repository.GetById(sessionId);
            Assert.Equal("provider-b", persistedSession.CcSwitchProviderId);
            Assert.Equal("Provider B", persistedSession.CcSwitchProviderName);
            Assert.Equal("official", persistedSession.CcSwitchProviderCategory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SyncSessionCcSwitchSnapshotAsync_PreservesStoredLaunchOverrides()
    {
        const string sessionId = "session-sync-preserve-overrides";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, "workspace");
        var liveConfigDirectory = Path.Combine(tempRoot, "live");
        var liveConfigPath = Path.Combine(liveConfigDirectory, "config.toml");
        const string liveConfigContent = "provider = \"provider-a\"\n";
        var overrideJson = SessionLaunchOverrideHelper.Serialize(new Dictionary<string, SessionToolLaunchOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = new() { Model = "gpt-5.4", ReasoningEffort = "high" }
        });

        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(liveConfigDirectory);
        await File.WriteAllTextAsync(liveConfigPath, liveConfigContent);

        try
        {
            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    ToolLaunchOverridesJson = overrideJson,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools = []
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                new StubCcSwitchService(new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
                {
                    ["codex"] = new()
                    {
                        ToolId = "codex",
                        ToolName = "Codex",
                        IsManaged = true,
                        IsLaunchReady = true,
                        ActiveProviderId = "provider-a",
                        ActiveProviderName = "Provider A",
                        ActiveProviderCategory = "custom",
                        LiveConfigPath = liveConfigPath,
                        StatusMessage = "Codex 已由 cc-switch 管理并可直接启动。"
                    }
                }));

            await service.SyncSessionCcSwitchSnapshotAsync(sessionId);

            Assert.Equal(overrideJson, repository.GetById(sessionId).ToolLaunchOverridesJson);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenManagedSessionHasPinnedSnapshot_ContinuesUsingPinnedProvider()
    {
        const string sessionId = "session-pinned-snapshot";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, "workspace");
        var snapshotPath = Path.Combine(workspacePath, ".codex", "config.toml");

        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        await File.WriteAllTextAsync(snapshotPath, "provider = \"provider-a\"\n");

        try
        {
            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    UsesCcSwitchSnapshot = true,
                    CcSwitchSnapshotToolId = "codex",
                    CcSwitchProviderId = "provider-a",
                    CcSwitchProviderName = "Provider A",
                    CcSwitchSnapshotRelativePath = Path.Combine(".codex", "config.toml"),
                    CcSwitchSnapshotSyncedAt = DateTime.Now,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var tool = new CliToolConfig
            {
                Id = "codex",
                Name = "Codex",
                Command = "powershell.exe",
                ArgumentTemplate = "-NoProfile -Command \"Write-Output 'pinned-ok'\"",
                TimeoutSeconds = 10,
                Enabled = true
            };

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools = [tool]
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                new StubCcSwitchService(new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
                {
                    ["codex"] = new()
                    {
                        ToolId = "codex",
                        ToolName = "Codex",
                        IsManaged = true,
                        IsLaunchReady = false,
                        StatusMessage = "Codex 当前未在 cc-switch 中激活 Provider。"
                    }
                }));

            var chunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, tool.Id, "ignored"))
            {
                chunks.Add(chunk);
            }

            Assert.DoesNotContain(chunks, c => c.IsError && c.IsCompleted);
            Assert.Contains(chunks, c => c.Content.Contains("pinned-ok", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenManagedCodexSessionHasPinnedSnapshot_UsesSessionScopedAuthSnapshot()
    {
        const string sessionId = "session-pinned-auth-snapshot";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, "workspace");
        var liveConfigDirectory = Path.Combine(tempRoot, "live");
        var liveConfigPath = Path.Combine(liveConfigDirectory, "config.toml");
        var sourceHomePath = Path.Combine(tempRoot, "source-home");
        var sourceAuthPath = Path.Combine(sourceHomePath, ".codex", "auth.json");
        const string liveConfigContent = "provider = \"provider-a\"\nrequires_openai_auth = true\n";
        const string pinnedAuthContent = "{\n  \"OPENAI_API_KEY\": \"sk-pinned-provider\"\n}\n";
        const string globalAuthAfterSwitchContent = "{\n  \"OPENAI_API_KEY\": \"sk-current-provider\"\n}\n";
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(liveConfigDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceAuthPath)!);
        await File.WriteAllTextAsync(liveConfigPath, liveConfigContent);
        await File.WriteAllTextAsync(sourceAuthPath, pinnedAuthContent);

        try
        {
            Environment.SetEnvironmentVariable("HOME", sourceHomePath);
            Environment.SetEnvironmentVariable("USERPROFILE", sourceHomePath);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(sourceHomePath, ".codex"));
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(sourceHomePath, ".codex"));

            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var ccSwitchService = new StubCcSwitchService(new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new()
                {
                    ToolId = "codex",
                    ToolName = "Codex",
                    IsManaged = true,
                    IsLaunchReady = true,
                    ActiveProviderId = "provider-a",
                    ActiveProviderName = "Provider A",
                    ActiveProviderCategory = "custom",
                    LiveConfigPath = liveConfigPath,
                    StatusMessage = "Codex 宸茬敱 cc-switch 绠＄悊骞跺彲鐩存帴鍚姩銆?"
                }
            });

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools =
                    [
                        new CliToolConfig
                        {
                            Id = "codex",
                            Name = "Codex",
                            Command = "powershell.exe",
                            ArgumentTemplate = "-NoProfile -Command \"$authPath = Join-Path $env:CODEX_HOME 'auth.json'; Write-Output (Get-Content $authPath -Raw)\"",
                            TimeoutSeconds = 10,
                            Enabled = true
                        }
                    ]
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                ccSwitchService);

            await service.SyncSessionCcSwitchSnapshotAsync(sessionId);

            await File.WriteAllTextAsync(sourceAuthPath, globalAuthAfterSwitchContent);
            ccSwitchService.SetToolStatus("codex", new CcSwitchToolStatus
            {
                ToolId = "codex",
                ToolName = "Codex",
                IsManaged = true,
                IsLaunchReady = false,
                StatusMessage = "Codex 褰撳墠鏈湪 cc-switch 涓縺娲?Provider銆?"
            });

            var chunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, "codex", "ignored"))
            {
                chunks.Add(chunk);
            }

            Assert.DoesNotContain(chunks, c => c.IsError && c.IsCompleted);
            Assert.Contains(chunks, c => c.Content.Contains("sk-pinned-provider", StringComparison.Ordinal));
            Assert.DoesNotContain(chunks, c => c.Content.Contains("sk-current-provider", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenManagedCodexSessionHasPinnedSnapshot_SetsCodeXHomeToSessionConfigDirectory()
    {
        const string sessionId = "session-pinned-codex-home";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, "workspace");
        var liveConfigDirectory = Path.Combine(tempRoot, "live");
        var liveConfigPath = Path.Combine(liveConfigDirectory, "config.toml");
        var sourceHomePath = Path.Combine(tempRoot, "source-home");
        var sourceAuthPath = Path.Combine(sourceHomePath, ".codex", "auth.json");
        const string liveConfigContent = "provider = \"provider-a\"\nrequires_openai_auth = true\n";
        const string pinnedAuthContent = "{\n  \"OPENAI_API_KEY\": \"sk-pinned-provider\"\n}\n";
        const string globalAuthAfterSwitchContent = "{\n  \"OPENAI_API_KEY\": \"sk-current-provider\"\n}\n";
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(liveConfigDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceAuthPath)!);
        await File.WriteAllTextAsync(liveConfigPath, liveConfigContent);
        await File.WriteAllTextAsync(sourceAuthPath, pinnedAuthContent);

        try
        {
            Environment.SetEnvironmentVariable("HOME", sourceHomePath);
            Environment.SetEnvironmentVariable("USERPROFILE", sourceHomePath);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(sourceHomePath, ".codex"));

            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var ccSwitchService = new StubCcSwitchService(new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new()
                {
                    ToolId = "codex",
                    ToolName = "Codex",
                    IsManaged = true,
                    IsLaunchReady = true,
                    ActiveProviderId = "provider-a",
                    ActiveProviderName = "Provider A",
                    ActiveProviderCategory = "custom",
                    LiveConfigPath = liveConfigPath,
                    StatusMessage = "Codex managed by cc-switch."
                }
            });

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools =
                    [
                        new CliToolConfig
                        {
                            Id = "codex",
                            Name = "Codex",
                            Command = "powershell.exe",
                            ArgumentTemplate = "-NoProfile -Command \"Write-Output ('CODEX_HOME=' + $env:CODEX_HOME); $authPath = Join-Path $env:CODEX_HOME 'auth.json'; Write-Output (Get-Content $authPath -Raw)\"",
                            TimeoutSeconds = 10,
                            Enabled = true
                        }
                    ]
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                ccSwitchService);

            await service.SyncSessionCcSwitchSnapshotAsync(sessionId);

            await File.WriteAllTextAsync(sourceAuthPath, globalAuthAfterSwitchContent);
            ccSwitchService.SetToolStatus("codex", new CcSwitchToolStatus
            {
                ToolId = "codex",
                ToolName = "Codex",
                IsManaged = true,
                IsLaunchReady = false,
                StatusMessage = "Codex provider switched globally."
            });

            var chunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, "codex", "ignored"))
            {
                chunks.Add(chunk);
            }

            var expectedCodexHome = Path.Combine(workspacePath, ".codex");
            Assert.DoesNotContain(chunks, c => c.IsError && c.IsCompleted);
            Assert.Contains(chunks, c => c.Content.Contains($"CODEX_HOME={expectedCodexHome}", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(chunks, c => c.Content.Contains("sk-pinned-provider", StringComparison.Ordinal));
            Assert.DoesNotContain(chunks, c => c.Content.Contains("sk-current-provider", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenManagedCodexSessionConfigContainsClaudeMcp_RemovesItFromProjectScopedConfig()
    {
        const string sessionId = "session-codex-strip-claude-mcp";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, "workspace");
        var liveConfigDirectory = Path.Combine(tempRoot, "live");
        var liveConfigPath = Path.Combine(liveConfigDirectory, "config.toml");
        var sourceHomePath = Path.Combine(tempRoot, "source-home");
        var sourceAuthPath = Path.Combine(sourceHomePath, ".codex", "auth.json");
        const string liveConfigContent = """
provider = "provider-a"
requires_openai_auth = true

[mcp_servers.claude]
type = "stdio"
command = "claude"
args = ["mcp", "serve"]
""";
        const string authContent = "{\n  \"OPENAI_API_KEY\": \"sk-pinned-provider\"\n}\n";
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");

        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(liveConfigDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceAuthPath)!);
        await File.WriteAllTextAsync(liveConfigPath, liveConfigContent);
        await File.WriteAllTextAsync(sourceAuthPath, authContent);

        try
        {
            Environment.SetEnvironmentVariable("HOME", sourceHomePath);
            Environment.SetEnvironmentVariable("USERPROFILE", sourceHomePath);

            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var ccSwitchService = new StubCcSwitchService(new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new()
                {
                    ToolId = "codex",
                    ToolName = "Codex",
                    IsManaged = true,
                    IsLaunchReady = true,
                    ActiveProviderId = "provider-a",
                    ActiveProviderName = "Provider A",
                    ActiveProviderCategory = "custom",
                    LiveConfigPath = liveConfigPath,
                    StatusMessage = "Codex managed by cc-switch."
                }
            });

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools =
                    [
                        new CliToolConfig
                        {
                            Id = "codex",
                            Name = "Codex",
                            Command = "powershell.exe",
                            ArgumentTemplate = "-NoProfile -Command \"$configPath = Join-Path $env:CODEX_HOME 'config.toml'; Write-Output (Get-Content $configPath -Raw)\"",
                            TimeoutSeconds = 10,
                            Enabled = true
                        }
                    ]
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                ccSwitchService);

            await service.SyncSessionCcSwitchSnapshotAsync(sessionId);

            var chunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, "codex", "ignored"))
            {
                chunks.Add(chunk);
            }

            var projectConfigPath = Path.Combine(workspacePath, ".codex", "config.toml");
            var baseConfigPath = Path.Combine(workspacePath, ".codex", "config.webcode.base.toml");
            var projectConfigContent = await File.ReadAllTextAsync(projectConfigPath);
            var baseConfigContent = await File.ReadAllTextAsync(baseConfigPath);

            Assert.DoesNotContain(chunks, c => c.IsError && c.IsCompleted);
            Assert.DoesNotContain("[mcp_servers.claude]", projectConfigContent, StringComparison.Ordinal);
            Assert.DoesNotContain("command = \"claude\"", projectConfigContent, StringComparison.Ordinal);
            Assert.DoesNotContain("[mcp_servers.claude]", baseConfigContent, StringComparison.Ordinal);
            Assert.DoesNotContain("command = \"claude\"", baseConfigContent, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenManagedCodexSessionResumesLegacyThread_CopiesRolloutIntoSessionCodeXHome()
    {
        const string sessionId = "session-codex-legacy-rollout";
        const string cliThreadId = "019db9a3-19cf-7260-bece-1c08f337520a";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, "workspace");
        var liveConfigDirectory = Path.Combine(tempRoot, "live");
        var liveConfigPath = Path.Combine(liveConfigDirectory, "config.toml");
        var sourceHomePath = Path.Combine(tempRoot, "source-home");
        var sourceAuthPath = Path.Combine(sourceHomePath, ".codex", "auth.json");
        var sourceRolloutPath = Path.Combine(
            sourceHomePath,
            ".codex",
            "sessions",
            "2026",
            "04",
            "23",
            $"rollout-2026-04-23T17-19-27-{cliThreadId}.jsonl");
        const string liveConfigContent = "provider = \"provider-a\"\nrequires_openai_auth = true\n";
        const string pinnedAuthContent = "{\n  \"OPENAI_API_KEY\": \"sk-pinned-provider\"\n}\n";
        const string rolloutContent = "{\"type\":\"thread.started\",\"thread_id\":\"019db9a3-19cf-7260-bece-1c08f337520a\"}\n";
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(liveConfigDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceAuthPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceRolloutPath)!);
        await File.WriteAllTextAsync(liveConfigPath, liveConfigContent);
        await File.WriteAllTextAsync(sourceAuthPath, pinnedAuthContent);
        await File.WriteAllTextAsync(sourceRolloutPath, rolloutContent);

        try
        {
            Environment.SetEnvironmentVariable("HOME", sourceHomePath);
            Environment.SetEnvironmentVariable("USERPROFILE", sourceHomePath);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(sourceHomePath, ".codex"));

            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    CliThreadId = cliThreadId,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var ccSwitchService = new StubCcSwitchService(new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new()
                {
                    ToolId = "codex",
                    ToolName = "Codex",
                    IsManaged = true,
                    IsLaunchReady = true,
                    ActiveProviderId = "provider-a",
                    ActiveProviderName = "Provider A",
                    ActiveProviderCategory = "custom",
                    LiveConfigPath = liveConfigPath,
                    StatusMessage = "Codex managed by cc-switch."
                }
            });

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools =
                    [
                        new CliToolConfig
                        {
                            Id = "codex",
                            Name = "Codex",
                            Command = "powershell.exe",
                            ArgumentTemplate = "-NoProfile -Command \"$rollout = Join-Path $env:CODEX_HOME 'sessions\\2026\\04\\23\\rollout-2026-04-23T17-19-27-019db9a3-19cf-7260-bece-1c08f337520a.jsonl'; Write-Output ('ROLLOUT_EXISTS=' + [bool](Test-Path $rollout)); if (Test-Path $rollout) { Write-Output (Get-Content $rollout -Raw) }\"",
                            TimeoutSeconds = 10,
                            Enabled = true
                        }
                    ]
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                ccSwitchService);

            await service.SyncSessionCcSwitchSnapshotAsync(sessionId);

            var chunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, "codex", "ignored"))
            {
                chunks.Add(chunk);
            }

            Assert.DoesNotContain(chunks, c => c.IsError && c.IsCompleted);
            Assert.Contains(chunks, c => c.Content.Contains("ROLLOUT_EXISTS=True", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(chunks, c => c.Content.Contains(rolloutContent.Trim(), StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenWorkspaceRolloutAlreadyHasNewerContent_PreservesWorkspaceCopy()
    {
        const string sessionId = "session-codex-preserve-newer-rollout";
        const string cliThreadId = "019db9a3-19cf-7260-bece-1c08f337520a";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, "workspace");
        var liveConfigDirectory = Path.Combine(tempRoot, "live");
        var liveConfigPath = Path.Combine(liveConfigDirectory, "config.toml");
        var sourceHomePath = Path.Combine(tempRoot, "source-home");
        var sourceAuthPath = Path.Combine(sourceHomePath, ".codex", "auth.json");
        var sourceRolloutPath = Path.Combine(
            sourceHomePath,
            ".codex",
            "sessions",
            "2026",
            "04",
            "23",
            $"rollout-2026-04-23T17-19-27-{cliThreadId}.jsonl");
        var workspaceRolloutPath = Path.Combine(
            workspacePath,
            ".codex",
            "sessions",
            "2026",
            "04",
            "23",
            $"rollout-2026-04-23T17-19-27-{cliThreadId}.jsonl");
        const string liveConfigContent = "provider = \"provider-a\"\nrequires_openai_auth = true\n";
        const string pinnedAuthContent = "{\n  \"OPENAI_API_KEY\": \"sk-pinned-provider\"\n}\n";
        const string sourceRolloutContent = "{\"type\":\"thread.started\",\"thread_id\":\"019db9a3-19cf-7260-bece-1c08f337520a\"}\n";
        const string workspaceRolloutContent = "{\"type\":\"thread.started\",\"thread_id\":\"019db9a3-19cf-7260-bece-1c08f337520a\"}\n{\"type\":\"item.completed\",\"item\":{\"id\":\"item_0\",\"type\":\"agent_message\",\"text\":\"workspace newer content\"}}\n";
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");

        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(liveConfigDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceAuthPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceRolloutPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(workspaceRolloutPath)!);
        await File.WriteAllTextAsync(liveConfigPath, liveConfigContent);
        await File.WriteAllTextAsync(sourceAuthPath, pinnedAuthContent);
        await File.WriteAllTextAsync(sourceRolloutPath, sourceRolloutContent);
        await File.WriteAllTextAsync(workspaceRolloutPath, workspaceRolloutContent);
        File.SetLastWriteTimeUtc(sourceRolloutPath, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(workspaceRolloutPath, DateTime.UtcNow);

        try
        {
            Environment.SetEnvironmentVariable("HOME", sourceHomePath);
            Environment.SetEnvironmentVariable("USERPROFILE", sourceHomePath);

            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    CliThreadId = cliThreadId,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var ccSwitchService = new StubCcSwitchService(new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new()
                {
                    ToolId = "codex",
                    ToolName = "Codex",
                    IsManaged = true,
                    IsLaunchReady = true,
                    ActiveProviderId = "provider-a",
                    ActiveProviderName = "Provider A",
                    ActiveProviderCategory = "custom",
                    LiveConfigPath = liveConfigPath,
                    StatusMessage = "Codex managed by cc-switch."
                }
            });

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools =
                    [
                        new CliToolConfig
                        {
                            Id = "codex",
                            Name = "Codex",
                            Command = "powershell.exe",
                            ArgumentTemplate = "-NoProfile -Command \\\"$rollout = Join-Path $env:CODEX_HOME 'sessions\\\\2026\\\\04\\\\23\\\\rollout-2026-04-23T17-19-27-019db9a3-19cf-7260-bece-1c08f337520a.jsonl'; Write-Output ('ROLLOUT_EXISTS=' + [bool](Test-Path $rollout)); if (Test-Path $rollout) { Write-Output (Get-Content $rollout -Raw) }\\\"",
                            TimeoutSeconds = 10,
                            Enabled = true
                        }
                    ]
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                ccSwitchService);

            await service.SyncSessionCcSwitchSnapshotAsync(sessionId);

            var chunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, "codex", "ignored"))
            {
                chunks.Add(chunk);
            }

            Assert.DoesNotContain(chunks, c => c.IsError && c.IsCompleted);
            var syncedWorkspaceRollout = await File.ReadAllTextAsync(workspaceRolloutPath);
            Assert.Equal(workspaceRolloutContent, syncedWorkspaceRollout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenSourceRolloutHasNewerContent_UpdatesWorkspaceCopy()
    {
        const string sessionId = "session-codex-refresh-rollout";
        const string cliThreadId = "019db9a3-19cf-7260-bece-1c08f337520a";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, "workspace");
        var liveConfigDirectory = Path.Combine(tempRoot, "live");
        var liveConfigPath = Path.Combine(liveConfigDirectory, "config.toml");
        var sourceHomePath = Path.Combine(tempRoot, "source-home");
        var sourceAuthPath = Path.Combine(sourceHomePath, ".codex", "auth.json");
        var sourceRolloutPath = Path.Combine(
            sourceHomePath,
            ".codex",
            "sessions",
            "2026",
            "04",
            "23",
            $"rollout-2026-04-23T17-19-27-{cliThreadId}.jsonl");
        var workspaceRolloutPath = Path.Combine(
            workspacePath,
            ".codex",
            "sessions",
            "2026",
            "04",
            "23",
            $"rollout-2026-04-23T17-19-27-{cliThreadId}.jsonl");
        const string liveConfigContent = "provider = \"provider-a\"\nrequires_openai_auth = true\n";
        const string pinnedAuthContent = "{\n  \"OPENAI_API_KEY\": \"sk-pinned-provider\"\n}\n";
        const string sourceRolloutContent = "{\"type\":\"thread.started\",\"thread_id\":\"019db9a3-19cf-7260-bece-1c08f337520a\"}\n{\"type\":\"item.completed\",\"item\":{\"id\":\"item_0\",\"type\":\"agent_message\",\"text\":\"source newer content\"}}\n";
        const string workspaceRolloutContent = "{\"type\":\"thread.started\",\"thread_id\":\"019db9a3-19cf-7260-bece-1c08f337520a\"}\n";
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(liveConfigDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceAuthPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceRolloutPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(workspaceRolloutPath)!);
        await File.WriteAllTextAsync(liveConfigPath, liveConfigContent);
        await File.WriteAllTextAsync(sourceAuthPath, pinnedAuthContent);
        await File.WriteAllTextAsync(sourceRolloutPath, sourceRolloutContent);
        await File.WriteAllTextAsync(workspaceRolloutPath, workspaceRolloutContent);
        File.SetLastWriteTimeUtc(sourceRolloutPath, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(workspaceRolloutPath, DateTime.UtcNow.AddMinutes(-5));

        try
        {
            Environment.SetEnvironmentVariable("HOME", sourceHomePath);
            Environment.SetEnvironmentVariable("USERPROFILE", sourceHomePath);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(sourceHomePath, ".codex"));

            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    CliThreadId = cliThreadId,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var ccSwitchService = new StubCcSwitchService(new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new()
                {
                    ToolId = "codex",
                    ToolName = "Codex",
                    IsManaged = true,
                    IsLaunchReady = true,
                    ActiveProviderId = "provider-a",
                    ActiveProviderName = "Provider A",
                    ActiveProviderCategory = "custom",
                    LiveConfigPath = liveConfigPath,
                    StatusMessage = "Codex managed by cc-switch."
                }
            });

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools =
                    [
                        new CliToolConfig
                        {
                            Id = "codex",
                            Name = "Codex",
                            Command = "powershell.exe",
                            ArgumentTemplate = "-NoProfile -Command \\\"$rollout = Join-Path $env:CODEX_HOME 'sessions\\\\2026\\\\04\\\\23\\\\rollout-2026-04-23T17-19-27-019db9a3-19cf-7260-bece-1c08f337520a.jsonl'; Write-Output ('ROLLOUT_EXISTS=' + [bool](Test-Path $rollout)); if (Test-Path $rollout) { Write-Output (Get-Content $rollout -Raw) }\\\"",
                            TimeoutSeconds = 10,
                            Enabled = true
                        }
                    ]
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                ccSwitchService);

            await service.SyncSessionCcSwitchSnapshotAsync(sessionId);

            var chunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, "codex", "ignored"))
            {
                chunks.Add(chunk);
            }

            Assert.DoesNotContain(chunks, c => c.IsError && c.IsCompleted);
            var syncedWorkspaceRollout = await File.ReadAllTextAsync(workspaceRolloutPath);
            Assert.Equal(sourceRolloutContent, syncedWorkspaceRollout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SyncSessionCcSwitchSnapshotAsync_WhenCustomWorkspaceAlreadyHasCodexAuth_OverwritesItWithSyncedProviderAuth()
    {
        const string sessionId = "session-custom-workspace-auth";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var customWorkspacePath = Path.Combine(tempRoot, "MMIS-Server");
        var liveConfigDirectory = Path.Combine(tempRoot, "live");
        var liveConfigPath = Path.Combine(liveConfigDirectory, "config.toml");
        var projectAuthPath = Path.Combine(customWorkspacePath, ".codex", "auth.json");
        var sourceHomePath = Path.Combine(tempRoot, "source-home");
        var sourceAuthPath = Path.Combine(sourceHomePath, ".codex", "auth.json");
        const string liveConfigContent = "provider = \"provider-a\"\nrequires_openai_auth = true\n";
        const string projectAuthContent = "{\n  \"OPENAI_API_KEY\": \"sk-project-workspace\"\n}\n";
        const string globalAuthContent = "{\n  \"OPENAI_API_KEY\": \"sk-global-home\"\n}\n";
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        Directory.CreateDirectory(Path.GetDirectoryName(projectAuthPath)!);
        Directory.CreateDirectory(liveConfigDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceAuthPath)!);
        await File.WriteAllTextAsync(projectAuthPath, projectAuthContent);
        await File.WriteAllTextAsync(liveConfigPath, liveConfigContent);
        await File.WriteAllTextAsync(sourceAuthPath, globalAuthContent);

        try
        {
            Environment.SetEnvironmentVariable("HOME", sourceHomePath);
            Environment.SetEnvironmentVariable("USERPROFILE", sourceHomePath);
            Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(sourceHomePath, ".codex"));

            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = customWorkspacePath,
                    IsCustomWorkspace = true,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var ccSwitchService = new StubCcSwitchService(new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new()
                {
                    ToolId = "codex",
                    ToolName = "Codex",
                    IsManaged = true,
                    IsLaunchReady = true,
                    ActiveProviderId = "provider-a",
                    ActiveProviderName = "Provider A",
                    ActiveProviderCategory = "custom",
                    LiveConfigPath = liveConfigPath,
                    StatusMessage = "Codex 宸茬敱 cc-switch 绠＄悊骞跺彲鐩存帴鍚姩銆?"
                }
            });

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools =
                    [
                        new CliToolConfig
                        {
                            Id = "codex",
                            Name = "Codex",
                            Command = "powershell.exe",
                            ArgumentTemplate = "-NoProfile -Command \"$authPath = Join-Path $env:CODEX_HOME 'auth.json'; Write-Output (Get-Content $authPath -Raw)\"",
                            TimeoutSeconds = 10,
                            Enabled = true
                        }
                    ]
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                ccSwitchService);

            await service.SyncSessionCcSwitchSnapshotAsync(sessionId);

            Assert.Equal(globalAuthContent, await File.ReadAllTextAsync(projectAuthPath));

            var chunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, "codex", "ignored"))
            {
                chunks.Add(chunk);
            }

            Assert.DoesNotContain(chunks, c => c.IsError && c.IsCompleted);
            Assert.Contains(chunks, c => c.Content.Contains("sk-global-home", StringComparison.Ordinal));
            Assert.DoesNotContain(chunks, c => c.Content.Contains("sk-project-workspace", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenManagedCodexSessionHasLaunchOverride_PatchesAndRestoresSnapshotConfig()
    {
        const string sessionId = "session-managed-codex-launch-override";
        const string baseConfigContent = "provider = \"provider-a\"\nmodel = \"provider-default\"\nmodel_reasoning_effort = \"medium\"\n";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, "workspace");
        var snapshotPath = Path.Combine(workspacePath, ".codex", "config.toml");

        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        await File.WriteAllTextAsync(snapshotPath, baseConfigContent);

        try
        {
            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    UsesCcSwitchSnapshot = true,
                    CcSwitchSnapshotToolId = "codex",
                    CcSwitchProviderId = "provider-a",
                    CcSwitchProviderName = "Provider A",
                    CcSwitchSnapshotRelativePath = Path.Combine(".codex", "config.toml"),
                    CcSwitchSnapshotSyncedAt = DateTime.Now,
                    ToolLaunchOverridesJson = SessionLaunchOverrideHelper.Serialize(new Dictionary<string, SessionToolLaunchOverride>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["codex"] = new() { Model = "gpt-5.4", ReasoningEffort = "high" }
                    }),
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var tool = new CliToolConfig
            {
                Id = "codex",
                Name = "Codex",
                Command = "powershell.exe",
                ArgumentTemplate = "-NoProfile -Command \"Write-Output 'override-ok'\"",
                TimeoutSeconds = 10,
                Enabled = true
            };

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools = [tool]
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                new StubCcSwitchService(new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase)
                {
                    ["codex"] = new()
                    {
                        ToolId = "codex",
                        ToolName = "Codex",
                        IsManaged = true,
                        IsLaunchReady = false,
                        StatusMessage = "Codex 当前未在 cc-switch 中激活 Provider。"
                    }
                }));

            var firstRunChunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, tool.Id, "ignored"))
            {
                firstRunChunks.Add(chunk);
            }

            Assert.Contains(firstRunChunks, c => c.Content.Contains("override-ok", StringComparison.OrdinalIgnoreCase));

            var overriddenConfig = await File.ReadAllTextAsync(snapshotPath);
            Assert.Contains("provider = \"provider-a\"", overriddenConfig, StringComparison.Ordinal);
            Assert.Contains("model = \"gpt-5.4\"", overriddenConfig, StringComparison.Ordinal);
            Assert.Contains("model_reasoning_effort = \"high\"", overriddenConfig, StringComparison.Ordinal);

            repository.GetById(sessionId).ToolLaunchOverridesJson = null;
            await service.ResetSessionRuntimeAsync(sessionId);

            var secondRunChunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, tool.Id, "ignored"))
            {
                secondRunChunks.Add(chunk);
            }

            Assert.Contains(secondRunChunks, c => c.Content.Contains("override-ok", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(
                baseConfigContent.ReplaceLineEndings("\n"),
                (await File.ReadAllTextAsync(snapshotPath)).ReplaceLineEndings("\n"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenPinnedSnapshotFileIsMissing_ReturnsSyncRequiredMessage()
    {
        const string sessionId = "session-missing-snapshot";
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, "workspace");
        Directory.CreateDirectory(workspacePath);

        try
        {
            var repository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = sessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    UsesCcSwitchSnapshot = true,
                    CcSwitchSnapshotToolId = "codex",
                    CcSwitchProviderId = "provider-a",
                    CcSwitchProviderName = "Provider A",
                    CcSwitchSnapshotRelativePath = Path.Combine(".codex", "config.toml"),
                    CcSwitchSnapshotSyncedAt = DateTime.Now,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var tool = new CliToolConfig
            {
                Id = "codex",
                Name = "Codex",
                Command = "powershell.exe",
                ArgumentTemplate = "-NoProfile -Command \"Write-Output 'should-not-run'\"",
                TimeoutSeconds = 10,
                Enabled = true
            };

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = tempRoot,
                    Tools = [tool]
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(repository, new StubSessionOutputService()),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                new StubCcSwitchService());

            var chunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync(sessionId, tool.Id, "ignored"))
            {
                chunks.Add(chunk);
            }

            var failureChunk = Assert.Single(chunks.Where(c => c.IsError && c.IsCompleted));
            Assert.Contains("当前会话固定的 Codex Provider 快照已缺失或不可用。", failureChunk.ErrorMessage);
            Assert.Contains("同步到当前 cc-switch Provider", failureChunk.ErrorMessage);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void CodexAdapter_ParseOutputLine_WhenTurnFailed_SetsErrorMessage()
    {
        const string upstreamError = "unexpected status 402 Payment Required";
        var adapter = new CodexAdapter();

        var outputEvent = adapter.ParseOutputLine(
            """{"type":"turn.failed","error":{"message":"unexpected status 402 Payment Required","code":402}}""");

        var failureEvent = Assert.IsType<CliOutputEvent>(outputEvent);
        Assert.True(failureEvent.IsError);
        Assert.Equal(upstreamError, failureEvent.ErrorMessage);
    }

    [Fact]
    public void RewriteCodexLaunchToNode_WhenCommandIsCmdWrapper_RewritesToNodeJsEntry()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var npmRoot = Path.Combine(tempRoot, "npm");
        var codexCmdPath = Path.Combine(npmRoot, "codex.cmd");
        var codexJsPath = Path.Combine(npmRoot, "node_modules", "@openai", "codex", "bin", "codex.js");
        var fakeNodePath = Path.Combine(tempRoot, "node.exe");

        Directory.CreateDirectory(Path.GetDirectoryName(codexJsPath)!);
        File.WriteAllText(codexCmdPath, "@echo off");
        File.WriteAllText(codexJsPath, "console.log('codex');");
        File.WriteAllText(fakeNodePath, string.Empty);

        try
        {
            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
                    Tools = []
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                new StubCcSwitchService());

            typeof(CliExecutorService)
                .GetField("_preferredNodeExecutablePath", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, fakeNodePath);

            var startInfo = new ProcessStartInfo
            {
                FileName = codexCmdPath,
                Arguments = "exec --json \"problematic \\\"prompt\\\"\""
            };

            typeof(CliExecutorService)
                .GetMethod("RewriteCodexLaunchToNode", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [startInfo, new CliToolConfig { Id = "codex", Command = "codex" }, codexCmdPath, startInfo.Arguments]);

            Assert.Equal(fakeNodePath, startInfo.FileName);
            Assert.StartsWith($"\"{codexJsPath}\" ", startInfo.Arguments, StringComparison.Ordinal);
            Assert.Contains("problematic \\\"prompt\\\"", startInfo.Arguments, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void BuildCodexConfigContent_UsesNewSchema()
    {
        var envVars = new Dictionary<string, string>
        {
            ["CODEX_BASE_URL"] = "https://api.routin.ai/v1",
            ["CODEX_MODEL"] = "gpt-5.4",
            ["CODEX_MODEL_PROVIDER"] = "meteor-ai",
            ["CODEX_PROVIDER_NAME"] = "meteor-ai",
            ["CODEX_WIRE_API"] = "responses",
            ["CODEX_APPROVAL_POLICY"] = "never",
            ["CODEX_MODEL_REASONING_EFFORT"] = "xhigh",
            ["CODEX_SANDBOX_MODE"] = "danger-full-access"
        };

        var configContent = (string)typeof(CliExecutorService)
            .GetMethod("BuildCodexConfigContent", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [envVars, false])!;

        Assert.Contains("model_provider = \"meteor-ai\"", configContent, StringComparison.Ordinal);
        Assert.Contains("disable_response_storage = true", configContent, StringComparison.Ordinal);
        Assert.Contains("max_context = 1000000", configContent, StringComparison.Ordinal);
        Assert.Contains("context_compact_limit = 800000", configContent, StringComparison.Ordinal);
        Assert.DoesNotContain("rmcp_client = true", configContent, StringComparison.Ordinal);
        Assert.Contains("model_verbosity = \"high\"", configContent, StringComparison.Ordinal);
        Assert.DoesNotContain("[mcp_servers.claude]", configContent, StringComparison.Ordinal);
        Assert.Contains("[model_providers.\"meteor-ai\"]", configContent, StringComparison.Ordinal);
        Assert.Contains("requires_openai_auth = true", configContent, StringComparison.Ordinal);
        Assert.Contains("[windows]", configContent, StringComparison.Ordinal);
        Assert.DoesNotContain("\nprofile = ", configContent, StringComparison.Ordinal);
        Assert.DoesNotContain("[profiles.", configContent, StringComparison.Ordinal);
        Assert.DoesNotContain("env_key =", configContent, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCodexConfigContent_WhenGoalsEnabled_AppendsGoalsFeatureFlag()
    {
        var configContent = (string)typeof(CliExecutorService)
            .GetMethod("BuildCodexConfigContent", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new Dictionary<string, string>(), true])!;

        Assert.Contains("[features]", configContent, StringComparison.Ordinal);
        Assert.Contains("goals = true", configContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveCommandPath_WhenCommandExistsOnWindowsPath_ReturnsFullPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var pathEntry = Path.Combine(tempRoot, "bin");
        const string commandName = "webcode-path-only-test";
        var commandPath = Path.Combine(pathEntry, commandName + ".cmd");
        Directory.CreateDirectory(pathEntry);
        File.WriteAllText(commandPath, "@echo off");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalPathExt = Environment.GetEnvironmentVariable("PATHEXT");

        try
        {
            Environment.SetEnvironmentVariable("PATH", pathEntry);
            Environment.SetEnvironmentVariable("PATHEXT", ".COM;.EXE;.BAT;.CMD");

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                Options.Create(new CliToolsOption
                {
                    TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
                    Tools = []
                }),
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                new StubCcSwitchService());

            var resolvedCommand = (string)typeof(CliExecutorService)
                .GetMethod("ResolveCommandPath", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [commandName])!;

            Assert.Equal(commandPath, resolvedCommand, ignoreCase: OperatingSystem.IsWindows());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("PATHEXT", originalPathExt);

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenCommandExistsAsWindowsCmdShimOnPath_StartsSuccessfully()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var shimDirectory = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(shimDirectory);

        var shimPath = Path.Combine(shimDirectory, "fakecli.cmd");
        await File.WriteAllTextAsync(shimPath, "@ECHO off\r\necho shim-ok\r\n");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", shimDirectory + ";" + originalPath);

        try
        {
            var tool = new CliToolConfig
            {
                Id = "fakecli",
                Name = "Fake CLI",
                Command = "fakecli",
                ArgumentTemplate = "",
                TimeoutSeconds = 5,
                Enabled = true
            };

            var options = Options.Create(new CliToolsOption
            {
                TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
                Tools = [tool]
            });

            var service = new CliExecutorService(
                NullLogger<CliExecutorService>.Instance,
                options,
                NullLogger<PersistentProcessManager>.Instance,
                new NullServiceProvider(),
                new StubChatSessionService(),
                new StubCliAdapterFactory(),
                new StubCcSwitchService());

            var chunks = new List<StreamOutputChunk>();
            await foreach (var chunk in service.ExecuteStreamAsync("session-cmd-shim", tool.Id, "ignored"))
            {
                chunks.Add(chunk);
            }

            Assert.DoesNotContain(chunks, c => c.IsError && c.IsCompleted);
            Assert.Contains(chunks, c => c.Content.Contains("shim-ok", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);

            if (Directory.Exists(shimDirectory))
            {
                Directory.Delete(shimDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExecuteStreamAsync_WhenWorkingDirectoryMissing_ReturnsFriendlyError()
    {
        var tool = new CliToolConfig
        {
            Id = "missing-working-dir-tool",
            Name = "Missing Working Dir Tool",
            Command = "powershell.exe",
            ArgumentTemplate = "-NoProfile -Command \"Write-Output 'should not start'\"",
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"), "missing"),
            TimeoutSeconds = 10,
            Enabled = true
        };

        var options = Options.Create(new CliToolsOption
        {
            TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
            Tools = [tool]
        });

        var service = new CliExecutorService(
            NullLogger<CliExecutorService>.Instance,
            options,
            NullLogger<PersistentProcessManager>.Instance,
            new NullServiceProvider(),
            new StubChatSessionService(),
            new StubCliAdapterFactory(),
            new StubCcSwitchService());

        var chunks = new List<StreamOutputChunk>();
        await foreach (var chunk in service.ExecuteStreamAsync("session-missing-working-dir", tool.Id, "ignored"))
        {
            chunks.Add(chunk);
        }

        var failureChunk = Assert.Single(chunks.Where(c => c.IsError && c.IsCompleted));
        Assert.Contains("工作目录不存在", failureChunk.ErrorMessage);
        Assert.Contains(tool.WorkingDirectory, failureChunk.ErrorMessage);
    }

    private sealed class StubChatSessionService : IChatSessionService
    {
        public void AddMessage(string sessionId, ChatMessage message) { }

        public List<ChatMessage> GetMessages(string sessionId) => [];

        public void ClearSession(string sessionId) { }

        public void UpdateMessage(string sessionId, string messageId, Action<ChatMessage> updateAction) { }

        public ChatMessage? GetMessage(string sessionId, string messageId) => null;
    }

    private sealed class StubCliAdapterFactory(params ICliToolAdapter[] adapters) : ICliAdapterFactory
    {
        private readonly ICliToolAdapter[] _adapters = adapters;

        public ICliToolAdapter? GetAdapter(CliToolConfig tool)
            => _adapters.FirstOrDefault(adapter => adapter.CanHandle(tool));

        public ICliToolAdapter? GetAdapter(string toolId)
            => _adapters.FirstOrDefault(adapter =>
                adapter.SupportedToolIds.Any(id => string.Equals(id, toolId, StringComparison.OrdinalIgnoreCase)));

        public bool SupportsStreamParsing(CliToolConfig tool)
            => GetAdapter(tool)?.SupportsStreamParsing ?? false;

        public IEnumerable<ICliToolAdapter> GetAllAdapters() => _adapters;
    }

    private sealed class StubErrorAdapter : ICliToolAdapter
    {
        public string[] SupportedToolIds => ["adapter-error-tool"];

        public bool SupportsStreamParsing => true;

        public bool CanHandle(CliToolConfig tool)
            => string.Equals(tool.Id, "adapter-error-tool", StringComparison.OrdinalIgnoreCase);

        public string BuildArguments(CliToolConfig tool, string prompt, CliSessionContext context)
            => tool.ArgumentTemplate;

        public string BuildLowInterruptionArguments(CliToolConfig tool, CliSessionContext context)
            => tool.ArgumentTemplate;

        public CliOutputEvent? ParseOutputLine(string line)
        {
            if (line.StartsWith("RETRY: ", StringComparison.OrdinalIgnoreCase))
            {
                return new CliOutputEvent
                {
                    EventType = "error",
                    IsError = true,
                    ErrorMessage = line["RETRY: ".Length..]
                };
            }

            if (line.StartsWith("ERROR: ", StringComparison.OrdinalIgnoreCase))
            {
                return new CliOutputEvent
                {
                    EventType = "error",
                    IsError = true,
                    ErrorMessage = line["ERROR: ".Length..]
                };
            }

            return null;
        }

        public string? ExtractSessionId(CliOutputEvent outputEvent) => null;

        public string? ExtractAssistantMessage(CliOutputEvent outputEvent) => null;

        public string GetEventTitle(CliOutputEvent outputEvent) => outputEvent.Title ?? string.Empty;

        public string GetEventBadgeClass(CliOutputEvent outputEvent) => string.Empty;

        public string GetEventBadgeLabel(CliOutputEvent outputEvent) => string.Empty;
    }

    private sealed class TestCodexLikeAdapter(string toolId) : ICliToolAdapter
    {
        private readonly CodexAdapter _inner = new();

        public string[] SupportedToolIds => [toolId];

        public bool SupportsStreamParsing => true;

        public bool CanHandle(CliToolConfig tool)
            => string.Equals(tool.Id, toolId, StringComparison.OrdinalIgnoreCase);

        public string BuildArguments(CliToolConfig tool, string prompt, CliSessionContext context)
            => prompt;

        public string BuildLowInterruptionArguments(CliToolConfig tool, CliSessionContext context)
            => throw new NotSupportedException();

        public CliOutputEvent? ParseOutputLine(string line) => _inner.ParseOutputLine(line);

        public string? ExtractSessionId(CliOutputEvent outputEvent) => _inner.ExtractSessionId(outputEvent);

        public string? ExtractAssistantMessage(CliOutputEvent outputEvent) => _inner.ExtractAssistantMessage(outputEvent);

        public string GetEventTitle(CliOutputEvent outputEvent) => _inner.GetEventTitle(outputEvent);

        public string GetEventBadgeClass(CliOutputEvent outputEvent) => _inner.GetEventBadgeClass(outputEvent);

        public string GetEventBadgeLabel(CliOutputEvent outputEvent) => _inner.GetEventBadgeLabel(outputEvent);
    }

    private sealed class TestCodexLikeCommandAdapter(string toolId) : ICliToolAdapter
    {
        private readonly CodexAdapter _inner = new();

        public string[] SupportedToolIds => [toolId];

        public bool SupportsStreamParsing => true;

        public bool CanHandle(CliToolConfig tool)
            => string.Equals(tool.Id, toolId, StringComparison.OrdinalIgnoreCase);

        public string BuildArguments(CliToolConfig tool, string prompt, CliSessionContext context)
            => tool.ArgumentTemplate;

        public string BuildLowInterruptionArguments(CliToolConfig tool, CliSessionContext context)
            => throw new NotSupportedException();

        public CliOutputEvent? ParseOutputLine(string line) => _inner.ParseOutputLine(line);

        public string? ExtractSessionId(CliOutputEvent outputEvent) => _inner.ExtractSessionId(outputEvent);

        public string? ExtractAssistantMessage(CliOutputEvent outputEvent) => _inner.ExtractAssistantMessage(outputEvent);

        public string GetEventTitle(CliOutputEvent outputEvent) => _inner.GetEventTitle(outputEvent);

        public string GetEventBadgeClass(CliOutputEvent outputEvent) => _inner.GetEventBadgeClass(outputEvent);

        public string GetEventBadgeLabel(CliOutputEvent outputEvent) => _inner.GetEventBadgeLabel(outputEvent);
    }

    private sealed class RecordingLowInterruptionAdapter : ICliToolAdapter
    {
        public string[] SupportedToolIds => ["recording-low-interruption-tool"];

        public bool SupportsStreamParsing => false;

        public int BuildArgumentsCallCount { get; private set; }

        public int BuildLowInterruptionArgumentsCallCount { get; private set; }

        public CliSessionContext? LastLowInterruptionContext { get; private set; }

        public bool CanHandle(CliToolConfig tool)
            => string.Equals(tool.Id, "recording-low-interruption-tool", StringComparison.OrdinalIgnoreCase);

        public string BuildArguments(CliToolConfig tool, string prompt, CliSessionContext context)
        {
            BuildArgumentsCallCount++;
            return "-NoProfile -Command \"Write-Output 'normal-path'\"";
        }

        public string BuildLowInterruptionArguments(CliToolConfig tool, CliSessionContext context)
        {
            BuildLowInterruptionArgumentsCallCount++;
            LastLowInterruptionContext = context;
            return "-NoProfile -Command \"Write-Output 'low-interruption-path'\"";
        }

        public CliOutputEvent? ParseOutputLine(string line) => null;

        public string? ExtractSessionId(CliOutputEvent outputEvent) => null;

        public string? ExtractAssistantMessage(CliOutputEvent outputEvent) => null;

        public string GetEventTitle(CliOutputEvent outputEvent) => outputEvent.Title ?? string.Empty;

        public string GetEventBadgeClass(CliOutputEvent outputEvent) => string.Empty;

        public string GetEventBadgeLabel(CliOutputEvent outputEvent) => string.Empty;
    }

    private sealed class TrackingGracefulExitCliExecutorService : CliExecutorService
    {
        public TrackingGracefulExitCliExecutorService(
            ILogger<CliExecutorService> logger,
            IOptions<CliToolsOption> options,
            ILogger<PersistentProcessManager> processManagerLogger,
            IServiceProvider serviceProvider,
            IChatSessionService chatSessionService,
            ICliAdapterFactory adapterFactory,
            ICcSwitchService ccSwitchService)
            : base(logger, options, processManagerLogger, serviceProvider, chatSessionService, adapterFactory, ccSwitchService)
        {
        }

        public int GracefulExitAttemptCount { get; private set; }

        protected override async Task<bool> TryRequestGracefulProcessExitAsync(Process process, string toolId, string sessionId)
        {
            GracefulExitAttemptCount++;

            if (process.HasExited)
            {
                return true;
            }

            process.Kill(true);
            await process.WaitForExitAsync();
            return true;
        }
    }

    private sealed class RecordingCodexLowInterruptionInputAdapter : ICliToolAdapter
    {
        public string[] SupportedToolIds => ["codex"];

        public bool SupportsStreamParsing => false;

        public bool CanHandle(CliToolConfig tool)
            => string.Equals(tool.Id, "codex-like-stdin", StringComparison.OrdinalIgnoreCase);

        public string BuildArguments(CliToolConfig tool, string prompt, CliSessionContext context)
            => throw new NotSupportedException();

        public string BuildLowInterruptionArguments(CliToolConfig tool, CliSessionContext context)
            => "-NoProfile -Command \"$inputText = [Console]::In.ReadToEnd(); Write-Output $inputText\"";

        public CliOutputEvent? ParseOutputLine(string line) => null;

        public string? ExtractSessionId(CliOutputEvent outputEvent) => null;

        public string? ExtractAssistantMessage(CliOutputEvent outputEvent) => null;

        public string GetEventTitle(CliOutputEvent outputEvent) => outputEvent.Title ?? string.Empty;

        public string GetEventBadgeClass(CliOutputEvent outputEvent) => string.Empty;

        public string GetEventBadgeLabel(CliOutputEvent outputEvent) => string.Empty;
    }

    private sealed class StubSessionOutputService : ISessionOutputService
    {
        public OutputPanelState? State { get; set; }

        public int SaveCallCount { get; private set; }

        public Task<OutputPanelState?> GetBySessionIdAsync(string sessionId)
            => Task.FromResult(State ?? new OutputPanelState
            {
                SessionId = sessionId,
                ActiveThreadId = string.Empty
            });

        public Task<bool> SaveAsync(OutputPanelState state)
        {
            SaveCallCount++;
            State = state;
            return Task.FromResult(true);
        }

        public Task<bool> DeleteBySessionIdAsync(string sessionId) => Task.FromResult(true);
    }

    private sealed class RecordingCodexThreadProviderSyncService : ICodexThreadProviderSyncService
    {
        public List<CodexThreadProviderSyncRequest> Requests { get; } = new();

        public CodexThreadProviderSyncResult Result { get; set; } = new()
        {
            Message = "thread sync complete"
        };

        public Task<CodexThreadProviderSyncResult> SyncThreadProviderAsync(
            CodexThreadProviderSyncRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private sealed class StubCcSwitchService : ICcSwitchService
    {
        private readonly Dictionary<string, CcSwitchToolStatus> _statuses;
        private readonly Dictionary<string, CcSwitchModelCatalog> _modelCatalogs;

        public StubCcSwitchService(
            Dictionary<string, CcSwitchToolStatus>? statuses = null,
            Dictionary<string, CcSwitchModelCatalog>? modelCatalogs = null)
        {
            _statuses = statuses ?? new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase);
            _modelCatalogs = modelCatalogs ?? new Dictionary<string, CcSwitchModelCatalog>(StringComparer.OrdinalIgnoreCase);
        }

        public bool IsManagedTool(string toolId)
        {
            return string.Equals(toolId, "claude-code", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolId, "codex", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolId, "opencode", StringComparison.OrdinalIgnoreCase);
        }

        public Task<CcSwitchStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CcSwitchStatus
            {
                IsDetected = true,
                Tools = new Dictionary<string, CcSwitchToolStatus>(_statuses, StringComparer.OrdinalIgnoreCase)
            });
        }

        public Task<CcSwitchToolStatus> GetToolStatusAsync(string toolId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_statuses.TryGetValue(toolId, out var status))
            {
                return Task.FromResult(CloneStatus(status));
            }

            return Task.FromResult(new CcSwitchToolStatus
            {
                ToolId = toolId,
                ToolName = toolId,
                IsManaged = IsManagedTool(toolId),
                IsLaunchReady = true,
                StatusMessage = "ok"
            });
        }

        public Task<IReadOnlyDictionary<string, CcSwitchToolStatus>> GetToolStatusesAsync(IEnumerable<string> toolIds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new Dictionary<string, CcSwitchToolStatus>(StringComparer.OrdinalIgnoreCase);
            foreach (var toolId in toolIds)
            {
                result[toolId] = GetToolStatusAsync(toolId, cancellationToken).GetAwaiter().GetResult();
            }

            return Task.FromResult<IReadOnlyDictionary<string, CcSwitchToolStatus>>(result);
        }

        public Task<CcSwitchModelCatalog> GetModelCatalogAsync(string toolId, string? providerId = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_modelCatalogs.TryGetValue(toolId, out var catalog))
            {
                return Task.FromResult(CloneCatalog(catalog));
            }

            return Task.FromResult(new CcSwitchModelCatalog
            {
                ToolId = toolId,
                ToolName = toolId,
                IsManaged = IsManagedTool(toolId),
                ProviderId = providerId,
                StatusMessage = "ok"
            });
        }

        public void SetToolStatus(string toolId, CcSwitchToolStatus status)
        {
            _statuses[toolId] = CloneStatus(status);
        }

        public void SetModelCatalog(string toolId, CcSwitchModelCatalog catalog)
        {
            _modelCatalogs[toolId] = CloneCatalog(catalog);
        }

        private static CcSwitchToolStatus CloneStatus(CcSwitchToolStatus status)
        {
            return new CcSwitchToolStatus
            {
                ToolId = status.ToolId,
                ToolName = status.ToolName,
                AppType = status.AppType,
                IsManaged = status.IsManaged,
                IsDetected = status.IsDetected,
                HasDatabase = status.HasDatabase,
                HasSettingsFile = status.HasSettingsFile,
                HasLiveConfig = status.HasLiveConfig,
                UsesSettingsOverride = status.UsesSettingsOverride,
                IsLaunchReady = status.IsLaunchReady,
                ConfigFileName = status.ConfigFileName,
                ConfigDirectory = status.ConfigDirectory,
                DatabasePath = status.DatabasePath,
                SettingsPath = status.SettingsPath,
                LiveConfigPath = status.LiveConfigPath,
                ActiveProviderId = status.ActiveProviderId,
                ActiveProviderName = status.ActiveProviderName,
                ActiveProviderCategory = status.ActiveProviderCategory,
                StatusMessage = status.StatusMessage
            };
        }

        private static CcSwitchModelCatalog CloneCatalog(CcSwitchModelCatalog catalog)
        {
            return new CcSwitchModelCatalog
            {
                ToolId = catalog.ToolId,
                ToolName = catalog.ToolName,
                AppType = catalog.AppType,
                IsManaged = catalog.IsManaged,
                IsDetected = catalog.IsDetected,
                ProviderId = catalog.ProviderId,
                ProviderName = catalog.ProviderName,
                ProviderCategory = catalog.ProviderCategory,
                IsRemoteFetched = catalog.IsRemoteFetched,
                StatusMessage = catalog.StatusMessage,
                Models = catalog.Models
                    .Select(option => new CcSwitchModelOption
                    {
                        Id = option.Id,
                        DisplayName = option.DisplayName
                    })
                    .ToList()
            };
        }
    }

    private sealed class NullServiceProvider(
        IChatSessionRepository? chatSessionRepository = null,
        ISessionOutputService? sessionOutputService = null,
        ICodexThreadProviderSyncService? codexThreadProviderSyncService = null)
        : IServiceProvider, IServiceScopeFactory, IServiceScope
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IServiceScopeFactory))
            {
                return this;
            }

            if (serviceType == typeof(IChatSessionRepository))
            {
                return chatSessionRepository;
            }

            if (serviceType == typeof(ISessionOutputService))
            {
                return sessionOutputService;
            }

            if (serviceType == typeof(ICodexThreadProviderSyncService))
            {
                return codexThreadProviderSyncService;
            }

            return null;
        }

        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public void Dispose()
        {
        }
    }

    private sealed class StubChatSessionRepository(IEnumerable<ChatSessionEntity> sessions) : IChatSessionRepository
    {
        private readonly List<ChatSessionEntity> _sessions = sessions.ToList();

        public string? LastUpdatedCliThreadId { get; private set; }

        public SqlSugarScope GetDB() => throw new NotSupportedException();
        public List<ChatSessionEntity> GetList() => _sessions.ToList();
        public Task<List<ChatSessionEntity>> GetListAsync() => Task.FromResult(GetList());
        public List<ChatSessionEntity> GetList(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => _sessions.AsQueryable().Where(whereExpression).ToList();
        public Task<List<ChatSessionEntity>> GetListAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(GetList(whereExpression));
        public int Count(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => _sessions.AsQueryable().Count(whereExpression);
        public Task<int> CountAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(Count(whereExpression));
        public PageList<ChatSessionEntity> GetPageList(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public PageList<P> GetPageList<P>(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public Task<PageList<ChatSessionEntity>> GetPageListAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public Task<PageList<P>> GetPageListAsync<P>(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public PageList<ChatSessionEntity> GetPageList(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<ChatSessionEntity>> GetPageListAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public PageList<P> GetPageList<P>(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<P>> GetPageListAsync<P>(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public PageList<ChatSessionEntity> GetPageList(List<IConditionalModel> conditionalList, PageModel page) => throw new NotSupportedException();
        public Task<PageList<ChatSessionEntity>> GetPageListAsync(List<IConditionalModel> conditionalList, PageModel page) => throw new NotSupportedException();
        public PageList<ChatSessionEntity> GetPageList(List<IConditionalModel> conditionalList, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<ChatSessionEntity>> GetPageListAsync(List<IConditionalModel> conditionalList, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public ChatSessionEntity GetById(dynamic id) => _sessions.First(x => string.Equals(x.SessionId, id?.ToString(), StringComparison.OrdinalIgnoreCase));
        public Task<ChatSessionEntity> GetByIdAsync(dynamic id) => Task.FromResult(GetById(id));
        public ChatSessionEntity GetSingle(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => _sessions.AsQueryable().Single(whereExpression);
        public Task<ChatSessionEntity> GetSingleAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(GetSingle(whereExpression));
        public ChatSessionEntity GetFirst(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => _sessions.AsQueryable().First(whereExpression);
        public Task<ChatSessionEntity> GetFirstAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(GetFirst(whereExpression));
        public bool Insert(ChatSessionEntity obj) { _sessions.Add(obj); return true; }
        public Task<bool> InsertAsync(ChatSessionEntity obj) => Task.FromResult(Insert(obj));
        public bool InsertRange(List<ChatSessionEntity> objs) { _sessions.AddRange(objs); return true; }
        public Task<bool> InsertRangeAsync(List<ChatSessionEntity> objs) => Task.FromResult(InsertRange(objs));
        public int InsertReturnIdentity(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<int> InsertReturnIdentityAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public long InsertReturnBigIdentity(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<long> InsertReturnBigIdentityAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public bool DeleteByIds(dynamic[] ids) => throw new NotSupportedException();
        public Task<bool> DeleteByIdsAsync(dynamic[] ids) => throw new NotSupportedException();
        public bool Delete(dynamic id) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(dynamic id) => throw new NotSupportedException();
        public bool Delete(ChatSessionEntity obj) => _sessions.Remove(obj);
        public Task<bool> DeleteAsync(ChatSessionEntity obj) => Task.FromResult(Delete(obj));
        public bool Delete(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => throw new NotSupportedException();
        public bool Update(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public bool UpdateRange(List<ChatSessionEntity> objs) => throw new NotSupportedException();
        public bool InsertOrUpdate(ChatSessionEntity obj)
        {
            var existing = _sessions.FirstOrDefault(x => string.Equals(x.SessionId, obj.SessionId, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                _sessions.Add(obj);
                return true;
            }

            var index = _sessions.IndexOf(existing);
            _sessions[index] = obj;
            return true;
        }
        public Task<bool> InsertOrUpdateAsync(ChatSessionEntity obj) => Task.FromResult(InsertOrUpdate(obj));
        public Task<bool> UpdateRangeAsync(List<ChatSessionEntity> objs) => throw new NotSupportedException();
        public bool IsAny(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => _sessions.AsQueryable().Any(whereExpression);
        public Task<bool> IsAnyAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(IsAny(whereExpression));
        public Task<List<ChatSessionEntity>> GetByUsernameAsync(string username) => Task.FromResult(_sessions.Where(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)).ToList());
        public Task<ChatSessionEntity?> GetByIdAndUsernameAsync(string sessionId, string username) => Task.FromResult(_sessions.FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)));
        public Task<bool> DeleteByIdAndUsernameAsync(string sessionId, string username) => Task.FromResult(_sessions.RemoveAll(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)) > 0);
        public Task<List<ChatSessionEntity>> GetByUsernameOrderByUpdatedAtAsync(string username) => Task.FromResult(_sessions.Where(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.UpdatedAt).ToList());
        public Task<ChatSessionEntity?> GetByUsernameToolAndCliThreadIdAsync(string username, string toolId, string cliThreadId) => Task.FromResult(_sessions.FirstOrDefault(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase) && string.Equals(x.ToolId, toolId, StringComparison.OrdinalIgnoreCase) && string.Equals(x.CliThreadId, cliThreadId, StringComparison.OrdinalIgnoreCase)));
        public Task<ChatSessionEntity?> GetByToolAndCliThreadIdAsync(string toolId, string cliThreadId) => Task.FromResult(_sessions.FirstOrDefault(x => string.Equals(x.ToolId, toolId, StringComparison.OrdinalIgnoreCase) && string.Equals(x.CliThreadId, cliThreadId, StringComparison.OrdinalIgnoreCase)));
        public Task<bool> UpdateCliThreadIdAsync(string sessionId, string? cliThreadId)
        {
            var session = _sessions.FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            if (session == null)
            {
                return Task.FromResult(false);
            }

            session.CliThreadId = cliThreadId;
            session.UpdatedAt = DateTime.Now;
            LastUpdatedCliThreadId = cliThreadId;
            return Task.FromResult(true);
        }

        public Task<bool> UpdateWorkspaceBindingAsync(string sessionId, string? workspacePath, bool isCustomWorkspace) => Task.FromResult(true);
        public Task<bool> UpdateSessionTitleAsync(string sessionId, string title) => Task.FromResult(true);
        public Task<bool> UpdateCcSwitchSnapshotAsync(string sessionId, CcSwitchSessionSnapshot snapshot)
        {
            var session = _sessions.FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            if (session == null)
            {
                return Task.FromResult(false);
            }

            session.UsesCcSwitchSnapshot = snapshot.UsesSnapshot;
            session.CcSwitchSnapshotToolId = snapshot.ToolId;
            session.CcSwitchProviderId = snapshot.ProviderId;
            session.CcSwitchProviderName = snapshot.ProviderName;
            session.CcSwitchProviderCategory = snapshot.ProviderCategory;
            session.CcSwitchLiveConfigPath = snapshot.SourceLiveConfigPath;
            session.CcSwitchSnapshotRelativePath = snapshot.SnapshotRelativePath;
            session.CcSwitchSnapshotSyncedAt = snapshot.SyncedAt;
            session.UpdatedAt = DateTime.Now;
            return Task.FromResult(true);
        }
        public Task<List<ChatSessionEntity>> GetByFeishuChatKeyAsync(string feishuChatKey) => Task.FromResult(new List<ChatSessionEntity>());
        public Task<ChatSessionEntity?> GetActiveByFeishuChatKeyAsync(string feishuChatKey) => Task.FromResult<ChatSessionEntity?>(null);
        public Task<bool> SetActiveSessionAsync(string feishuChatKey, string sessionId) => Task.FromResult(true);
        public Task<bool> CloseFeishuSessionAsync(string feishuChatKey, string sessionId) => Task.FromResult(true);
        public Task<string> CreateFeishuSessionAsync(string feishuChatKey, string username, string? workspacePath = null, string? toolId = null) => Task.FromResult(Guid.NewGuid().ToString("N"));
    }
}
