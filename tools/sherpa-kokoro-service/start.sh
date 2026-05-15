#!/usr/bin/env bash
set -euo pipefail

storage_root="${1:-${KOKORO_STORAGE_ROOT:-}}"
port="${KOKORO_PORT:-5058}"
default_voice_id="${KOKORO_DEFAULT_VOICE_ID:-zh_47}"
provider="${KOKORO_PROVIDER:-cpu}"
num_threads="${KOKORO_NUM_THREADS:-4}"

if [[ -z "${storage_root}" ]]; then
  echo "Storage root is required." >&2
  exit 1
fi

case "${storage_root}" in
  /|/root|/home|/tmp)
    echo "Refusing unsafe storage root: ${storage_root}" >&2
    exit 1
    ;;
esac

mkdir -p \
  "${storage_root}/cache/pip" \
  "${storage_root}/logs" \
  "${storage_root}/models" \
  "${storage_root}/service" \
  "${storage_root}/temp" \
  "${storage_root}/venv"

export TEMP="${storage_root}/temp"
export TMP="${storage_root}/temp"
export PIP_CACHE_DIR="${storage_root}/cache/pip"
export KOKORO_CACHE_ROOT="${storage_root}/cache"
export KOKORO_STORAGE_ROOT="${storage_root}"
export KOKORO_DEFAULT_VOICE_ID="${default_voice_id}"
export KOKORO_PROVIDER="${provider}"
export KOKORO_NUM_THREADS="${num_threads}"
export KOKORO_HOST="127.0.0.1"
export KOKORO_PORT="${port}"

script_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${script_root}"
python -m uvicorn app:app --host 127.0.0.1 --port "${port}"
