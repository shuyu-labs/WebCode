# Feishu Final-Only Reply TTS Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a second Feishu reply-TTS mode that speaks only the current turn's structured Codex `final_answer` content while preserving the existing full-reply speech mode.

**Architecture:** Preserve assistant message `phase` in the Codex structured streaming path, accumulate both full-reply and final-only text during Feishu streaming, and let a new `ReplyTtsMode` configuration choose which completed text the existing reply-TTS orchestrator consumes. Keep rollout re-read as an optional Codex-only fallback when the live final-answer buffer is empty.

**Tech Stack:** ASP.NET Core, Blazor Server, SqlSugar entities, existing CLI adapter/event pipeline, Feishu streaming card services, xUnit test projects, current reply-TTS orchestration and audio delivery services.

Depends on:
- `docs/superpowers/specs/2026-05-27-feishu-final-only-reply-tts-design.md`

---

## File Map

### Configuration and persistence

- Modify: `WebCodeCli.Domain/Repositories/Base/UserFeishuBotConfig/UserFeishuBotConfigEntity.cs`
  Replace the single reply-TTS boolean with a mode field while keeping the voice field intact.
- Modify: `WebCodeCli.Domain/Domain/Service/UserFeishuBotConfigService.cs`
  Normalize and persist the new mode, including legacy boolean compatibility mapping.
- Modify: `WebCodeCli/Controllers/AdminController.cs`
  Read and write the new mode through the admin Feishu bot config API.
- Modify: `WebCodeCli/Components/AdminUserManagementModal.razor`
  Render two mutually exclusive speech-mode choices instead of a single toggle.
- Modify: `WebCodeCli/Components/AdminUserManagementModal.razor.cs`
  Carry the new mode field through modal load/save state.
- Modify: `WebCodeCli/Helpers/AdminUserManagementReplyTtsUiState.cs`
  Adjust derived UI state for `Off`, `FullReply`, and `FinalOnly`.

### Feishu quick toggle UI and actions

- Modify: `WebCodeCli.Domain/Domain/Model/Channels/FeishuHelpCardAction.cs`
  Add a dedicated help-card action for final-only reply TTS mode.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuHelpCardBuilder.cs`
  Show both `语音回复` and `结论语音回复` actions as mutually exclusive top-row buttons.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuMessageHandler.cs`
  Resolve the effective reply-TTS mode when building help cards.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
  Toggle between `Off`, `FullReply`, and `FinalOnly` from help-card callbacks and update toast/card text.

### Codex structured event preservation

- Modify: `WebCodeCli.Domain/Domain/Service/Adapters/CliOutputEvent.cs`
  Add assistant phase metadata to the structured adapter event model.
- Modify: `WebCodeCli.Domain/Domain/Service/CodexAppServerSessionManager.cs`
  Preserve assistant `phase` when normalizing Codex app-server notifications to adapter-facing JSONL.
- Modify: `WebCodeCli.Domain/Domain/Service/Adapters/CodexAdapter.cs`
  Parse assistant `phase` into `CliOutputEvent` while preserving current assistant-text extraction behavior.

### Feishu completed-reply text selection

- Modify: `WebCodeCli.Domain/Domain/Model/Channels/FeishuCompletedReplyTtsRequest.cs`
  Carry both the merged completed output and the final-answer-only output.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs`
  Accumulate full assistant text and final-answer-only text during normal streaming, reset both at turn boundaries, and queue both values into the completed reply TTS request.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
  Mirror the same dual-buffer behavior for card-action and low-interruption streaming paths.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsOrchestrator.cs`
  Choose speech text based on the new mode, silently skipping `FinalOnly` when no final-answer text exists.

### Optional Codex rollout fallback

- Modify: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsOrchestrator.cs`
  Add a narrowly-scoped Codex-only fallback that can re-read the latest rollout file for `final_answer` content when the live final-answer buffer is empty.
- Modify: `WebCodeCli.Domain/Domain/Service/ExternalCliSessionHistoryService.cs`
  Expose or factor the rollout parsing logic needed to extract only assistant `final_answer` text without duplicating Codex rollout parsing.

### Tests

- Modify: `WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs`
  Cover new help-card toggles, mutual exclusion, and final-only mode toasts.
- Modify: `WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs`
  Cover full vs final-only completed-reply accumulation and turn-boundary resets.
- Modify: `WebCodeCli.Domain.Tests/ReplyTtsOrchestratorTests.cs`
  Cover `Off`, `FullReply`, `FinalOnly`, silent skip, and rollout fallback behavior.
- Add: `WebCodeCli.Domain.Tests/CodexAdapterTests.cs`
  Cover assistant phase parsing from normalized JSONL.
- Add: `WebCodeCli.Domain.Tests/CodexAppServerSessionManagerTests.cs`
  Cover normalized assistant-message JSONL including `phase`.
- Add: `tests/WebCodeCli.Tests/AdminUserManagementReplyTtsModeTests.cs`
  Cover admin DTO/UI state compatibility and mode persistence.

### Explicit non-goals

- Do not implement text slicing, regex extraction, or summarization heuristics for conclusions.
- Do not change the visible Feishu card body to final-only text.
- Do not change non-Codex providers unless their adapters already expose equivalent structured phases.
- Do not block text completion on rollout fallback success.

---

## Chunk 1: Replace the Boolean Reply-TTS Switch with a Mode

### Task 1: Add `ReplyTtsMode` to Feishu bot config and admin persistence

**Files:**
- Modify: `WebCodeCli.Domain/Repositories/Base/UserFeishuBotConfig/UserFeishuBotConfigEntity.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/UserFeishuBotConfigService.cs`
- Modify: `WebCodeCli/Controllers/AdminController.cs`
- Modify: `WebCodeCli/Components/AdminUserManagementModal.razor.cs`
- Modify: `WebCodeCli/Helpers/AdminUserManagementReplyTtsUiState.cs`
- Test: `tests/WebCodeCli.Tests/AdminUserManagementReplyTtsModeTests.cs`

- [ ] **Step 1: Write the failing persistence and compatibility tests**

Add tests that prove:

- legacy `ReplyTtsEnabled = true` maps to `FullReply`
- legacy `ReplyTtsEnabled = false` maps to `Off`
- a saved `FinalOnly` mode round-trips through the admin DTO and persistence service
- voice-selection UI state still disables correctly when the mode is `Off`

```csharp
[Fact]
public void Create_WhenModeIsOff_DisablesVoiceSelection()
{
    var state = AdminUserManagementReplyTtsUiState.Create(
        replyTtsMode: "Off",
        selectedVoiceId: "voice-a",
        availableVoices: []);

    Assert.True(state.IsVoiceSelectorDisabled);
}
```

- [ ] **Step 2: Run the focused test command and confirm it fails**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~ReplyTtsMode"
```

Expected:

- FAIL because the new mode field and compatibility logic do not exist yet

- [ ] **Step 3: Add the mode field to the entity and DTO surface**

Update the entity and admin DTOs to include a mode field while temporarily preserving boolean compatibility during migration.

Use a simple string-backed mode:

```csharp
public static class ReplyTtsModes
{
    public const string Off = "Off";
    public const string FullReply = "FullReply";
    public const string FinalOnly = "FinalOnly";
}
```

Persist the effective mode on the entity and expose it through the admin request/response model.

- [ ] **Step 4: Implement compatibility mapping in the config service**

In `UserFeishuBotConfigService`, normalize incoming mode values and map legacy boolean state to:

- `true -> FullReply`
- `false -> Off`

Reject unknown values by normalizing them to `Off`.

- [ ] **Step 5: Update admin modal models and UI-state helper**

Make the modal editor, payload model, and UI-state helper all use the new mode field rather than a single boolean toggle.

- [ ] **Step 6: Run tests to verify they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~ReplyTtsMode"
```

Expected:

- PASS

- [ ] **Step 7: Commit**

```powershell
git add WebCodeCli.Domain/Repositories/Base/UserFeishuBotConfig/UserFeishuBotConfigEntity.cs WebCodeCli.Domain/Domain/Service/UserFeishuBotConfigService.cs WebCodeCli/Controllers/AdminController.cs WebCodeCli/Components/AdminUserManagementModal.razor.cs WebCodeCli/Helpers/AdminUserManagementReplyTtsUiState.cs tests/WebCodeCli.Tests/AdminUserManagementReplyTtsModeTests.cs
git commit -m "feat: add feishu reply tts mode config"
```

### Task 2: Replace the help-card boolean toggle with explicit full/final-only actions

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Model/Channels/FeishuHelpCardAction.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuHelpCardBuilder.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuMessageHandler.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
- Test: `WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs`

- [ ] **Step 1: Write the failing help-card action tests**

Add tests that prove:

- clicking `toggle_reply_tts` still toggles full-reply mode
- clicking the new final-only action sets mode to `FinalOnly`
- enabling one mode disables the other
- toast text and card labels reflect the effective mode

```csharp
[Fact]
public async Task HandleCardActionAsync_WhenToggleFinalOnlyReplyTts_SetsFinalOnlyMode()
{
    var response = await service.HandleCardActionAsync(
        """{"action":"toggle_final_only_reply_tts"}""",
        chatId: "oc_final_only_tts_chat");

    Assert.Equal("✅ 已开启结论语音回复", ExtractToastContent(response));
    Assert.Contains("结论语音回复：开", ExtractCardContentStrings(response));
    Assert.Contains("语音回复：关", ExtractCardContentStrings(response));
}
```

- [ ] **Step 2: Run the focused test command and confirm it fails**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTts"
```

Expected:

- FAIL because the final-only action and new card text do not exist yet

- [ ] **Step 3: Add the new help-card action constant and card labels**

Add:

```csharp
public const string ToggleFinalOnlyReplyTtsAction = "toggle_final_only_reply_tts";
```

Update the help card to show two explicit buttons:

- `语音回复：开/关`
- `结论语音回复：开/关`

Render the active one as `primary` and the inactive one as `default`.

- [ ] **Step 4: Implement mutually exclusive action handlers**

In `FeishuCardActionService`, make the handlers set the effective mode directly:

- full action toggles between `FullReply` and `Off`
- final-only action toggles between `FinalOnly` and `Off`

Do not keep a state where both are active.

- [ ] **Step 5: Use the mode when building help cards**

Replace boolean `replyTtsEnabled` lookups with mode-aware resolution in `FeishuMessageHandler` and any other help-card builder call sites.

- [ ] **Step 6: Run tests to verify they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTts"
```

Expected:

- PASS

- [ ] **Step 7: Commit**

```powershell
git add WebCodeCli.Domain/Domain/Model/Channels/FeishuHelpCardAction.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuHelpCardBuilder.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuMessageHandler.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs
git commit -m "feat: add feishu final-only tts toggle actions"
```

---

## Chunk 2: Preserve Codex Assistant Phase in Structured Streaming

### Task 3: Extend the structured CLI output event model with assistant phase

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/Adapters/CliOutputEvent.cs`
- Add: `WebCodeCli.Domain.Tests/CodexAdapterTests.cs`

- [ ] **Step 1: Write the failing Codex adapter phase test**

Add a test that proves a normalized assistant-message JSONL line with `phase = "final_answer"` is parsed into:

- `ItemType = "agent_message"`
- `Content = "hello"`
- `AssistantPhase = "final_answer"`

```csharp
[Fact]
public void ParseOutputLine_WhenAgentMessageIncludesPhase_PreservesAssistantPhase()
{
    var adapter = new CodexAdapter();
    var line = """{"type":"item.updated","item":{"type":"agent_message","text":"hello","phase":"final_answer"}}""";

    var outputEvent = adapter.ParseOutputLine(line);

    Assert.NotNull(outputEvent);
    Assert.Equal("agent_message", outputEvent!.ItemType);
    Assert.Equal("hello", outputEvent.Content);
    Assert.Equal("final_answer", outputEvent.AssistantPhase);
}
```

- [ ] **Step 2: Run the focused test command and confirm it fails**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~CodexAdapterTests"
```

Expected:

- FAIL because `AssistantPhase` does not exist yet

- [ ] **Step 3: Add the `AssistantPhase` property**

Update `CliOutputEvent` with:

```csharp
public string? AssistantPhase { get; set; }
```

Keep it optional and do not require non-Codex adapters to populate it.

- [ ] **Step 4: Run the test again to confirm it still fails for parsing**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~CodexAdapterTests"
```

Expected:

- FAIL because the Codex adapter still ignores phase

- [ ] **Step 5: Commit**

```powershell
git add WebCodeCli.Domain/Domain/Service/Adapters/CliOutputEvent.cs WebCodeCli.Domain.Tests/CodexAdapterTests.cs
git commit -m "refactor: add assistant phase to cli output events"
```

### Task 4: Preserve and parse assistant phase in Codex normalized JSONL

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/CodexAppServerSessionManager.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Adapters/CodexAdapter.cs`
- Add: `WebCodeCli.Domain.Tests/CodexAppServerSessionManagerTests.cs`
- Modify: `WebCodeCli.Domain.Tests/CodexAdapterTests.cs`

- [ ] **Step 1: Write the failing app-server normalization test**

Add a test that proves `item/agentMessage/delta` with a `phase` parameter produces JSONL that still includes `phase`.

```csharp
[Fact]
public void BuildAgentMessageDeltaPayload_WhenPhaseExists_IncludesPhase()
{
    var json = InvokeBuildCliOutputJsonl(
        method: "item/agentMessage/delta",
        paramsJson: """{"delta":"done","phase":"final_answer","itemId":"item-1"}""");

    Assert.Contains(@"""phase"":""final_answer""", json);
}
```

- [ ] **Step 2: Run the focused test command and confirm it fails**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~CodexAppServerSessionManagerTests|FullyQualifiedName~CodexAdapterTests"
```

Expected:

- FAIL because normalized JSONL and adapter parsing still drop phase

- [ ] **Step 3: Preserve `phase` in normalized assistant-message payloads**

Update `BuildAgentMessageDeltaPayload(...)` so the generated `item.updated.item` includes `phase` whenever the app-server notification provides it.

- [ ] **Step 4: Parse `phase` in the Codex adapter**

Update the `agent_message` parsing path in both `item.updated` and `item.completed` handling so `outputEvent.AssistantPhase` is set from `item.phase`.

- [ ] **Step 5: Run tests to verify they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~CodexAppServerSessionManagerTests|FullyQualifiedName~CodexAdapterTests"
```

Expected:

- PASS

- [ ] **Step 6: Commit**

```powershell
git add WebCodeCli.Domain/Domain/Service/CodexAppServerSessionManager.cs WebCodeCli.Domain/Domain/Service/Adapters/CodexAdapter.cs WebCodeCli.Domain.Tests/CodexAppServerSessionManagerTests.cs WebCodeCli.Domain.Tests/CodexAdapterTests.cs
git commit -m "feat: preserve codex assistant phase in streaming events"
```

---

## Chunk 3: Accumulate Final-Only Text in Feishu Streaming

### Task 5: Extend the completed-reply TTS request with final-answer text

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Model/Channels/FeishuCompletedReplyTtsRequest.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsOrchestrator.cs`
- Modify: `WebCodeCli.Domain.Tests/ReplyTtsOrchestratorTests.cs`

- [ ] **Step 1: Write the failing orchestrator mode-selection tests**

Add tests that prove:

- `FullReply` uses `Output`
- `FinalOnly` uses `FinalAnswerOutput`
- `FinalOnly` with empty `FinalAnswerOutput` skips audio and sends no failure notice

```csharp
[Fact]
public async Task QueueCompletedReplyAsync_WhenModeIsFinalOnlyAndFinalAnswerMissing_SkipsAudioSilently()
{
    var request = new FeishuCompletedReplyTtsRequest
    {
        ChatId = "oc_final_only_skip",
        Output = "commentary text",
        FinalAnswerOutput = "",
        Username = "alice"
    };

    await orchestrator.QueueCompletedReplyAsync(request);

    await WaitForQueueDrainAsync();
    Assert.Empty(audioMessageService.SentAudioMessages);
    Assert.Empty(cardKitClient.SentTextMessages);
}
```

- [ ] **Step 2: Run the focused test command and confirm it fails**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTtsOrchestratorTests"
```

Expected:

- FAIL because the request model lacks `FinalAnswerOutput` and the orchestrator only knows one text source

- [ ] **Step 3: Extend the request contract**

Add:

```csharp
public string FinalAnswerOutput { get; set; } = string.Empty;
```

Optionally include an effective `ReplyTtsMode` property if that simplifies downstream selection and reduces extra config lookups.

- [ ] **Step 4: Update the orchestrator selection logic**

Choose speech text by effective mode:

- `FullReply -> Output`
- `FinalOnly -> FinalAnswerOutput`
- `Off -> return`

If `FinalOnly` and `FinalAnswerOutput` is empty or whitespace, return without enqueueing synthesis or failure notice work.

- [ ] **Step 5: Run tests to verify they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTtsOrchestratorTests"
```

Expected:

- PASS

- [ ] **Step 6: Commit**

```powershell
git add WebCodeCli.Domain/Domain/Model/Channels/FeishuCompletedReplyTtsRequest.cs WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsOrchestrator.cs WebCodeCli.Domain.Tests/ReplyTtsOrchestratorTests.cs
git commit -m "feat: add final-only reply tts selection"
```

### Task 6: Add dual text buffers to Feishu normal streaming completion

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs`
- Modify: `WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs`

- [ ] **Step 1: Write the failing Feishu normal-stream tests**

Add tests that prove:

- commentary text contributes to the full assistant buffer
- `final_answer` text contributes to both the full buffer and the final-only buffer
- queued TTS request includes both `Output` and `FinalAnswerOutput`
- goal-runtime turn boundary clears both buffers

```csharp
[Fact]
public async Task ExecuteCliToolAndStreamReplyAsync_WhenCodexOutputsCommentaryAndFinalAnswer_QueuesBothBuffers()
{
    // Arrange a stream with commentary, final_answer, and completion.
    // Assert queued request Output == full merged text and FinalAnswerOutput == final-only text.
}
```

- [ ] **Step 2: Run the focused test command and confirm it fails**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuChannelServiceTests"
```

Expected:

- FAIL because the service only accumulates one assistant buffer

- [ ] **Step 3: Add a final-only buffer to the streaming path**

In `ProcessJsonlLine(...)`, continue appending every assistant message to the existing assistant buffer, but also append to a second buffer when:

```csharp
outputEvent.AssistantPhase == "final_answer"
```

- [ ] **Step 4: Reset both buffers on turn handoff**

At the current goal-runtime turn-boundary reset points, clear:

- full assistant buffer
- final-only assistant buffer
- JSONL buffer if already cleared today

- [ ] **Step 5: Queue both texts into the completed reply TTS request**

When creating `FeishuCompletedReplyTtsRequest`, populate:

- `Output = finalOutput`
- `FinalAnswerOutput = finalAnswerOutput`

- [ ] **Step 6: Run tests to verify they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuChannelServiceTests"
```

Expected:

- PASS

- [ ] **Step 7: Commit**

```powershell
git add WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs
git commit -m "feat: accumulate final-only tts text in feishu channel"
```

### Task 7: Add dual text buffers to Feishu card-action and low-interruption streaming

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
- Modify: `WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs`

- [ ] **Step 1: Write the failing card-action streaming tests**

Add tests that prove:

- card-action streaming queues both `Output` and `FinalAnswerOutput`
- low-interruption continue also queues both text buffers
- final-only mode still resets per goal-runtime turn

```csharp
[Fact]
public async Task HandleCardActionAsync_LowInterruptionContinue_PreservesFinalOnlyBufferPerTurn()
{
    // Arrange Codex output with commentary + final_answer.
    // Assert queued TTS request contains final-only text for the current turn only.
}
```

- [ ] **Step 2: Run the focused test command and confirm it fails**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuCardActionServiceTests"
```

Expected:

- FAIL because the card-action streaming path still tracks only one assistant buffer

- [ ] **Step 3: Mirror the dual-buffer logic in `FeishuCardActionService`**

Update both the ordinary card-action streaming path and the low-interruption continue path to accumulate:

- full assistant text
- final-only assistant text

Use the same `AssistantPhase == "final_answer"` gate as the normal channel path.

- [ ] **Step 4: Queue both texts into the TTS request**

Update `TryQueueCompletedReplyTtsAsync(...)` callers so they pass both full and final-only text.

- [ ] **Step 5: Run tests to verify they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuCardActionServiceTests"
```

Expected:

- PASS

- [ ] **Step 6: Commit**

```powershell
git add WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs
git commit -m "feat: accumulate final-only tts text in feishu card actions"
```

---

## Chunk 4: Add Conservative Codex Rollout Fallback

### Task 8: Expose a Codex final-answer rollout extraction path

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/ExternalCliSessionHistoryService.cs`
- Modify: `WebCodeCli.Domain.Tests/ReplyTtsOrchestratorTests.cs`

- [ ] **Step 1: Write the failing rollout fallback extraction tests**

Add tests that prove a rollout stream containing:

- assistant commentary message
- assistant final-answer message

returns only the final-answer text for fallback purposes.

```csharp
[Fact]
public void ExtractCodexFinalAnswerText_WhenRolloutContainsCommentaryAndFinalAnswer_ReturnsFinalOnly()
{
    var jsonl = new[]
    {
        """{"type":"response_item","payload":{"type":"message","role":"assistant","phase":"commentary","content":[{"type":"output_text","text":"thinking"}]}}""",
        """{"type":"response_item","payload":{"type":"message","role":"assistant","phase":"final_answer","content":[{"type":"output_text","text":"answer"}]}}"""
    };

    var result = ExtractFinalAnswerText(jsonl);

    Assert.Equal("answer", result);
}
```

- [ ] **Step 2: Run the focused test command and confirm it fails**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTtsOrchestratorTests"
```

Expected:

- FAIL because no final-only rollout extraction exists yet

- [ ] **Step 3: Add a reusable Codex final-answer extraction helper**

Factor the existing rollout parsing logic so a caller can extract only assistant `final_answer` content from Codex rollout files without duplicating parser code.

- [ ] **Step 4: Run tests to verify extraction works**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTtsOrchestratorTests"
```

Expected:

- PASS

- [ ] **Step 5: Commit**

```powershell
git add WebCodeCli.Domain/Domain/Service/ExternalCliSessionHistoryService.cs WebCodeCli.Domain.Tests/ReplyTtsOrchestratorTests.cs
git commit -m "refactor: add codex final-answer rollout extraction"
```

### Task 9: Use rollout fallback only for Codex final-only reply TTS

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsOrchestrator.cs`
- Modify: `WebCodeCli.Domain.Tests/ReplyTtsOrchestratorTests.cs`

- [ ] **Step 1: Write the failing fallback behavior tests**

Add tests that prove:

- when mode is `FinalOnly` and live `FinalAnswerOutput` is empty, the orchestrator tries Codex rollout fallback once
- when fallback returns final-answer text, audio is generated from that text
- when fallback returns empty or throws, the orchestrator skips audio silently
- full-reply mode never uses this fallback

```csharp
[Fact]
public async Task QueueCompletedReplyAsync_WhenFinalOnlyLiveTextMissing_UsesCodexRolloutFallback()
{
    // Arrange request with empty FinalAnswerOutput and a mocked fallback extractor returning "final text".
    // Assert synthesis receives "final text".
}
```

- [ ] **Step 2: Run the focused test command and confirm it fails**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTtsOrchestratorTests"
```

Expected:

- FAIL because rollout fallback is not wired into the orchestrator

- [ ] **Step 3: Implement the conservative fallback**

In `ReplyTtsOrchestrator`, only attempt fallback when all conditions hold:

- tool/provider context is Codex-capable
- reply-TTS mode is `FinalOnly`
- `FinalAnswerOutput` is empty
- the request completed normally
- session/thread context is sufficient to locate the rollout

If fallback yields text, use it.
If not, return silently.

- [ ] **Step 4: Run tests to verify they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTtsOrchestratorTests"
```

Expected:

- PASS

- [ ] **Step 5: Commit**

```powershell
git add WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsOrchestrator.cs WebCodeCli.Domain.Tests/ReplyTtsOrchestratorTests.cs
git commit -m "feat: add codex final-only reply tts rollout fallback"
```

---

## Chunk 5: Verification and Cleanup

### Task 10: Run the full targeted regression suite

**Files:**
- Modify: `docs/agent-notes/2026-05-27.md`

> Execution status on 2026-05-27:
> - Steps 1-3 were completed and verified in-session.
> - Step 4 remains intentionally unchecked because no commit was created in this run.

- [x] **Step 1: Run the domain test targets**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTts|FullyQualifiedName~FeishuChannelServiceTests|FullyQualifiedName~FeishuCardActionServiceTests|FullyQualifiedName~CodexAdapterTests|FullyQualifiedName~CodexAppServerSessionManagerTests"
```

Expected:

- PASS

- [x] **Step 2: Run the web/admin test targets**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~ReplyTtsMode"
```

Expected:

- PASS

- [x] **Step 3: Update the current agent note with any implementation-specific findings**

Add any confirmed constraints discovered during implementation, such as:

- exact fallback timing behavior
- any provider-specific phase quirks
- any migration nuance for legacy boolean config

- [ ] **Step 4: Commit**

```powershell
git add docs/agent-notes/2026-05-27.md
git commit -m "test: verify feishu final-only reply tts changes"
```

---

## Self-Review

### Spec coverage

- product definition and mutual exclusivity are covered in Tasks 1-2
- assistant phase preservation is covered in Tasks 3-4
- dual full/final-only buffering is covered in Tasks 6-7
- TTS mode selection and silent skip are covered in Task 5
- rollout fallback is covered in Tasks 8-9
- regression verification and implementation notes are covered in Task 10

### Placeholder scan

- no `TODO`, `TBD`, or deferred "implement later" placeholders remain
- each test or code change task includes concrete files and commands

### Type consistency

- the plan consistently uses `ReplyTtsMode`, `AssistantPhase`, and `FinalAnswerOutput`
- `FinalOnly` and `FullReply` are used consistently as the canonical mode values
