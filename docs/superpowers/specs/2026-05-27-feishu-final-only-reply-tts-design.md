# Feishu Final-Only Reply TTS Design

Date: 2026-05-27

## Goal

Add a second Feishu reply-TTS mode that sends speech only for the assistant's structured final answer content for the current turn.

The new mode must:

- use the current turn boundary as the speech scope
- synthesize only assistant content marked as `final_answer`
- send no audio when the turn has no structured final-answer content
- keep the existing full-reply speech mode intact

## Why

The current Feishu reply-TTS pipeline always speaks the merged completed assistant text. That merged text is suitable for "full reply" playback, but it is not suitable for a "conclusion-only" mode because:

- commentary and progress text can be mixed into the final visible answer
- goal runtime now rotates per app-server turn, so the desired speech unit is one turn, not a multi-turn aggregate
- Codex rollout records already distinguish assistant message phases such as `commentary` and `final_answer`

The feature should therefore consume structured final-answer events instead of trying to guess the conclusion from plain text.

## Product Definition

### User-facing behavior

Add a second toggle next to the existing Feishu `语音回复` action:

- `语音回复`
  - speaks the full completed assistant reply for the current turn
- `结论语音回复`
  - speaks only the structured `final_answer` assistant text for the current turn

These modes are mutually exclusive:

- `Off`
- `FullReply`
- `FinalOnly`

If `FinalOnly` is selected and the turn has no `final_answer` text, the system must not send any audio and must not send a failure notice.

### Scope

Included:

- Feishu normal streaming replies
- Feishu card-action initiated streaming replies
- one-time CLI execution paths that produce structured Codex assistant message phases
- goal-runtime app-server turns, one turn at a time

Not included:

- text summarization or heuristic conclusion extraction
- cross-turn speech aggregation
- changing the visible Feishu card body to show only final-answer text
- adding new TTS engines or delivery channels

## Existing Constraints

### Turn boundary

Goal-runtime Feishu flows already treat each app-server turn as one card lifecycle. This is the correct speech boundary for the new mode.

### Current information loss

The current WebCode Codex streaming pipeline keeps assistant text but discards the assistant message `phase` before the Feishu streaming layer builds the completed text. That means the TTS layer currently cannot distinguish:

- commentary text
- final-answer text

The design must preserve that structure earlier in the pipeline.

### Rollout evidence

Observed local Codex rollout files contain assistant `message` items with structured `phase` values including:

- `commentary`
- `final_answer`

This confirms that the model already emits the distinction needed by the feature.

## Recommended Architecture

Use a three-layer approach:

1. Preserve assistant phase information in the Codex streaming event model
2. Build both full-reply and final-only text buffers in Feishu streaming consumers
3. Let reply-TTS mode selection choose which buffer to speak

This keeps conclusion selection out of the TTS engine and out of ad-hoc text post-processing.

## Data Model Changes

### Feishu bot config

Replace the single boolean reply-TTS switch with a mode field.

Suggested shape:

- `ReplyTtsMode = Off | FullReply | FinalOnly`
- keep `ReplyTtsVoiceId`

Backward compatibility:

- legacy `ReplyTtsEnabled = true` maps to `FullReply`
- legacy `ReplyTtsEnabled = false` maps to `Off`

### Reply TTS request

Extend the completed-reply TTS request payload to carry both texts:

- `Output`
- `FinalAnswerOutput`

Optional:

- resolved `ReplyTtsMode`

The orchestrator should not need to reconstruct speech text from raw events.

### CLI output event

Add an assistant-phase field to the structured CLI output event model.

Suggested field:

- `AssistantPhase`

Keep it as a string for now so that the pipeline can pass through provider-specific values without overfitting to Codex-only enums.

## Streaming Pipeline Changes

### Codex app-server normalization

In the Codex app-server session manager, preserve assistant phase when converting notifications into adapter-facing JSONL.

Current normalized assistant message payload includes only:

- item type
- text

The new normalized payload must also include:

- `phase`

### Codex adapter parsing

The Codex adapter must parse the assistant message phase and store it on the structured output event.

The adapter should continue to expose assistant message text through the existing extraction API so current UI rendering behavior remains unchanged.

### Feishu streaming consumers

Both Feishu streaming consumers must maintain two assistant-text buffers per turn:

- full assistant buffer
- final-answer-only buffer

Suggested behavior:

- append every assistant message text to the full buffer
- append assistant message text to the final-only buffer only when `AssistantPhase == "final_answer"`

Reset both buffers:

- when a turn ends
- when a goal-runtime turn boundary hands off to a fresh card

### Completed turn handling

At the end of a turn:

- `finalOutput` comes from the full assistant buffer as it does today
- `finalAnswerOutput` comes from the final-only buffer

The Feishu card still finishes with the full visible answer, not the final-only answer.

## TTS Selection Rules

When reply TTS is enabled:

- `FullReply`
  - speak `Output`
- `FinalOnly`
  - speak `FinalAnswerOutput`

If mode is `FinalOnly` and `FinalAnswerOutput` is empty or whitespace:

- do not synthesize
- do not upload audio
- do not send a failure notice
- do not fall back to `Output`

This silent skip is expected behavior, not an error.

## Rollout Fallback

The primary path must use the live structured stream, not disk re-read.

An optional conservative fallback may be used only when:

- tool is Codex
- mode is `FinalOnly`
- live `FinalAnswerOutput` is empty
- the turn completed normally
- thread/session context is available

Fallback behavior:

- read the newest matching rollout file
- extract only assistant `message` records with `phase = "final_answer"`
- use that text if available

If the rollout file is not flushed yet, missing, or malformed:

- skip audio silently

Do not use rollout fallback to guess or synthesize a conclusion from commentary text.

## Failure Handling

### Expected non-audio case

`FinalOnly` with no `final_answer` is not a failure.

No failure card update or TTS failure notice should be emitted.

### Actual TTS failure

If final-answer text exists but later steps fail:

- synthesis failure
- transcode failure
- upload failure
- Feishu audio send failure

then use the existing reply-TTS failure path.

### Unknown phase values

If assistant phase is present but not recognized:

- treat it as non-final
- keep full visible text behavior unchanged

## Testing

Add or update tests for the following:

- Codex app-server normalized assistant-message JSONL includes `phase`
- Codex adapter parses `AssistantPhase`
- Feishu normal streaming accumulates:
  - full assistant text
  - final-only assistant text
- Feishu card-action streaming accumulates:
  - full assistant text
  - final-only assistant text
- goal-runtime turn boundary clears both buffers
- `FullReply` still speaks the complete merged assistant reply
- `FinalOnly` speaks only `final_answer`
- `FinalOnly` with no `final_answer` sends no audio and no failure notice
- rollout fallback can recover `final_answer` when available
- rollout fallback failure remains silent

## Migration Notes

Admin and per-user Feishu settings UI must present two mutually exclusive choices without allowing conflicting stored state.

A simple compatibility path is:

- keep loading old boolean data
- map it to `FullReply` during read/update
- persist only the new mode value going forward

## Acceptance Criteria

The feature is complete when:

1. Users can choose between full-reply speech and final-only speech
2. Codex final-answer phase survives from structured stream to Feishu completion handling
3. `FinalOnly` mode never speaks commentary text
4. turns with no structured final answer produce no audio and no false failure warning
5. existing full-reply TTS behavior remains unchanged
