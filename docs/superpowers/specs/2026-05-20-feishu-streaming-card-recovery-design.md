# Feishu Streaming Card Recovery Design

## Context

WebCode currently streams CLI output into Feishu CardKit cards through two separate service paths:

- `FeishuChannelService` for ordinary chat-driven streaming
- `FeishuCardActionService` for card-triggered streaming actions

Both paths currently assume a single `FeishuStreamingHandle` remains valid for the full lifetime of the stream. When card updates fail, the handle flips into `AreCardUpdatesStopped`, and the services treat that as a terminal disconnect. The result is that the CLI stream can still be running, but Feishu users stop receiving card updates.

The observed production failure pattern is:

1. a card update times out
2. the client retries the same `sequence`
3. Feishu returns `300317 sequence number compare failed`
4. WebCode treats that as a hard failure and stops the card stream

This pattern strongly suggests the first timed-out update may already have been accepted by Feishu, and the retry is colliding with an already-applied sequence.

## Goal

Make Feishu streaming card delivery more resilient by:

- preserving the current card when the timeout-plus-sequence-conflict pattern indicates the previous update probably succeeded
- falling back to a replacement card only when the current card is truly no longer writable
- applying the same behavior to both streaming entry points

## Non-Goals

- redesigning CardKit payload structure
- changing completion-state semantics beyond what is required for replacement-card continuity
- allowing unlimited replacement cards
- refactoring unrelated Goal runtime logic

## User-Facing Behavior

### Normal recovery on likely-successful timeout

If a card update times out and the immediate retry with the same `sequence` returns `300317 sequence number compare failed`, WebCode should treat that retry result as recoverable, not terminal. The stream should continue on the same card.

User-visible effect:

- no replacement card is created
- the stream continues on the original card
- no disconnect error is appended

### Replacement-card fallback

If the current streaming card is genuinely no longer writable, WebCode should create a single replacement streaming card and continue sending output there.

User-visible effect:

- the latest full rendered content appears in the replacement card
- subsequent chunks and final completion land on the replacement card
- the old card may optionally receive a best-effort notice that output has moved, but failure to write that notice must not block recovery

### Replacement-card limit

Each logical stream may create at most one replacement card.

If the replacement card also becomes unwritable, WebCode should stop card streaming and fall back to the existing disconnect behavior.

## Design

### 1. CardKit update classification

`FeishuCardKitClient.UpdateCardCoreAsync(...)` should classify update failures into three groups:

- `success`
- `recoverable same-card result`
- `terminal current-card failure`

The specific same-card recovery rule is:

- if attempt `n` timed out without caller cancellation
- and the retry for the same `cardId` and `sequence` returns business code `300317`
- then treat the update as effectively successful, because the previous attempt likely already advanced the sequence on Feishu

This rule must be narrowly scoped to the retry-after-timeout case. A plain `300317` without a preceding timeout should still be considered a current-card write failure, because it may indicate real concurrent writers or sequence drift.

### 2. Streaming-card session wrapper

Introduce a higher-level runtime object that owns the logical stream's active card handle rather than assuming the original handle never changes.

Required responsibilities:

- hold the current `FeishuStreamingHandle`
- hold the latest fully rendered content
- hold the immutable stream context needed to create a replacement card
- expose unified `UpdateAsync(...)`, `FinishAsync(...)`, and `CurrentMessageId`
- create at most one replacement card

This wrapper belongs above `FeishuStreamingHandle`, not inside it. `FeishuStreamingHandle` should remain a single-card transport abstraction.

### 3. Replacement-card creation rules

When the active handle becomes stopped, the wrapper should:

1. check whether replacement is still allowed
2. create a new streaming card using the same chat target and effective Feishu options
3. seed it with the latest full rendered content, not a delta
4. continue subsequent updates and finish operations on the new handle

The replacement card should preserve:

- the same title
- the same Chrome layout and action affordances appropriate for the stream type
- the latest status markdown, normalized back into a writable running state before continuing

### 4. Reply target behavior

For ordinary channel-driven streaming, replacement cards should continue to be posted to the chat as new streaming cards, matching the existing initial-card behavior.

For card-action-driven streaming, replacement cards should follow the same placement rule as the original streaming card created by that flow. If the original flow started as a fresh card in the chat, the replacement should do the same. Do not try to retrofit a different reply threading rule only for recovery.

### 5. Shared implementation path

Both `FeishuChannelService` and `FeishuCardActionService` currently duplicate most of the disconnect-handling flow. The replacement-card behavior should be implemented through shared logic so that both services follow the same rules for:

- same-card recovery
- replacement-card creation
- replacement-card limit
- final disconnect fallback

The services may still keep their own surrounding orchestration, but the card-recovery decision path should not diverge.

## Error Handling

### Recoverable same-card case

- continue the stream
- do not append disconnect text
- do not increment replacement-card count

### Replacement-card case

- create one replacement card
- continue using the latest full rendered content
- do not send duplicate completion notifications

### Final disconnect case

If replacement creation fails, or the replacement card also stops updates, fall back to the existing disconnect behavior:

- stop further card updates
- append the disconnect message
- allow the CLI execution itself to finish independently

## Testing

### CardKit client tests

Add unit coverage for:

- timed-out update followed by `300317` on the same `sequence` being treated as success
- plain `300317` without a preceding timeout remaining a failure

### Channel streaming tests

Add unit coverage for:

- original card failure causing exactly one replacement card
- replacement card receiving subsequent streamed content and the final completion content
- second card failure falling back to disconnect behavior

### Card-action streaming tests

Add the same coverage pattern to the card-action path so both services are verified against the same recovery expectations.

## Implementation Notes

- Keep replacement attempts bounded to one per logical stream.
- Do not move replacement logic into `FeishuStreamingHandle` itself.
- Do not let replacement-card recovery alter the existing explicit completion text notification behavior.
- Update `docs/agent-notes/2026-05-20.md` with the final working rule once implementation is confirmed.
