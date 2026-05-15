# Unified CLI Attachment Submission Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a shared attachment-aware message submission flow for Web, mobile, and Feishu that preserves session history, stages files in hidden workspace paths, prefers native CLI attachment support where available, and falls back to structured workspace references otherwise.

**Architecture:** Keep the existing shared session and streaming execution flow intact, but insert a new orchestration boundary between channel input and CLI execution. Split the work into seven units: persistent attachment-aware message history, staging and submission orchestration, CLI execution request translation, shared Web/mobile composer state, Web/mobile wiring, Feishu draft ingestion, and Feishu explicit submit plus regression docs.

**Tech Stack:** ASP.NET Core, Blazor Server, SqlSugar code-first entities, existing CLI adapter architecture, Feishu Open Platform callbacks/cards, xUnit test projects in `WebCodeCli.Domain.Tests` and `tests/WebCodeCli.Tests`.

Depends on:
- `docs/superpowers/specs/2026-05-14-unified-cli-attachment-submission-design.md`

---

## File Map

### Attachment-aware chat history

- Create: `WebCodeCli.Domain/Domain/Model/MessageAttachmentKind.cs`
  Shared enum for `image`, `text`, `pdf`, and `office`.
- Create: `WebCodeCli.Domain/Domain/Model/MessageAttachment.cs`
  Product-level attachment metadata stored on `ChatMessage`.
- Modify: `WebCodeCli.Domain/Domain/Model/ChatMessage.cs`
  Add `Attachments` to the existing message model.
- Modify: `WebCodeCli.Domain/Domain/Model/SessionHistory.cs`
  No shape change beyond reading/writing attachment-aware messages, but the task updates any comments and assumptions about `Messages`.
- Modify: `WebCodeCli.Domain/Repositories/Base/ChatSession/ChatMessageEntity.cs`
  Persist the logical message id alongside the existing database identity.
- Create: `WebCodeCli.Domain/Repositories/Base/ChatSession/ChatMessageAttachmentEntity.cs`
  New one-to-many persistence table for attachment metadata.
- Create: `WebCodeCli.Domain/Repositories/Base/ChatSession/IChatMessageAttachmentRepository.cs`
  Read/write abstraction for attachment rows.
- Create: `WebCodeCli.Domain/Repositories/Base/ChatSession/ChatMessageAttachmentRepository.cs`
  SqlSugar implementation.
- Modify: `WebCodeCli.Domain/Common/Extensions/DatabaseInitializer.cs`
  Initialize the new table and add the new `ChatMessage.MessageId` column and indexes.
- Modify: `WebCodeCli.Domain/Domain/Service/SessionHistoryManager.cs`
  Persist and hydrate attachment-aware session history.
- Modify: `WebCodeCli.Domain.Tests/SessionHistoryManagerTests.cs`
  Round-trip coverage for attachments and logical message ids.

### Draft, staging, and orchestration

- Create: `WebCodeCli.Domain/Domain/Model/MessageSubmissionChannel.cs`
  Shared channel enum for `Web`, `Mobile`, and `Feishu`.
- Create: `WebCodeCli.Domain/Domain/Model/MessageDraftAttachmentInput.cs`
  Raw attachment bytes and metadata received from a channel before staging.
- Create: `WebCodeCli.Domain/Domain/Model/MessageDraft.cs`
  Channel-agnostic input payload submitted by Web, mobile, or Feishu.
- Create: `WebCodeCli.Domain/Domain/Model/StagedMessageAttachment.cs`
  Internal result from staging: product metadata plus absolute file path.
- Create: `WebCodeCli.Domain/Domain/Model/MessageSubmissionWarning.cs`
  Non-fatal warnings such as partial downgrade.
- Create: `WebCodeCli.Domain/Domain/Model/PreparedMessageSubmission.cs`
  Orchestrated result handed to the caller: `ChatMessage`, `CliExecutionRequest`, warnings.
- Create: `WebCodeCli.Domain/Domain/Service/IAttachmentStagingService.cs`
  Contract for hidden workspace staging.
- Create: `WebCodeCli.Domain/Domain/Service/AttachmentStagingService.cs`
  Stages files under `.webcode/message-inputs/<submission-id>/`.
- Create: `WebCodeCli.Domain/Domain/Service/IMessageSubmissionService.cs`
  Contract for validating drafts, staging files, and building execution requests.
- Create: `WebCodeCli.Domain/Domain/Service/MessageSubmissionService.cs`
  Main orchestration service.
- Modify: `WebCodeCli.Domain/Common/Extensions/ServiceCollectionExtensions.cs`
  Register the new services and repositories.
- Create: `WebCodeCli.Domain.Tests/AttachmentStagingServiceTests.cs`
- Create: `WebCodeCli.Domain.Tests/MessageSubmissionServiceTests.cs`

### CLI execution boundary

- Create: `WebCodeCli.Domain/Domain/Service/Adapters/CliExecutionAttachment.cs`
  Execution-time attachment object with absolute path and kind.
- Create: `WebCodeCli.Domain/Domain/Service/Adapters/CliExecutionRequest.cs`
  Rich request object replacing prompt-only assumptions.
- Create: `WebCodeCli.Domain/Domain/Service/Adapters/CliAttachmentCapabilities.cs`
  Per-adapter native/reference attachment declaration.
- Modify: `WebCodeCli.Domain/Domain/Service/Adapters/ICliToolAdapter.cs`
  Replace prompt-only argument building with execution-request translation and capability declaration.
- Modify: `WebCodeCli.Domain/Domain/Service/Adapters/CodexAdapter.cs`
  Native image support plus structured reference preamble for fallback files.
- Modify: `WebCodeCli.Domain/Domain/Service/Adapters/OpenCodeAdapter.cs`
  Reference-only behavior in v1 unless native support is explicitly implemented.
- Modify: `WebCodeCli.Domain/Domain/Service/Adapters/ClaudeCodeAdapter.cs`
  Reference-only behavior in v1.
- Modify: `WebCodeCli.Domain/Domain/Service/ICliExecutorService.cs`
  Add `ExecuteStreamAsync(CliExecutionRequest request, ...)`.
- Modify: `WebCodeCli.Domain/Domain/Service/CliExecutorService.cs`
  Implement the new overload and keep the old overload as a wrapper.
- Create: `WebCodeCli.Domain.Tests/CliExecutionRequestAdapterTests.cs`
- Modify: `WebCodeCli.Domain.Tests/CliExecutorServiceTests.cs`

### Shared Web/mobile attachment composer

- Create: `WebCodeCli/Helpers/MessageAttachmentComposerState.cs`
  UI-only pending attachment list, removal, and reset logic that can be tested without bUnit.
- Create: `tests/WebCodeCli.Tests/MessageAttachmentComposerStateTests.cs`
- Modify: `WebCodeCli/Components/ChatInputPanel.razor`
  Add a message-scoped attachment picker and attachment chip display while keeping workspace upload intact.
- Modify: `WebCodeCli/Pages/CodeAssistant.razor.cs`
  Use `MessageSubmissionService` for Web sends.
- Modify: `WebCodeCli/Pages/CodeAssistantMobile.razor`
  Add mobile attachment picker/chips.
- Modify: `WebCodeCli/Pages/CodeAssistantMobile.razor.cs`
  Use `MessageSubmissionService` for mobile sends.

### Feishu explicit draft flow

- Create: `WebCodeCli.Domain/Domain/Model/Channels/FeishuIncomingAttachment.cs`
  Parsed attachment metadata from incoming Feishu image/file messages.
- Modify: `WebCodeCli.Domain/Domain/Model/Channels/FeishuIncomingMessage.cs`
  Carry incoming attachment metadata in addition to text content.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuAttachmentDraftService.cs`
  Draft-state API keyed by `appId/chatId/senderId`.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/FeishuAttachmentDraftState.cs`
  Explicit draft state: text, session/tool binding, staged attachments, card status.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/FeishuAttachmentDraftService.cs`
  In-memory draft store for v1.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/FeishuIncomingAttachmentParser.cs`
  Focused parser for incoming Feishu `image` and `file` payload JSON.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/FeishuAttachmentDraftCardBuilder.cs`
  Small dedicated builder for draft-management cards so attachment UI does not bloat `FeishuHelpCardBuilder`.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuMessageHandler.cs`
  Parse incoming image/file messages and pass structured attachments downstream.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuCardKitClient.cs`
  Add incoming attachment download methods.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardKitClient.cs`
  Implement attachment download with existing auth/http plumbing.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
  Add `open_attachment_draft`, `submit_attachment_draft`, `clear_attachment_draft`, and `remove_attachment_draft_item`.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs`
  Route open-draft text and attachment messages to the draft service; submit prepared drafts through the shared orchestration service.
- Create: `WebCodeCli.Domain.Tests/FeishuAttachmentDraftServiceTests.cs`
- Create: `WebCodeCli.Domain.Tests/FeishuIncomingAttachmentParserTests.cs`
- Modify: `WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs`
- Modify: `WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs`

### Docs and regression

- Modify: `README.md`
  Add a short note that sessions now support attachment-aware submission across Web, mobile, and Feishu.
- Modify: `README_EN.md`
  Mirror the same note in English.

### Explicit non-goals

- Do not add OCR.
- Do not expose hidden staging folders as normal workspace content.
- Do not build a retry queue or resumable attachment upload path.
- Do not add per-tool dynamic attachment picker restrictions in the UI in v1.

---

## Chunk 1: Persist Attachment-Aware Session History

### Task 1: Add attachment metadata models and make session history round-trip them

**Files:**
- Create: `WebCodeCli.Domain/Domain/Model/MessageAttachmentKind.cs`
- Create: `WebCodeCli.Domain/Domain/Model/MessageAttachment.cs`
- Modify: `WebCodeCli.Domain/Domain/Model/ChatMessage.cs`
- Modify: `WebCodeCli.Domain/Repositories/Base/ChatSession/ChatMessageEntity.cs`
- Create: `WebCodeCli.Domain/Repositories/Base/ChatSession/ChatMessageAttachmentEntity.cs`
- Create: `WebCodeCli.Domain/Repositories/Base/ChatSession/IChatMessageAttachmentRepository.cs`
- Create: `WebCodeCli.Domain/Repositories/Base/ChatSession/ChatMessageAttachmentRepository.cs`
- Modify: `WebCodeCli.Domain/Common/Extensions/DatabaseInitializer.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/SessionHistoryManager.cs`
- Test: `WebCodeCli.Domain.Tests/SessionHistoryManagerTests.cs`

- [ ] **Step 1: Write the failing round-trip test first**

Add this test to `WebCodeCli.Domain.Tests/SessionHistoryManagerTests.cs`:

```csharp
[Fact]
public async Task SaveSessionImmediateAsync_RoundTripsMessageAttachments()
{
    var manager = CreateManager();
    var session = new SessionHistory
    {
        SessionId = "session-attachments",
        Title = "Attachment session",
        WorkspacePath = @"D:\VSWorkshop\WebCode\artifacts\session-attachments",
        ToolId = "codex",
        Messages =
        [
            new ChatMessage
            {
                Id = "msg-user-1",
                Role = "user",
                Content = "Review these files",
                Attachments =
                [
                    new MessageAttachment
                    {
                        Id = "att-image-1",
                        DisplayName = "diagram.png",
                        MimeType = "image/png",
                        Extension = ".png",
                        SizeBytes = 12,
                        Kind = MessageAttachmentKind.Image,
                        WorkspaceRelativePath = ".webcode/message-inputs/submission-1/diagram.png"
                    }
                ]
            }
        ]
    };

    await manager.SaveSessionImmediateAsync(session);
    manager.ClearCache();

    var reloaded = await manager.GetSessionAsync("session-attachments");

    var userMessage = Assert.Single(reloaded!.Messages);
    Assert.Equal("msg-user-1", userMessage.Id);
    var attachment = Assert.Single(userMessage.Attachments);
    Assert.Equal("att-image-1", attachment.Id);
    Assert.Equal(".webcode/message-inputs/submission-1/diagram.png", attachment.WorkspaceRelativePath);
}
```

- [ ] **Step 2: Run the focused test to confirm the current code fails**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~SessionHistoryManagerTests.SaveSessionImmediateAsync_RoundTripsMessageAttachments"
```

Expected:

- FAIL because `ChatMessage` has no `Attachments`
- FAIL because the persistence layer has no attachment table or `MessageId` column

- [ ] **Step 3: Add the shared attachment model and the persistence entities**

Create `MessageAttachmentKind.cs`:

```csharp
namespace WebCodeCli.Domain.Domain.Model;

public enum MessageAttachmentKind
{
    Image,
    Text,
    Pdf,
    Office
}
```

Create `MessageAttachment.cs`:

```csharp
namespace WebCodeCli.Domain.Domain.Model;

public class MessageAttachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public string MimeType { get; set; } = "application/octet-stream";
    public string Extension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public MessageAttachmentKind Kind { get; set; }
    public string WorkspaceRelativePath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

Update `ChatMessage.cs`:

```csharp
public List<MessageAttachment> Attachments { get; set; } = new();
```

Update `ChatMessageEntity.cs`:

```csharp
[SugarColumn(Length = 64, IsNullable = false)]
public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
```

Create `ChatMessageAttachmentEntity.cs`:

```csharp
[SugarTable("ChatMessageAttachment")]
public class ChatMessageAttachmentEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string MessageId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string SessionId { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string Username { get; set; } = string.Empty;

    [SugarColumn(Length = 256, IsNullable = false)]
    public string AttachmentId { get; set; } = string.Empty;

    [SugarColumn(Length = 256, IsNullable = false)]
    public string DisplayName { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string MimeType { get; set; } = "application/octet-stream";

    [SugarColumn(Length = 32, IsNullable = false)]
    public string Extension { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    [SugarColumn(Length = 32, IsNullable = false)]
    public string Kind { get; set; } = string.Empty;

    [SugarColumn(Length = 512, IsNullable = false)]
    public string WorkspaceRelativePath { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

- [ ] **Step 4: Wire repositories, database initialization, and session history mapping**

Add a repository contract and implementation with:

```csharp
Task<List<ChatMessageAttachmentEntity>> GetBySessionIdAndUsernameAsync(string sessionId, string username);
Task<bool> DeleteBySessionIdAndUsernameAsync(string sessionId, string username);
Task<bool> InsertAttachmentsAsync(List<ChatMessageAttachmentEntity> attachments);
```

In `DatabaseInitializer.cs`, initialize the new table and `MessageId` column:

```csharp
db.CodeFirst.InitTables<ChatMessageAttachmentEntity>();
EnsureColumnIfNotExists(db, "ChatMessage", "MessageId", "varchar(64) NOT NULL DEFAULT ''", logger);
CreateIndexIfNotExists(db, "ChatMessageAttachment", "IX_ChatMessageAttachment_MessageId", new[] { "MessageId" }, logger);
CreateIndexIfNotExists(db, "ChatMessageAttachment", "IX_ChatMessageAttachment_SessionId", new[] { "SessionId" }, logger);
```

In `SessionHistoryManager.cs`, map message ids and attachments:

```csharp
private ChatMessageEntity MapToMessageEntity(ChatMessage message, string sessionId, string username)
{
    return new ChatMessageEntity
    {
        MessageId = string.IsNullOrWhiteSpace(message.Id) ? Guid.NewGuid().ToString("N") : message.Id,
        SessionId = sessionId,
        Username = username,
        Role = message.Role,
        Content = message.Content,
        CreatedAt = message.CreatedAt
    };
}
```

```csharp
private static ChatMessageAttachmentEntity MapToAttachmentEntity(
    MessageAttachment attachment,
    string messageId,
    string sessionId,
    string username)
{
    return new ChatMessageAttachmentEntity
    {
        MessageId = messageId,
        SessionId = sessionId,
        Username = username,
        AttachmentId = attachment.Id,
        DisplayName = attachment.DisplayName,
        MimeType = attachment.MimeType,
        Extension = attachment.Extension,
        SizeBytes = attachment.SizeBytes,
        Kind = attachment.Kind.ToString(),
        WorkspaceRelativePath = attachment.WorkspaceRelativePath,
        CreatedAt = attachment.CreatedAt
    };
}
```

When loading a session, group attachments by `MessageId` and hydrate `ChatMessage.Attachments`.

- [ ] **Step 5: Run the focused test again and then the session-history suite**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~SessionHistoryManagerTests.SaveSessionImmediateAsync_RoundTripsMessageAttachments"
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~SessionHistoryManagerTests"
```

Expected:

- PASS for the new round-trip test
- PASS for the existing session-history tests after fixture updates for `MessageId`

- [ ] **Step 6: Commit**

```bash
git add WebCodeCli.Domain/Domain/Model/MessageAttachmentKind.cs WebCodeCli.Domain/Domain/Model/MessageAttachment.cs WebCodeCli.Domain/Domain/Model/ChatMessage.cs WebCodeCli.Domain/Repositories/Base/ChatSession/ChatMessageEntity.cs WebCodeCli.Domain/Repositories/Base/ChatSession/ChatMessageAttachmentEntity.cs WebCodeCli.Domain/Repositories/Base/ChatSession/IChatMessageAttachmentRepository.cs WebCodeCli.Domain/Repositories/Base/ChatSession/ChatMessageAttachmentRepository.cs WebCodeCli.Domain/Common/Extensions/DatabaseInitializer.cs WebCodeCli.Domain/Domain/Service/SessionHistoryManager.cs WebCodeCli.Domain.Tests/SessionHistoryManagerTests.cs
git commit -m "feat: persist chat message attachments"
```

---

## Chunk 2: Build Draft Validation and Hidden Staging

### Task 2: Add message draft models, hidden staging, and submission orchestration

**Files:**
- Create: `WebCodeCli.Domain/Domain/Model/MessageSubmissionChannel.cs`
- Create: `WebCodeCli.Domain/Domain/Model/MessageDraftAttachmentInput.cs`
- Create: `WebCodeCli.Domain/Domain/Model/MessageDraft.cs`
- Create: `WebCodeCli.Domain/Domain/Model/StagedMessageAttachment.cs`
- Create: `WebCodeCli.Domain/Domain/Model/MessageSubmissionWarning.cs`
- Create: `WebCodeCli.Domain/Domain/Model/PreparedMessageSubmission.cs`
- Create: `WebCodeCli.Domain/Domain/Service/IAttachmentStagingService.cs`
- Create: `WebCodeCli.Domain/Domain/Service/AttachmentStagingService.cs`
- Create: `WebCodeCli.Domain/Domain/Service/IMessageSubmissionService.cs`
- Create: `WebCodeCli.Domain/Domain/Service/MessageSubmissionService.cs`
- Modify: `WebCodeCli.Domain/Common/Extensions/ServiceCollectionExtensions.cs`
- Test: `WebCodeCli.Domain.Tests/AttachmentStagingServiceTests.cs`
- Test: `WebCodeCli.Domain.Tests/MessageSubmissionServiceTests.cs`

- [ ] **Step 1: Write failing staging and orchestration tests**

Add `AttachmentStagingServiceTests.cs`:

```csharp
[Fact]
public async Task StageAsync_WritesFilesUnderHiddenSubmissionDirectory()
{
    var workspaceRoot = CreateWorkspaceRoot();
    var service = new AttachmentStagingService(new StubCliExecutorService(workspaceRoot));

    var staged = await service.StageAsync(
        sessionId: "session-1",
        submissionId: "submission-123",
        attachments:
        [
            new MessageDraftAttachmentInput
            {
                FileName = "diagram.png",
                ContentType = "image/png",
                Content = [1, 2, 3]
            }
        ],
        CancellationToken.None);

    var attachment = Assert.Single(staged);
    Assert.Equal(".webcode/message-inputs/submission-123/diagram.png", attachment.Metadata.WorkspaceRelativePath);
    Assert.True(File.Exists(attachment.AbsolutePath));
}

private sealed class StubCliExecutorService : ICliExecutorService
{
    private readonly string _workspacePath;

    public StubCliExecutorService(string workspacePath)
    {
        _workspacePath = workspacePath;
        Directory.CreateDirectory(_workspacePath);
    }

    public string GetSessionWorkspacePath(string sessionId) => _workspacePath;

    // All other members throw NotSupportedException in this test file.
}
```

Add `MessageSubmissionServiceTests.cs`:

```csharp
[Fact]
public async Task PrepareAsync_WithEmptyTextAndAttachments_ThrowsValidationError()
{
    var service = CreateSubmissionService(capabilities: CliAttachmentCapabilities.ForNativeKinds(MessageAttachmentKind.Image));

    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(
        new MessageDraft
        {
            SessionId = "session-1",
            ToolId = "codex",
            Channel = MessageSubmissionChannel.Web,
            Text = "   ",
            Attachments =
            [
                new MessageDraftAttachmentInput
                {
                    FileName = "diagram.png",
                    ContentType = "image/png",
                    Content = [1, 2, 3]
                }
            ]
        },
        CancellationToken.None));

    Assert.Contains("required", error.Message, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public async Task PrepareAsync_WithTextAndMixedAttachments_BuildsUserMessageAndDowngradeWarning()
{
    var service = CreateSubmissionService(capabilities: CliAttachmentCapabilities.ForNativeKinds(MessageAttachmentKind.Image));

    var prepared = await service.PrepareAsync(
        new MessageDraft
        {
            SessionId = "session-1",
            ToolId = "codex",
            Channel = MessageSubmissionChannel.Web,
            Text = "Review all attached files",
            Attachments =
            [
                new MessageDraftAttachmentInput
                {
                    FileName = "diagram.png",
                    ContentType = "image/png",
                    Content = [1, 2, 3]
                },
                new MessageDraftAttachmentInput
                {
                    FileName = "spec.pdf",
                    ContentType = "application/pdf",
                    Content = [4, 5, 6]
                }
            ]
        },
        CancellationToken.None);

    Assert.Equal("Review all attached files", prepared.UserMessage.Content);
    Assert.Equal(2, prepared.UserMessage.Attachments.Count);
    Assert.Single(prepared.ExecutionRequest.NativeAttachments);
    Assert.Single(prepared.ExecutionRequest.ReferenceAttachments);
    Assert.Contains(prepared.Warnings, w => w.Code == "partial-downgrade");
}

private static MessageSubmissionService CreateSubmissionService(CliAttachmentCapabilities capabilities)
{
    var workspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(workspaceRoot, "session-1"));

    var cliExecutor = new StubCliExecutorService(Path.Combine(workspaceRoot, "session-1"));
    var adapter = new StubAttachmentAwareCliToolAdapter(capabilities);

    return new MessageSubmissionService(
        new AttachmentStagingService(cliExecutor),
        cliExecutor,
        new StubCliAdapterFactory(adapter));
}

private sealed class StubAttachmentAwareCliToolAdapter : ICliToolAdapter
{
    private readonly CliAttachmentCapabilities _capabilities;

    public StubAttachmentAwareCliToolAdapter(CliAttachmentCapabilities capabilities)
    {
        _capabilities = capabilities;
    }

    public string[] SupportedToolIds => ["codex"];
    public bool SupportsStreamParsing => false;
    public bool CanHandle(CliToolConfig tool) => true;
    public CliAttachmentCapabilities GetAttachmentCapabilities(CliToolConfig tool) => _capabilities;
    public string BuildArguments(CliToolConfig tool, CliExecutionRequest request) => request.PromptText;
    public string BuildLowInterruptionArguments(CliToolConfig tool, CliSessionContext context) => string.Empty;
    public CliOutputEvent? ParseOutputLine(string line) => null;
    public string? ExtractSessionId(CliOutputEvent outputEvent) => null;
    public string? ExtractAssistantMessage(CliOutputEvent outputEvent) => null;
    public string GetEventTitle(CliOutputEvent outputEvent) => string.Empty;
    public string GetEventBadgeClass(CliOutputEvent outputEvent) => string.Empty;
    public string GetEventBadgeLabel(CliOutputEvent outputEvent) => string.Empty;
}

private sealed class StubCliAdapterFactory : ICliAdapterFactory
{
    private readonly ICliToolAdapter _adapter;

    public StubCliAdapterFactory(ICliToolAdapter adapter)
    {
        _adapter = adapter;
    }

    public ICliToolAdapter? GetAdapter(CliToolConfig tool) => _adapter;
}
```

- [ ] **Step 2: Run the new tests to confirm both fail**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~AttachmentStagingServiceTests|FullyQualifiedName~MessageSubmissionServiceTests"
```

Expected:

- FAIL because none of the draft/staging/orchestration types exist

- [ ] **Step 3: Implement the draft and orchestration models**

Create the shared input/output models:

```csharp
public enum MessageSubmissionChannel
{
    Web,
    Mobile,
    Feishu
}
```

```csharp
public class MessageDraftAttachmentInput
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] Content { get; set; } = [];
}
```

```csharp
public class MessageDraft
{
    public string DraftId { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public MessageSubmissionChannel Channel { get; set; }
    public string Text { get; set; } = string.Empty;
    public List<MessageDraftAttachmentInput> Attachments { get; set; } = new();
    public string? SubmittedBy { get; set; }
}
```

```csharp
public class MessageSubmissionWarning
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
```

```csharp
public class PreparedMessageSubmission
{
    public ChatMessage UserMessage { get; set; } = new();
    public CliExecutionRequest ExecutionRequest { get; set; } = new();
    public List<MessageSubmissionWarning> Warnings { get; set; } = new();
}
```

- [ ] **Step 4: Implement hidden staging under `.webcode/message-inputs/<submission-id>/`**

Create `AttachmentStagingService.cs` with this core behavior:

```csharp
public async Task<List<StagedMessageAttachment>> StageAsync(
    string sessionId,
    string submissionId,
    IReadOnlyList<MessageDraftAttachmentInput> attachments,
    CancellationToken cancellationToken)
{
    var workspaceRoot = _cliExecutorService.GetSessionWorkspacePath(sessionId);
    var relativeRoot = Path.Combine(".webcode", "message-inputs", submissionId).Replace("\\", "/");
    var absoluteRoot = Path.Combine(workspaceRoot, ".webcode", "message-inputs", submissionId);
    Directory.CreateDirectory(absoluteRoot);

    var staged = new List<StagedMessageAttachment>();
    foreach (var attachment in attachments)
    {
        var normalizedFileName = NormalizeFileName(attachment.FileName);
        var absolutePath = Path.Combine(absoluteRoot, normalizedFileName);
        await File.WriteAllBytesAsync(absolutePath, attachment.Content, cancellationToken);

        staged.Add(new StagedMessageAttachment
        {
            AbsolutePath = absolutePath,
            Metadata = new MessageAttachment
            {
                DisplayName = attachment.FileName,
                MimeType = attachment.ContentType,
                Extension = Path.GetExtension(normalizedFileName),
                SizeBytes = attachment.Content.LongLength,
                Kind = ResolveKind(attachment.FileName, attachment.ContentType),
                WorkspaceRelativePath = $"{relativeRoot}/{normalizedFileName}".Replace("\\", "/")
            }
        });
    }

    return staged;
}
```

- [ ] **Step 5: Implement `MessageSubmissionService` validation and prepared-result building**

Create `MessageSubmissionService.cs` with this flow:

```csharp
public async Task<PreparedMessageSubmission> PrepareAsync(MessageDraft draft, CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(draft.Text))
    {
        throw new InvalidOperationException("Message text is required when attachments are present.");
    }

    var stagedAttachments = await _attachmentStagingService.StageAsync(
        draft.SessionId,
        draft.DraftId,
        draft.Attachments,
        cancellationToken);

    var capabilities = ResolveCapabilities(draft.ToolId);
    var request = BuildExecutionRequest(draft, stagedAttachments, capabilities, out var warnings);

    return new PreparedMessageSubmission
    {
        UserMessage = new ChatMessage
        {
            Role = "user",
            Content = draft.Text.Trim(),
            CliToolId = draft.ToolId,
            IsCompleted = true,
            CreatedAt = DateTime.UtcNow,
            Attachments = stagedAttachments.Select(x => x.Metadata).ToList()
        },
        ExecutionRequest = request,
        Warnings = warnings
    };
}
```

`BuildExecutionRequest` must:

- keep image attachments native only if the adapter says so
- put all other supported whitelist files in `ReferenceAttachments`
- emit a warning with code `partial-downgrade` when both groups are non-empty

- [ ] **Step 6: Register the new services and run the focused suites**

Register:

```csharp
services.AddScoped<IAttachmentStagingService, AttachmentStagingService>();
services.AddScoped<IMessageSubmissionService, MessageSubmissionService>();
services.AddScoped<IChatMessageAttachmentRepository, ChatMessageAttachmentRepository>();
```

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~AttachmentStagingServiceTests|FullyQualifiedName~MessageSubmissionServiceTests"
```

Expected:

- PASS for both new test files

- [ ] **Step 7: Commit**

```bash
git add WebCodeCli.Domain/Domain/Model/MessageSubmissionChannel.cs WebCodeCli.Domain/Domain/Model/MessageDraftAttachmentInput.cs WebCodeCli.Domain/Domain/Model/MessageDraft.cs WebCodeCli.Domain/Domain/Model/StagedMessageAttachment.cs WebCodeCli.Domain/Domain/Model/MessageSubmissionWarning.cs WebCodeCli.Domain/Domain/Model/PreparedMessageSubmission.cs WebCodeCli.Domain/Domain/Service/IAttachmentStagingService.cs WebCodeCli.Domain/Domain/Service/AttachmentStagingService.cs WebCodeCli.Domain/Domain/Service/IMessageSubmissionService.cs WebCodeCli.Domain/Domain/Service/MessageSubmissionService.cs WebCodeCli.Domain/Common/Extensions/ServiceCollectionExtensions.cs WebCodeCli.Domain.Tests/AttachmentStagingServiceTests.cs WebCodeCli.Domain.Tests/MessageSubmissionServiceTests.cs
git commit -m "feat: add attachment submission orchestration"
```

---

## Chunk 3: Expand the CLI Execution Boundary

### Task 3: Add execution-request translation and adapter attachment capabilities

**Files:**
- Create: `WebCodeCli.Domain/Domain/Service/Adapters/CliExecutionAttachment.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Adapters/CliExecutionRequest.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Adapters/CliAttachmentCapabilities.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Adapters/ICliToolAdapter.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Adapters/CodexAdapter.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Adapters/OpenCodeAdapter.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Adapters/ClaudeCodeAdapter.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/ICliExecutorService.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/CliExecutorService.cs`
- Test: `WebCodeCli.Domain.Tests/CliExecutionRequestAdapterTests.cs`
- Test: `WebCodeCli.Domain.Tests/CliExecutorServiceTests.cs`

- [ ] **Step 1: Write the adapter-translation tests first**

Create `CliExecutionRequestAdapterTests.cs`:

```csharp
[Fact]
public void CodexAdapter_BuildArguments_AddsNativeImageFlags_AndReferencePreamble()
{
    var tool = new CliToolConfig { Id = "codex", Command = "codex" };
    var adapter = new CodexAdapter();
    var request = new CliExecutionRequest
    {
        SessionId = "session-1",
        ToolId = "codex",
        PromptText = "Review everything",
        SessionContext = new CliSessionContext { SessionId = "session-1", WorkingDirectory = @"D:\repo" },
        NativeAttachments =
        [
            new CliExecutionAttachment
            {
                DisplayName = "diagram.png",
                Kind = MessageAttachmentKind.Image,
                WorkspaceRelativePath = ".webcode/message-inputs/sub-1/diagram.png",
                AbsolutePath = @"D:\repo\.webcode\message-inputs\sub-1\diagram.png"
            }
        ],
        ReferenceAttachments =
        [
            new CliExecutionAttachment
            {
                DisplayName = "spec.pdf",
                Kind = MessageAttachmentKind.Pdf,
                WorkspaceRelativePath = ".webcode/message-inputs/sub-1/spec.pdf",
                AbsolutePath = @"D:\repo\.webcode\message-inputs\sub-1\spec.pdf"
            }
        ]
    };

    var args = adapter.BuildArguments(tool, request);

    Assert.Contains("-i", args);
    Assert.Contains("diagram.png", args);
    Assert.Contains("Attached files in workspace:", args);
    Assert.Contains(".webcode/message-inputs/sub-1/spec.pdf", args);
}
```

Add a `CliExecutorServiceTests` capture test:

```csharp
[Fact]
public async Task ExecuteStreamAsync_RequestOverload_PassesExecutionRequestToAdapter()
{
    var adapter = new RecordingCliToolAdapter();
    var service = CreateCliExecutorService(adapter);

    var request = new CliExecutionRequest
    {
        SessionId = "session-1",
        ToolId = "recording-tool",
        PromptText = "Explain the file",
        SessionContext = new CliSessionContext
        {
            SessionId = "session-1",
            WorkingDirectory = @"D:\repo"
        },
        ReferenceAttachments =
        [
            new CliExecutionAttachment
            {
                DisplayName = "notes.txt",
                Kind = MessageAttachmentKind.Text,
                WorkspaceRelativePath = ".webcode/message-inputs/sub-1/notes.txt",
                AbsolutePath = @"D:\repo\.webcode\message-inputs\sub-1\notes.txt"
            }
        ]
    };

    await foreach (var _ in service.ExecuteStreamAsync(request, CancellationToken.None))
    {
    }

    Assert.NotNull(adapter.LastRequest);
    Assert.Single(adapter.LastRequest!.ReferenceAttachments);
}

private sealed class RecordingCliToolAdapter : ICliToolAdapter
{
    public CliExecutionRequest? LastRequest { get; private set; }

    public string[] SupportedToolIds => ["recording-tool"];
    public bool SupportsStreamParsing => false;
    public bool CanHandle(CliToolConfig tool) => tool.Id == "recording-tool";
    public CliAttachmentCapabilities GetAttachmentCapabilities(CliToolConfig tool) => CliAttachmentCapabilities.ReferenceOnly();
    public string BuildArguments(CliToolConfig tool, CliExecutionRequest request)
    {
        LastRequest = request;
        return "\"captured\"";
    }
    public string BuildLowInterruptionArguments(CliToolConfig tool, CliSessionContext context) => string.Empty;
    public CliOutputEvent? ParseOutputLine(string line) => null;
    public string? ExtractSessionId(CliOutputEvent outputEvent) => null;
    public string? ExtractAssistantMessage(CliOutputEvent outputEvent) => null;
    public string GetEventTitle(CliOutputEvent outputEvent) => string.Empty;
    public string GetEventBadgeClass(CliOutputEvent outputEvent) => string.Empty;
    public string GetEventBadgeLabel(CliOutputEvent outputEvent) => string.Empty;
}

private static CliExecutorService CreateCliExecutorService(ICliToolAdapter adapter)
{
    return new CliExecutorService(
        NullLogger<CliExecutorService>.Instance,
        Options.Create(new CliToolsOption
        {
            TempWorkspaceRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N")),
            Tools =
            [
                new CliToolConfig
                {
                    Id = "recording-tool",
                    Name = "Recording Tool",
                    Command = "cmd.exe",
                    ArgumentTemplate = "{prompt}"
                }
            ]
        }),
        NullLogger<PersistentProcessManager>.Instance,
        new NullServiceProvider(new StubChatSessionRepository(), new StubSessionOutputService()),
        new StubChatSessionService(),
        new StubCliAdapterFactory(adapter),
        new StubCcSwitchService());
}
```

Reuse the existing `NullServiceProvider`, `StubChatSessionRepository`, `StubSessionOutputService`, `StubChatSessionService`, and `StubCcSwitchService` helper types already present in `WebCodeCli.Domain.Tests/CliExecutorServiceTests.cs`.

- [ ] **Step 2: Run the focused execution tests to verify failure**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~CliExecutionRequestAdapterTests|FullyQualifiedName~CliExecutorServiceTests.ExecuteStreamAsync_RequestOverload_PassesExecutionRequestToAdapter"
```

Expected:

- FAIL because `CliExecutionRequest`, `CliExecutionAttachment`, and capability-aware adapter methods do not exist

- [ ] **Step 3: Add the new execution request and capability types**

Create `CliExecutionAttachment.cs`:

```csharp
namespace WebCodeCli.Domain.Domain.Service.Adapters;

public class CliExecutionAttachment
{
    public string DisplayName { get; set; } = string.Empty;
    public MessageAttachmentKind Kind { get; set; }
    public string WorkspaceRelativePath { get; set; } = string.Empty;
    public string AbsolutePath { get; set; } = string.Empty;
}
```

Create `CliAttachmentCapabilities.cs`:

```csharp
public class CliAttachmentCapabilities
{
    public bool SupportsNativeAttachments { get; init; }
    public bool SupportsMultipleNativeAttachments { get; init; }
    public HashSet<MessageAttachmentKind> NativeKinds { get; init; } = new();
    public bool AllowsReferenceFallback { get; init; } = true;

    public static CliAttachmentCapabilities ReferenceOnly() => new();

    public static CliAttachmentCapabilities ForNativeKinds(params MessageAttachmentKind[] kinds) =>
        new()
        {
            SupportsNativeAttachments = kinds.Length > 0,
            SupportsMultipleNativeAttachments = true,
            NativeKinds = new HashSet<MessageAttachmentKind>(kinds)
        };
}
```

Create `CliExecutionRequest.cs`:

```csharp
public class CliExecutionRequest
{
    public string SessionId { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public string PromptText { get; set; } = string.Empty;
    public CliSessionContext SessionContext { get; set; } = new();
    public List<CliExecutionAttachment> NativeAttachments { get; set; } = new();
    public List<CliExecutionAttachment> ReferenceAttachments { get; set; } = new();
}
```

- [ ] **Step 4: Update the adapter interface and the Codex adapter**

Change `ICliToolAdapter.cs`:

```csharp
CliAttachmentCapabilities GetAttachmentCapabilities(CliToolConfig tool);
string BuildArguments(CliToolConfig tool, CliExecutionRequest request);
```

In `CodexAdapter.cs`:

```csharp
public CliAttachmentCapabilities GetAttachmentCapabilities(CliToolConfig tool) =>
    CliAttachmentCapabilities.ForNativeKinds(MessageAttachmentKind.Image);
```

```csharp
public string BuildArguments(CliToolConfig tool, CliExecutionRequest request)
{
    var prompt = AppendReferencePreamble(request.PromptText, request.ReferenceAttachments);
    var escapedPrompt = EscapeJsonString(prompt);
    var nativeArgs = string.Join(" ",
        request.NativeAttachments.Select(x => $"-i {EscapeShellArgument(x.AbsolutePath)}"));
    var sessionArg = request.SessionContext.IsResume
        ? $"resume {request.SessionContext.CliThreadId}"
        : string.Empty;

    var template = !string.IsNullOrWhiteSpace(tool.ArgumentTemplate)
        ? tool.ArgumentTemplate
        : DefaultArgumentTemplate;

    return NormalizeArguments(template
        .Replace("{prompt}", escapedPrompt)
        .Replace("{session}", sessionArg)
        .Replace("{attachments}", nativeArgs));
}
```

For `ClaudeCodeAdapter` and `OpenCodeAdapter`, return `ReferenceOnly()` in v1 and prepend the structured reference block to `PromptText` before escaping.

- [ ] **Step 5: Add the new executor overload and keep the old overload as a wrapper**

In `ICliExecutorService.cs` add:

```csharp
IAsyncEnumerable<StreamOutputChunk> ExecuteStreamAsync(
    CliExecutionRequest request,
    CancellationToken cancellationToken = default);
```

In `CliExecutorService.cs`, make the old overload build a request and delegate:

```csharp
public IAsyncEnumerable<StreamOutputChunk> ExecuteStreamAsync(
    string sessionId,
    string toolId,
    string userPrompt,
    CancellationToken cancellationToken = default)
{
    return ExecuteStreamAsync(
        new CliExecutionRequest
        {
            SessionId = sessionId,
            ToolId = toolId,
            PromptText = userPrompt
        },
        cancellationToken);
}
```

The new overload should resolve `CliSessionContext`, call `adapter.BuildArguments(tool, request)`, and leave the stream-processing logic otherwise unchanged.

- [ ] **Step 6: Run the focused execution tests and then the broader executor suite**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~CliExecutionRequestAdapterTests|FullyQualifiedName~CliExecutorServiceTests.ExecuteStreamAsync_RequestOverload_PassesExecutionRequestToAdapter"
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~CliExecutorServiceTests"
```

Expected:

- PASS for the new adapter and executor tests
- PASS for the existing executor tests after stub updates to the new adapter interface

- [ ] **Step 7: Commit**

```bash
git add WebCodeCli.Domain/Domain/Service/Adapters/CliExecutionAttachment.cs WebCodeCli.Domain/Domain/Service/Adapters/CliExecutionRequest.cs WebCodeCli.Domain/Domain/Service/Adapters/CliAttachmentCapabilities.cs WebCodeCli.Domain/Domain/Service/Adapters/ICliToolAdapter.cs WebCodeCli.Domain/Domain/Service/Adapters/CodexAdapter.cs WebCodeCli.Domain/Domain/Service/Adapters/OpenCodeAdapter.cs WebCodeCli.Domain/Domain/Service/Adapters/ClaudeCodeAdapter.cs WebCodeCli.Domain/Domain/Service/ICliExecutorService.cs WebCodeCli.Domain/Domain/Service/CliExecutorService.cs WebCodeCli.Domain.Tests/CliExecutionRequestAdapterTests.cs WebCodeCli.Domain.Tests/CliExecutorServiceTests.cs
git commit -m "feat: add attachment-aware cli execution requests"
```

---

## Chunk 4: Wire Shared Web and Mobile Composer State

### Task 4: Add a shared pending-attachment composer and integrate Web/mobile send flows

**Files:**
- Create: `WebCodeCli/Helpers/MessageAttachmentComposerState.cs`
- Modify: `WebCodeCli/Components/ChatInputPanel.razor`
- Modify: `WebCodeCli/Pages/CodeAssistant.razor.cs`
- Modify: `WebCodeCli/Pages/CodeAssistantMobile.razor`
- Modify: `WebCodeCli/Pages/CodeAssistantMobile.razor.cs`
- Test: `tests/WebCodeCli.Tests/MessageAttachmentComposerStateTests.cs`

- [ ] **Step 1: Write the helper-state tests before touching UI**

Create `tests/WebCodeCli.Tests/MessageAttachmentComposerStateTests.cs`:

```csharp
[Fact]
public void ReplaceAndRemove_KeepsPendingAttachmentsInOrder()
{
    var state = new MessageAttachmentComposerState();

    state.Replace(
    [
        new MessageDraftAttachmentInput { Id = "att-1", FileName = "diagram.png", ContentType = "image/png", Content = [1] },
        new MessageDraftAttachmentInput { Id = "att-2", FileName = "notes.txt", ContentType = "text/plain", Content = [2] }
    ]);

    state.Remove("att-1");

    var remaining = Assert.Single(state.Attachments);
    Assert.Equal("att-2", remaining.Id);
}
```

```csharp
[Fact]
public void Clear_RemovesAllPendingAttachments()
{
    var state = new MessageAttachmentComposerState();
    state.Replace([new MessageDraftAttachmentInput { Id = "att-1", FileName = "diagram.png", ContentType = "image/png", Content = [1] }]);

    state.Clear();

    Assert.Empty(state.Attachments);
}
```

- [ ] **Step 2: Run the helper test to confirm it fails**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~MessageAttachmentComposerStateTests"
```

Expected:

- FAIL because `MessageAttachmentComposerState` does not exist

- [ ] **Step 3: Implement the shared composer helper**

Create `MessageAttachmentComposerState.cs`:

```csharp
public sealed class MessageAttachmentComposerState
{
    private readonly List<MessageDraftAttachmentInput> _attachments = new();

    public IReadOnlyList<MessageDraftAttachmentInput> Attachments => _attachments;

    public bool HasAttachments => _attachments.Count > 0;

    public void Replace(IEnumerable<MessageDraftAttachmentInput> attachments)
    {
        _attachments.Clear();
        _attachments.AddRange(attachments);
    }

    public void Remove(string attachmentId)
    {
        _attachments.RemoveAll(x => string.Equals(x.Id, attachmentId, StringComparison.Ordinal));
    }

    public void Clear()
    {
        _attachments.Clear();
    }
}
```

- [ ] **Step 4: Add message-scoped attachment UI to `ChatInputPanel` and Web send orchestration**

In `ChatInputPanel.razor`, keep the existing workspace upload row and add a separate message-attachment row:

```razor
@if (PendingMessageAttachments.Any())
{
    <div class="mb-2 flex flex-wrap gap-2">
        @foreach (var attachment in PendingMessageAttachments)
        {
            <span class="inline-flex items-center gap-2 rounded-full bg-slate-100 px-3 py-1 text-xs text-slate-700">
                @attachment.FileName
                <button type="button" class="text-slate-500 hover:text-slate-900" @onclick="() => OnRemoveMessageAttachment.InvokeAsync(attachment.Id)">×</button>
            </span>
        }
    </div>
}

<label class="btn-default cursor-pointer">
    <span class="hidden sm:inline">Attach to message</span>
    <InputFile OnChange="OnMessageAttachmentUpload" multiple disabled="@IsLoading" class="hidden" />
</label>
```

Add parameters:

```csharp
[Parameter] public IReadOnlyList<MessageDraftAttachmentInput> PendingMessageAttachments { get; set; } = Array.Empty<MessageDraftAttachmentInput>();
[Parameter] public EventCallback<InputFileChangeEventArgs> OnMessageAttachmentUpload { get; set; }
[Parameter] public EventCallback<string> OnRemoveMessageAttachment { get; set; }
```

In `CodeAssistant.razor.cs`, inject `IMessageSubmissionService`, hold a `MessageAttachmentComposerState`, and change send flow:

```csharp
[Inject] private IMessageSubmissionService MessageSubmissionService { get; set; } = default!;

private readonly MessageAttachmentComposerState _messageAttachmentComposer = new();

private async Task HandleMessageAttachmentUpload(InputFileChangeEventArgs e)
{
    var attachments = new List<MessageDraftAttachmentInput>();
    foreach (var file in e.GetMultipleFiles(10))
    {
        using var stream = file.OpenReadStream(MaxFileSize);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        attachments.Add(new MessageDraftAttachmentInput
        {
            FileName = file.Name,
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            Content = buffer.ToArray()
        });
    }

    _messageAttachmentComposer.Replace(attachments);
}
```

```csharp
var prepared = await MessageSubmissionService.PrepareAsync(
    new MessageDraft
    {
        SessionId = _sessionId,
        ToolId = _selectedToolId,
        Channel = MessageSubmissionChannel.Web,
        Text = message,
        Attachments = _messageAttachmentComposer.Attachments.ToList()
    },
    default);

var userMessage = prepared.UserMessage;
_messages.Add(userMessage);
ChatSessionService.AddMessage(_sessionId, userMessage);

await foreach (var chunk in CliExecutorService.ExecuteStreamAsync(prepared.ExecutionRequest, default))
{
    // existing assistant streaming loop
}

_messageAttachmentComposer.Clear();
```

- [ ] **Step 5: Mirror the same send contract in the mobile page**

In `CodeAssistantMobile.razor`, add a mobile attachment picker and chip row near the composer:

```razor
<div class="flex flex-wrap gap-2 mb-2" hidden="@(!_messageAttachmentComposer.HasAttachments)">
    @foreach (var attachment in _messageAttachmentComposer.Attachments)
    {
        <span class="inline-flex items-center gap-2 rounded-full bg-slate-100 px-3 py-1 text-xs">
            @attachment.FileName
            <button type="button" @onclick="() => RemoveMessageAttachment(attachment.Id)">×</button>
        </span>
    }
</div>

<label class="inline-flex items-center rounded-full bg-slate-100 px-3 py-2 text-xs text-slate-700">
    <span>Attach</span>
    <InputFile OnChange="HandleMessageAttachmentUpload" multiple class="hidden" />
</label>
```

In `CodeAssistantMobile.razor.cs`, use the same helper and the same `MessageSubmissionService.PrepareAsync(...)` pattern, but set:

```csharp
Channel = MessageSubmissionChannel.Mobile
```

- [ ] **Step 6: Run the helper tests, then build the solution, then do one manual smoke pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~MessageAttachmentComposerStateTests"
dotnet build D:\VSWorkshop\WebCode\WebCodeCli.sln
```

Expected:

- PASS for the helper tests
- BUILD SUCCEEDED for the solution

Manual smoke:

1. Start the app with `dotnet run --project D:\VSWorkshop\WebCode\WebCodeCli\WebCodeCli.csproj`
2. In Web, attach `diagram.png` and `notes.txt`, send text, and verify:
   - the user message shows both attachments
   - the assistant reply still streams normally
3. In mobile view, repeat the same flow and verify attachment chips clear after send

- [ ] **Step 7: Commit**

```bash
git add WebCodeCli/Helpers/MessageAttachmentComposerState.cs WebCodeCli/Components/ChatInputPanel.razor WebCodeCli/Pages/CodeAssistant.razor.cs WebCodeCli/Pages/CodeAssistantMobile.razor WebCodeCli/Pages/CodeAssistantMobile.razor.cs tests/WebCodeCli.Tests/MessageAttachmentComposerStateTests.cs
git commit -m "feat: add web and mobile attachment composers"
```

---

## Chunk 5: Add Feishu Draft Ingestion

### Task 5: Parse Feishu attachment messages and store explicit draft state

**Files:**
- Create: `WebCodeCli.Domain/Domain/Model/Channels/FeishuIncomingAttachment.cs`
- Modify: `WebCodeCli.Domain/Domain/Model/Channels/FeishuIncomingMessage.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuAttachmentDraftService.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/FeishuAttachmentDraftState.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/FeishuAttachmentDraftService.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/FeishuIncomingAttachmentParser.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuMessageHandler.cs`
- Test: `WebCodeCli.Domain.Tests/FeishuAttachmentDraftServiceTests.cs`
- Test: `WebCodeCli.Domain.Tests/FeishuIncomingAttachmentParserTests.cs`

- [ ] **Step 1: Write the failing Feishu draft-state tests**

Create `FeishuAttachmentDraftServiceTests.cs`:

```csharp
[Fact]
public void StartDraft_CreatesIsolatedDraftPerChatAndSender()
{
    var service = new FeishuAttachmentDraftService();

    var draft = service.StartDraft("app-1", "chat-1", "sender-1", "session-1", "codex");

    Assert.Equal("session-1", draft.SessionId);
    Assert.Equal("codex", draft.ToolId);
    Assert.Empty(draft.Attachments);
}
```

```csharp
[Fact]
public void UpdateTextAndAddAttachment_PreservesDraftUntilExplicitClear()
{
    var service = new FeishuAttachmentDraftService();
    service.StartDraft("app-1", "chat-1", "sender-1", "session-1", "codex");

    service.UpdateText("app-1", "chat-1", "sender-1", "Review the uploaded files");
    service.AddAttachment(
        "app-1",
        "chat-1",
        "sender-1",
        new MessageAttachment
        {
            DisplayName = "diagram.png",
            MimeType = "image/png",
            Extension = ".png",
            SizeBytes = 12,
            Kind = MessageAttachmentKind.Image,
            WorkspaceRelativePath = ".webcode/message-inputs/sub-1/diagram.png"
        });

    var draft = service.GetDraft("app-1", "chat-1", "sender-1");
    Assert.Equal("Review the uploaded files", draft!.Text);
    Assert.Single(draft.Attachments);
}
```

Create `FeishuIncomingAttachmentParserTests.cs`:

```csharp
[Fact]
public void Parse_ImagePayload_ReturnsStructuredAttachment()
{
    var parser = new FeishuIncomingAttachmentParser();

    var attachments = parser.Parse(
        "{\"image_key\":\"img_v2_test\"}",
        "image");

    var attachment = Assert.Single(attachments);
    Assert.Equal("image", attachment.MessageType);
    Assert.Equal("img_v2_test", attachment.FileKey);
}
```

- [ ] **Step 2: Run the focused Feishu draft tests to confirm they fail**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuAttachmentDraftServiceTests|FullyQualifiedName~FeishuIncomingAttachmentParserTests"
```

Expected:

- FAIL because draft-state types and incoming attachment parsing do not exist

- [ ] **Step 3: Add the incoming attachment and draft-state models**

Create `FeishuIncomingAttachment.cs`:

```csharp
namespace WebCodeCli.Domain.Domain.Model.Channels;

public class FeishuIncomingAttachment
{
    public string MessageType { get; set; } = string.Empty;
    public string FileKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string MimeType { get; set; } = "application/octet-stream";
    public long? SizeBytes { get; set; }
}
```

Update `FeishuIncomingMessage.cs`:

```csharp
public List<FeishuIncomingAttachment> Attachments { get; set; } = new();
```

Create `FeishuAttachmentDraftState.cs`:

```csharp
public class FeishuAttachmentDraftState
{
    public string DraftId { get; set; } = Guid.NewGuid().ToString("N");
    public string AppId { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public List<MessageAttachment> Attachments { get; set; } = new();
}
```

- [ ] **Step 4: Implement the draft service, parser, and handler wiring**

Create `FeishuIncomingAttachmentParser.cs`:

```csharp
public sealed class FeishuIncomingAttachmentParser
{
    public List<FeishuIncomingAttachment> Parse(string rawContent, string messageType)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return [];
        }

        using var json = JsonDocument.Parse(rawContent);
        var root = json.RootElement;

        return messageType switch
        {
            "image" => [new FeishuIncomingAttachment
            {
                MessageType = "image",
                FileKey = root.TryGetProperty("image_key", out var key) ? key.GetString() ?? string.Empty : string.Empty,
                DisplayName = "image"
            }],
            "file" => [new FeishuIncomingAttachment
            {
                MessageType = "file",
                FileKey = root.TryGetProperty("file_key", out var key) ? key.GetString() ?? string.Empty : string.Empty,
                DisplayName = root.TryGetProperty("file_name", out var name) ? name.GetString() ?? "file" : "file"
            }],
            _ => []
        };
    }
}
```

Create `FeishuAttachmentDraftService.cs` with a keyed in-memory store:

```csharp
public sealed class FeishuAttachmentDraftService : IFeishuAttachmentDraftService
{
    private readonly ConcurrentDictionary<string, FeishuAttachmentDraftState> _drafts = new(StringComparer.OrdinalIgnoreCase);

    public FeishuAttachmentDraftState StartDraft(string appId, string chatId, string senderId, string sessionId, string toolId)
    {
        var draft = new FeishuAttachmentDraftState
        {
            AppId = appId ?? string.Empty,
            ChatId = chatId,
            SenderId = senderId,
            SessionId = sessionId,
            ToolId = toolId
        };
        _drafts[BuildKey(appId, chatId, senderId)] = draft;
        return draft;
    }

    public FeishuAttachmentDraftState? GetDraft(string appId, string chatId, string senderId) =>
        _drafts.TryGetValue(BuildKey(appId, chatId, senderId), out var draft) ? draft : null;

    public void UpdateText(string appId, string chatId, string senderId, string text) =>
        GetDraft(appId, chatId, senderId)!.Text = text.Trim();

    public void AddAttachment(string appId, string chatId, string senderId, MessageAttachment attachment) =>
        GetDraft(appId, chatId, senderId)!.Attachments.Add(attachment);

    public void Clear(string appId, string chatId, string senderId) =>
        _drafts.TryRemove(BuildKey(appId, chatId, senderId), out _);
}
```

Inject `FeishuIncomingAttachmentParser` into `FeishuMessageHandler` and, when building `FeishuIncomingMessage`, assign:

```csharp
Attachments = _incomingAttachmentParser.Parse(message.Content, message.MessageType)
```

- [ ] **Step 5: Register the draft service and run the focused Feishu tests**

Register:

```csharp
services.AddSingleton<IFeishuAttachmentDraftService, FeishuAttachmentDraftService>();
```

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuAttachmentDraftServiceTests|FullyQualifiedName~FeishuIncomingAttachmentParserTests"
```

Expected:

- PASS for draft-state and handler parsing tests

- [ ] **Step 6: Commit**

```bash
git add WebCodeCli.Domain/Domain/Model/Channels/FeishuIncomingAttachment.cs WebCodeCli.Domain/Domain/Model/Channels/FeishuIncomingMessage.cs WebCodeCli.Domain/Domain/Service/Channels/IFeishuAttachmentDraftService.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuAttachmentDraftState.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuAttachmentDraftService.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuIncomingAttachmentParser.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuMessageHandler.cs WebCodeCli.Domain.Tests/FeishuAttachmentDraftServiceTests.cs WebCodeCli.Domain.Tests/FeishuIncomingAttachmentParserTests.cs
git commit -m "feat: add feishu attachment draft ingestion"
```

---

## Chunk 6: Add Feishu Explicit Submit and Shared Execution

### Task 6: Add Feishu draft-management cards, attachment download, and explicit submit-to-CLI

**Files:**
- Create: `WebCodeCli.Domain/Domain/Service/Channels/FeishuAttachmentDraftCardBuilder.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuCardKitClient.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardKitClient.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs`
- Test: `WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs`
- Test: `WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs`

- [ ] **Step 1: Write the failing submit-flow tests**

Add a `FeishuChannelServiceTests` case:

```csharp
[Fact]
public async Task HandleIncomingMessageAsync_TextMessageWithOpenDraft_UpdatesDraftInsteadOfExecutingCli()
{
    var draftService = new FeishuAttachmentDraftService();
    draftService.StartDraft("app-1", "chat-1", "sender-1", "session-1", "codex");

    var service = CreateFeishuChannelService(draftService: draftService);

    await service.HandleIncomingMessageAsync(new FeishuIncomingMessage
    {
        AppId = "app-1",
        ChatId = "chat-1",
        SenderId = "sender-1",
        SenderName = "alice",
        Content = "Please review these files"
    });

    Assert.Equal("Please review these files", draftService.GetDraft("app-1", "chat-1", "sender-1")!.Text);
    Assert.Empty(_cliRequests);
}

private readonly List<CliExecutionRequest> _cliRequests = new();
```

Add a `FeishuCardActionServiceTests` case:

```csharp
[Fact]
public async Task SubmitAttachmentDraftAction_BuildsMessageDraftAndClearsDraftOnSuccess()
{
    var draftService = new FeishuAttachmentDraftService();
    draftService.StartDraft("app-1", "chat-1", "sender-1", "session-1", "codex");
    draftService.UpdateText("app-1", "chat-1", "sender-1", "Review the attachments");
    draftService.AddAttachment("app-1", "chat-1", "sender-1", new MessageAttachment
    {
        DisplayName = "diagram.png",
        MimeType = "image/png",
        Extension = ".png",
        SizeBytes = 12,
        Kind = MessageAttachmentKind.Image,
        WorkspaceRelativePath = ".webcode/message-inputs/sub-1/diagram.png"
    });

    var service = CreateCardActionService(draftService: draftService);

    var response = await service.HandleActionForTestsAsync("submit_attachment_draft", "chat-1", "sender-1", "app-1");

    Assert.Contains("submitted", response.ToString(), StringComparison.OrdinalIgnoreCase);
    Assert.Null(draftService.GetDraft("app-1", "chat-1", "sender-1"));
}
```

- [ ] **Step 2: Run the focused Feishu submit tests to confirm they fail**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuChannelServiceTests.HandleIncomingMessageAsync_TextMessageWithOpenDraft_UpdatesDraftInsteadOfExecutingCli|FullyQualifiedName~FeishuCardActionServiceTests.SubmitAttachmentDraftAction_BuildsMessageDraftAndClearsDraftOnSuccess"
```

Expected:

- FAIL because no draft card actions, no attachment download path, and no draft-submit branch exist

- [ ] **Step 3: Add the draft card builder and card actions**

Create `FeishuAttachmentDraftCardBuilder.cs` with a focused card payload:

```csharp
public string BuildDraftCard(FeishuAttachmentDraftState draft)
{
    var attachmentLines = draft.Attachments.Count == 0
        ? "- no attachments staged yet"
        : string.Join("\n", draft.Attachments.Select(x => $"- {x.DisplayName}"));

    return JsonSerializer.Serialize(new
    {
        schema = "2.0",
        body = new
        {
            elements = new object[]
            {
                new
                {
                    tag = "markdown",
                    content = $"**Attachment draft**\n\nText: {(string.IsNullOrWhiteSpace(draft.Text) ? "_pending_" : draft.Text)}\n\nAttachments:\n{attachmentLines}"
                },
                new
                {
                    tag = "action",
                    actions = new object[]
                    {
                        new
                        {
                            tag = "button",
                            text = new { tag = "plain_text", content = "Submit to CLI" },
                            type = "primary",
                            value = new { action = "submit_attachment_draft" }
                        },
                        new
                        {
                            tag = "button",
                            text = new { tag = "plain_text", content = "Clear draft" },
                            type = "default",
                            value = new { action = "clear_attachment_draft" }
                        }
                    }
                }
            }
        }
    });
}
```

In `FeishuCardActionService.cs`, add action routing:

```csharp
case "open_attachment_draft":
    return await HandleOpenAttachmentDraftAsync(chatKey, operatorUserId, appId);
case "submit_attachment_draft":
    return await HandleSubmitAttachmentDraftAsync(chatKey, operatorUserId, appId);
case "clear_attachment_draft":
    return await HandleClearAttachmentDraftAsync(chatKey, operatorUserId, appId);
```

- [ ] **Step 4: Add attachment download and draft-aware `FeishuChannelService` routing**

Extend `IFeishuCardKitClient`:

```csharp
Task<(byte[] Content, string FileName, string MimeType)> DownloadIncomingAttachmentAsync(
    string chatId,
    FeishuIncomingAttachment attachment,
    string? username = null,
    string? appId = null);
```

In `FeishuChannelService.cs`, add this high-level branch at the top of `OnMessageReceivedAsync`:

```csharp
var openDraft = _attachmentDraftService.GetDraft(message.AppId ?? string.Empty, message.ChatId, message.SenderId);
if (openDraft != null)
{
    if (message.Attachments.Count > 0)
    {
        foreach (var incomingAttachment in message.Attachments)
        {
            var download = await _cardKit.DownloadIncomingAttachmentAsync(
                message.ChatId,
                incomingAttachment,
                message.SenderName,
                message.AppId);

            var staged = await _attachmentStagingService.StageAsync(
                openDraft.SessionId,
                openDraft.DraftId,
                [
                    new MessageDraftAttachmentInput
                    {
                        FileName = download.FileName,
                        ContentType = download.MimeType,
                        Content = download.Content
                    }
                ],
                CancellationToken.None);

            _attachmentDraftService.AddAttachment(message.AppId ?? string.Empty, message.ChatId, message.SenderId, staged.Single().Metadata);
        }

        await RefreshAttachmentDraftCardAsync(openDraft, message.SenderName, message.AppId);
        return;
    }

    if (!string.IsNullOrWhiteSpace(message.Content))
    {
        _attachmentDraftService.UpdateText(message.AppId ?? string.Empty, message.ChatId, message.SenderId, FeishuPromptNormalizer.Normalize(message.Content));
        await RefreshAttachmentDraftCardAsync(_attachmentDraftService.GetDraft(message.AppId ?? string.Empty, message.ChatId, message.SenderId)!, message.SenderName, message.AppId);
        return;
    }
}
```

The key rule is explicit:

- while a draft is open, attachment and text messages update the draft instead of starting CLI execution

- [ ] **Step 5: Submit the Feishu draft through the shared `MessageSubmissionService`**

In `FeishuCardActionService.cs`, implement `HandleSubmitAttachmentDraftAsync(...)`:

```csharp
var draft = _attachmentDraftService.GetDraft(appId ?? string.Empty, chatKey, operatorUserId);
if (draft == null)
{
    return BuildToastResponse("No draft is currently open.");
}

var prepared = await _messageSubmissionService.PrepareAsync(
    new MessageDraft
    {
        DraftId = draft.DraftId,
        SessionId = draft.SessionId,
        ToolId = draft.ToolId,
        Channel = MessageSubmissionChannel.Feishu,
        Text = draft.Text,
        Attachments = await RehydrateDraftInputsAsync(draft),
        SubmittedBy = operatorUserId
    },
    CancellationToken.None);

_attachmentDraftService.Clear(appId ?? string.Empty, chatKey, operatorUserId);
await _feishuChannel.ExecutePreparedSubmissionAsync(chatKey, draft.SessionId, prepared, operatorUserId, appId);

return BuildToastResponse("Attachment draft submitted.");
```

Add a small new internal method on `FeishuChannelService` for the shared execution path so the card action can reuse the existing streaming reply pipeline without re-implementing it.

Define the two helper methods in the same step so they are not left implicit:

```csharp
private async Task<List<MessageDraftAttachmentInput>> RehydrateDraftInputsAsync(FeishuAttachmentDraftState draft)
{
    var inputs = new List<MessageDraftAttachmentInput>();
    foreach (var attachment in draft.Attachments)
    {
        var workspaceRoot = _cliExecutor.GetSessionWorkspacePath(draft.SessionId);
        var absolutePath = Path.Combine(workspaceRoot, attachment.WorkspaceRelativePath.Replace('/', Path.DirectorySeparatorChar));
        inputs.Add(new MessageDraftAttachmentInput
        {
            FileName = attachment.DisplayName,
            ContentType = attachment.MimeType,
            Content = await File.ReadAllBytesAsync(absolutePath)
        });
    }

    return inputs;
}
```

```csharp
internal async Task ExecutePreparedSubmissionAsync(
    string chatId,
    string sessionId,
    PreparedMessageSubmission prepared,
    string? username,
    string? appId)
{
    _chatSessionService.AddMessage(sessionId, prepared.UserMessage);
    await ExecuteStreamingReplyAsync(
        chatId,
        sessionId,
        prepared.ExecutionRequest.ToolId,
        prepared.ExecutionRequest,
        username,
        appId);
}
```

Extract the existing streaming-reply body into a helper with this exact signature and move the old text-only call site to it first, then reuse it from `ExecutePreparedSubmissionAsync`:

```csharp
private async Task ExecuteStreamingReplyAsync(
    string chatId,
    string sessionId,
    string toolId,
    CliExecutionRequest request,
    string? username,
    string? appId)
{
    // Move the current "build streaming card -> iterate ExecuteStreamAsync -> update card -> finish" body here.
    // Replace the old prompt-only call with:
    await foreach (var chunk in _cliExecutor.ExecuteStreamAsync(request, executionCancellationToken))
    {
        // existing chunk handling
    }
}
```

- [ ] **Step 6: Run the focused Feishu submit tests, then the broader Feishu suites**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuChannelServiceTests.HandleIncomingMessageAsync_TextMessageWithOpenDraft_UpdatesDraftInsteadOfExecutingCli|FullyQualifiedName~FeishuCardActionServiceTests.SubmitAttachmentDraftAction_BuildsMessageDraftAndClearsDraftOnSuccess"
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuChannelServiceTests|FullyQualifiedName~FeishuCardActionServiceTests"
```

Expected:

- PASS for the new draft-submit tests
- PASS for the existing Feishu suites after fixture updates

- [ ] **Step 7: Commit**

```bash
git add WebCodeCli.Domain/Domain/Service/Channels/FeishuAttachmentDraftCardBuilder.cs WebCodeCli.Domain/Domain/Service/Channels/IFeishuCardKitClient.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuCardKitClient.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs
git commit -m "feat: add feishu attachment draft submit flow"
```

---

## Chunk 7: Final Docs and Regression

### Task 7: Update the product docs and run the full relevant regression slice

**Files:**
- Modify: `README.md`
- Modify: `README_EN.md`

- [ ] **Step 1: Add the feature note to the Chinese README**

Update the shared-session capability section with one new bullet:

```markdown
- 支持在桌面 Web、移动端和飞书中提交“文本 + 附件”消息；附件会按工具能力走原生附加或工作区引用降级
```

- [ ] **Step 2: Add the matching note to the English README**

Add:

```markdown
- Supports attachment-aware messages across desktop Web, mobile, and Feishu, with native CLI attachment input when available and workspace-reference fallback otherwise
```

- [ ] **Step 3: Run the full regression slice that covers this feature**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~SessionHistoryManagerTests|FullyQualifiedName~AttachmentStagingServiceTests|FullyQualifiedName~MessageSubmissionServiceTests|FullyQualifiedName~CliExecutionRequestAdapterTests|FullyQualifiedName~CliExecutorServiceTests|FullyQualifiedName~FeishuAttachmentDraftServiceTests|FullyQualifiedName~FeishuIncomingAttachmentParserTests|FullyQualifiedName~FeishuChannelServiceTests|FullyQualifiedName~FeishuCardActionServiceTests"
dotnet test D:\VSWorkshop\WebCode\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~MessageAttachmentComposerStateTests"
dotnet build D:\VSWorkshop\WebCode\WebCodeCli.sln
```

Expected:

- PASS for all filtered domain tests
- PASS for the shared composer helper tests
- BUILD SUCCEEDED for the solution

- [ ] **Step 4: Run one end-to-end manual smoke checklist**

Use a local run of `WebCodeCli` and verify:

1. Web:
   - attach `diagram.png` and `spec.pdf`
   - send text
   - confirm attachment chips appear on the user message
   - confirm the assistant still streams
2. Mobile:
   - attach one image and one text file
   - send text
   - confirm chips clear after send
3. Feishu:
   - open attachment draft card
   - send one file or image message
   - send one text message
   - click `Submit to CLI`
   - confirm the draft card clears and a streaming reply starts

- [ ] **Step 5: Commit**

```bash
git add README.md README_EN.md
git commit -m "docs: document unified attachment submission"
```

---

## Self-Review

### Spec Coverage

- Unified orchestration layer:
  - Task 2
- Hidden staging under `.webcode/message-inputs/<submission-id>/`:
  - Task 2
- Product-level message history with attachment metadata:
  - Task 1
- CLI execution request and adapter capability declaration:
  - Task 3
- Web and mobile attachment-aware submission:
  - Task 4
- Feishu explicit draft management and submit flow:
  - Tasks 5 and 6
- Partial downgrade warnings and failure categories:
  - Tasks 2, 3, and 6
- Documentation and regression:
  - Task 7

No approved spec section is left without a task.

### Placeholder Scan

- No `TBD`
- No `TODO`
- No "implement later"
- No "similar to Task N"
- Every code-changing step includes concrete code or a concrete command

### Type Consistency

The plan uses the same names throughout:

- `MessageAttachmentKind`
- `MessageAttachment`
- `MessageDraftAttachmentInput`
- `MessageDraft`
- `PreparedMessageSubmission`
- `CliExecutionAttachment`
- `CliExecutionRequest`
- `CliAttachmentCapabilities`
- `MessageAttachmentComposerState`
- `FeishuIncomingAttachment`
- `FeishuAttachmentDraftState`
- `IFeishuAttachmentDraftService`

---

Plan complete and saved to `docs/superpowers/plans/2026-05-14-unified-cli-attachment-submission-implementation.md`. Two execution options:

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

**Which approach?**
