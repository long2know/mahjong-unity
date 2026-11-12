#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
IMAGE="${MAHJONG_IMAGE:-mahjong-web:local}"
PLATFORM="${MAHJONG_PLATFORM:-linux/amd64}"
ARCHIVE=""
NO_CACHE=false
usage() {
    printf '%s\n' "Usage: ./build.sh [--tag IMAGE] [--platform linux/ARCH] [--archive FILE] [--no-cache]" \
        "Builds the browser WebGL2/.NET image from source using Docker Buildx. Does not deploy or publish." \
        "No engine Editor, prebuilt player, activation or reference bundle is required."
}
while (($#)); do
    case "$1" in
        --tag|--platform|--archive)
            if (($# < 2)) || [[ -z "$2" ]]; then usage >&2; exit 2; fi
            case "$1" in
                --tag) IMAGE="$2" ;; --platform) PLATFORM="$2" ;; --archive) ARCHIVE="$2" ;;
            esac
            shift 2 ;;
        --no-cache) NO_CACHE=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'Unknown argument: %s\n' "$1" >&2; exit 2 ;;
    esac
done
[[ "$PLATFORM" =~ ^linux/[^,/]+(/[^,/]+)?$ ]] || { printf 'Choose one Linux platform.\n' >&2; exit 2; }
[[ -z "$ARCHIVE" || ! -e "$ARCHIVE" ]] || { printf 'Archive already exists.\n' >&2; exit 2; }
docker buildx version >/dev/null
if [[ -z "${BUILD_SHA+x}" ]]; then
    BUILD_SHA="$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || printf local)"
    if [[ -n "$(git -C "$ROOT" status --porcelain --untracked-files=normal 2>/dev/null)" ]]; then BUILD_SHA="$BUILD_SHA-dirty"; fi
fi
ARGS=(buildx build --load --platform "$PLATFORM" --tag "$IMAGE" --file "$ROOT/Dockerfile" --build-arg "BUILD_SHA=$BUILD_SHA")
if [[ "$NO_CACHE" == true ]]; then ARGS+=(--no-cache); fi
docker "${ARGS[@]}" "$ROOT"
[[ "$(docker image inspect "$IMAGE" --format '{{.Os}}')" == linux ]]
docker image inspect "$IMAGE" --format 'Built {{.Id}} ({{.Os}}/{{.Architecture}})'
if [[ -n "$ARCHIVE" ]]; then docker image save --output "$ARCHIVE" "$IMAGE"; fi
