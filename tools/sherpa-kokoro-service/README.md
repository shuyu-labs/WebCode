# Kokoro sherpa-onnx Local Service

This directory contains the same-host Python wrapper used by WebCode reply TTS.

When shipped inside a Windows WebCode release, this folder is placed under:

```text
<WebCodeInstallRoot>\tools\sherpa-kokoro-service
```

In the source repository, the same files live under:

```text
<RepoRoot>\tools\sherpa-kokoro-service
```

- `GET /health`
- `GET /voices`
- `POST /synthesize`

The service is intentionally Kokoro/sherpa-onnx only. There is no secondary engine fallback. If synthesis fails, WebCode keeps the already-delivered streaming text reply and stops audio generation.

## Storage Policy

Do not install this service, model, cache, virtual environment, or temporary files on the Windows system drive, normally `C:`.

Approved Windows layout:

```text
E:\WebCodeData\Kokoro\
  cache\
    pip\
  ffmpeg\
    bin\
      ffmpeg.exe
  logs\
  models\
    kokoro-int8-multi-lang-v1_1\
      model.int8.onnx
      voices.bin
      tokens.txt
      lexicon-us-en.txt
      lexicon-zh.txt
      date-zh.fst
      phone-zh.fst
      number-zh.fst
      espeak-ng-data\
  service\
  temp\
  venv\
```

If Windows has only the system drive and no non-system data drive, do not start the TTS service. Attach or map a non-system drive first, then set `FeishuReplyTts:TtsStorageRoot` to that path.

Non-Windows default layout is `/data/webcode/kokoro`.

## Install

Install the Python runtime and virtual environment under the approved non-system-drive storage root. The example below uses `uv` only as the installer; the Python runtime, venv, packages, cache, temp files, and models all stay under `E:\WebCodeData\Kokoro`.

```powershell
$env:UV_PYTHON_INSTALL_DIR = "E:\WebCodeData\Kokoro\python"
$env:UV_CACHE_DIR = "E:\WebCodeData\Kokoro\cache\uv"
$env:PIP_CACHE_DIR = "E:\WebCodeData\Kokoro\cache\pip"
$env:TEMP = "E:\WebCodeData\Kokoro\temp"
$env:TMP = "E:\WebCodeData\Kokoro\temp"
uv python install 3.9

E:\WebCodeData\Kokoro\python\cpython-3.9.23-windows-x86_64-none\python.exe -m venv E:\WebCodeData\Kokoro\venv
E:\WebCodeData\Kokoro\venv\Scripts\python.exe -m pip install --upgrade pip
E:\WebCodeData\Kokoro\venv\Scripts\python.exe -m pip install -r <WebCodeRoot>\tools\sherpa-kokoro-service\requirements.txt
```

Place the extracted `kokoro-int8-multi-lang-v1_1` model directory under:

```text
E:\WebCodeData\Kokoro\models\kokoro-int8-multi-lang-v1_1
```

## Windows Startup

The script refuses Windows system-drive storage roots. If `-StorageRoot` is omitted on Windows, it selects the first writable fixed non-system drive; if no such drive exists, it exits before creating any service directories. On non-Windows PowerShell, the default storage root is `/data/webcode/kokoro`.

```powershell
cd <WebCodeRoot>\tools\sherpa-kokoro-service
.\start.ps1 -StorageRoot E:\WebCodeData\Kokoro -Port 5058 -Python E:\WebCodeData\Kokoro\venv\Scripts\python.exe
```

## Non-Windows Startup

```bash
cd <WebCodeRoot>/tools/sherpa-kokoro-service
chmod +x start.sh
./start.sh /data/webcode/kokoro
```

## Manual Smoke Check

```powershell
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5058/health
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5058/voices
```

Healthy `/health` output has `status = "ok"` and `device = "cpu"`.
