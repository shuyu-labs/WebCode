# Feishu Reply Documents Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Feishu reply-TTS feature with two independent Feishu cloud-document toggles: `完整回复文档` and `结论回复文档`, reuse the existing completed-reply turn pipeline, and remove TTS-specific runtime, UI, packaging, and tests.

**Architecture:** Rename the completed-reply TTS orchestration pipeline into a completed-reply document pipeline, keep the existing turn-boundary accumulation of `Output` and `FinalAnswerOutput`, add document creation/writing/permission APIs to the existing Feishu client, migrate config from mode-based TTS to two independent document booleans, and delete TTS-only code paths after the new document flow passes.

**Tech Stack:** ASP.NET Core, Blazor Server, SqlSugar entities, existing Feishu message/card client, Codex structured output pipeline, xUnit test projects, existing chat/session repository, Feishu cloud-document APIs.

Depends on:
- `docs/superpowers/specs/2026-05-28-feishu-reply-documents-design.md`

---

## File Map

### Configuration and persistence

- Modify: `WebCodeCli.Domain/Repositories/Base/UserFeishuBotConfig/UserFeishuBotConfigEntity.cs`
  Replace reply-TTS mode/voice fields with two independent document toggles.
- Modify: `WebCodeCli.Domain/Domain/Service/UserFeishuBotConfigService.cs`
  Migrate old reply-TTS data into new document toggles and normalize saved config.
- Modify: `WebCodeCli/Controllers/AdminController.cs`
  Replace reply-TTS DTO fields with full/final document booleans.
- Modify: `WebCodeCli/Components/AdminUserManagementModal.razor`
  Replace reply-TTS mode controls with two independent document toggles.
- Modify: `WebCodeCli/Components/AdminUserManagementModal.razor.cs`
  Carry the new document toggles through load/save state.
- Modify or replace: `WebCodeCli/Helpers/AdminUserManagementReplyTtsUiState.cs`
  Remove voice/TTS-specific UI state and replace it with document-toggle UI state as needed.

### Feishu quick toggle UI and actions

- Modify: `WebCodeCli.Domain/Domain/Model/Channels/FeishuHelpCardAction.cs`
  Replace reply-TTS actions with `完整回复文档` and `结论回复文档` actions.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuHelpCardBuilder.cs`
  Render two independent document toggles instead of reply-TTS mode buttons.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuMessageHandler.cs`
  Resolve document-toggle state when building help cards.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
  Toggle full/final document flags independently and update toast/card text.

### Completed reply request and orchestration rename

- Modify and rename: `WebCodeCli.Domain/Domain/Model/Channels/FeishuCompletedReplyTtsRequest.cs`
  Rename to a completed reply document request model and add `CliThreadId` and `OriginalUserQuestion`.
- Modify and rename: `WebCodeCli.Domain/Domain/Service/Channels/IReplyTtsOrchestrator.cs`
  Rename to a reply-document orchestrator interface.
- Modify and rename: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsOrchestrator.cs`
  Replace TTS processing with document generation and link-message sending.

### Completion producers and turn context

- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs`
  Continue accumulating `Output` / `FinalAnswerOutput`, but enqueue completed document requests with thread/question context.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
  Mirror the same behavior for card-action completion and low-interruption flows.
- Modify as needed: session/context lookup call sites
  Resolve `CliThreadId` from current session and preserve original user question through completion.

### Feishu document client support

- Modify: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuCardKitClient.cs`
  Add methods for document create, document body write, and permission patch.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardKitClient.cs`
  Implement Feishu cloud-document API calls while reusing tenant token retrieval.
- Reuse: `WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs`
  Use the existing text-message send path for link delivery.

### Optional Codex final-answer fallback

- Modify and rename call sites in the new document orchestrator
  Keep the Codex-only `final_answer` rollout fallback only for `结论回复文档`.
- Modify: `WebCodeCli.Domain/Domain/Service/ExternalCliSessionHistoryService.cs`
  Reuse the existing structured final-answer extraction path without TTS-specific naming.

### TTS runtime removal

- Delete: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsLocalServiceManager.cs`
- Delete: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsStorageRootResolver.cs`
- Delete: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsSpeechTextNormalizer.cs`
- Delete: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsChunker.cs`
- Delete: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuReplyTtsPlatformService.cs`
- Delete: `WebCodeCli.Domain/Domain/Service/Channels/FeishuReplyTtsPlatformService.cs`
- Delete: `WebCodeCli.Domain/Domain/Service/Channels/ISherpaKokoroTtsClient.cs`
- Delete: `WebCodeCli.Domain/Domain/Service/Channels/SherpaKokoroTtsClient.cs`
- Delete: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuAudioMessageService.cs`
- Delete: `WebCodeCli.Domain/Domain/Service/Channels/FeishuAudioMessageService.cs`
- Delete: audio-transcode interfaces/implementations used only by reply TTS
- Remove TTS-related options/models/service registration paths

### Packaging cleanup

- Modify: `tools/build-windows-installer.ps1`
  Remove reply-TTS-only payload bundling and startup-time assumptions.
- Modify related installer/package files if referenced
  Remove TTS asset copy, bundled runtime, or release-note sections that only exist for reply TTS.

### Tests

- Modify or replace: `tests/WebCodeCli.Tests/AdminControllerReplyTtsTests.cs`
- Modify or replace: `tests/WebCodeCli.Tests/AdminUserManagementReplyTtsUiStateTests.cs`
- Modify or replace: `tests/WebCodeCli.Tests/AdminUserManagementModalStateTests.cs`
- Modify or replace: `tests/WebCodeCli.Tests/AdminUserManagementReplyTtsModeTests.cs`
- Modify: `WebCodeCli.Domain.Tests/FeishuHelpCardBuilderTests.cs`
- Modify: `WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs`
- Modify: `WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs`
- Modify or replace: `WebCodeCli.Domain.Tests/ReplyTtsOrchestratorTests.cs`
- Add if needed: dedicated document-orchestrator tests after rename
- Modify: `WebCodeCli.Domain.Tests/ExternalCliSessionHistoryServiceTests.cs`
- Remove TTS-only client/platform tests that no longer apply

### Explicit non-goals

- Do not preserve any reply-TTS audio behavior.
- Do not keep voice selection UI or voice persistence.
- Do not inject document links into the streaming/final card body.
- Do not generate a mixed full+final single document when both toggles are enabled.
- Do not implement text heuristics for conclusion extraction.

---

## Chunk 1: Replace Reply-TTS Config with Reply-Document Toggles

### Task 1: Migrate persistence, DTOs, and admin UI from reply-TTS mode to two document booleans

**Files:**
- Modify: `WebCodeCli.Domain/Repositories/Base/UserFeishuBotConfig/UserFeishuBotConfigEntity.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/UserFeishuBotConfigService.cs`
- Modify: `WebCodeCli/Controllers/AdminController.cs`
- Modify: `WebCodeCli/Components/AdminUserManagementModal.razor`
- Modify: `WebCodeCli/Components/AdminUserManagementModal.razor.cs`
- Modify or replace: `WebCodeCli/Helpers/AdminUserManagementReplyTtsUiState.cs`
- Modify or replace tests under `tests/WebCodeCli.Tests/`

- [ ] **Step 1: Write the failing admin/config migration tests**

Add tests that prove:

- old `ReplyTtsMode = FullReply` maps to `FullReplyDocEnabled = true`
- old `ReplyTtsMode = FinalOnly` maps to `FinalReplyDocEnabled = true`
- old `ReplyTtsMode = Off` maps to both false
- admin DTO round-trips the two new booleans
- no voice-selection state remains in the UI model

- [ ] **Step 2: Run the focused admin test command and confirm it fails**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~ReplyDoc|FullyQualifiedName~AdminUserManagement"
```

Expected:

- FAIL because the new document fields do not exist yet

- [ ] **Step 3: Replace entity/controller/modal fields with document booleans**

Persist:

- `FullReplyDocEnabled`
- `FinalReplyDocEnabled`

Remove reply-TTS mode and voice fields from the admin surface.

- [ ] **Step 4: Implement compatibility migration in the config service**

Map old saved mode values into the new booleans during normalization/save.

- [ ] **Step 5: Update admin modal copy and controls**

Render:

- `完整回复文档`
- `结论回复文档`

as independent toggles rather than a mode selector.

- [ ] **Step 6: Run focused admin tests and verify they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~ReplyDoc|FullyQualifiedName~AdminUserManagement"
```

Expected:

- PASS

---

## Chunk 2: Replace Help-Card Reply-TTS Actions with Reply-Document Actions

### Task 2: Convert help-card quick toggles to independent document switches

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Model/Channels/FeishuHelpCardAction.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuHelpCardBuilder.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuMessageHandler.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
- Modify tests: `WebCodeCli.Domain.Tests/FeishuHelpCardBuilderTests.cs`, `WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs`

- [ ] **Step 1: Write the failing help-card document-toggle tests**

Add tests that prove:

- `完整回复文档` and `结论回复文档` both render
- both can be on simultaneously
- toggling one does not forcibly disable the other
- toast text reflects document behavior rather than TTS behavior

- [ ] **Step 2: Run the focused domain tests and confirm they fail**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuHelpCardBuilderTests|FullyQualifiedName~FeishuCardActionServiceTests"
```

Expected:

- FAIL because the new actions and copy do not exist yet

- [ ] **Step 3: Replace reply-TTS actions with document actions**

Suggested action constants:

- `toggle_full_reply_doc`
- `toggle_final_reply_doc`

- [ ] **Step 4: Implement independent toggle handlers**

Persist and toast the two booleans independently.

- [ ] **Step 5: Update all help-card builder paths**

Include filtered-card and alternate builder paths so the document toggle state stays consistent everywhere.

- [ ] **Step 6: Run focused domain tests and verify they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuHelpCardBuilderTests|FullyQualifiedName~FeishuCardActionServiceTests"
```

Expected:

- PASS

---

## Chunk 3: Rename the Completed Reply TTS Pipeline to a Reply-Document Pipeline

### Task 3: Rename the completed reply request and orchestrator surface

**Files:**
- Modify and rename: `WebCodeCli.Domain/Domain/Model/Channels/FeishuCompletedReplyTtsRequest.cs`
- Modify and rename: `WebCodeCli.Domain/Domain/Service/Channels/IReplyTtsOrchestrator.cs`
- Modify and rename: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsOrchestrator.cs`
- Modify dependent call sites

- [ ] **Step 1: Write failing orchestrator API-shape tests**

Add tests that prove the orchestrator now reasons in terms of:

- full reply document
- final reply document
- `CliThreadId`
- `OriginalUserQuestion`

- [ ] **Step 2: Run the focused orchestrator tests and confirm they fail**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTtsOrchestratorTests"
```

Expected:

- FAIL because the renamed document request/orchestrator surface does not exist yet

- [ ] **Step 3: Rename request/orchestrator types and queue methods**

Keep the asynchronous per-chat serialization behavior intact.

- [ ] **Step 4: Expand the completed reply request payload**

Add:

- `CliThreadId`
- `OriginalUserQuestion`

- [ ] **Step 5: Update all compile references**

Rename and rebuild all call sites before changing behavior.

- [ ] **Step 6: Run focused orchestrator tests and verify they compile to the new surface**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyDoc|FullyQualifiedName~Orchestrator"
```

Expected:

- PASS or at least reach the new behavior-level failures for the next task

---

## Chunk 4: Carry Thread ID and Original Question Through Completion

### Task 4: Enqueue completed document requests with full/final output plus title context

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
- Modify tests: `WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs`, `WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs`

- [ ] **Step 1: Write failing completion-payload tests**

Add tests that prove:

- normal streaming completion enqueues `Output`, `FinalAnswerOutput`, `OriginalUserQuestion`, and `CliThreadId`
- card-action completion does the same
- goal-runtime turn-boundary still queues before clearing buffers

- [ ] **Step 2: Run the focused completion tests and confirm they fail**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuChannelServiceTests|FullyQualifiedName~FeishuCardActionServiceTests"
```

Expected:

- FAIL because title-context fields are not populated yet

- [ ] **Step 3: Thread the original user question through completion**

Use the current turn's original user message, not normalized CLI prompt text.

- [ ] **Step 4: Resolve and attach `CliThreadId`**

Prefer current session `CliThreadId`; allow orchestrator fallback to `SessionId` if absent.

- [ ] **Step 5: Preserve turn-boundary queue-before-clear ordering**

Do not regress the current goal-runtime boundary behavior.

- [ ] **Step 6: Run focused completion tests and verify they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuChannelServiceTests|FullyQualifiedName~FeishuCardActionServiceTests"
```

Expected:

- PASS

---

## Chunk 5: Add Feishu Cloud-Document Client Support

### Task 5: Extend the existing Feishu client with document create/write/permission APIs

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuCardKitClient.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardKitClient.cs`
- Add/modify tests around Feishu client request construction if present

- [ ] **Step 1: Add failing client request-shape tests**

Prove that the client sends the correct requests for:

- document create
- body write
- tenant-readable permission patch

- [ ] **Step 2: Run the focused client tests and confirm they fail**

Run the relevant Feishu client test subset if available.

- [ ] **Step 3: Implement document API methods in the existing client**

Reuse:

- tenant token resolution
- options override resolution
- business-success checking

- [ ] **Step 4: Add URL construction helper**

Return a document URL suitable for chat-link delivery.

- [ ] **Step 5: Run focused client tests and verify they pass**

Expected:

- PASS

---

## Chunk 6: Replace TTS Processing with Document Processing

### Task 6: Implement full/final document generation in the renamed orchestrator

**Files:**
- Modify renamed orchestrator and request tests
- Modify: `WebCodeCli.Domain/Domain/Service/ExternalCliSessionHistoryService.cs` if fallback naming needs cleanup
- Modify or replace orchestrator tests

- [ ] **Step 1: Write failing document-behavior tests**

Add tests that prove:

- both toggles off => no document work
- only full document on => one full document + one link message
- only final document on => one final document + one link message
- both toggles on => two documents + two link messages
- empty final content => final document skipped silently
- missing `CliThreadId` => title falls back to `SessionId`
- title suffixes are distinct
- partial failure in one branch does not block the other branch

- [ ] **Step 2: Run the focused orchestrator tests and confirm they fail**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyDoc|FullyQualifiedName~Orchestrator"
```

Expected:

- FAIL because document behavior is not implemented yet

- [ ] **Step 3: Remove all TTS chunk/audio processing from the orchestrator**

Delete:

- normalization/chunking/audio/temp-file logic
- TTS retry/failure-notice behavior

- [ ] **Step 4: Implement document generation branches**

For each enabled branch:

1. resolve body text
2. create document
3. write body
4. set tenant-readable permission
5. send text link message

- [ ] **Step 5: Keep Codex-only final-answer fallback for final document**

Reuse the current non-heuristic `final_answer` extraction path.

- [ ] **Step 6: Run focused orchestrator tests and verify they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyDoc|FullyQualifiedName~Orchestrator|FullyQualifiedName~ExternalCliSessionHistoryServiceTests"
```

Expected:

- PASS

---

## Chunk 7: Delete TTS-Only Runtime and Packaging Code

### Task 7: Remove TTS-only services, options, and installer bundling

**Files:**
- Delete TTS-only runtime files under `WebCodeCli.Domain/Domain/Service/Channels/`
- Modify package/installer scripts such as `tools/build-windows-installer.ps1`
- Remove TTS-only tests

- [ ] **Step 1: Identify all remaining compile/runtime references to deleted TTS code**

Use ripgrep before deleting to avoid orphaned registrations or options usage.

- [ ] **Step 2: Delete TTS-only runtime files and DI registrations**

Remove code that no longer has any document-era consumer.

- [ ] **Step 3: Remove installer/package TTS bundling**

Delete asset copy, bundled runtime, and TTS-specific release-note assumptions.

- [ ] **Step 4: Remove or rewrite TTS-only tests**

Keep only tests that still validate reused completed-reply document behavior.

- [ ] **Step 5: Run solution build to catch dangling references**

Run:

```powershell
dotnet build D:\VSWorkshop\WebCode\WebCodeCli.sln -c Debug
```

Expected:

- PASS

---

## Chunk 8: Final Verification

### Task 8: Run focused regression suites and final build

**Files:**
- No new files; verification only

- [ ] **Step 1: Run focused domain document suites**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuChannelServiceTests|FullyQualifiedName~FeishuCardActionServiceTests|FullyQualifiedName~ReplyDoc|FullyQualifiedName~Orchestrator|FullyQualifiedName~ExternalCliSessionHistoryServiceTests|FullyQualifiedName~FeishuHelpCardBuilderTests"
```

Expected:

- PASS

- [ ] **Step 2: Run focused web/admin suites**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~AdminController|FullyQualifiedName~AdminUserManagement"
```

Expected:

- PASS

- [ ] **Step 3: Run final solution build**

Run:

```powershell
dotnet build D:\VSWorkshop\WebCode\WebCodeCli.sln -c Debug
```

Expected:

- PASS

- [ ] **Step 4: Update agent notes with notable implementation findings**

Record any confirmed Feishu document permission, title, or fallback constraints in:

- `docs/agent-notes/2026-05-28.md`

