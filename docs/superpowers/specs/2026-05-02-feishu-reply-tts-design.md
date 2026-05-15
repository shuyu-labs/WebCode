# Feishu Reply TTS Design

Date: 2026-05-02
Status: approved in discussion, pending implementation planning

## Goal

Add an optional "reply text-to-speech" capability for Feishu conversations so that, after a streaming reply card finishes, the system can synthesize the completed assistant reply into one or more audio messages and send them back to Feishu.

The design must satisfy the following approved constraints:

- use `MeloTTS` as the TTS engine
- deploy `MeloTTS` on the same machine as `WebCode` and the Feishu bot
- do not install service files, models, caches, temp files, or `ffmpeg` on `C:` by default
- support Windows and non-Windows deployments
- if Windows only has `C:` and the administrator has not explicitly allowed system-drive installation, TTS is unavailable
- prefer GPU inference when available, and automatically fall back to CPU when GPU startup fails or is unavailable
- let each Feishu user enable or disable reply TTS in user management
- let each Feishu user choose from the runtime-discovered `MeloTTS` voice list
- if a saved voice is no longer available, automatically fall back to the platform default voice
- read the completed reply as full text, but normalize markdown, code-heavy sections, and links into speech-friendly text
- if the reply is too long, split it into multiple audio messages using paragraph and sentence boundaries first
- if synthesis, transcoding, upload, or audio send fails, preserve the text reply and append a short text failure notice

## Scope

In scope:

- Feishu-side reply TTS only
- triggering TTS after streaming reply completion
- user-level Feishu TTS preferences in the existing admin user management surface
- a local HTTP wrapper service around `MeloTTS`
- dynamic voice discovery from the local `MeloTTS` service
- audio chunking, transcoding to `opus`, Feishu upload, and Feishu audio-message send
- OS-aware storage-root resolution and system-drive safeguards
- verification and health-check paths for the local TTS stack

Out of scope:

- Web or mobile browser-side TTS playback
- provider-agnostic multi-engine TTS abstraction beyond the boundary needed to wrap `MeloTTS`
- user-provided custom voice cloning
- live streaming audio while the text reply is still generating
- automatic voice preview UI in the first version
- editing or replaying previously generated TTS jobs

## Problem

The current Feishu experience ends with a completed streaming card plus a text completion notification. Users who want an audio version of the answer must handle that manually outside the product.

The desired workflow is:

- the assistant finishes its normal streaming reply card
- the text reply remains the primary source of truth
- the system optionally performs a background TTS job
- the user receives one or more Feishu `audio` messages generated from the completed answer

There are several practical constraints that shape the design:

- Feishu supports sending `audio` messages, but the platform does not provide a public TTS API for text synthesis
- a local or external TTS engine must therefore generate audio before Feishu upload
- deployment environments differ across Windows and non-Windows hosts
- Windows hosts may have only a system drive available, and the approved rule is to avoid silently using `C:`
- runtime voice lists may change when the local `MeloTTS` installation or models change

This feature must therefore be modeled as a separate post-processing pipeline attached to Feishu reply completion, not as part of the main streaming-card execution path.

## User Experience

### Feishu Reply Completion

When a Feishu reply finishes streaming successfully, the system behavior is:

1. complete the existing streaming reply card as it does today
2. keep the existing completion text notification
3. if reply TTS is enabled for the bound Web user, start a background TTS job
4. send one or more Feishu `audio` messages after synthesis completes

The TTS job must not block the normal text reply from finishing.

### TTS User Preference

The existing Feishu bot settings area in admin user management gains:

- a toggle: `Enable reply TTS`
- a voice selector populated from the current `MeloTTS` runtime voice list
- a refresh action to reload the available voice list from the local TTS service

If the local TTS service is temporarily unavailable:

- existing saved values remain visible
- the voice selector may be disabled or show an availability warning
- saving the toggle should still be allowed
- runtime execution will use the saved voice if available, otherwise the platform default voice

If the saved voice is missing:

- the UI should indicate that the saved voice is unavailable
- runtime execution automatically falls back to the platform default voice

### Failure Messaging

If text synthesis, audio transcoding, Feishu upload, or audio send fails:

- do not alter or retract the completed text reply
- send one short text notice to Feishu, for example:
  - `⚠️ 本次文字转语音失败，已仅保留文字回复。`

Only one failure notice should be sent per completed reply, even if multiple internal steps fail.

## Trigger Rules

The approved trigger scope is:

- run reply TTS for all Feishu flows that end in a completed streaming reply card

This includes:

- normal Feishu user message execution through `FeishuChannelService`
- Feishu card-action initiated execution through `FeishuCardActionService`
- other future Feishu flows that reuse the same streaming completion path

This does not include:

- partial streaming updates before completion
- standalone help cards that do not execute a reply stream
- Web-only or mobile-only reply flows outside Feishu

## Functional Requirements

### 1. User-Level Settings

Add user-scoped Feishu bot settings for:

- `ReplyTtsEnabled`
- `ReplyTtsVoiceId`

These values are stored with the existing Feishu bot config record and are exposed through the existing admin APIs and admin modal models.

The stored voice value should be the stable runtime voice identifier, not just a display label.

### 2. Platform-Level Settings

Add platform-level settings for:

- `TtsStorageRoot`
- `AllowSystemDriveInstall`
- `TtsServiceBaseUrl`
- `TtsServiceTimeoutSeconds`
- `TtsPreferredDevice`
- `TtsDefaultVoiceId`
- `TtsChunkMaxChars`
- `FfmpegExecutablePath`

These values are deployment concerns and must not be stored per user.

### 3. Local TTS Service

Run a local HTTP service that wraps `MeloTTS`.

Required endpoints:

- `GET /health`
- `GET /voices`
- `POST /synthesize`

The `POST /synthesize` response should return synthesized audio bytes, preferably `wav`, rather than a machine-local file path contract.

### 4. GPU and CPU Fallback

The local `MeloTTS` service is responsible for choosing the actual runtime device.

Rules:

- attempt GPU first when configured to prefer GPU
- if CUDA or model initialization fails, fall back to CPU
- expose the actual device in `GET /health`
- WebCode should treat the service as a black box and should not implement its own GPU-selection logic

### 5. Speech-Friendly Text Normalization

Before synthesis, the completed reply must be normalized for speech:

- strip markdown syntax
- avoid reading raw URLs verbatim
- replace code-heavy sections with a short summary cue
- preserve normal prose and list meaning where possible

The output should still reflect the completed reply, but in a speech-friendly form rather than a literal character-for-character rendering of markdown and code.

### 6. Chunking

If the normalized text exceeds the per-chunk limit:

- split by paragraph boundaries first
- split paragraphs by sentence boundaries second
- merge short neighboring sentences where this stays within the chunk limit
- if a single sentence still exceeds the limit, fall back to smaller punctuation or hard character boundaries

The chunker should prioritize natural listening boundaries over fixed-width slicing.

### 7. Sequential Audio Delivery

For a single completed reply:

- synthesize and send chunks in order
- send audio messages sequentially, not in parallel

For a single Feishu chat:

- queue reply TTS jobs sequentially to preserve message order and reduce local resource spikes

The first version should prefer determinism and clarity over maximum throughput.

### 8. Voice Fallback

If the user-selected voice is unavailable at runtime:

- automatically fall back to the platform default voice
- continue with synthesis if the default voice is available
- only treat the job as failed if no usable runtime voice remains

### 9. Failure Behavior

Any of the following should cause the TTS job to fail gracefully:

- local TTS service unavailable
- synthesis failure
- `ffmpeg` failure
- Feishu file upload failure
- Feishu audio send failure

When a job fails:

- preserve the completed text reply
- stop processing remaining chunks for that reply
- send one short failure notice
- log detailed failure information server-side

## Architecture

### 1. Overall Topology

The system is split into three layers:

- `Feishu reply completion layer`
  - existing streaming completion points in `FeishuChannelService` and `FeishuCardActionService`
- `WebCode TTS orchestration layer`
  - settings lookup, text normalization, chunking, synthesize/transcode/upload/send orchestration
- `local MeloTTS service layer`
  - health, voice discovery, waveform generation, device fallback

This keeps TTS engine concerns separate from Feishu business flow concerns.

### 2. Recommended WebCode Components

Recommended boundaries:

- `IReplyTtsOrchestrator`
- `ITtsEligibilityService`
- `ITtsSpeechTextNormalizer`
- `ITtsChunker`
- `IMeloTtsClient`
- `IAudioTranscodeService`
- `IFeishuAudioMessageService`
- `ITtsStoragePathResolver`

The reply completion points should only trigger orchestration and should not directly implement audio-generation details.

### 3. Completion Hooking

After the existing completion flow finishes:

- keep the existing completion notification text
- keep `FinishAsync(finalOutput)`
- keep normal assistant-message persistence
- then enqueue or fire a background reply TTS job

The background TTS job must not delay the visible completion of the normal text response.

## Deployment and Path Policy

### 1. Storage Root

All TTS-related writable data must live under a single storage root:

- service files
- models
- caches
- temp files
- logs
- Python virtual environment
- optional `ffmpeg` install location

Recommended root structure:

- `<TtsStorageRoot>/service`
- `<TtsStorageRoot>/models`
- `<TtsStorageRoot>/cache`
- `<TtsStorageRoot>/temp`
- `<TtsStorageRoot>/logs`
- `<TtsStorageRoot>/venv`

### 2. Environment Variables

To prevent libraries from silently writing to undesired locations, the runtime must explicitly redirect:

- `HF_HOME`
- `TRANSFORMERS_CACHE`
- `TORCH_HOME`
- `TEMP`
- `TMP`
- `PIP_CACHE_DIR`

These should all resolve under `TtsStorageRoot`.

### 3. Windows Rules

If `TtsStorageRoot` is explicitly configured:

- use it exactly

If `TtsStorageRoot` is not configured:

- scan for the first writable non-system drive
- choose a default path such as `<Drive>:\WebCodeData\MeloTTS`

If the machine only has `C:` and `AllowSystemDriveInstall = false`:

- local reply TTS is unavailable
- the system must not silently install to `C:`
- management UI should clearly explain that only the system drive is available and policy forbids using it

If administrators want to allow `C:`:

- they must explicitly configure `AllowSystemDriveInstall = true`
- or explicitly set `TtsStorageRoot` to a `C:` path

### 4. Non-Windows Rules

If `TtsStorageRoot` is explicitly configured:

- use it exactly

If `TtsStorageRoot` is not configured:

- use a writable default such as `/data/webcode/melotts`

If the default is not writable:

- require explicit administrator configuration
- do not chain through a long list of silent fallbacks

## Runtime Flow

For one completed Feishu reply:

1. reply text completes normally
2. existing completion notification is sent
3. reply TTS enablement is checked for the bound Web user
4. completed reply text is normalized for speech
5. runtime voice is resolved
6. missing voice falls back to the platform default
7. text is split into ordered chunks
8. each chunk is synthesized to `wav`
9. each `wav` is transcoded to `opus`
10. each `opus` file is uploaded to Feishu
11. each uploaded file is sent as an `audio` message in order
12. failure at any step stops the remaining chunks and emits one short failure notice

## Admin and API Surface

The admin UI should call WebCode-owned APIs rather than talking directly to the local TTS service.

Recommended admin-facing endpoints:

- `GET /api/admin/feishu-tts/health`
- `GET /api/admin/feishu-tts/voices`

This preserves one stable browser-facing trust boundary and keeps the local Python service private to the host machine.

## Operational Requirements

- the local TTS service should produce structured logs including device choice, selected voice, chunk counts, and failures
- temp files should be deleted after success
- failed jobs may retain temp files briefly for diagnostics
- a cleanup task should delete stale temp artifacts after a bounded retention window
- health checks should reveal whether the service is running on GPU or CPU

## Testing Strategy

### Unit-Level

- user setting mapping and persistence
- path resolution across Windows and non-Windows cases
- Windows-only-`C:` unavailability behavior
- speech text normalization
- chunking logic
- voice fallback resolution

### Integration-Level

- local `MeloTTS` service health and voice enumeration
- successful synthesis to `wav`
- successful `wav` to `opus` transcode
- Feishu upload and `audio` send with a stubbed or test client

### End-to-End

- normal Feishu message reply completion followed by audio
- Feishu card-action initiated reply completion followed by audio
- missing saved voice with default-voice fallback
- TTS failure path with a single text failure notice
- oversized replies split into multiple ordered audio messages

## Design Principles

1. Keep normal text completion authoritative and non-blocking.
2. Treat TTS as an asynchronous Feishu post-processing pipeline.
3. Keep `MeloTTS` engine concerns behind a narrow local HTTP service.
4. Never silently fall back to Windows `C:` when policy forbids it.
5. Prefer explicit operational behavior over clever hidden fallback chains.
6. Preserve user trust by sending either ordered audio or one clear failure notice, never a noisy mix of partial errors.
