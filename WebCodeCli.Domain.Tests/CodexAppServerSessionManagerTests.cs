using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service.Adapters;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Domain.Tests;

public class CodexAppServerSessionManagerTests
{
    [Fact]
    public void Constructor_CanBeResolvedFromStandardLoggingDi()
    {
        var managerType = typeof(CliExecutorService).Assembly.GetType(
            "WebCodeCli.Domain.Domain.Service.CodexAppServerSessionManager",
            throwOnError: true)!;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(typeof(ICodexAppServerSessionManager), managerType);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var manager = provider.GetRequiredService<ICodexAppServerSessionManager>();

        Assert.Equal(managerType, manager.GetType());
    }

    [Fact]
    public void GetTransportEncoding_ReturnsUtf8WithoutBom()
    {
        var managerType = typeof(CliExecutorService).Assembly.GetType(
            "WebCodeCli.Domain.Domain.Service.CodexAppServerSessionManager",
            throwOnError: true)!;
        var method = managerType.GetMethod(
            "GetTransportEncoding",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var encoding = Assert.IsType<UTF8Encoding>(method!.Invoke(null, null));

        Assert.Equal("utf-8", encoding.WebName);
        Assert.Empty(encoding.GetPreamble());
    }

    [Fact]
    public void TryBuildCliOutputJsonl_NormalizesAgentMessageDeltaForCodexAdapter()
    {
        var method = GetPrivateStaticMethod("TryBuildCliOutputJsonl");
        Assert.NotNull(method);

        using var document = JsonDocument.Parse("""
            {
              "threadId": "thread-123",
              "turnId": "turn-456",
              "itemId": "msg-789",
              "delta": "Hello from app-server"
            }
            """);

        var jsonl = method!.Invoke(null, new object[] { "item/agentMessage/delta", document.RootElement }) as string;

        Assert.False(string.IsNullOrWhiteSpace(jsonl));

        var adapter = new CodexAdapter();
        var outputEvent = adapter.ParseOutputLine(jsonl!);

        Assert.NotNull(outputEvent);
        Assert.Equal("item.updated", outputEvent!.EventType);
        Assert.Equal("agent_message", outputEvent.ItemType);
        Assert.Null(adapter.ExtractSessionId(outputEvent));
        Assert.Equal("Hello from app-server", adapter.ExtractAssistantMessage(outputEvent));
    }

    [Fact]
    public void TryBuildCliOutputJsonl_PreservesAgentMessagePhaseForCodexAdapter()
    {
        var method = GetPrivateStaticMethod("TryBuildCliOutputJsonl");
        Assert.NotNull(method);

        using var document = JsonDocument.Parse("""
            {
              "threadId": "thread-123",
              "turnId": "turn-456",
              "itemId": "msg-789",
              "delta": "done",
              "phase": "final_answer"
            }
            """);

        var jsonl = method!.Invoke(null, new object[] { "item/agentMessage/delta", document.RootElement }) as string;

        Assert.False(string.IsNullOrWhiteSpace(jsonl));
        Assert.Contains(@"""phase"":""final_answer""", jsonl, StringComparison.Ordinal);

        var adapter = new CodexAdapter();
        var outputEvent = adapter.ParseOutputLine(jsonl!);

        Assert.NotNull(outputEvent);
        Assert.Equal("final_answer", outputEvent!.AssistantPhase);
    }

    [Fact]
    public void TryBuildCliOutputJsonl_PreservesCompletedAgentMessagePhaseForCodexAdapter()
    {
        var method = GetPrivateStaticMethod("TryBuildCliOutputJsonl");
        Assert.NotNull(method);

        using var document = JsonDocument.Parse("""
            {
              "threadId": "thread-123",
              "turnId": "turn-456",
              "item": {
                "type": "agentMessage",
                "id": "msg-789",
                "text": "done",
                "phase": "final_answer"
              }
            }
            """);

        var jsonl = method!.Invoke(null, new object[] { "item/completed", document.RootElement }) as string;

        Assert.False(string.IsNullOrWhiteSpace(jsonl));
        Assert.Contains(@"""phase"":""final_answer""", jsonl, StringComparison.Ordinal);

        var adapter = new CodexAdapter();
        var outputEvent = adapter.ParseOutputLine(jsonl!);

        Assert.NotNull(outputEvent);
        Assert.Equal("item.completed", outputEvent!.EventType);
        Assert.Equal("agent_message", outputEvent.ItemType);
        Assert.Equal("done", adapter.ExtractAssistantMessage(outputEvent));
        Assert.Equal("final_answer", outputEvent.AssistantPhase);
    }

    [Fact]
    public void TryBuildCliOutputJsonl_IgnoresVerboseCommandOutputDeltas()
    {
        var method = GetPrivateStaticMethod("TryBuildCliOutputJsonl");
        Assert.NotNull(method);

        using var document = JsonDocument.Parse("""
            {
              "threadId": "thread-123",
              "turnId": "turn-456",
              "itemId": "call-789",
              "delta": "line 1\r\nline 2"
            }
            """);

        var jsonl = method!.Invoke(null, new object[] { "item/commandExecution/outputDelta", document.RootElement }) as string;

        Assert.Null(jsonl);
    }

    [Fact]
    public void TryBuildCliOutputJsonl_NormalizesCompactCommandCompletionForCodexAdapter()
    {
        var method = GetPrivateStaticMethod("TryBuildCliOutputJsonl");
        Assert.NotNull(method);

        using var document = JsonDocument.Parse("""
            {
              "threadId": "thread-123",
              "turnId": "turn-456",
              "item": {
                "type": "commandExecution",
                "id": "call-789",
                "command": "pwsh -Command Get-Date",
                "status": "completed",
                "aggregatedOutput": "very large output that should not be mirrored to Feishu",
                "exitCode": 0
              }
            }
            """);

        var jsonl = method!.Invoke(null, new object[] { "item/completed", document.RootElement }) as string;

        Assert.False(string.IsNullOrWhiteSpace(jsonl));

        var adapter = new CodexAdapter();
        var outputEvent = adapter.ParseOutputLine(jsonl!);

        Assert.NotNull(outputEvent);
        Assert.Equal("item.completed", outputEvent!.EventType);
        Assert.Equal("command_execution", outputEvent.ItemType);
        Assert.Null(adapter.ExtractSessionId(outputEvent));
        Assert.Equal("pwsh -Command Get-Date", outputEvent.CommandExecution?.Command);
        Assert.Null(outputEvent.CommandExecution?.Output);
        Assert.DoesNotContain("very large output", outputEvent.Content ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuildCliOutputJsonl_FileChangeWithObjectKind_DoesNotThrowAndOmitsInvalidKind()
    {
        var method = GetPrivateStaticMethod("TryBuildCliOutputJsonl");
        Assert.NotNull(method);

        using var document = JsonDocument.Parse("""
            {
              "threadId": "thread-123",
              "turnId": "turn-456",
              "item": {
                "type": "fileChange",
                "id": "file-789",
                "status": "completed",
                "changes": [
                  {
                    "path": "src/Example.cs",
                    "kind": {
                      "name": "update"
                    }
                  }
                ]
              }
            }
            """);

        var jsonl = method!.Invoke(null, new object[] { "item/completed", document.RootElement }) as string;

        Assert.False(string.IsNullOrWhiteSpace(jsonl));

        using var payload = JsonDocument.Parse(jsonl!);
        var item = payload.RootElement.GetProperty("item");
        Assert.Equal("file_change", item.GetProperty("type").GetString());
        Assert.Equal("completed", item.GetProperty("status").GetString());

        var changes = item.GetProperty("changes");
        Assert.Equal(JsonValueKind.Array, changes.ValueKind);
        var change = Assert.Single(changes.EnumerateArray().ToArray());
        Assert.Equal("src/Example.cs", change.GetProperty("path").GetString());
        Assert.False(change.TryGetProperty("kind", out _));
    }

    [Fact]
    public void TryBuildCliOutputJsonl_ExtractsNestedErrorMessage()
    {
        var method = GetPrivateStaticMethod("TryBuildCliOutputJsonl");
        Assert.NotNull(method);

        using var document = JsonDocument.Parse("""
            {
              "error": {
                "message": "rate limit exceeded"
              }
            }
            """);

        var jsonl = method!.Invoke(null, new object[] { "error", document.RootElement }) as string;

        Assert.False(string.IsNullOrWhiteSpace(jsonl));

        using var payload = JsonDocument.Parse(jsonl!);
        Assert.Equal("error", payload.RootElement.GetProperty("type").GetString());
        Assert.Equal("rate limit exceeded", payload.RootElement.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("Reconnecting... 1/5", true)]
    [InlineData("Current interaction failed.", true)]
    [InlineData("An unknown error occurred.", true)]
    [InlineData("rate limit exceeded", false)]
    [InlineData("", false)]
    public void IsTransientAppServerErrorMessage_ClassifiesExpectedMessages(string message, bool expected)
    {
        var method = GetPrivateStaticMethod("IsTransientAppServerErrorMessage");
        Assert.NotNull(method);

        var actual = Assert.IsType<bool>(method!.Invoke(null, new object?[] { message }));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("error", "Reconnecting... 1/5", true)]
    [InlineData("error", "Current interaction failed.", true)]
    [InlineData("error", "rate limit exceeded", false)]
    [InlineData("turn/failed", "Reconnecting... 1/5", false)]
    public void ShouldSuppressTransientErrorNotification_OnlySuppressesTransientErrorMethod(string methodName, string message, bool expected)
    {
        var managerType = typeof(CliExecutorService).Assembly.GetType(
            "WebCodeCli.Domain.Domain.Service.CodexAppServerSessionManager",
            throwOnError: true)!;
        var method = managerType.GetMethod(
            "ShouldSuppressTransientErrorNotification",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(string),
                typeof(string)
            ],
            modifiers: null);
        Assert.NotNull(method);

        var actual = Assert.IsType<bool>(method!.Invoke(null, new object?[] { methodName, message }));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(true, "rate limit exceeded", true)]
    [InlineData(false, "rate limit exceeded", false)]
    [InlineData(false, "Reconnecting... 1/5", true)]
    public void ShouldSuppressTransientErrorNotification_PrefersWillRetryFromProtocol(
        bool willRetry,
        string message,
        bool expected)
    {
        var managerType = typeof(CliExecutorService).Assembly.GetType(
            "WebCodeCli.Domain.Domain.Service.CodexAppServerSessionManager",
            throwOnError: true)!;
        var method = managerType.GetMethod(
            "ShouldSuppressTransientErrorNotification",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(string),
                typeof(JsonElement),
                typeof(string)
            ],
            modifiers: null);

        Assert.NotNull(method);

        using var document = JsonDocument.Parse($$"""
            {
              "error": {
                "message": "{{message}}"
              },
              "threadId": "thread-123",
              "turnId": "turn-456",
              "willRetry": {{willRetry.ToString().ToLowerInvariant()}}
            }
            """);

        var actual = Assert.IsType<bool>(method!.Invoke(null, new object?[] { "error", document.RootElement, message }));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(
        "a36077c9-0a43-4ad6-9bc1-2ec37b67f961",
        "expected active turn id 019e440e-097c-7002-a7b8-1fa6ba765d39 but found a36077c9-0a43-4ad6-9bc1-2ec37b67f961",
        "019e440e-097c-7002-a7b8-1fa6ba765d39")]
    [InlineData(
        "019e440e-097c-7002-a7b8-1fa6ba765d39",
        "expected active turn id 019e440e-097c-7002-a7b8-1fa6ba765d39 but found a36077c9-0a43-4ad6-9bc1-2ec37b67f961",
        "a36077c9-0a43-4ad6-9bc1-2ec37b67f961")]
    [InlineData(
        "turn-123",
        "some other error",
        null)]
    public void TryResolveReplacementActiveTurnIdForInterruptMismatch_ReturnsAlternateTurnIdWhenMessageMatches(
        string currentTurnId,
        string errorMessage,
        string? expectedReplacementTurnId)
    {
        var managerType = typeof(CliExecutorService).Assembly.GetType(
            "WebCodeCli.Domain.Domain.Service.CodexAppServerSessionManager",
            throwOnError: true)!;
        var method = managerType.GetMethod(
            "TryResolveReplacementActiveTurnIdForInterruptMismatch",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var actual = method!.Invoke(null, new object?[] { currentTurnId, errorMessage }) as string;

        Assert.Equal(expectedReplacementTurnId, actual);
    }

    [Fact]
    public async Task RunWithSessionCreationLockAsync_SerializesCallsPerSession()
    {
        var managerType = typeof(CliExecutorService).Assembly.GetType(
            "WebCodeCli.Domain.Domain.Service.CodexAppServerSessionManager",
            throwOnError: true)!;
        var method = managerType.GetMethod(
            "RunWithSessionCreationLockAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(string),
                typeof(Func<Task>),
                typeof(CancellationToken)
            ],
            modifiers: null);

        Assert.NotNull(method);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(typeof(ICodexAppServerSessionManager), managerType);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var manager = provider.GetRequiredService<ICodexAppServerSessionManager>();
        var activeCount = 0;
        var maxActiveCount = 0;

        Task RunLockedAsync()
        {
            Func<Task> action = async () =>
            {
                var current = Interlocked.Increment(ref activeCount);
                UpdateMax(ref maxActiveCount, current);
                await Task.Delay(100);
                Interlocked.Decrement(ref activeCount);
            };

            return (Task)method!.Invoke(
                manager,
                new object[]
                {
                    "session-1",
                    action,
                    CancellationToken.None
                })!;
        }

        await Task.WhenAll(RunLockedAsync(), RunLockedAsync());

        Assert.Equal(1, maxActiveCount);
        Assert.Equal(0, activeCount);
    }

    [Fact]
    public async Task GetGoalAsync_WhenOriginalRequestTokenIsCanceled_KeepsLiveSessionReadable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, "workspace");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(workspacePath);

        try
        {
            var commandPath = await CreateFakeCodexAppServerShimAsync(tempRoot);
            var managerType = typeof(CliExecutorService).Assembly.GetType(
                "WebCodeCli.Domain.Domain.Service.CodexAppServerSessionManager",
                throwOnError: true)!;

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(typeof(ICodexAppServerSessionManager), managerType);

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
            using var manager = provider.GetRequiredService<ICodexAppServerSessionManager>();

            var tool = new CliToolConfig
            {
                Id = "codex",
                Name = "Codex",
                Command = commandPath,
                Enabled = true
            };
            var sessionContext = new CliSessionContext
            {
                SessionId = "session-goal-token-reuse",
                WorkingDirectory = workspacePath
            };

            using var startupCts = new CancellationTokenSource();
            var threadId = await manager.EnsureThreadAsync(
                sessionContext.SessionId,
                commandPath,
                tool,
                workspacePath,
                environmentVariables: null,
                sessionContext,
                existingThreadId: null,
                startupCts.Token);

            Assert.Equal("thread-1", threadId);

            startupCts.Cancel();
            await Task.Delay(200);

            using var followUpCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var goal = await manager.GetGoalAsync(
                sessionContext.SessionId,
                commandPath,
                tool,
                workspacePath,
                environmentVariables: null,
                sessionContext,
                threadId,
                followUpCts.Token);

            Assert.NotNull(goal);
            Assert.Equal("ship it", goal!.Objective);
            Assert.Equal("active", goal.Status);
            Assert.Equal(200, goal.TokenBudget);
            Assert.Equal(12, goal.TokensUsed);
            Assert.Equal(34, goal.TimeUsedSeconds);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                DeleteDirectoryWithRetry(tempRoot);
            }
        }
    }

    [Fact]
    public async Task StartTurnAsync_WhenInterruptSucceededWithoutCompletionNotification_AllowsImmediateRestart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(tempRoot, "workspace");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(workspacePath);

        try
        {
            var commandPath = await CreateFakeCodexAppServerShimAsync(
                tempRoot,
                scriptMode: "interrupt-without-completion");
            var managerType = typeof(CliExecutorService).Assembly.GetType(
                "WebCodeCli.Domain.Domain.Service.CodexAppServerSessionManager",
                throwOnError: true)!;

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(typeof(ICodexAppServerSessionManager), managerType);

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
            using var manager = provider.GetRequiredService<ICodexAppServerSessionManager>();

            var tool = new CliToolConfig
            {
                Id = "codex",
                Name = "Codex",
                Command = commandPath,
                Enabled = true
            };
            var sessionContext = new CliSessionContext
            {
                SessionId = "session-goal-immediate-restart",
                WorkingDirectory = workspacePath
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var firstTurn = await manager.StartTurnAsync(
                sessionContext.SessionId,
                commandPath,
                tool,
                workspacePath,
                environmentVariables: null,
                sessionContext,
                "ship it",
                existingThreadId: null,
                cts.Token);

            Assert.Equal("thread-1", firstTurn.ThreadId);
            Assert.Equal("turn-1", firstTurn.TurnId);

            var interrupted = await manager.InterruptActiveTurnAsync(
                sessionContext.SessionId,
                commandPath,
                tool,
                workspacePath,
                environmentVariables: null,
                sessionContext,
                firstTurn.ThreadId,
                cts.Token);

            Assert.True(interrupted);

            var secondTurn = await manager.StartTurnAsync(
                sessionContext.SessionId,
                commandPath,
                tool,
                workspacePath,
                environmentVariables: null,
                sessionContext,
                "keep working",
                firstTurn.ThreadId,
                cts.Token);

            Assert.Equal("thread-1", secondTurn.ThreadId);
            Assert.Equal("turn-2", secondTurn.TurnId);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                DeleteDirectoryWithRetry(tempRoot);
            }
        }
    }

    private static MethodInfo? GetPrivateStaticMethod(string methodName)
    {
        var managerType = typeof(CliExecutorService).Assembly.GetType(
            "WebCodeCli.Domain.Domain.Service.CodexAppServerSessionManager",
            throwOnError: true)!;

        return managerType.GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.NonPublic);
    }

    private static void UpdateMax(ref int target, int value)
    {
        while (true)
        {
            var snapshot = target;
            if (value <= snapshot)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, value, snapshot) == snapshot)
            {
                return;
            }
        }
    }

    private static async Task<string> CreateFakeCodexAppServerShimAsync(string tempRoot, string scriptMode = "default")
    {
        var scriptPath = Path.Combine(tempRoot, "fake-codex-app-server.ps1");
        var shimPath = Path.Combine(tempRoot, "fake-codex.cmd");

        await File.WriteAllTextAsync(
            scriptPath,
            """
            param(
                [string]$Mode = "default"
            )

            $threadId = "thread-1"
            $turnCounter = 0
            $goal = @{
                objective = "ship it"
                status = "active"
                tokenBudget = 200
                tokensUsed = 12
                timeUsedSeconds = 34
            }

            while (($line = [Console]::In.ReadLine()) -ne $null) {
                if ([string]::IsNullOrWhiteSpace($line)) {
                    continue
                }

                $request = $line | ConvertFrom-Json
                if ($null -eq $request.PSObject.Properties["id"]) {
                    continue
                }

                $id = $request.id
                $method = [string]$request.method

                switch ($method) {
                    "initialize" {
                        $response = @{
                            id = $id
                            result = @{
                                serverInfo = @{
                                    name = "fake-codex"
                                    version = "1.0.0"
                                }
                            }
                        }
                    }
                    "thread/start" {
                        $response = @{
                            id = $id
                            result = @{
                                thread = @{
                                    id = $threadId
                                }
                            }
                        }
                    }
                    "thread/resume" {
                        $response = @{
                            id = $id
                            result = @{
                                thread = @{
                                    id = [string]$request.params.threadId
                                }
                            }
                        }
                    }
                    "thread/goal/get" {
                        $response = @{
                            id = $id
                            result = @{
                                goal = $goal
                            }
                        }
                    }
                    "turn/start" {
                        $turnCounter += 1
                        $response = @{
                            id = $id
                            result = @{
                                turn = @{
                                    id = "turn-$turnCounter"
                                }
                            }
                        }
                    }
                    "turn/interrupt" {
                        $response = @{
                            id = $id
                            result = @{}
                        }
                    }
                    default {
                        $response = @{
                            id = $id
                            result = @{}
                        }
                    }
                }

                [Console]::Out.WriteLine(($response | ConvertTo-Json -Compress -Depth 10))
                [Console]::Out.Flush()
            }
            """);
        await File.WriteAllTextAsync(
            shimPath,
            $"@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0fake-codex-app-server.ps1\" \"{scriptMode}\" %*\r\n");

        return shimPath;
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(100 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }
}
