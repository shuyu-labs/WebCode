using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        Assert.Equal("Hello from app-server", adapter.ExtractAssistantMessage(outputEvent));
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
        Assert.Equal("pwsh -Command Get-Date", outputEvent.CommandExecution?.Command);
        Assert.Null(outputEvent.CommandExecution?.Output);
        Assert.DoesNotContain("very large output", outputEvent.Content ?? string.Empty, StringComparison.Ordinal);
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
        var method = GetPrivateStaticMethod("ShouldSuppressTransientErrorNotification");
        Assert.NotNull(method);

        var actual = Assert.IsType<bool>(method!.Invoke(null, new object?[] { methodName, message }));

        Assert.Equal(expected, actual);
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
}
