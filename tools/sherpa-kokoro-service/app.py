from __future__ import annotations

import io
import logging
import ntpath
import os
import sys
import wave
from array import array
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any, Callable, Protocol

import uvicorn
from fastapi import FastAPI, HTTPException, Response
from pydantic import BaseModel

try:
    from pydantic import field_validator
except ImportError:  # pragma: no cover - compatibility path
    from pydantic import validator as field_validator


LOGGER = logging.getLogger("sherpa-kokoro-service")

DEFAULT_MODEL_NAME = "kokoro-int8-multi-lang-v1_1"
DEFAULT_VOICE_ID = "zh_47"
DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 5058
DEFAULT_PROVIDER = "cpu"
DEFAULT_NUM_THREADS = 4
DEFAULT_WINDOWS_STORAGE_ROOT = "E:/WebCodeData/Kokoro"
DEFAULT_NON_WINDOWS_STORAGE_ROOT = "/data/webcode/kokoro"

KNOWN_ZH_SPEAKERS = {
    45: "zh_45",
    46: "zh_46",
    47: "zh_47",
    48: "zh_48",
    49: "zh_49",
    50: "zh_50",
    51: "zh_51",
    52: "zh_52",
}


class EngineAdapter(Protocol):
    provider: str

    def list_voices(self) -> list[dict[str, str]]:
        ...

    def has_voice(self, voice_id: str) -> bool:
        ...

    def synthesize(self, text: str, voice_id: str) -> bytes:
        ...


class EngineRuntime:
    def __init__(
        self,
        *,
        adapter: EngineAdapter,
        status: str,
        provider: str,
        error: str | None = None,
    ):
        self.adapter = adapter
        self.status = status
        self.provider = provider
        self.error = error


class SynthesizeRequest(BaseModel):
    text: str
    voice_id: str

    @field_validator("text")
    @classmethod
    def validate_text(cls, value: str) -> str:
        if not value or not value.strip():
            raise ValueError("text must not be blank")

        return value.strip()

    @field_validator("voice_id")
    @classmethod
    def validate_voice_id(cls, value: str) -> str:
        if not value or not value.strip():
            raise ValueError("voice_id must not be blank")

        return value.strip()


class UnavailableEngineAdapter:
    def __init__(self, reason: str):
        self.provider = "unavailable"
        self.reason = reason

    def list_voices(self) -> list[dict[str, str]]:
        return []

    def has_voice(self, voice_id: str) -> bool:
        return False

    def synthesize(self, text: str, voice_id: str) -> bytes:
        raise RuntimeError(self.reason)


class KokoroEngineAdapter:
    def __init__(
        self,
        *,
        model_dir: Path,
        provider: str = DEFAULT_PROVIDER,
        num_threads: int = DEFAULT_NUM_THREADS,
    ):
        try:
            import sherpa_onnx
        except Exception as exc:  # pragma: no cover - depends on local runtime
            raise RuntimeError(
                "sherpa-onnx is not installed. Install the service requirements in the configured non-C storage root."
            ) from exc

        self.provider = provider
        self.model_dir = model_dir

        required_files = [
            "model.int8.onnx",
            "voices.bin",
            "tokens.txt",
            "lexicon-us-en.txt",
            "lexicon-zh.txt",
            "date-zh.fst",
            "phone-zh.fst",
            "number-zh.fst",
            "espeak-ng-data",
        ]
        missing = [name for name in required_files if not (model_dir / name).exists()]
        if missing:
            raise RuntimeError(
                f"Kokoro model directory is incomplete: {model_dir}. Missing: {', '.join(missing)}"
            )

        kokoro = sherpa_onnx.OfflineTtsKokoroModelConfig(
            model=str(model_dir / "model.int8.onnx"),
            voices=str(model_dir / "voices.bin"),
            tokens=str(model_dir / "tokens.txt"),
            data_dir=str(model_dir / "espeak-ng-data"),
            lexicon=",".join(
                [
                    str(model_dir / "lexicon-us-en.txt"),
                    str(model_dir / "lexicon-zh.txt"),
                ]
            ),
        )
        model_config = sherpa_onnx.OfflineTtsModelConfig(
            kokoro=kokoro,
            num_threads=max(1, int(num_threads)),
            provider=provider,
        )
        config = sherpa_onnx.OfflineTtsConfig(
            model=model_config,
            rule_fsts=",".join(
                [
                    str(model_dir / "date-zh.fst"),
                    str(model_dir / "phone-zh.fst"),
                    str(model_dir / "number-zh.fst"),
                ]
            ),
            max_num_sentences=1,
        )
        self._tts = sherpa_onnx.OfflineTts(config)
        self._sample_rate = int(self._tts.sample_rate)
        self._voices = build_voice_catalog(int(self._tts.num_speakers))

    def list_voices(self) -> list[dict[str, str]]:
        return list(self._voices.values())

    def has_voice(self, voice_id: str) -> bool:
        return voice_id in self._voices

    def synthesize(self, text: str, voice_id: str) -> bytes:
        sid = parse_voice_id(voice_id)
        if sid is None or voice_id not in self._voices:
            raise ValueError(f"Unknown voice_id: {voice_id}")

        audio = self._tts.generate(text, sid=sid, speed=1.0)
        return samples_to_wav_bytes(audio.samples, int(audio.sample_rate or self._sample_rate))


def build_voice_catalog(num_speakers: int) -> dict[str, dict[str, str]]:
    voices: dict[str, dict[str, str]] = {}

    for sid in range(max(0, num_speakers)):
        voice_id = f"speaker_{sid}"
        voices[voice_id] = {
            "voiceId": voice_id,
            "displayName": f"Kokoro speaker {sid}",
            "language": "multi",
            "gender": "unknown",
        }

    for sid, voice_id in KNOWN_ZH_SPEAKERS.items():
        if sid >= num_speakers:
            continue

        voices[voice_id] = {
            "voiceId": voice_id,
            "displayName": f"Kokoro Chinese speaker {sid}",
            "language": "zh",
            "gender": "unknown",
        }

    return voices


def parse_voice_id(voice_id: str) -> int | None:
    normalized = (voice_id or "").strip().lower()
    if normalized.isdigit():
        return int(normalized)

    for prefix in ("speaker_", "zh_"):
        if normalized.startswith(prefix) and normalized[len(prefix) :].isdigit():
            return int(normalized[len(prefix) :])

    return None


def samples_to_wav_bytes(samples: Any, sample_rate: int) -> bytes:
    pcm = array("h")
    for sample in samples:
        value = max(-1.0, min(1.0, float(sample)))
        pcm.append(int(value * 32767))

    if sys.byteorder == "big":  # pragma: no cover - CI is little-endian
        pcm.byteswap()

    output = io.BytesIO()
    with wave.open(output, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        wav.writeframes(pcm.tobytes())

    return output.getvalue()


def normalize_voice(raw_voice: dict[str, Any]) -> dict[str, str]:
    voice_id = raw_voice.get("voiceId") or raw_voice.get("voice_id") or raw_voice.get("id") or ""
    display_name = raw_voice.get("displayName") or raw_voice.get("display_name") or raw_voice.get("name") or voice_id
    language = raw_voice.get("language") or "unknown"
    gender = raw_voice.get("gender") or "unknown"

    return {
        "voiceId": str(voice_id),
        "displayName": str(display_name),
        "language": str(language),
        "gender": str(gender),
    }


def default_adapter_factory(model_dir: Path, provider: str, num_threads: int) -> EngineAdapter:
    return KokoroEngineAdapter(
        model_dir=model_dir,
        provider=provider,
        num_threads=num_threads,
    )


def normalize_windows_drive(value: str | None) -> str:
    if not value or not value.strip():
        return ""

    candidate = value.strip()
    drive = ntpath.splitdrive(candidate)[0]
    if not drive and len(candidate) >= 2 and candidate[1] == ":":
        drive = candidate[:2]

    return drive.rstrip("\\/").upper()


def get_windows_system_drive(system_drive: str | None = None) -> str:
    candidate = system_drive or os.getenv("SystemDrive")
    if not candidate:
        candidate = ntpath.splitdrive(os.getenv("SystemRoot", ""))[0]

    return normalize_windows_drive(candidate) or "C:"


def validate_windows_non_system_path(
    path: str | None,
    *,
    label: str,
    os_name: str | None = None,
    system_drive: str | None = None,
) -> None:
    if not path or not path.strip():
        return

    if (os_name or os.name) != "nt":
        return

    path_drive = normalize_windows_drive(path)
    if not path_drive:
        return

    system_drive_normalized = get_windows_system_drive(system_drive)
    if path_drive == system_drive_normalized:
        raise RuntimeError(
            f"Kokoro/sherpa-onnx {label} must be on a Windows non-system drive. "
            f"If this machine only has {system_drive_normalized}, attach or map a non-system drive first."
        )


def resolve_model_dir(storage_root: str | None, model_dir: str | None) -> Path:
    if model_dir and model_dir.strip():
        validate_windows_non_system_path(model_dir, label="model directory")
        return Path(model_dir.strip()).resolve()

    if not storage_root or not storage_root.strip():
        storage_root = DEFAULT_WINDOWS_STORAGE_ROOT if os.name == "nt" else DEFAULT_NON_WINDOWS_STORAGE_ROOT

    validate_windows_non_system_path(storage_root, label="storage root")
    return (Path(storage_root.strip()) / "models" / DEFAULT_MODEL_NAME).resolve()


def initialize_runtime(
    adapter_factory: Callable[[Path, str, int], EngineAdapter],
    *,
    model_dir: Path,
    provider: str,
    num_threads: int,
    default_voice_id: str,
) -> EngineRuntime:
    try:
        adapter = adapter_factory(model_dir, provider, num_threads)
        if not adapter.has_voice(default_voice_id):
            raise RuntimeError(f"Default voice '{default_voice_id}' is not available.")

        LOGGER.info("Initialized Kokoro/sherpa-onnx adapter on %s", adapter.provider)
        return EngineRuntime(adapter=adapter, status="ok", provider=adapter.provider)
    except Exception as exc:
        message = f"Unable to initialize Kokoro/sherpa-onnx: {exc}"
        LOGGER.error(message)
        return EngineRuntime(
            adapter=UnavailableEngineAdapter(message),
            status="unavailable",
            provider="unavailable",
            error=message,
        )


def create_app(
    *,
    adapter_factory: Callable[[Path, str, int], EngineAdapter] | None = None,
    storage_root: str | None = None,
    model_dir: str | None = None,
    default_voice_id: str | None = None,
    provider: str | None = None,
    num_threads: int | None = None,
) -> FastAPI:
    adapter_factory = adapter_factory or default_adapter_factory
    storage_root = storage_root or os.getenv("KOKORO_STORAGE_ROOT")
    model_dir_path = resolve_model_dir(storage_root, model_dir or os.getenv("KOKORO_MODEL_DIR"))
    default_voice_id = default_voice_id or os.getenv("KOKORO_DEFAULT_VOICE_ID", DEFAULT_VOICE_ID)
    provider = provider or os.getenv("KOKORO_PROVIDER", DEFAULT_PROVIDER)
    num_threads = num_threads or int(os.getenv("KOKORO_NUM_THREADS", str(DEFAULT_NUM_THREADS)))

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        app.state.runtime = initialize_runtime(
            adapter_factory,
            model_dir=model_dir_path,
            provider=provider,
            num_threads=num_threads,
            default_voice_id=default_voice_id,
        )
        yield

    app = FastAPI(title="Kokoro sherpa-onnx Local Service", version="0.1.0", lifespan=lifespan)
    app.state.default_voice_id = default_voice_id
    app.state.model_dir = str(model_dir_path)

    @app.get("/health")
    async def health() -> dict[str, str]:
        runtime = app.state.runtime
        response = {
            "status": runtime.status,
            "device": runtime.provider,
            "defaultVoiceId": app.state.default_voice_id,
            "modelDir": app.state.model_dir,
        }
        if runtime.error:
            response["message"] = runtime.error

        return response

    @app.get("/voices")
    async def voices() -> dict[str, list[dict[str, str]]]:
        runtime = app.state.runtime
        if runtime.status != "ok":
            raise HTTPException(status_code=503, detail=runtime.error or "TTS unavailable")

        return {"voices": [normalize_voice(item) for item in runtime.adapter.list_voices()]}

    @app.post("/synthesize")
    async def synthesize(request: SynthesizeRequest) -> Response:
        runtime = app.state.runtime
        if runtime.status != "ok":
            raise HTTPException(status_code=503, detail=runtime.error or "TTS unavailable")

        try:
            audio = runtime.adapter.synthesize(request.text, request.voice_id)
        except ValueError as exc:
            raise HTTPException(status_code=400, detail=str(exc)) from exc
        except RuntimeError as exc:
            raise HTTPException(status_code=503, detail=str(exc)) from exc

        return Response(content=audio, media_type="audio/wav")

    return app


app = create_app()


if __name__ == "__main__":
    logging.basicConfig(level=os.getenv("KOKORO_LOG_LEVEL", "INFO").upper())
    uvicorn.run(
        app,
        host=os.getenv("KOKORO_HOST", DEFAULT_HOST),
        port=int(os.getenv("KOKORO_PORT", str(DEFAULT_PORT))),
        log_level=os.getenv("KOKORO_LOG_LEVEL", "info"),
    )
