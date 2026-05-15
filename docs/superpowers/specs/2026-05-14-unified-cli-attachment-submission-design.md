# Unified CLI Attachment Submission Design

Date: 2026-05-14
Status: approved in discussion, pending implementation planning

## Goal

Add a unified "message attachments" submission flow for `Web`, `mobile`, and `Feishu` so users can submit:

- text only
- text plus one or more attachments
- attachments that the selected CLI can consume natively
- attachments that must be downgraded to workspace file references

The design must preserve the repository's current "shared session flow" model: all three channels target the same session history, workspace, and CLI thread continuity rules.

## Approved Constraints

The following constraints were explicitly approved during discussion and are part of this design:

- use a unified submission orchestration layer rather than implementing attachment behavior separately in each entry point
- separate product-level message semantics from CLI execution semantics
- support `1 text + multiple attachments` in the first version
- when the selected CLI does not support native attachment input, automatically stage the files in the session workspace and submit them by structured file reference
- store attachments in a hidden session workspace directory rather than the user-visible working directory root
- preserve user-visible message history as `text + attachment metadata`
- do not store CLI lowering details such as native-vs-reference mode in the chat history
- define the first-version attachment scope as a common whitelist rather than "any file" or a per-tool dynamic UI
- first-version attachment whitelist includes:
  - images
  - text and code files
  - PDF
  - Office documents
- Feishu attachment submission is explicit:
  - attachments are staged through card actions
  - the user supplies text through a normal text message
  - the user explicitly clicks a card action to submit the draft to the CLI
- pure text Feishu messages keep the current direct-submit behavior
- do not implement implicit time-window message aggregation in Feishu
- do not add OCR, image captioning, or other content-derivation features in v1

## Scope

In scope:

- a new cross-channel attachment draft and submission model
- a new hidden attachment staging directory under each session workspace
- attachment metadata persistence in session history
- adapter capability declaration for native-vs-reference attachment handling
- CLI execution request preparation that includes attachments
- Web UI support for message-scoped attachments
- mobile UI support for message-scoped attachments
- Feishu card support for attachment drafts and explicit submission
- error reporting for validation, staging, downgrade, and execution failures
- tests for orchestration, persistence, adapter translation, and channel integration

Out of scope:

- OCR
- automatic image understanding outside the target CLI
- speech or audio processing
- arbitrary file-type upload without a whitelist
- attachment retry queues, resumable uploads, or background transfer recovery
- implicit Feishu multi-message aggregation
- per-tool dynamic attachment-picker UI in v1
- exposing hidden staging paths as first-class user-facing workspace content

## Problem

Today, the product has a shared session model across Web, mobile, and Feishu, but message submission is still fundamentally text-only.

This creates three structural gaps:

1. there is no unified "message with attachments" domain model
2. there is no channel-independent orchestration layer that can validate, stage, and normalize attachments before execution
3. the adapter boundary only understands `prompt + session context`, so native attachment support cannot be expressed cleanly

The repository already contains the pieces needed to make this worthwhile:

- shared session history persistence
- shared session workspaces
- CLI adapters
- Web and mobile chat entry points
- Feishu session and card flows

What is missing is a clear boundary between:

- what the user submitted
- how the system staged it
- how the selected CLI actually consumed it

Without that boundary, adding attachment support directly in each entry point would quickly fork behavior across the three channels and make future CLI capability changes expensive to maintain.

## Design Summary

The recommended design is a unified submission orchestration layer.

Each entry point produces a channel-agnostic `MessageDraft`. The orchestration layer validates the draft, stages its attachments into a hidden workspace directory, determines which attachments can be sent natively to the selected CLI, downgrades the remainder to workspace file references, persists the product-level message record, and then executes the request through the existing streaming session flow.

The design intentionally separates two levels of representation:

- product semantics:
  - "the user submitted this text and these attachments"
- execution semantics:
  - "this CLI request used these native attachments and these workspace references"

This separation keeps session history stable and user-facing while allowing per-tool execution behavior to evolve independently.

## Architecture

### Entry Layer

The existing Web, mobile, and Feishu entry points remain the channel-specific edges of the system, but they are reduced to thin draft collectors.

They should no longer:

- decide how the target CLI receives attachments
- directly build prompt text with attachment paths
- persist attachment semantics independently

They should only:

- gather the current text input
- gather the selected attachments or draft attachment references
- identify the target session and tool
- call a shared submission API

### Orchestration Layer

Add a dedicated submission orchestration service, referred to in this design as `MessageSubmissionService`.

It is responsible for:

- validating attachment count, type, and size
- normalizing filenames and attachment metadata
- materializing staged files under the hidden session directory
- loading CLI attachment capability rules for the selected tool
- partitioning attachments into native and reference groups
- generating the final execution request
- persisting the product-level user message and attachment metadata
- returning structured warnings, especially partial-downgrade warnings

This is the main cross-channel feature boundary.

### Adapter Layer

The adapter layer remains the only place that knows how a specific CLI translates an execution request into command-line arguments and prompt content.

It must not own:

- file-type whitelist policy
- channel behavior
- message persistence

It should own:

- whether the tool can consume native attachments
- which attachment kinds are supported natively
- how native attachments are encoded as command arguments
- how reference attachments are described to the CLI when downgrade is required

### History Layer

Session history stores the user-visible fact that a message included attachments.

It should not store:

- whether an attachment was native or downgraded in a specific run
- tool-specific command-line arguments
- prompt augmentation details used only for execution

## Core Domain Objects

### MessageDraft

`MessageDraft` is the cross-channel input object produced before validation and staging.

It should include:

- `SessionId`
- `ToolId`
- `Channel`
- `Text`
- `Attachments`
- `SubmittedBy`
- `DraftId` or equivalent request identifier

Its purpose is to capture raw user intent, not execution details.

### AttachmentDescriptor

`AttachmentDescriptor` is the normalized product-level attachment model.

It should include:

- logical attachment id
- original display name
- normalized file name
- MIME type
- extension
- size in bytes
- attachment kind:
  - `image`
  - `text`
  - `pdf`
  - `office`
- staged workspace-relative path
- created timestamp

This object is used both for history persistence and as the source input to execution preparation.

### PreparedSubmission

`PreparedSubmission` is the result of orchestration before adapter translation.

It should include:

- the resolved session id
- the resolved tool id
- the final user-visible text
- the normalized attachment list
- the hidden staging root for this submission
- warnings
- the lowerable execution payload

This object is still tool-agnostic with respect to command syntax.

### CliExecutionRequest

Add a new execution request object for adapter translation.

It should include:

- `PromptText`
- `SessionContext`
- `NativeAttachments`
- `ReferenceAttachments`
- `Warnings`

This object replaces the current de facto assumption that every execution request is just a single prompt string.

## Persistence Model

### Chat Message Identity

The in-memory `ChatMessage` model already has a logical message id. The persistent message table does not currently preserve that identity as an application-level message key.

The design therefore requires adding a stable logical message id column to persisted chat messages so attachment records can reference a message independently of the database identity key.

### Attachment Table

Add a new `ChatMessageAttachment` table linked one-to-many to a logical chat message.

Recommended fields:

- database identity key
- logical attachment id
- logical message id
- session id
- username
- display name
- normalized file name
- MIME type
- extension
- size in bytes
- attachment kind
- workspace-relative staged path
- created timestamp

This keeps attachment metadata queryable without encoding structured blobs inside `ChatMessage.Content`.

### History Semantics

When a user sends a message with attachments:

- the chat message record stores the user text
- attachment records store the attachment metadata
- execution-specific lowering details are not persisted in chat history

This preserves the approved behavior that users should be able to see what they submitted without exposing implementation details such as native argument forms or downgrade prompt snippets.

## Hidden Staging Directory

All message-scoped attachments are staged in a hidden per-session subtree:

```text
.webcode/message-inputs/<submission-id>/
```

This directory exists under the session workspace, not under a global temp root and not in the visible workspace root.

### Why This Structure

This structure is required because it:

- prevents accidental clutter in the user-visible workspace root
- avoids collisions between submissions with same-named files
- gives the system a stable file path for downgrade-by-reference
- groups one submission's attachments together for later inspection or cleanup
- keeps future "retry this submission" work possible without redesigning storage

### Lifecycle

The first version should keep staged files alongside the session workspace lifecycle.

This means:

- staged files are retained after execution
- staged files are removed when the session workspace is removed or explicitly cleaned
- there is no first-version requirement to delete them immediately after one execution

This is deliberate. Immediate deletion would break preview, retry, and future replay workflows and would conflict with the approved requirement to retain attachment metadata in message history.

## Channel Flows

### Web Flow

Web gains a message-scoped attachment picker distinct from the existing "upload file to workspace" behavior.

The user flow is:

1. select one or more supported attachments for the current message draft
2. provide non-empty text for the draft
3. click send
4. the client submits one unified draft
5. the service validates, stages, persists, and executes
6. the user message appears with attachment chips or metadata badges

The Web layer must not materialize final CLI prompt text on its own.

### Mobile Flow

Mobile follows the same backend flow as Web.

The difference is only presentation:

- lighter attachment affordances
- mobile-native file and image pickers
- compact warning presentation

The mobile layer should not introduce its own attachment semantics or a separate transport contract.

### Feishu Flow

Feishu requires explicit draft staging because its natural interaction surface is message-stream and card-driven rather than form-driven.

There are two flows:

- text-only messages:
  - keep the current direct-submit behavior
- attachment drafts:
  - attachments are staged through card actions
  - the user sends a normal text message to provide the draft text
  - the user clicks an explicit card action to submit the draft to the CLI

Approved assumption:

- when a Feishu conversation has an open attachment draft, the next ordinary text message updates that draft's text rather than immediately submitting to the CLI
- the final submission still requires an explicit card action

This avoids implicit aggregation windows and keeps attachment state visible and controllable.

### Shared Submission Contract

All three channels should ultimately use the same two service operations:

- update draft
- submit draft

The service owns the transition from draft state to persisted message plus streaming execution.

## Attachment Capability Model

The system needs an explicit attachment capability declaration per CLI adapter.

Each adapter should declare:

- whether native attachments are supported
- which attachment kinds are supported natively
- whether multiple native attachments are supported
- whether reference downgrade is allowed

This capability declaration should be data consumed by the orchestration layer, not inferred ad hoc in UI code.

## Native vs Reference Handling

### Decision Rule

For each prepared attachment:

- if the selected adapter declares that the attachment kind is supported natively, place it in `NativeAttachments`
- otherwise, place it in `ReferenceAttachments`

The orchestration result may therefore contain:

- only native attachments
- only reference attachments
- a mixture of both

Mixed mode is explicitly supported in v1.

### Initial Adapter Direction

The first implementation should assume:

- `Codex` can be extended to support native image attachments
- other attachment kinds should still be able to run through reference downgrade
- other tools should default to reference downgrade unless and until explicit native support is implemented in their adapters

This gives the product a stable baseline without blocking on per-tool parity.

## Downgrade Prompt Strategy

When downgrade-by-reference is used, the system should add a structured attachment preamble rather than a loosely concatenated natural-language path list.

Recommended form:

```text
Attached files in workspace:
- .webcode/message-inputs/<submission-id>/diagram.png
- .webcode/message-inputs/<submission-id>/spec.pdf

Please use these files together with the user request below.
```

The exact wording may be adapter-tunable, but the structure should be uniform.

This keeps downgrade behavior:

- inspectable
- testable
- consistent across channels

## Execution Boundary Changes

The current execution flow assumes every request can be represented as a prompt string. That is no longer sufficient.

The design therefore requires expanding the execution boundary so orchestration can pass a richer request object into adapter translation before invoking the CLI process.

This should be implemented by introducing a new execution request abstraction rather than overloading `CliSessionContext`, because attachments are request-level input, not session-level state.

## Validation Rules

The first version should validate at least:

- maximum attachment count per message
- supported whitelist kinds
- per-file size limit
- empty submission rules

The first version accepts:

- text-only submissions
- text-plus-attachments submissions

The first version rejects:

- empty submissions
- attachments-without-text submissions

This rule is shared across Web, mobile, and Feishu so all three channels preserve the same message semantics.

## Error Handling

Define four structured result categories.

### Draft Validation Failure

Examples:

- unsupported file type
- too many attachments
- file too large
- no text and no attachments

Behavior:

- do not execute the CLI
- return a business error that the channel can display clearly

### Staging Failure

Examples:

- invalid workspace
- write failure under hidden staging root
- normalization or collision failure

Behavior:

- do not execute the CLI
- preserve enough diagnostic detail for logs
- return a user-facing failure that explains attachment preparation failed

### Partial Downgrade

Examples:

- one attachment can be sent natively
- three attachments must be referenced by workspace path

Behavior:

- proceed with execution
- return a non-fatal warning
- surface that warning in channel-appropriate UI

### CLI Execution Failure

Examples:

- adapter-built invocation fails
- CLI exits with an error
- streaming execution is interrupted

Behavior:

- preserve the user message and attachment metadata
- mark the assistant-side response as failed through the existing execution flow
- do not erase the fact that the submission occurred

## Status Feedback

### Web

Web should show:

- attachment chips or a compact attachment summary on the user message
- validation or staging errors before execution
- a lightweight warning when partial downgrade occurred

It should not expose raw hidden paths unless the product explicitly chooses to reveal them in a debug context.

### Mobile

Mobile should mirror the same semantics with shorter presentation:

- attachment indicators on the message
- compact validation errors
- compact partial-downgrade warning

### Feishu

Feishu draft cards need explicit draft state:

- draft in progress
- ready to submit
- submitted
- failed

If submission includes reference downgrade, the card or confirmation reply should clearly state that some files were provided as workspace file references rather than silently implying uniform native attachment handling.

## Testing Strategy

### Orchestration Unit Tests

Cover:

- whitelist validation
- multi-attachment normalization
- hidden staging path generation
- attachment partitioning into native and reference groups
- downgrade preamble generation

### Adapter Unit Tests

Cover:

- native attachment argument translation for supported adapters
- downgrade prompt translation for unsupported attachment kinds
- mixed text-plus-attachment request escaping

### Persistence Tests

Cover:

- saving attachment metadata with a message
- reloading message history with attachments attached to the correct message
- session cleanup removing hidden staging files with the workspace lifecycle

### Channel Integration Tests

Cover:

- Web message with multiple attachments
- mobile message with multiple attachments
- Feishu attachment draft upload, text update, and explicit submit

## Non-Goals for V1

Do not include the following in the first implementation:

- OCR
- auto-generated image descriptions
- implicit Feishu message-window aggregation
- unrestricted arbitrary file uploads
- adapter-specific dynamic attachment pickers in the UI
- attachment retry pipelines or resumable transfer protocols

These can be added later without changing the core orchestration boundary if this design is followed.

## Implementation Notes

The design intentionally pushes complexity downward into shared services and adapter declarations instead of upward into channel-specific UI code.

That tradeoff is the correct one for this repository because:

- the product already treats Web, mobile, and Feishu as alternate views over a shared session system
- CLI capability differences are real and should be isolated
- message history must remain stable even as execution strategies evolve

The first implementation should therefore favor:

- explicit domain objects
- explicit capability declarations
- explicit staging and downgrade logic

over ad hoc string concatenation or channel-specific branching.

## Success Criteria

This design is successful when:

1. Web, mobile, and Feishu all submit attachments through the same orchestration boundary
2. users can see that a message contained attachments in session history
3. hidden staging paths are stable and isolated per submission
4. supported adapters can consume native attachments where implemented
5. unsupported attachment kinds still work through workspace-reference downgrade
6. partial downgrade is visible rather than silent
7. failure modes are explicit and channel-appropriate
