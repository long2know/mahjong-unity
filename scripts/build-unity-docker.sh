#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
MODE=--test-and-build
ALLOW="${ALLOW_SOURCE_DIRTY:-0}"
usage() {
    printf '%s\n' \
        "Usage: bash scripts/build-unity-docker.sh [--build|--test|--test-and-build] [--allow-source-dirty]" \
        "Only Docker with Compose is required on the host. Default: actual tests, actual Web build, verification." \
        "Set ONE of UNITY_LICENSE_FILE=/private/Unity_lic.ulf or UNITY_LICENSE_DIR=/private/Unity." \
        "Licensing must already be privately provisioned and valid; files/directories must be outside the checkout." \
        "A Docker image or mounted .ulf alone does not prove activation. No account credentials or activation is performed." \
        "GameCI pin: Unity6000.3.24f1 WebGL, linux/amd64; large build-only image, separate ASP.NET runtime." \
        "--allow-source-dirty is local qualification only; toolchain/hash/magic/development checks remain enforced."
}
while (($#)); do
    case "$1" in
        --build|--test|--test-and-build) MODE="$1"; shift ;;
        --allow-source-dirty) ALLOW=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'Unknown argument: %s\n' "$1" >&2; usage >&2; exit 2 ;;
    esac
done
case "$ALLOW" in
    0|1) ;;
    *) printf '%s\n' 'ALLOW_SOURCE_DIRTY must be exactly 0 or 1.' >&2; exit 2 ;;
esac
if [[ -n "${UNITY_LICENSE_FILE:-}" && -n "${UNITY_LICENSE_DIR:-}" ]]; then
    printf '%s\n' 'Set only ONE of UNITY_LICENSE_FILE or UNITY_LICENSE_DIR.' >&2
    exit 2
fi
if [[ -n "${UNITY_LICENSE_FILE:-}" ]]; then
    if [[ "$UNITY_LICENSE_FILE" != /* || ! -f "$UNITY_LICENSE_FILE" || -L "$UNITY_LICENSE_FILE" || "$UNITY_LICENSE_FILE" != *.ulf ]]; then
        printf '%s\n' 'UNITY_LICENSE_FILE must name an existing absolute private .ulf file (not a symlink); contents are never read by this wrapper.' >&2
        exit 1
    fi
    PRIVATE_PARENT="$(cd -- "${UNITY_LICENSE_FILE%/*}" && pwd -P)"
    PRIVATE_SOURCE="$PRIVATE_PARENT/${UNITY_LICENSE_FILE##*/}"
    PRIVATE_TARGET=/workspace/artifacts/unity-builder-home/.local/share/unity3d/Unity/Unity_lic.ulf
elif [[ -n "${UNITY_LICENSE_DIR:-}" ]]; then
    if [[ "$UNITY_LICENSE_DIR" != /* || ! -d "$UNITY_LICENSE_DIR" ]]; then
        printf '%s\n' 'UNITY_LICENSE_DIR must name an existing absolute private pre-provisioned Unity licensing directory.' >&2
        exit 1
    fi
    PRIVATE_SOURCE="$(cd -- "$UNITY_LICENSE_DIR" && pwd -P)"
    PRIVATE_TARGET=/workspace/artifacts/unity-builder-home/.local/share/unity3d/Unity
else
    printf '%s\n' \
        'Unity build blocked: provide privately provisioned valid UNITY_LICENSE_FILE (or UNITY_LICENSE_DIR) outside this checkout.' \
        'Docker supplies build tools, not a Unity license. Personal activation requires the supported Unity Hub/operator process; no unsupported manual activation is attempted.' \
        'Existing licensed-editor alternative: UNITY_EDITOR=/approved/Unity/Hub/Editor/6000.3.24f1/Editor/Unity bash scripts/build-unity-webgl.sh' >&2
    exit 1
fi
if [[ "$PRIVATE_SOURCE" == "$ROOT" || "$PRIVATE_SOURCE" == "$ROOT/"* ]]; then
    printf '%s\n' 'Licensing must stay outside the checkout; no source/build-context license files are accepted.' >&2
    exit 1
fi
if [[ ! -d "$ROOT/.git" ]]; then
    printf '%s\n' 'An actual Git checkout is required for renderer source revision metadata; host Git is not required.' >&2
    exit 1
fi
if ! command -v docker >/dev/null 2>&1; then
    printf '%s\n' 'Docker with Compose is required.' >&2
    exit 127
fi
docker compose version >/dev/null
docker info >/dev/null
mkdir -p "$ROOT/artifacts/unity-builder-home" "$ROOT/artifacts/tool-work" "$ROOT/artifacts/test-results"
export UNITY_LICENSE_MOUNT_SOURCE="$PRIVATE_SOURCE"
export UNITY_LICENSE_MOUNT_TARGET="$PRIVATE_TARGET"
export UNITY_BUILD_UID="${UNITY_BUILD_UID:-$(id -u)}"
export UNITY_BUILD_GID="${UNITY_BUILD_GID:-$(id -g)}"
export ALLOW_SOURCE_DIRTY="$ALLOW"
cd -- "$ROOT"
# An explicit safe empty env file prevents Compose from ever auto-reading .env.
: > "$ROOT/artifacts/tool-work/unity-compose-empty.env"
docker compose --env-file "$ROOT/artifacts/tool-work/unity-compose-empty.env" \
    --project-name mahjong-unity-build --profile unity-build \
    -f "$ROOT/docker-compose.unity-build.yml" \
    run --build --rm --no-deps unity-builder "$MODE"
