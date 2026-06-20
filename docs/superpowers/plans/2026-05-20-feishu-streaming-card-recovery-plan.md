# Feishu Streaming Card Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep Feishu streaming output alive by treating timeout-plus-sequence-conflict as same-card recovery and falling back to at most one replacement card only when the active card is truly no longer writable.

**Architecture:** Narrow the first fix into `FeishuCardKitClient` so retry-after-timeout `300317` is treated as a likely successful prior write. Then add a shared resilient streaming-card session abstraction above `FeishuStreamingHandle` and route both `FeishuChannelService` and `FeishuCardActionService` through it so replacement-card behavior, finish semantics, and disconnect fallback stay consistent.

**Tech Stack:** C#, xUnit, existing Feishu CardKit client/services, in-repo streaming test doubles.

---

### Task 1: Lock down same-card recovery in CardKit client

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardKitClient.cs`
- Test: `WebCodeCli.Domain.Tests/FeishuCardKitClientTests.cs`

- [ ] **Step 1: Write the failing test for timeout-then-300317 same-card recovery**

Add a new test in `WebCodeCli.Domain.Tests/FeishuCardKitClientTests.cs` next to `CreateStreamingHandleAsync_RetriesTimedOutUpdateOnceWithSameSequenceAndUuid`:

```csharp
[Fact]
public async Task CreateStreamingHandleAsync_TreatsTimeoutThenSequenceConflictAsSuccessfulPriorWrite()
{
    var handler = new TimeoutThenSequenceConflictCardUpdateHandler();
    var client = CreateClient(handler, new FeishuOptions
    {
        AppId = "app-id",
        AppSecret = "app-secret",
        HttpTimeoutSeconds = 1,
        StreamingThrottleMs = 0
    });

    var handle = await client.CreateStreamingHandleAsync(
        "oc_stream_chat",
        null,
        "initial",
        "AI 助手",
        TestContext.Current.CancellationToken);

    await handle.UpdateAsync("first update");
    await handle.UpdateAsync("second update");

    Assert.False(handle.AreCardUpdatesStopped);
    Assert.Equal(2, handler.SuccessfulLogicalUpdates);
}
```

- [ ] **Step 2: Add the HTTP handler test double that reproduces the real failure sequence**

In `WebCodeCli.Domain.Tests/FeishuCardKitClientTests.cs`, add a dedicated handler below `TimeoutOnFirstCardUpdateHandler`:

```csharp
private sealed class TimeoutThenSequenceConflictCardUpdateHandler : HttpMessageHandler
{
    private int _updateCount;

    public int SuccessfulLogicalUpdates { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (string.Equals(path, "/open-apis/auth/v3/tenant_access_token/internal", StringComparison.Ordinal))
        {
            return CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}""");
        }

        if (request.Method == HttpMethod.Post &&
            string.Equals(path, "/open-apis/cardkit/v1/cards", StringComparison.Ordinal))
        {
            return CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}""");
        }

        if (request.Method == HttpMethod.Post &&
            string.Equals(path, "/open-apis/im/v1/messages", StringComparison.Ordinal))
        {
            return CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""");
        }

        if (request.Method == HttpMethod.Put &&
            string.Equals(path, "/open-apis/cardkit/v1/cards/card_123", StringComparison.Ordinal))
        {
            _updateCount++;
            if (_updateCount == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
            }

            if (_updateCount == 2)
            {
                SuccessfulLogicalUpdates++;
                return CreateJsonResponse("""{"code":300317,"msg":"sequence number compare failed"}""");
            }

            SuccessfulLogicalUpdates++;
            return CreateJsonResponse("""{"code":0}""");
        }

        throw new Xunit.Sdk.XunitException($"Unexpected request sent to {request.RequestUri}.");
    }
}
```

- [ ] **Step 3: Write the failing test for plain 300317 without a prior timeout**

Add a second test to prove we do not over-broaden recovery:

```csharp
[Fact]
public async Task CreateStreamingHandleAsync_TreatsPlainSequenceConflictAsCardFailure()
{
    var handler = new PlainSequenceConflictCardUpdateHandler();
    var client = CreateClient(handler, new FeishuOptions
    {
        AppId = "app-id",
        AppSecret = "app-secret",
        HttpTimeoutSeconds = 1,
        StreamingThrottleMs = 0
    });

    var handle = await client.CreateStreamingHandleAsync(
        "oc_stream_chat",
        null,
        "initial",
        "AI 助手",
        TestContext.Current.CancellationToken);

    await handle.UpdateAsync("first update");

    Assert.True(handle.AreCardUpdatesStopped);
}
```

- [ ] **Step 4: Add the plain conflict test double**

In the same test file, add:

```csharp
private sealed class PlainSequenceConflictCardUpdateHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (string.Equals(path, "/open-apis/auth/v3/tenant_access_token/internal", StringComparison.Ordinal))
        {
            return Task.FromResult(CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""));
        }

        if (request.Method == HttpMethod.Post &&
            string.Equals(path, "/open-apis/cardkit/v1/cards", StringComparison.Ordinal))
        {
            return Task.FromResult(CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""));
        }

        if (request.Method == HttpMethod.Post &&
            string.Equals(path, "/open-apis/im/v1/messages", StringComparison.Ordinal))
        {
            return Task.FromResult(CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}"""));
        }

        if (request.Method == HttpMethod.Put &&
            string.Equals(path, "/open-apis/cardkit/v1/cards/card_123", StringComparison.Ordinal))
        {
            return Task.FromResult(CreateJsonResponse("""{"code":300317,"msg":"sequence number compare failed"}"""));
        }

        throw new Xunit.Sdk.XunitException($"Unexpected request sent to {request.RequestUri}.");
    }
}
```

- [ ] **Step 5: Run the focused CardKit client tests and verify they fail**

Run:

```powershell
dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "FeishuCardKitClientTests"
```

Expected:

- the new timeout-then-300317 test fails because `AreCardUpdatesStopped` becomes `true`
- the new plain-300317 test either passes already or confirms current failure classification

- [ ] **Step 6: Implement narrow same-card recovery in `FeishuCardKitClient`**

Update `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardKitClient.cs` inside `UpdateCardCoreAsync(...)` so the retry loop remembers whether a timeout already happened for the current `sequence`:

```csharp
var sawRecoverableTimeout = false;

for (var attempt = 1; attempt <= CardUpdateMaxAttempts; attempt++)
{
    try
    {
        var response = await PutAsync(...);
        var result = await ParseResponseAsync(response, cancellationToken);

        if (result.TryGetProperty("code", out var codeProp))
        {
            var code = codeProp.GetInt32();
            if (code == 0)
            {
                return true;
            }

            if (code == 300317 && sawRecoverableTimeout)
            {
                _logger.LogWarning(
                    "Update card retry hit sequence conflict after timeout; assuming previous write succeeded (cardId={CardId}, seq={Sequence}, uuid={Uuid})",
                    cardId,
                    sequence,
                    updateUuid);
                return true;
            }
        }

        EnsureBusinessSuccess(result, "Update CardKit card");
        return false;
    }
    catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
    {
        sawRecoverableTimeout = true;
        ...
    }
}
```

- [ ] **Step 7: Run the focused CardKit client tests and verify they pass**

Run:

```powershell
dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "FeishuCardKitClientTests"
```

Expected:

- the timeout-then-300317 test passes
- the plain-300317 test still shows `AreCardUpdatesStopped == true`

- [ ] **Step 8: Commit**

```powershell
git add WebCodeCli.Domain/Domain/Service/Channels/FeishuCardKitClient.cs WebCodeCli.Domain.Tests/FeishuCardKitClientTests.cs
git commit -m "fix: recover feishu card updates after timed out retry"
```

### Task 2: Introduce a shared resilient streaming-card session

**Files:**
- Create: `WebCodeCli.Domain/Domain/Service/Channels/FeishuStreamingCardSession.cs`
- Modify: `WebCodeCli.Domain/Domain/Model/Channels/FeishuStreamingHandle.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuCardKitClient.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
- Test: `WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs`
- Test: `WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs`

- [ ] **Step 1: Write the failing channel-stream test for one-time replacement-card recovery**

Add a new test in `WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs` using `StreamingRecordingFeishuCardKitClient`:

```csharp
[Fact]
public async Task HandleIncomingMessageAsync_ReplacesBrokenStreamingCardOnceAndFinishesOnReplacement()
{
    var repository = CreateRepository(out var repositoryProxy);
    var sessionDirectoryService = new RecordingSessionDirectoryService(repositoryProxy);
    var cardKit = new StreamingRecordingFeishuCardKitClient
    {
        FailUpdateOnAttempt = 1
    };
    var chatSessionService = new RecordingChatSessionService();
    var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-recovery-{Guid.NewGuid():N}");
    var workspacePath = Path.Combine(workspaceRoot, "superpowers");
    Directory.CreateDirectory(workspacePath);
    var cliExecutor = new PromptCapturingCliExecutor(workspacePath)
    {
        StreamChunks =
        [
            new StreamOutputChunk { Content = "first", IsCompleted = false },
            new StreamOutputChunk { Content = "second", IsCompleted = true }
        ]
    };
    var serviceProvider = new TestServiceProvider(
        repository,
        sessionDirectoryService,
        new StubFeishuUserBindingService(),
        new StubUserFeishuBotConfigService(),
        new StubUserContextService());

    var service = new FeishuChannelService(
        Options.Create(new FeishuOptions { Enabled = true, AppId = "cli_test", AppSecret = "secret" }),
        NullLogger<FeishuChannelService>.Instance,
        cardKit,
        serviceProvider,
        cliExecutor,
        chatSessionService);

    try
    {
        service.CreateNewSession(new FeishuIncomingMessage
        {
            ChatId = "oc_recovery_chat",
            SenderName = "luhaiyan"
        }, workspacePath, "codex");

        await service.HandleIncomingMessageAsync(new FeishuIncomingMessage
        {
            ChatId = "oc_recovery_chat",
            SenderName = "luhaiyan",
            MessageId = "msg-recovery",
            Content = "继续输出"
        });

        Assert.Equal(2, cardKit.Handles.Count);
        Assert.Equal("firstsecond", cardKit.Handles[1].FinalContent);
    }
    finally
    {
        Directory.Delete(workspaceRoot, recursive: true);
    }
}
```

- [ ] **Step 2: Extend the test double so one handle can fail and the next handle can continue**

Modify `StreamingRecordingFeishuCardKitClient` in `WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs` so failures can be configured per created handle:

```csharp
public Queue<int?> FailUpdateAttemptSequence { get; } = new();

...
var failUpdateOnAttempt = FailUpdateAttemptSequence.Count > 0
    ? FailUpdateAttemptSequence.Dequeue()
    : FailUpdateOnAttempt;

...
if (failUpdateOnAttempt.HasValue && record.UpdateAttemptCount >= failUpdateOnAttempt.Value)
{
    return Task.FromResult(false);
}
```

- [ ] **Step 3: Write the failing card-action stream test for replacement-card recovery**

Add a matching test in `WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs` that drives the card-action path and asserts:

```csharp
Assert.Equal(2, cardKit.Handles.Count);
Assert.Equal("已经处理完了。", cardKit.Handles[1].FinalContent);
Assert.Null(cardKit.Handles[0].FinalContent);
```

- [ ] **Step 4: Run the focused streaming tests and verify they fail**

Run:

```powershell
dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "HandleIncomingMessageAsync_ReplacesBrokenStreamingCardOnceAndFinishesOnReplacement|ExecuteCommand"
```

Expected:

- the new channel-stream recovery test fails because the first stopped card ends the stream
- the new card-action recovery test fails for the same reason

- [ ] **Step 5: Create the shared resilient streaming-card session**

Create `WebCodeCli.Domain/Domain/Service/Channels/FeishuStreamingCardSession.cs` with a focused abstraction:

```csharp
namespace WebCodeCli.Domain.Domain.Service.Channels;

internal sealed class FeishuStreamingCardSession
{
    private readonly Func<FeishuStreamingHandle, string, CancellationToken, Task<FeishuStreamingHandle?>> _replacementFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _replacementCount;

    public FeishuStreamingCardSession(
        FeishuStreamingHandle initialHandle,
        Func<FeishuStreamingHandle, string, CancellationToken, Task<FeishuStreamingHandle?>> replacementFactory)
    {
        CurrentHandle = initialHandle;
        _replacementFactory = replacementFactory;
    }

    public FeishuStreamingHandle CurrentHandle { get; private set; }

    public async Task<bool> UpdateAsync(string content, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await CurrentHandle.UpdateAsync(content);
            if (!CurrentHandle.AreCardUpdatesStopped)
            {
                return true;
            }

            if (_replacementCount >= 1)
            {
                return false;
            }

            var replacement = await _replacementFactory(CurrentHandle, content, cancellationToken);
            if (replacement == null)
            {
                return false;
            }

            _replacementCount++;
            CurrentHandle = replacement;
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task FinishAsync(string finalContent) => CurrentHandle.FinishAsync(finalContent);
}
```

- [ ] **Step 6: Remove stop-callback coupling from `FeishuStreamingHandle` if it is no longer needed**

If the only new change in `WebCodeCli.Domain/Domain/Model/Channels/FeishuStreamingHandle.cs` is the `onCardUpdatesStopped` callback experiment, revert that portion and keep the handle single-purpose:

```csharp
public FeishuStreamingHandle(
    string cardId,
    string messageId,
    Func<string, int, Task<bool>> updateAsync,
    Func<string, int, Task<bool>> finishAsync,
    int throttleMs = 500,
    int quietWindowAfterUpdateMs = 0)
```

and:

```csharp
public void StopCardUpdates()
{
    Interlocked.Exchange(ref _cardUpdatesStopped, 1);
}
```

- [ ] **Step 7: Add a small replacement-card factory helper in `FeishuChannelService`**

In `WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs`, add a helper near `SendStreamingMessageAsync`:

```csharp
private async Task<FeishuStreamingHandle?> TryCreateReplacementStreamingHandleAsync(
    string chatId,
    string? replyMessageId,
    string latestRenderedContent,
    FeishuStreamingCardChrome chrome,
    FeishuOptions effectiveOptions,
    CancellationToken cancellationToken)
{
    try
    {
        return await _cardKit.CreateStreamingHandleAsync(
            chatId,
            replyMessageId,
            latestRenderedContent,
            effectiveOptions.DefaultCardTitle,
            cancellationToken,
            effectiveOptions,
            chrome);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to create replacement Feishu streaming card for chat {ChatId}", chatId);
        return null;
    }
}
```

- [ ] **Step 8: Route the channel streaming path through the session wrapper**

In `FeishuChannelService`, replace direct `handle.UpdateAsync(...)` / `handle.FinishAsync(...)` usage in `ExecuteCliAndStreamAsync(...)`, the pulse task, and external history backfill with the wrapper:

```csharp
var cardSession = new FeishuStreamingCardSession(
    handle,
    (stoppedHandle, latestContent, token) => TryCreateReplacementStreamingHandleAsync(
        chatId,
        null,
        latestContent,
        activeExecution.Chrome,
        effectiveOptions,
        token));

...
var updateSucceeded = await cardSession.UpdateAsync(displayContent, cancellationToken);
handle = cardSession.CurrentHandle;
if (!updateSucceeded)
{
    var disconnectedContent = await TryHandleStreamingCardDisconnectAsync(...);
    ...
}
...
await cardSession.FinishAsync(finalOutput);
handle = cardSession.CurrentHandle;
```

- [ ] **Step 9: Add the same replacement-card factory helper in `FeishuCardActionService`**

In `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`, add a corresponding helper that preserves the same chat placement rule used by the original card-action flow:

```csharp
private async Task<FeishuStreamingHandle?> TryCreateReplacementStreamingHandleAsync(
    string chatId,
    string latestRenderedContent,
    FeishuStreamingCardChrome chrome,
    FeishuOptions effectiveOptions,
    CancellationToken cancellationToken)
{
    try
    {
        return await _cardKit.CreateStreamingHandleAsync(
            chatId,
            null,
            latestRenderedContent,
            effectiveOptions.DefaultCardTitle,
            cancellationToken,
            effectiveOptions,
            chrome);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to create replacement Feishu card-action streaming card for chat {ChatId}", chatId);
        return null;
    }
}
```

- [ ] **Step 10: Route both card-action streaming methods through the shared session wrapper**

Update both `ExecuteCliAndStreamAsync(...)` and `ExecuteLowInterruptionContinueAndStreamAsync(...)` in `FeishuCardActionService.cs` to use the wrapper in the same pattern as the channel service:

```csharp
var cardSession = new FeishuStreamingCardSession(
    handle,
    (stoppedHandle, latestContent, token) => TryCreateReplacementStreamingHandleAsync(
        chatId,
        latestContent,
        streamingChrome,
        effectiveOptions,
        token));

...
var updateSucceeded = await cardSession.UpdateAsync(displayContent, executionCancellationToken);
handle = cardSession.CurrentHandle;
```

- [ ] **Step 11: Run the focused streaming tests and verify they pass**

Run:

```powershell
dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "HandleIncomingMessageAsync_ReplacesBrokenStreamingCardOnceAndFinishesOnReplacement|FeishuCardActionServiceTests"
```

Expected:

- the channel-stream recovery test passes with exactly two handles
- the card-action recovery test passes with exactly two handles
- the final content appears only on the replacement card

- [ ] **Step 12: Commit**

```powershell
git add WebCodeCli.Domain/Domain/Service/Channels/FeishuStreamingCardSession.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs WebCodeCli.Domain/Domain/Model/Channels/FeishuStreamingHandle.cs WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs
git commit -m "fix: continue feishu streams on replacement card"
```

### Task 3: Final disconnect fallback and agent-note update

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
- Modify: `docs/agent-notes/2026-05-20.md`

- [ ] **Step 1: Write the failing test for second-card failure falling back to disconnect**

Add one more channel-path test in `WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs`:

```csharp
[Fact]
public async Task HandleIncomingMessageAsync_WhenReplacementCardAlsoFails_AppendsDisconnectMessage()
{
    var cardKit = new StreamingRecordingFeishuCardKitClient();
    cardKit.FailUpdateAttemptSequence.Enqueue(1);
    cardKit.FailUpdateAttemptSequence.Enqueue(1);
    ...
    Assert.Equal(2, cardKit.Handles.Count);
    Assert.Contains("飞书流式更新断连", cardKit.Handles[1].FinalContent);
}
```

- [ ] **Step 2: Ensure disconnect fallback still appends the existing terminal message after replacement exhaustion**

Keep `TryHandleStreamingCardDisconnectAsync(...)` in both services as the final fallback, but only invoke it after:

```csharp
if (!updateSucceeded)
{
    var disconnectedContent = await TryHandleStreamingCardDisconnectAsync(...);
    ...
}
```

Do not call disconnect fallback immediately when the first handle stops if replacement still succeeded.

- [ ] **Step 3: Run the focused recovery tests and verify replacement exhaustion works**

Run:

```powershell
dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "Disconnect|Replacement|FeishuCardKitClientTests"
```

Expected:

- same-card recovery tests pass
- one-time replacement tests pass
- second-card failure test passes with the existing disconnect message

- [ ] **Step 4: Update the daily agent note with the confirmed working rule**

Append a new section to `docs/agent-notes/2026-05-20.md`:

```markdown
## Feishu streaming card recovery must first preserve the current card after timeout-plus-sequence-conflict, then fall back to at most one replacement card

- Symptom:
  - CardKit streaming could stop after a timeout followed by `300317 sequence number compare failed`, even though the prior timed-out write likely already succeeded.
- Working rule:
  - Treat retry-after-timeout `300317` as a same-card success signal.
  - Only create one replacement streaming card when the current handle is truly no longer writable.
  - If the replacement card also fails, fall back to the existing disconnect text.
```

- [ ] **Step 5: Run the full targeted Feishu test suite**

Run:

```powershell
dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "Feishu"
```

Expected:

- Feishu-related tests pass
- no regression in existing card completion, quick action, or attachment tests

- [ ] **Step 6: Commit**

```powershell
git add WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs docs/agent-notes/2026-05-20.md WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs
git commit -m "docs: record feishu streaming card recovery rules"
```
