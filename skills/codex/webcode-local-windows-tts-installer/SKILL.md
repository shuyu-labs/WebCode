---
name: webcode-local-windows-tts-installer
description: Use when building a local Windows WebCode installer from this repo for machine testing, especially when the package must bundle the Kokoro or sherpa-onnx Reply TTS service, model files, ffmpeg, a private Python runtime, and non-system-drive deployment without publishing a GitHub Release.
---

# WebCode Local Windows TTS Installer

## Overview

Build the local WebCode Windows installer and portable ZIP with the bundled Reply TTS payload. Use this skill for local validation and handoff artifacts, not for branch push, tag push, or GitHub Release asset sync.

For GitHub publishing, use `webcode-github-release` instead.

## Workflow

1. Confirm the current checkout is the WebCode repo, or pass `-RepoRoot`.
2. Prefer `Debug` when the user wants to try the installer locally before cleanup. Use `Release` only when the user asks for release-grade artifacts.
3. Run `scripts/build-local-installer.ps1` from this skill instead of reconstructing the command by hand.
4. Report the generated installer, portable ZIP, checksums, and release notes from `artifacts/windows-installer/vX.Y.Z/`.
5. If the user asks for runtime verification, smoke-test the generated `tts-bundle` by starting `tools/sherpa-kokoro-service/start.ps1` against it and checking `http://127.0.0.1:<port>/health`.

## Command

Default local build:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-local-installer.ps1
```

Explicit repo root and release build:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-local-installer.ps1 `
  -RepoRoot "D:\VSWorkshop\WebCode" `
  -Configuration Release
```

Override the TTS payload source root if autodetection picks the wrong drive:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-local-installer.ps1 `
  -ReplyTtsSourceRoot "E:\WebCodeData\Kokoro"
```

## Rules

- Treat `Directory.Build.props` as the version source of truth.
- Treat `tools/build-windows-installer.ps1` as the single source of truth for packaging behavior. Fix that script instead of adding ad hoc shell steps.
- Expect the TTS source root to be a complete payload root containing `models\kokoro-int8-multi-lang-v1_1`, `venv\Scripts\python.exe`, and a bundled `python\` directory.
- Do not rely on a target-machine Python installation. The local installer path for this workflow is the bundled private runtime plus the bundled venv.
- Do not move the TTS payload onto the Windows system drive. The installer must target a writable non-system fixed drive and fail clearly otherwise.
- If multiple non-system drives exist and autodetection chooses the wrong one, rerun with `-ReplyTtsSourceRoot`.
- Expect the artifact set to be `WebCode-Setup-vX.Y.Z-win-x64.exe`, `WebCode-vX.Y.Z-win-x64-portable.zip`, `SHA256SUMS.txt`, and `RELEASE_NOTES.md`.

## Files

- Skill wrapper: `scripts/build-local-installer.ps1`
- Repo build script: `<RepoRoot>\tools\build-windows-installer.ps1`
- Installer definition: `<RepoRoot>\installer\windows\WebCode.iss`
- TTS service payload: `<RepoRoot>\tools\sherpa-kokoro-service\`
