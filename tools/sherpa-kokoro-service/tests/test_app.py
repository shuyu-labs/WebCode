from __future__ import annotations

import importlib.util
from pathlib import Path

from fastapi.testclient import TestClient


APP_PATH = Path(__file__).resolve().parent.parent / "app.py"


def load_app_module():
    spec = importlib.util.spec_from_file_location("sherpa_kokoro_service_app", APP_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Unable to load app module from {APP_PATH}")

    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class FakeEngineAdapter:
    def __init__(self, provider: str, voices: list[dict[str, str]], audio: bytes = b"RIFFfake"):
        self.provider = provider
        self.voices = voices
        self.audio = audio
        self.calls: list[tuple[str, str]] = []

    def list_voices(self):
        return list(self.voices)

    def has_voice(self, voice_id: str) -> bool:
        return any(voice["voiceId"] == voice_id for voice in self.voices)

    def synthesize(self, text: str, voice_id: str) -> bytes:
        self.calls.append((text, voice_id))
        return self.audio


def test_health_reports_active_provider_default_voice_and_model_dir():
    app_module = load_app_module()
    engine = FakeEngineAdapter(
        provider="cpu",
        voices=[
            {
                "voiceId": "zh_47",
                "displayName": "Kokoro Chinese speaker 47",
                "language": "zh",
                "gender": "unknown",
            }
        ],
    )

    app = app_module.create_app(
        adapter_factory=lambda model_dir, provider, num_threads: engine,
        storage_root="E:/WebCodeData/Kokoro",
        default_voice_id="zh_47",
    )

    with TestClient(app) as client:
        response = client.get("/health")

    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "ok"
    assert body["device"] == "cpu"
    assert body["defaultVoiceId"] == "zh_47"
    assert body["modelDir"].endswith("kokoro-int8-multi-lang-v1_1")


def test_voices_returns_normalized_voice_list():
    app_module = load_app_module()
    engine = FakeEngineAdapter(
        provider="cpu",
        voices=[
            {
                "voiceId": "zh_47",
                "displayName": "Kokoro Chinese speaker 47",
                "language": "zh",
                "gender": "unknown",
            }
        ],
    )

    app = app_module.create_app(
        adapter_factory=lambda model_dir, provider, num_threads: engine,
        storage_root="E:/WebCodeData/Kokoro",
        default_voice_id="zh_47",
    )

    with TestClient(app) as client:
        response = client.get("/voices")

    assert response.status_code == 200
    assert response.json() == {
        "voices": [
            {
                "voiceId": "zh_47",
                "displayName": "Kokoro Chinese speaker 47",
                "language": "zh",
                "gender": "unknown",
            }
        ]
    }


def test_synthesize_rejects_blank_input():
    app_module = load_app_module()
    engine = FakeEngineAdapter(
        provider="cpu",
        voices=[
            {
                "voiceId": "zh_47",
                "displayName": "Kokoro Chinese speaker 47",
                "language": "zh",
                "gender": "unknown",
            }
        ],
    )

    app = app_module.create_app(
        adapter_factory=lambda model_dir, provider, num_threads: engine,
        storage_root="E:/WebCodeData/Kokoro",
        default_voice_id="zh_47",
    )

    with TestClient(app) as client:
        response = client.post(
            "/synthesize",
            json={"text": "   ", "voice_id": "zh_47"},
        )

    assert response.status_code == 422


def test_missing_default_voice_makes_runtime_unavailable():
    app_module = load_app_module()
    engine = FakeEngineAdapter(provider="cpu", voices=[])

    app = app_module.create_app(
        adapter_factory=lambda model_dir, provider, num_threads: engine,
        storage_root="E:/WebCodeData/Kokoro",
        default_voice_id="zh_47",
    )

    with TestClient(app) as client:
        health = client.get("/health")
        voices = client.get("/voices")

    assert health.status_code == 200
    assert health.json()["status"] == "unavailable"
    assert voices.status_code == 503


def test_parse_voice_id_supports_known_aliases():
    app_module = load_app_module()

    assert app_module.parse_voice_id("zh_47") == 47
    assert app_module.parse_voice_id("speaker_47") == 47
    assert app_module.parse_voice_id("47") == 47
    assert app_module.parse_voice_id("bad") is None


def test_validate_storage_root_rejects_windows_system_drive():
    app_module = load_app_module()

    try:
        app_module.validate_windows_non_system_path(
            "C:/WebCodeData/Kokoro",
            label="storage root",
            os_name="nt",
            system_drive="C:",
        )
    except RuntimeError as exc:
        assert "non-system drive" in str(exc)
    else:
        raise AssertionError("Expected Windows system drive storage root to be rejected")


def test_validate_storage_root_allows_non_windows_paths():
    app_module = load_app_module()

    app_module.validate_windows_non_system_path(
        "/data/webcode/kokoro",
        label="storage root",
        os_name="posix",
        system_drive="C:",
    )
