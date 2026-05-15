# Feishu Reply TTS Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add optional Feishu reply TTS so every completed Feishu streaming reply can asynchronously produce one or more `audio` messages using a same-host `MeloTTS` service, user-level voice preferences, non-`C:` storage rules, and graceful failure fallback.

**Architecture:** Keep the current Feishu streaming-card execution flow authoritative for text completion, then enqueue a background reply-TTS orchestration pass after the card finishes. Split the feature into four boundaries: user/platform configuration, a local `MeloTTS` HTTP client plus platform availability service, speech normalization/chunking/transcode/audio delivery services, and lightweight completion hooks in `FeishuChannelService` and `FeishuCardActionService`.

**Tech Stack:** ASP.NET Core, Blazor Server, `HttpClient`, `System.Diagnostics.Process`, Feishu Open Platform IM APIs, SqlSugar code-first entities, xUnit v2/v3 test projects, FastAPI + Python for the local `MeloTTS` wrapper, existing DI scanning via `ServiceDescriptionAttribute`.

Depends on:
- `docs/superpowers/specs/2026-05-02-feishu-reply-tts-design.md`

---

## File Map

### Platform config and path policy

- Create: `WebCodeCli.Domain/Common/Options/FeishuReplyTtsOptions.cs`
  Platform-wide TTS settings: storage root, system-drive policy, service base URL, timeout, default voice, chunk size, and `ffmpeg` path.
- Create: `WebCodeCli.Domain/Domain/Model/Channels/FeishuReplyTtsHealthStatus.cs`
  DTO for admin health checks and runtime platform availability.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsStorageRootResolver.cs`
  OS-aware storage-root policy for Windows and non-Windows hosts, including the Windows-only-`C:` rejection rule.
- Modify: `WebCodeCli.Domain/Common/Extensions/ServiceCollectionExtensions.cs`
  Bind the new options section and register any named `HttpClient` usage needed by the TTS client.
- Modify: `WebCodeCli/appsettings.json`
  Add the sample `FeishuReplyTts` section with safe defaults and blank operator-supplied paths.

### User config and admin surface

- Modify: `WebCodeCli.Domain/Repositories/Base/UserFeishuBotConfig/UserFeishuBotConfigEntity.cs`
  Add `ReplyTtsEnabled` and `ReplyTtsVoiceId`.
- Modify: `WebCodeCli.Domain/Domain/Service/UserFeishuBotConfigService.cs`
  Normalize and persist the new user-level TTS settings.
- Modify: `WebCodeCli/Controllers/AdminController.cs`
  Expose the new Feishu bot config fields and add admin endpoints for TTS health and voice discovery.
- Create: `WebCodeCli/Helpers/AdminUserManagementReplyTtsUiState.cs`
  Small helper for modal warnings and selector state so the UI logic is unit-testable without bUnit.
- Modify: `WebCodeCli/Components/AdminUserManagementModal.razor`
  Add the TTS toggle, voice selector, health message, and reload button.
- Modify: `WebCodeCli/Components/AdminUserManagementModal.razor.cs`
  Load health/voices, carry the new DTO fields, and wire save/load behavior through the existing admin API.

### TTS runtime orchestration

- Create: `WebCodeCli.Domain/Domain/Model/Channels/FeishuReplyTtsVoiceOption.cs`
  Runtime-discovered voice descriptor returned by the local service.
- Create: `WebCodeCli.Domain/Domain/Model/Channels/FeishuCompletedReplyTtsRequest.cs`
  Request contract for a completed Feishu reply that should be queued for TTS.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuReplyTtsPlatformService.cs`
  Contract for health checks, voice enumeration, storage-root availability, and voice fallback resolution.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/FeishuReplyTtsPlatformService.cs`
  Platform availability service that combines path policy and local `MeloTTS` health/voice discovery.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/IMeloTtsClient.cs`
  Narrow HTTP client contract for `GET /health`, `GET /voices`, and `POST /synthesize`.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/MeloTtsClient.cs`
  Same-host HTTP client for the local Python TTS wrapper.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsSpeechTextNormalizer.cs`
  Converts markdown-heavy assistant output into speech-friendly text.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsChunker.cs`
  Paragraph-first, sentence-second chunking service with max-char enforcement.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/IExternalProcessRunner.cs`
  Tiny abstraction for invoking `ffmpeg` without binding unit tests to real subprocesses.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/ExternalProcessRunner.cs`
  Production process runner used by the transcode service.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/IAudioTranscodeService.cs`
  Contract for `wav` to `opus` conversion under the approved storage root.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/AudioTranscodeService.cs`
  `ffmpeg`-backed transcode implementation that writes only under the resolved storage root.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuAudioMessageService.cs`
  Contract for upload + ordered audio send to Feishu.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/FeishuAudioMessageService.cs`
  Feishu delivery service that resolves effective Feishu options and sends `audio` messages.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/IReplyTtsOrchestrator.cs`
  Public entrypoint for queuing a completed reply for background TTS work.
- Create: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsOrchestrator.cs`
  Background orchestrator that normalizes text, resolves voices, chunks, synthesizes, transcodes, uploads, and sends failure notices.

### Feishu API surface and completion hooks

- Modify: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuCardKitClient.cs`
  Add file-upload and audio-message methods.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardKitClient.cs`
  Implement Feishu file upload and `audio` send using the official IM APIs.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs`
  Queue reply TTS after successful normal streaming completion.
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
  Queue reply TTS after card-action streaming completion, including the low-interruption completion path.

### Local `MeloTTS` wrapper service and deployment docs

- Create: `tools/melotts-service/README.md`
  Setup and operational instructions, including non-`C:` storage and environment variables.
- Create: `tools/melotts-service/requirements.txt`
  Python dependencies for FastAPI, Uvicorn, and `MeloTTS`.
- Create: `tools/melotts-service/app.py`
  Local HTTP wrapper exposing `/health`, `/voices`, and `/synthesize` with GPU-to-CPU fallback.
- Create: `tools/melotts-service/start.ps1`
  Windows startup helper that refuses to default to `C:` unless explicitly allowed.
- Create: `tools/melotts-service/start.sh`
  Non-Windows startup helper for the same service.
- Create: `tools/melotts-service/tests/test_app.py`
  Lightweight FastAPI tests with a fake engine adapter.

### Tests

- Create: `WebCodeCli.Domain.Tests/ReplyTtsStorageRootResolverTests.cs`
- Create: `WebCodeCli.Domain.Tests/UserFeishuBotConfigServiceTests.cs`
- Create: `WebCodeCli.Domain.Tests/MeloTtsClientTests.cs`
- Create: `WebCodeCli.Domain.Tests/FeishuReplyTtsPlatformServiceTests.cs`
- Create: `WebCodeCli.Domain.Tests/ReplyTtsSpeechTextNormalizerTests.cs`
- Create: `WebCodeCli.Domain.Tests/ReplyTtsChunkerTests.cs`
- Create: `WebCodeCli.Domain.Tests/AudioTranscodeServiceTests.cs`
- Create: `WebCodeCli.Domain.Tests/FeishuAudioMessageServiceTests.cs`
- Create: `WebCodeCli.Domain.Tests/ReplyTtsOrchestratorTests.cs`
- Create: `tests/WebCodeCli.Tests/AdminControllerReplyTtsTests.cs`
- Create: `tests/WebCodeCli.Tests/AdminUserManagementReplyTtsUiStateTests.cs`
- Modify: `WebCodeCli.Domain.Tests/FeishuCardKitClientTests.cs`
- Modify: `WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs`
- Modify: `WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs`

### Explicit non-goals

- Do not add browser-side TTS playback.
- Do not add voice cloning or user-provided reference audio.
- Do not block text reply completion on TTS completion.
- Do not silently install to `C:` when Windows policy forbids it.
- Do not bundle a Dockerized `MeloTTS` service in this first implementation.

---

## Chunk 1: Establish Platform Policy and User Settings

### Task 1: Add the platform options and storage-root resolver

**Files:**
- Create: `WebCodeCli.Domain/Common/Options/FeishuReplyTtsOptions.cs`
- Create: `WebCodeCli.Domain/Domain/Model/Channels/FeishuReplyTtsHealthStatus.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsStorageRootResolver.cs`
- Modify: `WebCodeCli.Domain/Common/Extensions/ServiceCollectionExtensions.cs`
- Modify: `WebCodeCli/appsettings.json`
- Test: `WebCodeCli.Domain.Tests/ReplyTtsStorageRootResolverTests.cs`

- [ ] **Step 1: Write the failing resolver tests first**

Add tests that prove:

- an explicit `TtsStorageRoot` always wins
- Windows picks the first writable non-system drive when no explicit root is set
- Windows with only `C:` and `AllowSystemDriveInstall = false` returns an unavailable result with a clear message
- Windows with only `C:` and `AllowSystemDriveInstall = true` resolves a `C:`-based root
- non-Windows uses `/data/webcode/melotts` when no explicit root is set
- environment subpaths for `models`, `cache`, `temp`, `logs`, and `venv` are all rooted under the resolved storage root

Use a fake host-environment abstraction so the tests do not depend on the real machine's drives.

```csharp
[Fact]
public void Resolve_WhenWindowsOnlyHasSystemDriveAndPolicyForbidsIt_ReturnsUnavailable()
{
    var options = new FeishuReplyTtsOptions
    {
        AllowSystemDriveInstall = false,
        TtsStorageRoot = null
    };
    var environment = new StubReplyTtsHostEnvironment(
        isWindows: true,
        systemDriveRoot: @"C:\",
        writableRoots: [@"C:\"]);

    var result = new ReplyTtsStorageRootResolver(environment).Resolve(options);

    Assert.False(result.IsAvailable);
    Assert.Contains("only the system drive", result.Message, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run the focused test command and confirm it fails**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTtsStorageRootResolverTests"
```

Expected:

- FAIL because the new options type and resolver do not exist yet

- [ ] **Step 3: Implement the options type and resolver**

Create `FeishuReplyTtsOptions` with the approved platform-level settings:

```csharp
public sealed class FeishuReplyTtsOptions
{
    public string? TtsStorageRoot { get; set; }
    public bool AllowSystemDriveInstall { get; set; }
    public string TtsServiceBaseUrl { get; set; } = "http://127.0.0.1:5057";
    public int TtsServiceTimeoutSeconds { get; set; } = 60;
    public string TtsPreferredDevice { get; set; } = "gpu-auto";
    public string? TtsDefaultVoiceId { get; set; }
    public int TtsChunkMaxChars { get; set; } = 1200;
    public string? FfmpegExecutablePath { get; set; }
}
```

Implement `ReplyTtsStorageRootResolver` so it returns a structured result with:

- `IsAvailable`
- `StorageRoot`
- `Message`
- helper properties for `ModelsRoot`, `CacheRoot`, `TempRoot`, `LogsRoot`, and `VenvRoot`

The resolver must reject Windows-only-`C:` deployments unless the administrator explicitly allows system-drive installation.

- [ ] **Step 4: Bind the new options and add the sample config section**

Bind the new section in `AddFeishuChannel(...)` and add a sample `FeishuReplyTts` block to `WebCodeCli/appsettings.json`.

Use blank operator-supplied paths instead of hard-coded `C:` or `D:` paths:

```json
"FeishuReplyTts": {
  "TtsStorageRoot": "",
  "AllowSystemDriveInstall": false,
  "TtsServiceBaseUrl": "http://127.0.0.1:5057",
  "TtsServiceTimeoutSeconds": 60,
  "TtsPreferredDevice": "gpu-auto",
  "TtsDefaultVoiceId": "",
  "TtsChunkMaxChars": 1200,
  "FfmpegExecutablePath": ""
}
```

- [ ] **Step 5: Re-run the focused tests and confirm they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTtsStorageRootResolverTests"
```

Expected:

- PASS

- [ ] **Step 6: Commit the platform-policy chunk**

```powershell
git add WebCodeCli.Domain/Common/Options/FeishuReplyTtsOptions.cs WebCodeCli.Domain/Domain/Model/Channels/FeishuReplyTtsHealthStatus.cs WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsStorageRootResolver.cs WebCodeCli.Domain/Common/Extensions/ServiceCollectionExtensions.cs WebCodeCli/appsettings.json WebCodeCli.Domain.Tests/ReplyTtsStorageRootResolverTests.cs
git commit -m "feat: add Feishu reply TTS platform path policy"
```

### Task 2: Persist user-level TTS settings in the Feishu bot config

**Files:**
- Modify: `WebCodeCli.Domain/Repositories/Base/UserFeishuBotConfig/UserFeishuBotConfigEntity.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/UserFeishuBotConfigService.cs`
- Modify: `WebCodeCli/Controllers/AdminController.cs`
- Test: `WebCodeCli.Domain.Tests/UserFeishuBotConfigServiceTests.cs`
- Test: `tests/WebCodeCli.Tests/AdminControllerReplyTtsTests.cs`

- [ ] **Step 1: Write the failing persistence and controller tests**

Add tests that prove:

- saving a Feishu bot config preserves `ReplyTtsEnabled` and `ReplyTtsVoiceId`
- updates overwrite previous TTS values instead of leaving stale data
- blank voice ids normalize to `null`
- `AdminController.GetFeishuBotConfig(...)` returns the new fields
- `AdminController.SaveFeishuBotConfig(...)` forwards the new fields into `UserFeishuBotConfigEntity`

Use the same stub-repository style already used in `UserFeishuBotRuntimeServiceTests` and simple handmade controller stubs in the Web test project.

```csharp
[Fact]
public async Task SaveAsync_UpdatesReplyTtsFields()
{
    var repository = new InMemoryUserFeishuBotConfigRepository();
    await repository.InsertAsync(new UserFeishuBotConfigEntity
    {
        Username = "alice",
        ReplyTtsEnabled = false,
        ReplyTtsVoiceId = "old-voice"
    });

    var service = CreateService(repository);
    await service.SaveAsync(new UserFeishuBotConfigEntity
    {
        Username = "alice",
        ReplyTtsEnabled = true,
        ReplyTtsVoiceId = "zh_female"
    });

    var saved = await repository.GetByUsernameAsync("alice");
    Assert.True(saved!.ReplyTtsEnabled);
    Assert.Equal("zh_female", saved.ReplyTtsVoiceId);
}
```

- [ ] **Step 2: Run the focused failing tests**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~UserFeishuBotConfigServiceTests"
dotnet test D:\VSWorkshop\WebCode\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~AdminControllerReplyTtsTests"
```

Expected:

- FAIL because the entity and DTOs do not have the new fields yet

- [ ] **Step 3: Add the new entity and service fields**

Add these properties to `UserFeishuBotConfigEntity`:

```csharp
public bool ReplyTtsEnabled { get; set; }
public string? ReplyTtsVoiceId { get; set; }
```

Update `UserFeishuBotConfigService.SaveAsync(...)` and `NormalizeConfig(...)` so the new fields round-trip correctly.

- [ ] **Step 4: Extend the admin DTO mapping**

Update `UserFeishuBotConfigDto`, `MapFeishuConfig(...)`, and `SaveFeishuBotConfig(...)` so the admin API round-trips:

- `ReplyTtsEnabled`
- `ReplyTtsVoiceId`

Do not add per-user platform config here; only the user preference fields belong in this DTO.

- [ ] **Step 5: Re-run the focused tests and confirm they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~UserFeishuBotConfigServiceTests"
dotnet test D:\VSWorkshop\WebCode\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~AdminControllerReplyTtsTests"
```

Expected:

- PASS

- [ ] **Step 6: Commit the user-settings chunk**

```powershell
git add WebCodeCli.Domain/Repositories/Base/UserFeishuBotConfig/UserFeishuBotConfigEntity.cs WebCodeCli.Domain/Domain/Service/UserFeishuBotConfigService.cs WebCodeCli/Controllers/AdminController.cs WebCodeCli.Domain.Tests/UserFeishuBotConfigServiceTests.cs tests/WebCodeCli.Tests/AdminControllerReplyTtsTests.cs
git commit -m "feat: persist Feishu reply TTS user settings"
```

---

## Chunk 2: Build the Admin Voice Discovery Surface

### Task 3: Add the local `MeloTTS` client, platform service, and admin health/voice endpoints

**Files:**
- Create: `WebCodeCli.Domain/Domain/Model/Channels/FeishuReplyTtsVoiceOption.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/IMeloTtsClient.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/MeloTtsClient.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuReplyTtsPlatformService.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/FeishuReplyTtsPlatformService.cs`
- Modify: `WebCodeCli/Controllers/AdminController.cs`
- Test: `WebCodeCli.Domain.Tests/MeloTtsClientTests.cs`
- Test: `WebCodeCli.Domain.Tests/FeishuReplyTtsPlatformServiceTests.cs`
- Test: `tests/WebCodeCli.Tests/AdminControllerReplyTtsTests.cs`

- [ ] **Step 1: Write the failing client and platform-service tests**

Add tests that prove:

- `MeloTtsClient.GetHealthAsync()` parses the local service response
- `MeloTtsClient.GetVoicesAsync()` parses the voice list
- `FeishuReplyTtsPlatformService` reports unavailable when storage-root resolution fails
- `FeishuReplyTtsPlatformService` merges resolver availability with local service health
- `FeishuReplyTtsPlatformService` returns the runtime voice list
- `ResolveVoiceOrFallbackAsync(...)` prefers the saved voice, then the default voice, then fails cleanly

Use a stub `HttpMessageHandler` for the client and stubbed resolver results for the platform service.

```csharp
[Fact]
public async Task ResolveVoiceOrFallbackAsync_WhenSavedVoiceIsMissing_UsesDefaultVoice()
{
    var service = CreatePlatformService(
        voices: [new FeishuReplyTtsVoiceOption { VoiceId = "default-zh", DisplayName = "Default" }],
        defaultVoiceId: "default-zh");

    var result = await service.ResolveVoiceOrFallbackAsync("missing-voice");

    Assert.True(result.Success);
    Assert.Equal("default-zh", result.VoiceId);
    Assert.True(result.UsedFallback);
}
```

- [ ] **Step 2: Run the focused failing tests**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~MeloTtsClientTests|FullyQualifiedName~FeishuReplyTtsPlatformServiceTests"
dotnet test D:\VSWorkshop\WebCode\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~AdminControllerReplyTtsTests"
```

Expected:

- FAIL because the TTS client, platform service, and endpoints do not exist yet

- [ ] **Step 3: Implement the local client and platform service**

Use a narrow contract:

```csharp
public interface IMeloTtsClient
{
    Task<FeishuReplyTtsHealthStatus> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FeishuReplyTtsVoiceOption>> GetVoicesAsync(CancellationToken cancellationToken = default);
    Task<Stream> SynthesizeAsync(string text, string voiceId, CancellationToken cancellationToken = default);
}
```

Implement `FeishuReplyTtsPlatformService` with methods:

- `GetHealthAsync()`
- `GetVoicesAsync()`
- `ResolveVoiceOrFallbackAsync(string? savedVoiceId)`

The service must short-circuit to an unavailable health result when the path resolver says the platform is not allowed to run.

- [ ] **Step 4: Add admin endpoints**

Add:

- `GET /api/admin/feishu-tts/health`
- `GET /api/admin/feishu-tts/voices`

These endpoints should return WebCode-owned DTOs and should not expose the Python service directly to the browser.

- [ ] **Step 5: Re-run the focused tests and confirm they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~MeloTtsClientTests|FullyQualifiedName~FeishuReplyTtsPlatformServiceTests"
dotnet test D:\VSWorkshop\WebCode\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~AdminControllerReplyTtsTests"
```

Expected:

- PASS

- [ ] **Step 6: Commit the voice-discovery chunk**

```powershell
git add WebCodeCli.Domain/Domain/Model/Channels/FeishuReplyTtsVoiceOption.cs WebCodeCli.Domain/Domain/Service/Channels/IMeloTtsClient.cs WebCodeCli.Domain/Domain/Service/Channels/MeloTtsClient.cs WebCodeCli.Domain/Domain/Service/Channels/IFeishuReplyTtsPlatformService.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuReplyTtsPlatformService.cs WebCodeCli/Controllers/AdminController.cs WebCodeCli.Domain.Tests/MeloTtsClientTests.cs WebCodeCli.Domain.Tests/FeishuReplyTtsPlatformServiceTests.cs tests/WebCodeCli.Tests/AdminControllerReplyTtsTests.cs
git commit -m "feat: add MeloTTS health and voice discovery"
```

### Task 4: Wire the admin user-management modal to the new TTS settings and voice list

**Files:**
- Create: `WebCodeCli/Helpers/AdminUserManagementReplyTtsUiState.cs`
- Modify: `WebCodeCli/Components/AdminUserManagementModal.razor`
- Modify: `WebCodeCli/Components/AdminUserManagementModal.razor.cs`
- Test: `tests/WebCodeCli.Tests/AdminUserManagementReplyTtsUiStateTests.cs`

- [ ] **Step 1: Write the failing UI-state helper tests**

Add tests that prove:

- the voice selector is disabled when reply TTS is off
- the voice selector is disabled when platform health is unavailable
- a missing saved voice produces a fallback warning
- a healthy platform with voices produces no warning

Keep the helper pure so it can be unit-tested without a Razor harness.

```csharp
[Fact]
public void Build_WhenSavedVoiceIsMissing_ReturnsFallbackWarning()
{
    var state = AdminUserManagementReplyTtsUiState.Build(
        replyTtsEnabled: true,
        savedVoiceId: "missing",
        availableVoices: [new AdminReplyTtsVoiceOption("default-zh", "默认音色")],
        platformAvailable: true,
        platformMessage: null);

    Assert.Contains("fallback", state.WarningMessage, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run the focused failing helper tests**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~AdminUserManagementReplyTtsUiStateTests"
```

Expected:

- FAIL because the helper does not exist yet

- [ ] **Step 3: Implement the helper and modal wiring**

Add the helper and update the modal to:

- load `/api/admin/feishu-tts/health` and `/api/admin/feishu-tts/voices`
- carry `ReplyTtsEnabled` and `ReplyTtsVoiceId` through the nested config models
- render the toggle, voice dropdown, refresh action, and warning copy
- keep saved values visible even when health is temporarily unavailable

Add the nested DTO fields in `AdminUserManagementModal.razor.cs`:

```csharp
public bool ReplyTtsEnabled { get; set; }
public string? ReplyTtsVoiceId { get; set; }
```

- [ ] **Step 4: Run the helper tests and a compile-driven Razor check**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~AdminUserManagementReplyTtsUiStateTests"
dotnet msbuild D:\VSWorkshop\WebCode\WebCodeCli\WebCodeCli.csproj /t:CoreCompile /nologo
```

Expected:

- helper tests PASS
- `CoreCompile` PASS, confirming the Razor/C# modal wiring compiles

- [ ] **Step 5: Commit the admin-modal chunk**

```powershell
git add WebCodeCli/Helpers/AdminUserManagementReplyTtsUiState.cs WebCodeCli/Components/AdminUserManagementModal.razor WebCodeCli/Components/AdminUserManagementModal.razor.cs tests/WebCodeCli.Tests/AdminUserManagementReplyTtsUiStateTests.cs
git commit -m "feat: add Feishu reply TTS controls to admin modal"
```

---

## Chunk 3: Build the Speech Pipeline and Feishu Audio Delivery

### Task 5: Add speech normalization, chunking, and transcode services

**Files:**
- Create: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsSpeechTextNormalizer.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsChunker.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/IExternalProcessRunner.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/ExternalProcessRunner.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/IAudioTranscodeService.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/AudioTranscodeService.cs`
- Test: `WebCodeCli.Domain.Tests/ReplyTtsSpeechTextNormalizerTests.cs`
- Test: `WebCodeCli.Domain.Tests/ReplyTtsChunkerTests.cs`
- Test: `WebCodeCli.Domain.Tests/AudioTranscodeServiceTests.cs`

- [ ] **Step 1: Write the failing text and transcode tests**

Add tests that prove:

- markdown headings, emphasis markers, and bullet syntax are removed cleanly
- raw links are dropped or replaced with a short cue instead of being read verbatim
- code blocks are replaced with a short summary cue
- short paragraphs stay in one chunk
- long replies split on paragraph and sentence boundaries before falling back to hard breaks
- `AudioTranscodeService` rejects missing `ffmpeg` configuration
- `AudioTranscodeService` writes outputs under the resolved temp root and invokes `ffmpeg` with `libopus`, mono audio, and 16 kHz output

Use a fake `IExternalProcessRunner` in the transcode tests instead of launching real `ffmpeg`.

```csharp
[Fact]
public void Chunk_WhenParagraphsExceedLimit_SplitsOnSentenceBoundariesFirst()
{
    var chunker = new ReplyTtsChunker(maxChars: 20);
    var chunks = chunker.Split("第一句。第二句很短。\n\n第三段也很短。");

    Assert.Collection(chunks,
        chunk => Assert.Equal("第一句。第二句很短。", chunk),
        chunk => Assert.Equal("第三段也很短。", chunk));
}
```

- [ ] **Step 2: Run the focused failing tests**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTtsSpeechTextNormalizerTests|FullyQualifiedName~ReplyTtsChunkerTests|FullyQualifiedName~AudioTranscodeServiceTests"
```

Expected:

- FAIL because the normalizer, chunker, and transcode services do not exist yet

- [ ] **Step 3: Implement the normalizer and chunker**

Implement a speech-friendly normalizer that:

- strips markdown syntax
- replaces fenced code blocks with a short fixed sentence
- removes or shortens URLs
- preserves natural prose and list meaning

Implement a chunker that:

- splits on blank lines first
- then sentence punctuation
- then falls back to smaller punctuation or hard breaks only when needed

- [ ] **Step 4: Implement the transcode service**

Implement `AudioTranscodeService` so it:

- validates `FfmpegExecutablePath`
- creates a per-job temp folder under the resolved TTS temp root
- invokes `ffmpeg` through `IExternalProcessRunner`
- produces deterministic `chunk-001.opus` style output paths

Use a command shape like:

```text
ffmpeg -y -i <input.wav> -acodec libopus -ac 1 -ar 16000 <output.opus>
```

- [ ] **Step 5: Re-run the focused tests and confirm they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTtsSpeechTextNormalizerTests|FullyQualifiedName~ReplyTtsChunkerTests|FullyQualifiedName~AudioTranscodeServiceTests"
```

Expected:

- PASS

- [ ] **Step 6: Commit the speech-pipeline chunk**

```powershell
git add WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsSpeechTextNormalizer.cs WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsChunker.cs WebCodeCli.Domain/Domain/Service/Channels/IExternalProcessRunner.cs WebCodeCli.Domain/Domain/Service/Channels/ExternalProcessRunner.cs WebCodeCli.Domain/Domain/Service/Channels/IAudioTranscodeService.cs WebCodeCli.Domain/Domain/Service/Channels/AudioTranscodeService.cs WebCodeCli.Domain.Tests/ReplyTtsSpeechTextNormalizerTests.cs WebCodeCli.Domain.Tests/ReplyTtsChunkerTests.cs WebCodeCli.Domain.Tests/AudioTranscodeServiceTests.cs
git commit -m "feat: add Feishu reply TTS speech pipeline"
```

### Task 6: Add Feishu file upload and audio-message delivery support

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuCardKitClient.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardKitClient.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/IFeishuAudioMessageService.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/FeishuAudioMessageService.cs`
- Test: `WebCodeCli.Domain.Tests/FeishuCardKitClientTests.cs`
- Test: `WebCodeCli.Domain.Tests/FeishuAudioMessageServiceTests.cs`

- [ ] **Step 1: Write the failing upload/send tests**

Add tests that prove:

- `FeishuCardKitClient.UploadAudioFileAsync(...)` posts `multipart/form-data` to `/open-apis/im/v1/files`
- the upload uses `file_type=opus`
- the upload forwards the duration in milliseconds
- `SendAudioMessageAsync(...)` sends `msg_type = "audio"` with `{"file_key":"..."}` content
- `FeishuAudioMessageService` resolves effective Feishu options via username or app id and sends audio in order

Use the existing stubbed HTTP-client pattern from `FeishuCardKitClientTests` instead of real network calls.

```csharp
[Fact]
public async Task SendAudioMessageAsync_SendsAudioPayload()
{
    var client = CreateClient();
    await client.SendAudioMessageAsync("oc_xxx", "file_v2_123", 3200, optionsOverride: CreateOptions());

    Assert.Equal("audio", client.LastPayload!.GetProperty("msg_type").GetString());
    Assert.Contains("file_v2_123", client.LastPayload!.GetProperty("content").GetString());
}
```

- [ ] **Step 2: Run the focused failing tests**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuCardKitClientTests|FullyQualifiedName~FeishuAudioMessageServiceTests"
```

Expected:

- FAIL because the upload/audio methods do not exist yet

- [ ] **Step 3: Implement the upload and send methods**

Extend `IFeishuCardKitClient` and `FeishuCardKitClient` with:

```csharp
Task<string> UploadAudioFileAsync(string filePath, int durationMs, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null);
Task<string> SendAudioMessageAsync(string chatId, string fileKey, int durationMs, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null);
```

Then implement `FeishuAudioMessageService` so it:

- resolves effective `FeishuOptions`
- uploads the `opus` file
- sends the resulting `file_key` as an `audio` message

Keep this logic out of the orchestrator so upload/send failures can be tested independently.

- [ ] **Step 4: Re-run the focused tests and confirm they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuCardKitClientTests|FullyQualifiedName~FeishuAudioMessageServiceTests"
```

Expected:

- PASS

- [ ] **Step 5: Commit the Feishu-audio-delivery chunk**

```powershell
git add WebCodeCli.Domain/Domain/Service/Channels/IFeishuCardKitClient.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuCardKitClient.cs WebCodeCli.Domain/Domain/Service/Channels/IFeishuAudioMessageService.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuAudioMessageService.cs WebCodeCli.Domain.Tests/FeishuCardKitClientTests.cs WebCodeCli.Domain.Tests/FeishuAudioMessageServiceTests.cs
git commit -m "feat: add Feishu audio upload and send support"
```

---

## Chunk 4: Queue Background TTS After Completed Replies

### Task 7: Build the reply-TTS orchestrator and hook normal Feishu reply completion

**Files:**
- Create: `WebCodeCli.Domain/Domain/Model/Channels/FeishuCompletedReplyTtsRequest.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/IReplyTtsOrchestrator.cs`
- Create: `WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsOrchestrator.cs`
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs`
- Test: `WebCodeCli.Domain.Tests/ReplyTtsOrchestratorTests.cs`
- Test: `WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs`

- [ ] **Step 1: Write the failing orchestrator and channel-hook tests**

Add tests that prove:

- disabled `ReplyTtsEnabled` skips synthesis entirely
- empty or normalization-only-empty output skips synthesis
- a missing saved voice falls back to the platform default voice
- chunk synthesis, transcode, upload, and send happen in order
- one failed chunk stops the remaining chunks and sends exactly one text failure notice
- two jobs for the same chat do not interleave
- `FeishuChannelService` queues TTS only after successful completion, not on error or superseded execution

Use fakes for the platform service, client, transcode service, audio service, and message sender.

```csharp
[Fact]
public async Task QueueCompletedReplyAsync_WhenChunkFails_SendsSingleFailureNotice()
{
    var orchestrator = CreateOrchestrator(chunkFailureAt: 2);

    await orchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyTtsRequest
    {
        ChatId = "oc_chat",
        Username = "alice",
        AppId = "cli_app",
        SessionId = "session-1",
        ReplyText = "第一段。\n\n第二段。"
    });

    Assert.Equal(1, orchestrator.SentFailureNotices.Count);
    Assert.Equal(1, orchestrator.AudioMessagesSent.Count);
}
```

- [ ] **Step 2: Run the focused failing tests**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTtsOrchestratorTests|FullyQualifiedName~FeishuChannelServiceTests"
```

Expected:

- FAIL because the orchestrator and completion hook do not exist yet

- [ ] **Step 3: Implement the orchestrator**

Implement a singleton orchestrator with a public method like:

```csharp
Task QueueCompletedReplyAsync(FeishuCompletedReplyTtsRequest request);
```

Behavior rules:

- do not block the caller on actual TTS completion
- serialize work per chat, for example with a `ConcurrentDictionary<string, SemaphoreSlim>`
- normalize the completed reply
- resolve the saved voice or default-voice fallback
- split into ordered chunks
- for each chunk: synthesize `wav`, transcode to `opus`, upload, send
- on failure: stop remaining chunks and send one short text notice

Keep temp files under the resolver's `TempRoot` and clean them on success.

- [ ] **Step 4: Hook `FeishuChannelService` after successful completion**

After the existing completion flow finishes and the assistant message is persisted, resolve `IReplyTtsOrchestrator` from the existing service provider and queue:

```csharp
await replyTtsOrchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyTtsRequest
{
    ChatId = chatId,
    Username = username,
    AppId = appId,
    SessionId = sessionId,
    ReplyText = finalOutput
});
```

Do this only on the successful completion path. Do not queue on cancellation, supersession, or error.

- [ ] **Step 5: Re-run the focused tests and confirm they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTtsOrchestratorTests|FullyQualifiedName~FeishuChannelServiceTests"
```

Expected:

- PASS

- [ ] **Step 6: Commit the orchestrator chunk**

```powershell
git add WebCodeCli.Domain/Domain/Model/Channels/FeishuCompletedReplyTtsRequest.cs WebCodeCli.Domain/Domain/Service/Channels/IReplyTtsOrchestrator.cs WebCodeCli.Domain/Domain/Service/Channels/ReplyTtsOrchestrator.cs WebCodeCli.Domain/Domain/Service/Channels/FeishuChannelService.cs WebCodeCli.Domain.Tests/ReplyTtsOrchestratorTests.cs WebCodeCli.Domain.Tests/FeishuChannelServiceTests.cs
git commit -m "feat: queue reply TTS after Feishu completion"
```

### Task 8: Hook card-action and low-interruption streaming completion into the same orchestrator

**Files:**
- Modify: `WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs`
- Test: `WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs`

- [ ] **Step 1: Write the failing card-action completion tests**

Add tests that prove:

- normal card-action streaming completion queues reply TTS
- low-interruption completion also queues reply TTS
- error completion does not queue reply TTS
- the queued request carries the chat id, username, app id, session id, and final assistant output

Use the existing `FeishuCardActionServiceTests` stubs and extend the service-provider stub to supply a fake orchestrator.

- [ ] **Step 2: Run the focused failing tests**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuCardActionServiceTests"
```

Expected:

- FAIL because `FeishuCardActionService` does not queue reply TTS yet

- [ ] **Step 3: Implement the card-action completion hooks**

Queue the same `FeishuCompletedReplyTtsRequest` in both successful completion methods:

- the normal card-action streaming completion path
- the low-interruption completion path

Use the same lazy service-provider resolution approach as Task 7 to avoid broad constructor churn.

- [ ] **Step 4: Re-run the focused tests and confirm they pass**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~FeishuCardActionServiceTests"
```

Expected:

- PASS

- [ ] **Step 5: Commit the card-action hook chunk**

```powershell
git add WebCodeCli.Domain/Domain/Service/Channels/FeishuCardActionService.cs WebCodeCli.Domain.Tests/FeishuCardActionServiceTests.cs
git commit -m "feat: queue reply TTS for Feishu card actions"
```

---

## Chunk 5: Add the Local `MeloTTS` Wrapper and Deployment Assets

### Task 9: Create the local Python service, startup scripts, and operator docs

**Files:**
- Create: `tools/melotts-service/README.md`
- Create: `tools/melotts-service/requirements.txt`
- Create: `tools/melotts-service/app.py`
- Create: `tools/melotts-service/start.ps1`
- Create: `tools/melotts-service/start.sh`
- Create: `tools/melotts-service/tests/test_app.py`

- [ ] **Step 1: Write the failing Python service tests**

Add tests that prove:

- `/health` reports the active device and default voice
- `/voices` returns a normalized list of voices
- `/synthesize` rejects blank input
- if GPU engine initialization fails, the app falls back to CPU and still serves `/health`

Keep the FastAPI app structured around a tiny engine adapter so tests can inject a fake adapter instead of importing the real `MeloTTS` runtime.

```python
def test_health_reports_cpu_fallback(test_client):
    response = test_client.get("/health")
    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "ok"
    assert body["device"] in {"cpu", "cuda"}
```

- [ ] **Step 2: Run the failing Python tests**

Run on Windows:

```powershell
python -m pytest D:\VSWorkshop\WebCode\tools\melotts-service\tests\test_app.py -q
```

Run on non-Windows:

```bash
python -m pytest /path/to/WebCode/tools/melotts-service/tests/test_app.py -q
```

Expected:

- FAIL because the service files do not exist yet

- [ ] **Step 3: Implement the FastAPI wrapper**

Implement `app.py` with:

- startup-time engine creation that tries GPU first, then CPU
- `GET /health`
- `GET /voices`
- `POST /synthesize`

Keep the `/synthesize` contract narrow:

```json
{
  "text": "你好，这是测试语音。",
  "voice_id": "zh_female_default"
}
```

Return synthesized `wav` bytes from the endpoint instead of a shared local path contract.

- [ ] **Step 4: Add startup scripts and operator docs**

Write scripts that:

- require a non-empty storage root
- reject Windows-only-`C:` defaults unless an explicit override flag is set
- export `HF_HOME`, `TRANSFORMERS_CACHE`, `TORCH_HOME`, `TEMP`, `TMP`, and `PIP_CACHE_DIR` under the chosen storage root
- start Uvicorn on the configured loopback port

Document the approved non-`C:` layout in `README.md`, including:

- same-host deployment
- Windows vs non-Windows startup
- GPU-to-CPU fallback behavior
- `ffmpeg` placement

- [ ] **Step 5: Re-run the Python tests and a manual health smoke test**

Run:

```powershell
python -m pytest D:\VSWorkshop\WebCode\tools\melotts-service\tests\test_app.py -q
```

Then start the service manually and smoke-check:

```powershell
python D:\VSWorkshop\WebCode\tools\melotts-service\app.py
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5057/health
```

Expected:

- Python tests PASS
- `/health` returns JSON with `status`, `device`, and `defaultVoiceId`

- [ ] **Step 6: Commit the local-service chunk**

```powershell
git add tools/melotts-service/README.md tools/melotts-service/requirements.txt tools/melotts-service/app.py tools/melotts-service/start.ps1 tools/melotts-service/start.sh tools/melotts-service/tests/test_app.py
git commit -m "feat: add local MeloTTS wrapper service"
```

---

## Verification Pass

### Task 10: Run the final focused verification matrix before claiming completion

**Files:**
- No code changes expected unless a verification issue is discovered

- [ ] **Step 1: Run the Web and Domain test suites for the new feature**

Run:

```powershell
dotnet test D:\VSWorkshop\WebCode\WebCodeCli.Domain.Tests\WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ReplyTts|FullyQualifiedName~FeishuCardKitClientTests|FullyQualifiedName~FeishuChannelServiceTests|FullyQualifiedName~FeishuCardActionServiceTests"
dotnet test D:\VSWorkshop\WebCode\tests\WebCodeCli.Tests\WebCodeCli.Tests.csproj --filter "FullyQualifiedName~AdminControllerReplyTtsTests|FullyQualifiedName~AdminUserManagementReplyTtsUiStateTests"
dotnet msbuild D:\VSWorkshop\WebCode\WebCodeCli\WebCodeCli.csproj /t:CoreCompile /nologo
```

Expected:

- PASS for both test projects
- PASS for `CoreCompile`

- [ ] **Step 2: Run the Python wrapper tests**

Run:

```powershell
python -m pytest D:\VSWorkshop\WebCode\tools\melotts-service\tests\test_app.py -q
```

Expected:

- PASS

- [ ] **Step 3: Manually verify one same-host end-to-end Feishu flow**

Manual checklist:

- enable `ReplyTtsEnabled` for one bound Feishu user
- select a valid runtime voice
- trigger a normal Feishu streaming reply
- confirm the text reply completes normally
- confirm the existing completion text notification still appears
- confirm one or more ordered Feishu `audio` messages arrive afterward
- repeat once with a long reply to confirm chunk splitting
- repeat once with a deliberately broken TTS dependency to confirm one short failure notice appears and the text reply remains intact

- [ ] **Step 4: Commit only if verification exposed fixes**

If verification requires code changes:

```powershell
git add <files>
git commit -m "fix: address Feishu reply TTS verification issues"
```

If verification passes with no further code changes, do not create an extra empty commit.
