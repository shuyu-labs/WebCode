# Feishu Listening Reply Documents Design

Date: 2026-05-29

## Goal

Extend the existing Feishu reply-document feature with two additional document variants intended for in-app document listening on mobile Feishu:

- `听完整文档`
- `听结论文档`

These new variants must reuse the existing reply-document delivery pipeline, but their document bodies must be transformed into a listening-friendly form before upload.

The existing document variants must remain unchanged:

- `完整回复文档`
- `结论回复文档`

All four variants must be independently configurable and may all be enabled at the same time.

## Product Definition

### User-facing behavior

The product exposes four independent reply-document toggles:

- `完整回复文档`
- `结论回复文档`
- `听完整文档`
- `听结论文档`

Behavior rules:

- each toggle is independent
- any subset of the four toggles may be enabled
- each enabled toggle generates its own Feishu cloud document
- each generated document sends its own plain-text link message back into the same Feishu chat

The two new listening variants are not aliases of the existing variants. They are additional outputs.

### Document families

`完整回复文档`

- body contains the current turn full completed assistant reply text without listening-specific rewriting

`结论回复文档`

- body contains the current turn final-only reply text without listening-specific rewriting

`听完整文档`

- body starts from the current turn full completed assistant reply text
- body is transformed by the listening-document formatter before upload

`听结论文档`

- body starts from the current turn final-only reply text
- body is transformed by the listening-document formatter before upload

### Turn boundary

All four document variants remain turn-scoped.

They must keep the same completion boundaries already used by the current reply-document system:

- one normal streaming completion
- one card-action completion
- one goal-runtime app-server turn boundary

Do not aggregate across turns.

## Listening Document Formatting

### Purpose

Listening documents are intended to be read aloud by Feishu's built-in document voice playback, so raw file paths and filename-heavy output should be rewritten into stable spoken placeholders.

### Scope

The formatter applies only to:

- `听完整文档`
- `听结论文档`

The formatter must not modify:

- `完整回复文档`
- `结论回复文档`

### Replacement rule

When the listening formatter detects a file reference that contains an English filename with a suffix, it replaces each distinct matched reference with a sequential placeholder:

- `文件内容1`
- `文件内容2`
- `文件内容3`

The placeholder numbering is assigned in first-appearance order.

If the same matched reference appears again later in the same body, reuse the same placeholder instead of creating a new one.

### Target patterns

The formatter should cover the repository-style file references that commonly appear in assistant replies, including:

- full local paths such as `D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedWorkspace.razor`
- full local paths with line suffixes such as `D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedWorkspace.razor:812`
- slash-prefixed local paths such as `/D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedSession.razor:241`
- plain filename references with extensions when they are part of a file-like path or file mention

The formatter should not try to rewrite arbitrary English prose just because it contains a period.

### Mapping appendix

After replacing in-body references, append a mapping section at the end of the listening document body.

Example shape:

`文件内容1：/D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedWorkspace.razor:812`

`文件内容2：/D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedSession.razor:241`

Rules:

- append only when at least one replacement occurred
- preserve first-appearance ordering
- append once per distinct matched reference
- keep the original matched text in the appendix value

### Example

Input:

`构建过了。当前主要是仓库里原有警告，还包括 /D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedWorkspace.razor:812、/D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedSession.razor:241。`

Listening output:

`构建过了。当前主要是仓库里原有警告，还包括 文件内容1、文件内容2。`

`文件内容1：/D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedWorkspace.razor:812`

`文件内容2：/D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedSession.razor:241`

## Titles

The new variants need distinct document suffixes so users can tell them apart from the existing variants.

Recommended title format:

- `<thread-or-session-id> <original-user-question> - 完整回复`
- `<thread-or-session-id> <original-user-question> - 结论回复`
- `<thread-or-session-id> <original-user-question> - 听完整回复`
- `<thread-or-session-id> <original-user-question> - 听结论回复`

Reuse the current title-prefix logic:

1. prefer `CliThreadId`
2. otherwise fall back to `SessionId`
3. normalize the original user question by collapsing line breaks to spaces
4. truncate only the title string when required

## Recommended Architecture

Use a document-variant expansion of the current reply-document pipeline.

### Recommendation

Add two new document variants while keeping the current two variants unchanged.

This is the recommended approach because:

- it preserves the existing outputs exactly
- it lets all four toggles run independently
- it keeps the new listening-only transformation isolated to new variants
- it avoids contaminating the current full/final document semantics

### Rejected alternatives

Do not overload the existing two toggles with a "listening mode" flag.

That would make the existing feature ambiguous and would not satisfy the requirement that four buttons may all be enabled simultaneously.

Do not replace raw file references in the common completion buffers.

That would leak listening-specific output into the normal full/final document paths and any future reuse of the same buffers.

## Data Model Changes

### User Feishu bot config

Add two new independent boolean fields:

- `AudioFullReplyDocEnabled`
- `AudioFinalReplyDocEnabled`

The existing fields remain:

- `FullReplyDocEnabled`
- `FinalReplyDocEnabled`

Legacy reply-TTS compatibility behavior should remain unchanged and continue to derive only from the existing full/final document fields unless there is an explicit later requirement to expose the listening variants through legacy compatibility fields.

### Card action constants

Add two new Feishu help-card actions:

- toggle listening full reply document
- toggle listening final reply document

They must not reuse the action ids of the existing full/final toggles.

## Pipeline Changes

### Help card and filtered card buttons

Update the Feishu help-card builders so the top action area includes four buttons instead of two:

- `完整回复文档：开/关`
- `结论回复文档：开/关`
- `听完整文档：开/关`
- `听结论文档：开/关`

The button state rendering remains simple toggle state display with independent `primary` styling when enabled.

### Card action handling

Extend `FeishuCardActionService` with two new toggle handlers that mirror the existing full/final document toggle flow:

- resolve current chat user config
- toggle the relevant boolean field
- persist config
- rebuild the help card
- return success/failure toast and card update

### Admin UI

Extend the admin user management modal reply-document section to show four independent checkboxes:

- full reply document
- conclusion reply document
- listening full reply document
- listening conclusion reply document

Extend admin DTOs and save/load paths accordingly.

### Reply document orchestrator

Refactor the orchestrator core from two hardcoded branches into a small list of document variants.

Each variant definition should include:

- enabled predicate
- title suffix
- link prefix
- content source selector
- optional content transform

Recommended variants:

- full reply
- final reply
- listening full reply
- listening final reply

Processing rules:

- evaluate all four variants independently
- skip variants whose source body is blank
- if one variant fails, log and continue attempting the remaining variants

## Formatter Design

Introduce a dedicated listening-document formatter service or helper used only by the orchestrator.

Responsibilities:

- scan the chosen body text
- replace distinct file-like references with `文件内容N`
- produce the final transformed body with a mapping appendix

Non-responsibilities:

- no cloud-document upload logic
- no title generation
- no chat message sending
- no mutation of the original reply buffers

## Testing

### Formatter tests

Add focused tests for:

- single file path replacement
- repeated file path reuse of the same placeholder
- multiple file paths assigned in first-appearance order
- `:line` suffix preservation in the appendix
- no appendix when no replacements occur
- no replacement of unrelated English prose

### Orchestrator tests

Add tests proving:

- existing full reply document stays raw
- existing final reply document stays raw
- listening full reply document uses transformed content
- listening final reply document uses transformed content
- all four enabled generates four independent document requests
- one failed variant does not prevent the remaining variants from being attempted

### Card and config tests

Extend existing help-card, admin-controller, admin-modal, and card-action tests to cover:

- four toggle states
- save/load of the two new booleans
- new button labels and action ids

## Non-goals

- replace or remove the existing full/final reply document outputs
- change the current final-answer fallback rules
- rewrite normal chat messages or streaming card content into listening form
- change old document titles retroactively
- add actual audio synthesis or audio uploads
- translate the appendix values into human summaries

## Implementation Notes

Keep the listening formatter isolated behind a single helper so later product changes can adjust the replacement grammar without touching the Feishu delivery pipeline.

Do not modify the shared `Output` or `FinalAnswerOutput` buffers in-place. Always transform a local copy for the two listening variants only.
