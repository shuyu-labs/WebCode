# Feishu Goal Runtime Turn-Per-Card And Replacement Limit Design

## Context

WebCode currently keeps one Feishu streaming card alive across multiple Codex app-server goal-runtime turns.

That behavior was intentional when `CliExecutorService.StreamGoalRuntimeTurnsWhileActiveAsync(...)` was introduced: a completed inner app-server `turn` did not end the outer logical Feishu stream as long as the goal snapshot remained `active`.

That same-card-across-turns model now conflicts with the desired user-facing behavior:

- every app-server `turn` should have its own streaming card
- ordinary one-shot streaming should recover more than once when Feishu disconnects
- goal-runtime quick actions such as `/goal`, `/goal pause`, `/goal clear`, and `/goal resume` must remain valid on the current active card

Recent production logs also showed that when a later card update disconnected after the single allowed replacement had already been consumed, app-server mode stopped with the existing fallback text:

- `错误：飞书流式更新断连，已停止继续推送卡片`

The CLI/app-server execution often continued, but Feishu users no longer received updated cards for later turns.

## Goal

Change Feishu streaming behavior so that:

- each goal-runtime app-server `turn` always writes to a new streaming card
- old turn cards are explicitly closed instead of remaining in a running state
- goal-runtime quick actions remain attached to the current active card
- replacement-card recovery is increased from one attempt to ten attempts per logical stream
- the same ten-attempt recovery rule also applies to ordinary one-shot streaming flows

## Non-Goals

- redesigning Goal runtime semantics, thread reuse, or app-server session reuse
- changing `/goal` command meaning or button payloads
- allowing unlimited replacement cards
- changing non-Feishu channels
- redesigning CardKit payload structure beyond what is needed for card lifecycle correctness

## User-Facing Behavior

### Goal runtime

For goal-runtime sessions:

1. starting a turn creates a fresh streaming card
2. that card receives only the output for the current turn
3. when the turn ends but the goal remains `active`, the current card is finalized and the next turn starts on a new card
4. the newest card is the only card that should expose live goal-runtime controls

Expected visible effect:

- the user sees a sequence of per-turn cards instead of one endlessly reused card
- old cards clearly show that this round ended or output has moved on
- the latest card still has working `/goal`, `/goal pause`, `/goal clear`, `/goal resume`, and temporary-exit actions when applicable

### Ordinary one-shot streaming

Ordinary non-goal streaming still behaves as a single logical stream, but if the current card becomes unwritable, WebCode may create up to ten replacement cards before giving up.

Expected visible effect:

- transient Feishu write failures no longer stop after the first replacement
- once all ten replacement attempts are exhausted, WebCode still falls back to the existing disconnect text

## Design

### 1. Separate logical goal stream from Feishu card lifetime

`CliExecutorService` should keep the existing goal-runtime session, thread, and turn orchestration rules, but it must surface an explicit outer-stream signal when one app-server `turn` ends and the goal is still `active`.

That signal is not a terminal stream completion. It is a boundary event that means:

- the just-finished turn card should be finalized
- Feishu consumers should start a fresh card for the next turn
- the same goal-runtime thread/session continues underneath

This keeps the current app-server reuse model intact while allowing Feishu to switch cards deterministically at turn boundaries.

### 2. Feishu consumers must rotate cards at goal turn boundaries

Both `FeishuChannelService` and `FeishuCardActionService` currently consume one outer stream and assume one active streaming card per outer stream.

They must be updated so that, when they observe the goal-turn-boundary signal:

1. finish the current active card with turn-finished semantics
2. create a fresh streaming card using the same chat target and current chrome/actions
3. rebind the active execution to the new handle
4. continue streaming later chunks only to that new card

The old card must not remain in a running state after the next turn starts.

### 3. Goal buttons belong only to the current active card

The quick-action payloads for `/goal`, `/goal pause`, `/goal clear`, `/goal resume`, and temporary exit should remain session-based, not card-instance-based.

That means the implementation must preserve:

- the same `session_id`
- the same `tool_id`
- the same `chat_key`

But each newly created turn card must rebuild the current quick-action chrome so that users always interact with the latest card.

Old turn cards should no longer be treated as the active control surface after rotation.

### 4. Increase replacement-card limit from 1 to 10

`FeishuStreamingCardSession` currently hard-limits replacement to one card per logical stream.

That limit should move to an explicit constant and be raised to ten for:

- ordinary Feishu channel streaming
- card-action streaming
- goal-runtime per-turn streaming cards

The recovery rule remains bounded:

- each logical stream/card-session may create at most ten replacement cards
- after the limit is exhausted, the existing disconnect fallback still applies

This change is intentionally independent from goal turn rotation. Turn-per-card is a normal lifecycle transition, not a replacement-card recovery event.

### 5. Old-card terminal semantics

There are now two distinct reasons a card stops being current:

- normal goal turn handoff
- failure-driven replacement-card recovery

The implementation should keep these distinct:

- normal turn handoff should produce a normal completed-or-turn-finished state
- failure-driven replacement should keep the existing transferred/stopped semantics that indicate output moved because the prior card became unwritable

That distinction matters for user comprehension and for preserving the existing recovery behavior.

## Error Handling

### Goal turn handoff

If finishing the previous turn card fails during normal turn rotation:

- log the failure
- continue creating the next turn card
- do not abort the underlying goal-runtime execution solely because the previous card could not be finalized

### Replacement-card recovery

If a card becomes unwritable:

- use the existing replacement-card path
- allow up to ten replacement creations for that logical stream
- if replacement creation or later writes still fail after the limit is exhausted, append the existing disconnect message and stop Feishu streaming updates

### Goal controls

Button payload validity must not depend on reusing the same Feishu `cardId`.

The controls should continue to work because they target the reused session/thread/runtime state, not a prior card instance.

## Testing

Add regression coverage for:

### CliExecutorService

- goal-runtime outer stream emits a turn-boundary signal when one turn ends and the goal remains `active`
- final outer completion still happens only after the goal leaves `active`

### Feishu channel path

- goal-runtime channel streaming creates a new card for the second turn instead of continuing on the first card
- the first turn card is finalized before the second turn card becomes active
- the latest turn card still contains goal quick actions
- ordinary one-shot replacement-card recovery allows ten replacements before disconnect fallback

### Feishu card-action path

- goal-runtime card-action streaming rotates to a new card per turn
- the active handle and chrome are rebound to the newest turn card
- the latest turn card still contains goal quick actions
- replacement-card recovery also allows ten replacements before disconnect fallback

## Implementation Notes

- Keep goal-runtime session reuse, thread reuse, and `/goal` control semantics unchanged.
- Do not implement turn-per-card by starting a fresh goal session or fresh thread for each turn.
- Treat turn rotation as a normal lifecycle transition, not as a replacement-card failure.
- Update `docs/agent-notes/2026-05-27.md` with the final working rule after implementation is confirmed.
