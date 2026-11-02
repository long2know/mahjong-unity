#!/usr/bin/env bash
set -euo pipefail
umask 077

ROOT="${MAHJONG_BUILD_WORKSPACE:-/workspace}"
cd -- "$ROOT"
MODE="${1:---test-and-build}"
if (($# > 1)); then
    printf '%s\n' 'Usage: mahjong-unity-build [--build|--test|--test-and-build]' >&2
    exit 2
fi
case "$MODE" in
    --build) PRODUCER_ARGS=() ;;
    --test|--test-and-build) PRODUCER_ARGS=("$MODE") ;;
    *) printf 'Unknown Unity builder mode: %s\n' "$MODE" >&2; exit 2 ;;
esac
case "${ALLOW_SOURCE_DIRTY:-0}" in
    0|1) ;;
    *) printf '%s\n' 'ALLOW_SOURCE_DIRTY must be exactly 0 or 1.' >&2; exit 2 ;;
esac

export HOME="$ROOT/artifacts/unity-builder-home"
export TMPDIR="$ROOT/artifacts/tool-work"
export TMP="$TMPDIR" TEMP="$TMPDIR"
export UNITY_EDITOR=/opt/unity/Editor/Unity
export ALLOW_SOURCE_DIRTY="${ALLOW_SOURCE_DIRTY:-0}"
mkdir -p "$HOME" "$TMPDIR" "$ROOT/artifacts/test-results"
git config --global --add safe.directory "$ROOT"
if [[ ! -f scripts/build-unity-webgl.sh || ! -f scripts/verify-unity-build.py || ! -d .git ]]; then
    printf '%s\n' 'Mounted checkout is incomplete: require .git, renderer project, scripts and frozen contracts.' >&2
    exit 1
fi

# License contents are neither parsed nor passed on the command line. A mounted
# file is not proof of activation: actual licensed editor execution must succeed.
printf '%s\n' 'Running the pinned Unity Editor with privately provisioned licensing; no activation is performed.'
START_NS="$(python3 -c 'import time; print(time.time_ns())')"
bash scripts/build-unity-webgl.sh "${PRODUCER_ARGS[@]}"

if [[ "$MODE" != --test ]]; then
    python3 - "$START_NS" <<'PY'
import pathlib, sys
manifest = pathlib.Path("artifacts/unity-webgl/renderer-build.json")
if not manifest.is_file() or manifest.stat().st_mtime_ns < int(sys.argv[1]):
    sys.exit("Unity producer returned without a fresh real-build manifest; stale artifacts are not accepted.")
PY
    python3 scripts/verify-unity-build.py artifacts/unity-webgl/renderer-build.json
fi
