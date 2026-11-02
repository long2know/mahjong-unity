#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
IMAGE="${MAHJONG_IMAGE:-mahjong-unity:local}"
PLATFORM="${MAHJONG_PLATFORM:-linux/amd64}"
ARCHIVE=""
NO_CACHE=false
SKIP_UNITY=false
ALLOW="${ALLOW_SOURCE_DIRTY:-0}"

usage() {
    printf '%s\n' \
        "Usage: ./build.sh [--tag IMAGE] [--platform linux/ARCH] [--archive FILE] [--no-cache] [--skip-unity-build] [--allow-source-dirty]" \
        "Builds and loads a Linux image locally. Does not deploy or push." \
        "Defaults: MAHJONG_IMAGE=mahjong-unity:local, MAHJONG_PLATFORM=linux/amd64" \
        "Only Docker with Buildx/Compose is required on the host; Unity tests/build run in the pinned build-only container." \
        "Provide private, pre-provisioned UNITY_LICENSE_FILE or UNITY_LICENSE_DIR outside the checkout; no activation is automated." \
        "--skip-unity-build consumes existing REAL artifacts; Docker still validates every file and the frozen manifest." \
        "--allow-source-dirty is explicit local qualification, never a development/integrity bypass." \
        "Build identity defaults to Git commit[-dirty]-UTC timestamp (or local-source-unavailable-timestamp)." \
        "BUILD_SHA overrides that public identity exactly; no runtime secrets are needed." \
        "--archive writes raw tar; use gzip separately for a .tar.gz archive."
}

while (($#)); do
    case "$1" in
        --tag|--platform|--archive)
            if (($# < 2)) || [[ -z "$2" ]]; then
                printf 'Missing value for %s\n' "$1" >&2
                exit 2
            fi
            case "$1" in
                --tag) IMAGE="$2" ;;
                --platform) PLATFORM="$2" ;;
                --archive) ARCHIVE="$2" ;;
            esac
            shift 2
            ;;
        --no-cache) NO_CACHE=true; shift ;;
        --skip-unity-build) SKIP_UNITY=true; shift ;;
        --allow-source-dirty) ALLOW=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'Unknown argument: %s\n' "$1" >&2; usage >&2; exit 2 ;;
    esac
done
case "$ALLOW" in
    0|1) ;;
    *) printf '%s\n' 'ALLOW_SOURCE_DIRTY must be exactly 0 or 1.' >&2; exit 2 ;;
esac

if [[ ! "$PLATFORM" =~ ^linux/[^,/]+(/[^,/]+)?$ ]]; then
    printf 'Specify one Linux platform, for example linux/amd64 or linux/arm64.\n' >&2
    exit 2
fi
if [[ -n "$ARCHIVE" && -e "$ARCHIVE" ]]; then
    printf 'Archive already exists; refusing to overwrite: %s\n' "$ARCHIVE" >&2
    exit 2
fi
if ! command -v docker >/dev/null 2>&1; then
    printf 'Docker with the Buildx plugin is required.\n' >&2
    exit 127
fi
docker buildx version >/dev/null
if [[ "$SKIP_UNITY" != true ]]; then
    UNITY_ARGS=(--test-and-build)
    if [[ "$ALLOW" == 1 ]]; then UNITY_ARGS+=(--allow-source-dirty); fi
    bash "$ROOT/scripts/build-unity-docker.sh" "${UNITY_ARGS[@]}"
fi

if [[ -n "${BUILD_SHA+x}" ]]; then
    BUILD_ID="$BUILD_SHA"
else
    BUILD_TIME="$(date -u +%Y%m%dT%H%M%SZ)"
    BUILD_ID="local-source-unavailable-$BUILD_TIME"
    if command -v git >/dev/null 2>&1 && COMMIT="$(git -C "$ROOT" rev-parse --verify HEAD 2>/dev/null)"; then
        CHANGES="$(git --no-optional-locks -C "$ROOT" status --porcelain --untracked-files=normal)"
        DIRTY=""
        if [[ -n "$CHANGES" ]]; then DIRTY="-dirty"; fi
        BUILD_ID="$COMMIT$DIRTY-$BUILD_TIME"
    fi
fi
printf 'Build identity: %s\n' "$BUILD_ID"

ARGS=(buildx build --load --platform "$PLATFORM" --tag "$IMAGE"
    --file "$ROOT/Dockerfile" --build-arg "BUILD_SHA=$BUILD_ID"
    --build-arg "ALLOW_SOURCE_DIRTY=$ALLOW")
if [[ "$NO_CACHE" == true ]]; then
    ARGS+=(--no-cache)
fi
docker "${ARGS[@]}" "$ROOT"

OS="$(docker image inspect "$IMAGE" --format '{{.Os}}')"
if [[ "$OS" != linux ]]; then
    printf 'Expected a locally loaded Linux image; got OS=%s\n' "$OS" >&2
    exit 1
fi
docker image inspect "$IMAGE" --format 'Built {{.Id}} ({{.Os}}/{{.Architecture}})'
if [[ -n "$ARCHIVE" ]]; then
    docker image save --output "$ARCHIVE" "$IMAGE"
    printf 'Saved image archive: %s\n' "$ARCHIVE"
fi
