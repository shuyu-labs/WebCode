# Feishu Reply Document Folder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Place generated Feishu reply documents into a folder named from the current session title, falling back to CLI thread id and then session id.

**Architecture:** Extend the reply-document orchestration flow so it resolves a folder name from session metadata, asks the Feishu client to ensure that folder exists, then moves the newly created document into it. Keep document title generation unchanged and isolate Feishu Drive API details inside the Feishu client abstraction.

**Tech Stack:** C#, xUnit, existing `ReplyDocumentOrchestrator`, `IFeishuCardKitClient`, Feishu Open API HTTP client.

---

### Task 1: Add orchestrator regression coverage for folder naming and placement

**Files:**
- Modify: `WebCodeCli.Domain.Tests/ReplyDocumentOrchestratorTests.cs`

- [ ] **Step 1: Write the failing test for session-title folder placement**

Add a test that queues one full-reply document with:

- `SessionId = "session-folder-title"`
- `CliThreadId = "thread-folder-title"`
- session repository title = `"事务边界"`

Assert:

- one document is created,
- one folder ensure call is recorded with `"事务边界"`,
- one document move call is recorded for the created document into the ensured folder.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test WebCodeCli.Domain.Tests --filter "QueueCompletedReplyAsync_WhenSessionTitlePresent_UsesSessionTitleAsFolderName"`

Expected: FAIL because the stub Feishu client has no folder-placement behavior yet.

- [ ] **Step 3: Write the failing test for unnamed-title fallback**

Add a test with session title `"未命名"` and `CliThreadId = "thread-fallback-folder"`.

Assert the ensured folder name is `"thread-fallback-folder"`.

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test WebCodeCli.Domain.Tests --filter "QueueCompletedReplyAsync_WhenSessionTitleIsUnnamed_FallsBackToCliThreadIdForFolder"`

Expected: FAIL because fallback naming is not implemented yet.

- [ ] **Step 5: Write the failing test for missing-thread fallback**

Add a test with blank session title and blank `CliThreadId`.

Assert the ensured folder name is the `SessionId`.

- [ ] **Step 6: Run test to verify it fails**

Run: `dotnet test WebCodeCli.Domain.Tests --filter "QueueCompletedReplyAsync_WhenTitleAndThreadMissing_FallsBackToSessionIdForFolder"`

Expected: FAIL because session-id fallback is not implemented yet.

### Task 2: Extend the Feishu client abstraction for folder placement

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuCardKitClient.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardKitClient.cs`
- Modify: `WebCodeCli.Domain.Tests/ReplyDocumentOrchestratorTests.cs`
- Modify: `WebCodeCli.Domain.Tests/FeishuCardKitClientTests.cs`

- [ ] **Step 1: Write the failing client-level tests**

Add focused `FeishuCardKitClientTests` for:

- ensuring a folder by name returns a folder token/id when the API responds successfully,
- moving a document into a folder posts the expected request and succeeds.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test WebCodeCli.Domain.Tests --filter "EnsureCloudFolderAsync|MoveCloudDocumentToFolderAsync"`

Expected: FAIL because the interface and implementation do not exist yet.

- [ ] **Step 3: Add minimal interface methods**

Extend `IFeishuCardKitClient` with methods equivalent to:

```csharp
Task<string> EnsureCloudFolderAsync(
    string folderName,
    CancellationToken cancellationToken = default,
    FeishuOptions? optionsOverride = null);

Task MoveCloudDocumentToFolderAsync(
    string documentId,
    string folderToken,
    CancellationToken cancellationToken = default,
    FeishuOptions? optionsOverride = null);
```

- [ ] **Step 4: Implement the Feishu client methods**

In `FeishuCardKitClient.cs`, add the minimal HTTP flow needed to:

- search or create a folder by name,
- move the document into that folder.

Keep all HTTP payloads inside the client implementation and reuse existing token/error helpers.

- [ ] **Step 5: Update test stubs to satisfy the interface**

Add no-op or recording implementations for the new methods in the test stubs used by reply-document tests.

- [ ] **Step 6: Run the focused client tests**

Run: `dotnet test WebCodeCli.Domain.Tests --filter "EnsureCloudFolderAsync|MoveCloudDocumentToFolderAsync"`

Expected: PASS

### Task 3: Implement folder-name resolution in ReplyDocumentOrchestrator

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/ReplyDocumentOrchestrator.cs`
- Modify: `WebCodeCli.Domain.Tests/ReplyDocumentOrchestratorTests.cs`

- [ ] **Step 1: Add helper methods for folder-name resolution**

In `ReplyDocumentOrchestrator.cs`, add focused helpers to:

- load the session when available,
- resolve the preferred folder source,
- sanitize the folder name,
- detect unnamed titles such as `未命名`.

- [ ] **Step 2: Add minimal orchestration changes**

Update `TryCreateAndSendDocumentAsync(...)` so it:

- resolves the folder name before or during document creation,
- creates the document,
- ensures the folder when the folder name is non-blank,
- moves the document into that folder,
- continues with append/permission/send steps.

- [ ] **Step 3: Keep document title logic unchanged**

Do not modify `BuildTitlePrefix(...)`, `TruncateTitle(...)`, or the full/final/audio suffix logic.

- [ ] **Step 4: Run the reply-document naming tests**

Run: `dotnet test WebCodeCli.Domain.Tests --filter "QueueCompletedReplyAsync_WhenSessionTitlePresent_UsesSessionTitleAsFolderName|QueueCompletedReplyAsync_WhenSessionTitleIsUnnamed_FallsBackToCliThreadIdForFolder|QueueCompletedReplyAsync_WhenTitleAndThreadMissing_FallsBackToSessionIdForFolder"`

Expected: PASS

### Task 4: Verify existing reply-document behavior stays intact

**Files:**
- Modify: `docs/agent-notes/2026-06-02.md`

- [ ] **Step 1: Run existing focused reply-document regression coverage**

Run: `dotnet test WebCodeCli.Domain.Tests --filter "QueueCompletedReplyAsync_WhenFullReplyDocumentEnabled_CreatesOneDocumentAndSendsLink|QueueCompletedReplyAsync_WhenFinalReplyDocumentEnabled_UsesLiveFinalAnswerOnly|QueueCompletedReplyAsync_WhenBothReplyDocumentsEnabled_CreatesTwoDocuments"`

Expected: PASS

- [ ] **Step 2: Record the implementation note**

Add a note to `docs/agent-notes/2026-06-02.md` covering:

- folder naming precedence,
- `未命名` fallback behavior,
- where folder placement now happens.

- [ ] **Step 3: Commit**

```bash
git add WebCodeCli.Domain/Domain/Service/Channels/IFeishuCardKitClient.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuCardKitClient.cs WebCodeCli.Domain/Domain/Service/Channels/ReplyDocumentOrchestrator.cs WebCodeCli.Domain.Tests/FeishuCardKitClientTests.cs WebCodeCli.Domain.Tests/ReplyDocumentOrchestratorTests.cs docs/agent-notes/2026-06-02.md docs/superpowers/specs/2026-06-02-feishu-reply-document-folder-design.md docs/superpowers/plans/2026-06-02-feishu-reply-document-folder-implementation.md
git commit -m "feat: place Feishu reply documents into session folders"
```
