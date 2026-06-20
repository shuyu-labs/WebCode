# Feishu Listening Reply Documents Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add two independent Feishu reply-document variants, `听完整文档` and `听结论文档`, that coexist with the existing full/final reply documents and rewrite file-like references into `文件内容N` placeholders with an appendix mapping for listening.

**Architecture:** Keep the existing full/final reply-document outputs unchanged. Extend the config, help-card, admin UI, and orchestrator with two additional document variants, and isolate the listening-only body rewrite behind a dedicated formatter helper used only by the orchestrator.

**Tech Stack:** C#, ASP.NET Core / Blazor, xUnit, existing Feishu reply-document pipeline.

---

## File Map

### Existing files to modify

- `WebCodeCli.Domain/Domain/Model/Channels/FeishuHelpCardAction.cs`
  - add two new help-card action ids for toggling listening full/final reply documents
- `WebCodeCli.Domain/Repositories/Base/UserFeishuBotConfig/UserFeishuBotConfigEntity.cs`
  - add two new persisted booleans for listening full/final reply documents
- `WebCodeCli.Domain/Domain/Service/UserFeishuBotConfigService.cs`
  - persist and backfill the two new booleans through existing config save paths
- `WebCodeCli/Controllers/AdminController.cs`
  - expose the two new booleans on `UserFeishuBotConfigDto` and save/load mappings
- `WebCodeCli/Components/AdminUserManagementModal.razor`
  - render two new checkboxes in the reply-document section
- `WebCodeCli/Components/AdminUserManagementModal.razor.cs`
  - extend the editor/view models and custom-config detection logic
- `WebCodeCli.Domain/Domain/Service/Channels/FeishuHelpCardBuilder.cs`
  - render four reply-document buttons instead of two in the top action area
- `WebCodeCli.Domain/Domain/Service/Channels/FeishuMessageHandler.cs`
  - return four reply-document flags into the card builder
- `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
  - add two new toggle handlers and pass four flags into card rendering
- `WebCodeCli.Domain/Domain/Service/Channels/ReplyDocumentOrchestrator.cs`
  - expand hardcoded full/final branches into four document variants and apply the formatter only to listening variants
- `docs/agent-notes/2026-05-29.md`
  - record the confirmed listening-document replacement rule and the “four toggles are independent” constraint

### New files to create

- `WebCodeCli.Domain/Domain/Service/Channels/ListeningReplyDocumentFormatter.cs`
  - pure helper/service that rewrites file-like references into `文件内容N` placeholders and appends the mapping appendix
- `WebCodeCli.Domain.Tests/ListeningReplyDocumentFormatterTests.cs`
  - focused tests for replacement behavior and appendix generation

### Existing tests to modify

- `WebCodeCli.Domain.Tests/FeishuHelpCardBuilderTests.cs`
- `WebCodeCli.Domain.Tests/ReplyDocumentOrchestratorTests.cs`
- `WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs`
- `tests/WebCodeCli.Tests/AdminControllerReplyDocumentTests.cs`
- `tests/WebCodeCli.Tests/AdminUserManagementReplyDocumentModeTests.cs`
- `tests/WebCodeCli.Tests/AdminUserManagementModalStateTests.cs`

---

### Task 1: Add config and action surface for the two listening document toggles

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Model/Channels/FeishuHelpCardAction.cs`
- Modify: `WebCodeCli.Domain/Repositories/Base/UserFeishuBotConfig/UserFeishuBotConfigEntity.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/UserFeishuBotConfigService.cs`
- Modify: `WebCodeCli/Controllers/AdminController.cs`
- Test: `tests/WebCodeCli.Tests/AdminControllerReplyDocumentTests.cs`
- Test: `tests/WebCodeCli.Tests/AdminUserManagementReplyDocumentModeTests.cs`

- [x] **Step 1: Write the failing config DTO tests**

```csharp
[Fact]
public async Task GetFeishuBotConfig_ReturnsListeningReplyDocumentFields()
{
    var configService = new AdminControllerReplyDocumentTestsAccessor.StubUserFeishuBotConfigService
    {
        ConfigsByUsername =
        {
            ["alice"] = new UserFeishuBotConfigEntity
            {
                Username = "alice",
                AudioFullReplyDocEnabled = true,
                AudioFinalReplyDocEnabled = false
            }
        }
    };

    var controller = AdminControllerReplyDocumentTestsAccessor.CreateController(configService: configService);
    var result = await controller.GetFeishuBotConfig("alice");

    var ok = Assert.IsType<OkObjectResult>(result.Result);
    var dto = Assert.IsType<UserFeishuBotConfigDto>(ok.Value);
    Assert.True(dto.AudioFullReplyDocEnabled);
    Assert.False(dto.AudioFinalReplyDocEnabled);
}
```

- [x] **Step 2: Run the config DTO test to verify it fails**

Run:

```powershell
dotnet test tests/WebCodeCli.Tests/WebCodeCli.Tests.csproj --filter "GetFeishuBotConfig_ReturnsListeningReplyDocumentFields"
```

Expected:

- FAIL because `UserFeishuBotConfigEntity` / `UserFeishuBotConfigDto` do not yet contain the new properties

- [x] **Step 3: Add the two persisted booleans and DTO mappings**

Implement:

- `AudioFullReplyDocEnabled`
- `AudioFinalReplyDocEnabled`

in:

- `UserFeishuBotConfigEntity`
- `UserFeishuBotConfigDto`
- admin controller save/load mapping
- `UserFeishuBotConfigService`

Keep legacy reply-document compatibility behavior unchanged for the old full/final flags.

- [x] **Step 4: Run the config/document-mode tests to verify they pass**

Run:

```powershell
dotnet test tests/WebCodeCli.Tests/WebCodeCli.Tests.csproj --filter "AdminControllerReplyDocumentTests|AdminUserManagementReplyDocumentModeTests"
```

Expected:

- PASS for existing reply-document compatibility tests
- PASS for the new listening-field coverage tests

---

### Task 2: Add four-toggle admin UI and four-button help-card rendering

**Files:**
- Modify: `WebCodeCli/Components/AdminUserManagementModal.razor`
- Modify: `WebCodeCli/Components/AdminUserManagementModal.razor.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuHelpCardBuilder.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuMessageHandler.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
- Test: `WebCodeCli.Domain.Tests/FeishuHelpCardBuilderTests.cs`
- Test: `WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs`
- Test: `tests/WebCodeCli.Tests/AdminUserManagementModalStateTests.cs`

- [x] **Step 1: Write the failing help-card button test**

```csharp
[Fact]
public void BuildCommandListCard_IncludesListeningReplyDocumentButtons_WhenListeningDocumentsEnabled()
{
    var cardJson = _builder.BuildCommandListCard(
        CreateCategories(),
        fullReplyDocEnabled: false,
        finalReplyDocEnabled: false,
        audioFullReplyDocEnabled: true,
        audioFinalReplyDocEnabled: true);

    using var document = JsonDocument.Parse(cardJson);
    var elements = document.RootElement.GetProperty("body").GetProperty("elements");

    Assert.True(ContainsStringValue(elements, "听完整文档：开"));
    Assert.True(ContainsStringValue(elements, "听结论文档：开"));
    Assert.True(ContainsAction(elements, FeishuHelpCardAction.ToggleAudioFullReplyDocAction));
    Assert.True(ContainsAction(elements, FeishuHelpCardAction.ToggleAudioFinalReplyDocAction));
}
```

- [x] **Step 2: Run the help-card test to verify it fails**

Run:

```powershell
dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "BuildCommandListCard_IncludesListeningReplyDocumentButtons_WhenListeningDocumentsEnabled"
```

Expected:

- FAIL because builder method signatures and action ids do not yet support the listening variants

- [x] **Step 3: Extend UI and card plumbing to four independent toggles**

Implement:

- two new action ids in `FeishuHelpCardAction`
- four-button rendering in all three help-card builder entrypoints
- four-flag setting tuple in `FeishuMessageHandler`
- two new toggle handlers in `FeishuCardActionService`
- admin modal checkboxes and editor model fields

Use labels:

- `听完整文档`
- `听结论文档`

- [x] **Step 4: Run focused UI/card tests**

Run:

```powershell
dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "FeishuHelpCardBuilderTests|FeishuCardActionServiceTests"
dotnet test tests/WebCodeCli.Tests/WebCodeCli.Tests.csproj --filter "AdminUserManagementModalStateTests"
```

Expected:

- PASS with the two new listening toggles present and independent from the existing full/final toggles

---

### Task 3: Implement the listening reply document formatter with TDD

**Files:**
- Create: `WebCodeCli.Domain/Domain/Service/Channels/ListeningReplyDocumentFormatter.cs`
- Create: `WebCodeCli.Domain.Tests/ListeningReplyDocumentFormatterTests.cs`

- [x] **Step 1: Write the failing formatter tests**

```csharp
[Fact]
public void Format_ReplacesDistinctFileReferencesWithSequentialPlaceholders()
{
    var input = "构建过了。包括 /D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedWorkspace.razor:812 和 /D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedSession.razor:241。";

    var output = ListeningReplyDocumentFormatter.Format(input);

    Assert.Contains("文件内容1", output, StringComparison.Ordinal);
    Assert.Contains("文件内容2", output, StringComparison.Ordinal);
    Assert.Contains("文件内容1：/D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedWorkspace.razor:812", output, StringComparison.Ordinal);
    Assert.Contains("文件内容2：/D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedSession.razor:241", output, StringComparison.Ordinal);
}
```

```csharp
[Fact]
public void Format_ReusesPlaceholderForRepeatedFileReference()
{
    var input = "先看 /D:/repo/a.cs:1，再看 /D:/repo/a.cs:1。";

    var output = ListeningReplyDocumentFormatter.Format(input);

    Assert.Equal(2, Regex.Matches(output, "文件内容1", RegexOptions.CultureInvariant).Count);
    Assert.DoesNotContain("文件内容2", output, StringComparison.Ordinal);
}
```

- [x] **Step 2: Run the formatter test file to verify it fails**

Run:

```powershell
dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "ListeningReplyDocumentFormatterTests"
```

Expected:

- FAIL because the formatter file does not exist yet

- [x] **Step 3: Implement the minimal formatter**

Implementation requirements:

- preserve untouched text when no file-like references are found
- replace distinct matched references in first-appearance order
- append the mapping section only when at least one replacement occurred
- preserve the exact original matched value in the appendix

- [x] **Step 4: Run the formatter tests to verify they pass**

Run:

```powershell
dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "ListeningReplyDocumentFormatterTests"
```

Expected:

- PASS

---

### Task 4: Expand the reply-document orchestrator into four independent variants

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/ReplyDocumentOrchestrator.cs`
- Modify: `WebCodeCli.Domain.Tests/ReplyDocumentOrchestratorTests.cs`

- [x] **Step 1: Write the failing orchestrator tests for listening variants**

```csharp
[Fact]
public async Task QueueCompletedReplyAsync_WhenListeningFullReplyDocumentEnabled_CreatesFormattedListeningDocument()
{
    using var harness = new ReplyDocumentOrchestratorHarness(
        new UserFeishuBotConfigEntity
        {
            Username = "luhaiyan",
            AudioFullReplyDocEnabled = true
        });

    await harness.Orchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyDocumentRequest
    {
        ChatId = "oc-audio-full-chat",
        SessionId = "session-1",
        CliThreadId = "thread-1",
        OriginalUserQuestion = "question",
        Username = "luhaiyan",
        Output = "见 /D:/repo/a.cs:1"
    });

    await WaitUntilAsync(() => harness.CardKit.CreatedDocuments.Count == 1);

    Assert.Contains("听完整回复", harness.CardKit.CreatedDocuments.Single().Title, StringComparison.Ordinal);
    Assert.Contains("文件内容1", harness.CardKit.AppendedTexts.Single().Text, StringComparison.Ordinal);
}
```

- [x] **Step 2: Run the orchestrator listening test to verify it fails**

Run:

```powershell
dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "QueueCompletedReplyAsync_WhenListeningFullReplyDocumentEnabled_CreatesFormattedListeningDocument"
```

Expected:

- FAIL because the orchestrator only knows the existing full/final variants

- [x] **Step 3: Refactor orchestrator to evaluate four variants independently**

Implement:

- full reply raw variant
- final reply raw variant
- listening full reply transformed variant
- listening final reply transformed variant

For listening variants only:

- run the source body through `ListeningReplyDocumentFormatter`

Keep:

- current title prefix logic
- per-chat serialization
- full/final fallback behavior
- one variant failing must not block remaining variants

- [x] **Step 4: Run the orchestrator tests**

Run:

```powershell
dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "ReplyDocumentOrchestratorTests"
```

Expected:

- PASS for the previous full/final document tests
- PASS for the new listening-variant tests

---

### Task 5: Verification and documentation

**Files:**
- Modify: `docs/agent-notes/2026-05-29.md`

- [x] **Step 1: Record the implementation note**

Append a note covering:

- listening full/final documents are new independent variants
- full/final raw documents remain unchanged
- listening formatter rewrites file-like references into `文件内容N` and appends a mapping section

- [x] **Step 2: Run the focused verification suite**

Run:

```powershell
dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "ListeningReplyDocumentFormatterTests|ReplyDocumentOrchestratorTests|FeishuHelpCardBuilderTests|FeishuCardActionServiceTests"
dotnet test tests/WebCodeCli.Tests/WebCodeCli.Tests.csproj --filter "AdminControllerReplyDocumentTests|AdminUserManagementReplyDocumentModeTests|AdminUserManagementModalStateTests"
```

Expected:

- PASS

- [x] **Step 3: Run the full solution build**

Run:

```powershell
dotnet build D:\VSWorkshop\WebCode\WebCodeCli.sln
```

Expected:

- SUCCESS with no new build errors

- [x] **Step 4: Review git diff for touched files only**

Run:

```powershell
git diff -- WebCodeCli.Domain/Domain/Model/Channels/FeishuHelpCardAction.cs WebCodeCli.Domain/Repositories/Base/UserFeishuBotConfig/UserFeishuBotConfigEntity.cs WebCodeCli.Domain/Domain/Service/UserFeishuBotConfigService.cs WebCodeCli/Controllers/AdminController.cs WebCodeCli/Components/AdminUserManagementModal.razor WebCodeCli/Components/AdminUserManagementModal.razor.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuHelpCardBuilder.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuMessageHandler.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs WebCodeCli.Domain/Domain/Service/Channels/ReplyDocumentOrchestrator.cs WebCodeCli.Domain/Domain/Service/Channels/ListeningReplyDocumentFormatter.cs WebCodeCli.Domain.Tests/ListeningReplyDocumentFormatterTests.cs WebCodeCli.Domain.Tests/FeishuHelpCardBuilderTests.cs WebCodeCli.Domain.Tests/ReplyDocumentOrchestratorTests.cs WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs tests/WebCodeCli.Tests/AdminControllerReplyDocumentTests.cs tests/WebCodeCli.Tests/AdminUserManagementReplyDocumentModeTests.cs tests/WebCodeCli.Tests/AdminUserManagementModalStateTests.cs docs/agent-notes/2026-05-29.md
```

Expected:

- only the planned listening-document files and notes appear in the reviewed diff
