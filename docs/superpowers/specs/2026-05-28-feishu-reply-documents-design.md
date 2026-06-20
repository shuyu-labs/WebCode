# Feishu Reply Documents Design

Date: 2026-05-28

## Goal

Replace the Feishu reply-TTS feature with Feishu cloud-document delivery.

The new feature must let users automatically generate Feishu cloud documents from completed assistant replies and send the document links back into the current Feishu chat.

This change replaces reply TTS as a product capability. The existing turn-boundary completion pipeline, full-reply text accumulation, and structured `final_answer` accumulation should be reused where possible.

## Product Definition

### User-facing behavior

Replace the old reply-TTS controls with two independent Feishu document toggles:

- `完整回复文档`
  - when enabled, generate a new Feishu cloud document for the current turn's full completed assistant reply
- `结论回复文档`
  - when enabled, generate a new Feishu cloud document for the current turn's structured `final_answer` reply

These toggles are independent:

- both off
- only `完整回复文档` on
- only `结论回复文档` on
- both on

If both toggles are on for the same turn:

- generate two separate documents
- send two separate link messages

### Turn boundary

The document scope is one completed assistant turn:

- one normal streaming completion
- one card-action completion
- one goal-runtime app-server turn boundary

Do not aggregate across turns.

### Document content

`完整回复文档`:

- document body contains only the current turn's full completed assistant reply text

`结论回复文档`:

- document body contains only the current turn's structured `final_answer` text
- no summarization, slicing, regex extraction, or heuristics are allowed

### Document title

Document titles are based on:

- `thread id`
- current turn's original user question
- document type suffix

Title format:

- `<thread-or-session-id> <original-user-question> - 完整回复`
- `<thread-or-session-id> <original-user-question> - 结论回复`

ID resolution rule:

1. prefer native CLI thread id (`CliThreadId`)
2. if unavailable, fall back to `SessionId`

Question source rule:

- use the current turn's original user message text
- do not use normalized CLI prompt text

Question normalization rule:

- collapse line breaks to spaces
- trim outer whitespace
- if Feishu title length limits require truncation, truncate only the title string, never the document body

### Chat delivery

After a document is created successfully, send a separate plain text Feishu message containing the document link back into the same chat.

Recommended text:

- `已生成完整回复文档：[标题](链接)`
- `已生成结论回复文档：[标题](链接)`

Do not inject the link into the streaming card body or final card body.

### Permissions

Every generated document must be updated to allow tenant-internal link-based reading.

Target behavior:

- chat participants in the same Feishu tenant can open the link without additional manual permission changes

## Non-goals

- keep any reply-TTS audio capability
- preserve TTS engines, voice selection, audio upload, or audio sending
- emit failure notices into chat for document-generation failures
- change visible streaming card/body content to document-only output
- generate one shared document that mixes full reply and conclusion sections
- aggregate conclusions across turns

## Existing Constraints

### Reusable completion pipeline

The current Feishu completion pipeline already provides the right completion boundaries and text sources:

- current turn full assistant output
- current turn `final_answer`-only output

These should continue to be the sole sources for document bodies.

### Reusable Codex fallback

The existing Codex-only fallback for `final_answer` extraction from rollout files is acceptable to reuse for `结论回复文档`, as long as it remains narrowly scoped:

- only for Codex
- only for final-answer document generation
- only when live `FinalAnswerOutput` is empty
- only by extracting assistant `message` items with `phase = "final_answer"`

If fallback yields no text, skip the conclusion document silently.

### Current config shape is obsolete

The existing reply-TTS config is mode-based:

- `Off`
- `FullReply`
- `FinalOnly`

That shape no longer matches the new product requirement because full-reply and final-reply documents can now both be enabled simultaneously.

## Recommended Architecture

Use a direct replacement architecture:

1. Replace reply-TTS config with two independent document toggles
2. Rename the completed-reply orchestration pipeline from reply-TTS semantics to reply-document semantics
3. Reuse the existing completion boundaries and `Output` / `FinalAnswerOutput` payloads
4. Extend the Feishu API client with cloud-document creation, writing, and permission methods
5. Remove TTS-specific services, models, options, packaging, and tests

## Data Model Changes

### User Feishu bot config

Remove:

- `ReplyTtsEnabled`
- `ReplyTtsMode`
- `ReplyTtsVoiceId`

Add:

- `FullReplyDocEnabled`
- `FinalReplyDocEnabled`

Migration rules from old data:

- `FullReply` maps to `FullReplyDocEnabled = true`, `FinalReplyDocEnabled = false`
- `FinalOnly` maps to `FullReplyDocEnabled = false`, `FinalReplyDocEnabled = true`
- `Off` maps to both false

No voice-related compatibility behavior is needed after migration.

### Completed reply request payload

Rename the completed reply request model from reply-TTS semantics to reply-document semantics.

Suggested shape:

- `ChatId`
- `SessionId`
- `CliThreadId`
- `Output`
- `FinalAnswerOutput`
- `OriginalUserQuestion`
- `Username`
- `AppId`

This payload should carry all information needed to create and announce documents without reconstructing turn context later.

## Pipeline Changes

### Completion producers

Both completion producers must enqueue completed reply document requests:

- `FeishuChannelService`
- `FeishuCardActionService`

They must continue to preserve:

- full completed assistant text
- final-answer-only assistant text

They must additionally carry:

- the original user question for the current turn
- the session's `CliThreadId` if available

Goal-runtime turn-boundary ordering must remain:

1. queue completed side effects for the current turn
2. rotate card / clear turn-local buffers

### Orchestrator replacement

Rename:

- `IReplyTtsOrchestrator` -> reply-document equivalent
- `ReplyTtsOrchestrator` -> reply-document equivalent
- queue/process methods to reply-document names

Retain:

- per-chat serialization lock
- asynchronous background queue behavior

Remove:

- text normalization for speech
- chunk splitting
- TTS synthesis
- audio transcode
- audio upload
- audio message sending
- failure notice text specific to TTS
- temp audio storage

New orchestrator behavior:

1. resolve the user's document toggles
2. if full-reply document is enabled and full output is not empty, create full document and send link message
3. if final-reply document is enabled and final output is not empty, create final document and send link message
4. if both are enabled, perform both independently
5. if one fails, log and continue attempting the other

### Feishu client additions

Extend the existing Feishu client that already owns tenant-token retrieval and message sending.

Add document-oriented methods for:

- create document
- append/write document body text
- set tenant-readable link permission
- build/open document URL

Reuse the same tenant token flow already used for Feishu message APIs.

## Error Handling

### Expected skip cases

These are not failures:

- full document enabled but full output is empty
- final document enabled but final output is empty
- Codex final fallback yields no text

Handling:

- skip silently
- do not send failure messages

### Document operation failures

These are operational failures:

- create document API failure
- write body API failure
- permission update failure
- send-link-message failure

Handling:

- log warnings/errors
- do not emit an extra chat failure message by default

### Partial success

If both documents are enabled and one succeeds while the other fails:

- keep the success
- send the successful link
- log the failed branch

## Testing

Add or update tests for:

- config migration from old reply-TTS mode values into two new document booleans
- admin DTO/UI handling for two independent document toggles
- help-card rendering for two independent document toggles
- help-card action handling without mutual exclusion
- completed reply request payload includes:
  - `Output`
  - `FinalAnswerOutput`
  - `OriginalUserQuestion`
  - `SessionId`
  - `CliThreadId`
- normal streaming completion queues full/final document requests correctly
- card-action completion queues full/final document requests correctly
- goal-runtime turn-boundary still queues before clearing buffers
- orchestrator behavior for:
  - both toggles off
  - only full document on
  - only final document on
  - both on
  - missing `CliThreadId` fallback to `SessionId`
  - silent skip on empty final content
  - optional Codex final fallback
- Feishu API client request construction for:
  - document create
  - body write
  - permission patch
  - link message send

## Code Removal Scope

Remove all TTS-specific runtime, model, option, packaging, and testing code that is no longer used, including:

- reply-TTS storage/temp-audio helpers
- voice/platform/health models
- Kokoro / sherpa TTS client interfaces and implementations
- audio transcode services
- audio message services
- local reply-TTS service manager
- voice-selection admin UI and related tests
- installer/package logic that exists only to bundle reply-TTS runtime assets

Retain and rename the reusable completed-reply orchestration pipeline instead of rebuilding that flow from scratch.
