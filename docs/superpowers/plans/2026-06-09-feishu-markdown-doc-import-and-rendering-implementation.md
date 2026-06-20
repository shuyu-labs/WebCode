# Feishu Markdown Doc Import And Rendering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Completed checklist items in this file are intentionally closed as `- [x]`.

**Goal:** Upgrade Feishu reply documents from plain-text append behavior to Feishu-native Markdown rendering and add an independent toggle that imports referenced local Markdown files as Feishu online documents after a completed reply.

**Architecture:** Reuse the existing completed-reply orchestration pipeline instead of building a second completion subsystem. Keep Feishu HTTP details in `IFeishuCardKitClient` and `FeishuCardKitClient`, isolate reply-body Markdown rendering in a focused renderer, isolate Markdown reference reuse/import behavior in a dedicated importer, and preserve the existing non-fatal warning flow for folder and permission failures.

**Tech Stack:** C#, xUnit, existing Feishu reply-document pipeline, Feishu Docx convert API, Feishu Drive upload/import APIs.

---

### Task 1: Persist the independent Markdown-import toggle across admin UI, help cards, and runtime settings

**Files:**
- Modify: `WebCodeCli.Domain/Repositories/Base/UserFeishuBotConfig/UserFeishuBotConfigEntity.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/UserFeishuBotConfigService.cs`
- Modify: `WebCodeCli/Controllers/AdminController.cs`
- Modify: `WebCodeCli/Components/AdminUserManagementModal.razor`
- Modify: `WebCodeCli/Components/AdminUserManagementModal.razor.cs`
- Modify: `WebCodeCli.Domain/Domain/Model/Channels/FeishuHelpCardAction.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuHelpCardBuilder.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuMessageHandler.cs`
- Modify: `tests/WebCodeCli.Tests/AdminControllerReplyDocumentTests.cs`
- Modify: `tests/WebCodeCli.Tests/AdminControllerReplyDocumentTestsAccessor.cs`
- Modify: `tests/WebCodeCli.Tests/AdminUserManagementModalStateTests.cs`
- Modify: `tests/WebCodeCli.Tests/AdminUserManagementReplyDocumentModeTests.cs`
- Modify: `WebCodeCli.Domain.Tests/FeishuHelpCardBuilderTests.cs`
- Modify: `WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs`

- [x] **Step 1: Add `ReferencedMarkdownDocImportEnabled` to the persisted bot config model**

The user config entity and config service now preserve an independent `ReferencedMarkdownDocImportEnabled` boolean so the Markdown-import mode does not piggyback on any legacy reply-document flag.

- [x] **Step 2: Add the toggle to the admin DTO and modal round-trip**

The admin controller request/response DTO mapping and the admin user-management modal state now expose `ReferencedMarkdownDocImportEnabled`, with a dedicated Markdown online-document import label in the Feishu bot settings UI.

- [x] **Step 3: Add the help-card action and toggle handling**

The Feishu help-card and card-action layer now expose:

- action: `toggle_referenced_markdown_doc_import`
- button text for the enabled and disabled Markdown online-document import states
- toast behavior consistent with the existing reply-document toggles

- [x] **Step 4: Thread the toggle into reply-document runtime settings**

`FeishuMessageHandler` and `FeishuCardActionService` now include the Markdown-import flag in the reply-document settings tuple so completed replies can decide whether to scan or import referenced Markdown files.

- [x] **Step 5: Verify toggle persistence and help-card behavior**

Evidence:

- `tests/WebCodeCli.Tests/AdminControllerReplyDocumentTests.cs`
- `tests/WebCodeCli.Tests/AdminUserManagementModalStateTests.cs`
- `tests/WebCodeCli.Tests/AdminUserManagementReplyDocumentModeTests.cs`
- `WebCodeCli.Domain.Tests/FeishuHelpCardBuilderTests.cs`
- `WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs`

### Task 2: Add Feishu client primitives for Markdown document rendering and Markdown file import

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuCardKitClient.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardKitClient.cs`
- Modify: `WebCodeCli.Domain.Tests/FeishuCardKitClientTests.cs`

- [x] **Step 1: Extend the client contract for Markdown convert and import flows**

`IFeishuCardKitClient` now exposes the primitives needed by higher layers:

- `ConvertMarkdownToCloudDocumentBlocksAsync(...)`
- `AppendCloudDocumentBlocksAsync(...)`
- `FindCloudDocumentInFolderByTitleAsync(...)`
- `UploadCloudFileAsync(...)`
- `ImportMarkdownFileAsCloudDocumentAsync(...)`

- [x] **Step 2: Implement reply-document Markdown conversion primitives**

`FeishuCardKitClient` now posts reply Markdown to Feishu's official document-convert endpoint and appends returned block payloads through the existing docx children endpoint without adding business logic to the raw client.

- [x] **Step 3: Implement Markdown upload and import primitives with shared-folder and default-directory support**

`FeishuCardKitClient` now supports:

- Drive upload through `upload_all`
- import-task creation and polling
- exact-title lookup inside a shared folder
- `folderToken = null` fallback that resolves the root folder for upload and omits the import `point` payload
- a 30-second import polling deadline that throws a dedicated Chinese timeout message for Markdown import

- [x] **Step 4: Keep stage-aware error information inside the existing HTTP and business helper path**

The client continues to surface permission and validation failures through the shared response parser so higher layers can reuse the friendly Chinese warning summarization already used by reply documents.

- [x] **Step 5: Verify request shape and terminal-state behavior**

Evidence:

- `WebCodeCli.Domain.Tests/FeishuCardKitClientTests.cs`
- coverage includes convert requests, block append requests, direct import, failed import, and null-folder import

### Task 3: Render reply documents through Markdown conversion and import referenced local Markdown files after completion

**Files:**
- Create: `WebCodeCli.Domain/Domain/Service/Channels/ReplyDocumentMarkdownRenderer.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/MarkdownReferenceExtractor.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/ReferencedMarkdownDocumentImporter.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/ReplyDocumentOrchestrator.cs`
- Modify: `WebCodeCli.Domain.Tests/ReplyDocumentOrchestratorTests.cs`
- Create: `WebCodeCli.Domain.Tests/ReplyDocumentMarkdownRendererTests.cs`
- Create: `WebCodeCli.Domain.Tests/MarkdownReferenceExtractorTests.cs`
- Create: `WebCodeCli.Domain.Tests/ReferencedMarkdownDocumentImporterTests.cs`
- Create: `WebCodeCli.Domain.Tests/ReplyDocumentOrchestratorMarkdownIntegrationTests.cs`
- Modify: `WebCodeCli.Domain.Tests/ListeningReplyDocumentFormatterTests.cs`

- [x] **Step 1: Add a convert-first renderer for reply-document bodies**

`ReplyDocumentMarkdownRenderer` now keeps reply-document behavior within three explicit branches:

- convert succeeds and returns non-empty `blocks` -> append converted blocks
- convert throws -> fall back to plain-text append for the same document
- convert succeeds but returns no usable `blocks` -> fall back to plain-text append

Block-append failures are logged and rethrown instead of downgrading to plain text, so one partially converted document cannot silently duplicate content.

- [x] **Step 2: Keep listening-document formatting behavior intact before rendering**

`ReplyDocumentOrchestrator` still formats the two listening variants through `ListeningReplyDocumentFormatter.Format(...)` before those bodies enter the Markdown rendering path.

- [x] **Step 3: Add safe local Markdown reference extraction**

`MarkdownReferenceExtractor` now:

- detects Markdown links whose target ends in `.md`
- detects bare local-looking `.md` paths
- rejects remote URLs, query or anchor suffixed matches, and paths that escape the workspace root
- resolves candidates under the current workspace
- deduplicates by normalized relative path while preserving source order

- [x] **Step 4: Add reusable referenced-Markdown import behavior**

`ReferencedMarkdownDocumentImporter` now:

- reuses an existing shared-folder online document when its title exactly matches the normalized relative path
- otherwise imports the local `.md` file as a Feishu online document
- sends a separate chat link for each reused or generated document
- attempts folder-admin grant lazily after the first successful reuse or import
- degrades to default-directory import if direct shared-folder placement fails
- keeps warning-send failures isolated so later Markdown candidates still continue

- [x] **Step 5: Integrate the importer into the existing completed-reply pipeline**

`ReplyDocumentOrchestrator` now:

- renders all four reply-document variants through the Markdown renderer
- preserves existing document title, folder, and link behavior
- triggers Markdown reference import only when `ReferencedMarkdownDocImportEnabled` is enabled
- scans the completed full reply first, then falls back to final-only text when needed
- keeps the entire Markdown-import path non-fatal to normal reply-document generation

- [x] **Step 6: Verify renderer, extractor, importer, orchestrator, and formatter behavior**

Evidence:

- `WebCodeCli.Domain.Tests/ReplyDocumentMarkdownRendererTests.cs`
- `WebCodeCli.Domain.Tests/MarkdownReferenceExtractorTests.cs`
- `WebCodeCli.Domain.Tests/ReferencedMarkdownDocumentImporterTests.cs`
- `WebCodeCli.Domain.Tests/ReplyDocumentOrchestratorMarkdownIntegrationTests.cs`
- `WebCodeCli.Domain.Tests/ReplyDocumentOrchestratorTests.cs`
- `WebCodeCli.Domain.Tests/ListeningReplyDocumentFormatterTests.cs`

### Task 4: Close the notes and verification loop for the completed implementation

**Files:**
- Modify: `docs/agent-notes/2026-06-09.md`
- Create: `docs/superpowers/plans/2026-06-09-feishu-markdown-doc-import-and-rendering-implementation.md`

- [x] **Step 1: Record the implementation findings**

`docs/agent-notes/2026-06-09.md` now records:

- the convert-first reply-document renderer behavior
- the referenced-Markdown import workflow and warning ordering
- the null-folder import fallback and 30-second import deadline
- the final toggle and warning behavior at the documentation level

- [x] **Step 2: Re-run targeted domain verification mapped to this feature**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\.worktrees\feat-feishu-markdown-docs\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyDocument|FullyQualifiedName~MarkdownReference|FullyQualifiedName~FeishuCardKitClientTests|FullyQualifiedName~FeishuHelpCardBuilderTests|FullyQualifiedName~FeishuCardActionServiceTests" --no-restore -p:UseSharedCompilation=false -p:RunAnalyzers=false -v minimal
```

Observed result on 2026-06-09:

- `251` passed
- `0` failed

- [x] **Step 3: Re-run targeted web and admin verification mapped to this feature**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\.worktrees\feat-feishu-markdown-docs\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~ReplyDocument|FullyQualifiedName~ReferencedMarkdownDocImport" --no-restore -p:UseSharedCompilation=false -p:RunAnalyzers=false -v minimal
```

Observed result on 2026-06-09:

- `18` passed
- `0` failed

- [x] **Step 4: Re-run the solution build as the final compile gate**

Run:

```powershell
dotnet build D:\VSWorkshop\WebCode\.worktrees\feat-feishu-markdown-docs\WebCodeCli.sln --no-restore -p:UseSharedCompilation=false -v minimal /m:1
```

Observed result on 2026-06-09:

- `0` errors
- existing repository warnings remain

- [x] **Step 5: Confirm the implementation file exists and all checklist items are closed**

This implementation plan now exists at:

- `docs/superpowers/plans/2026-06-09-feishu-markdown-doc-import-and-rendering-implementation.md`

All steps in this document are intentionally closed because the branch and worktree state, together with the verification evidence above, already satisfy the implementation scope described in the paired design spec.
