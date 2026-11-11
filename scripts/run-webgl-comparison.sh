#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
PORT="${MAHJONG_COMPARISON_PORT:-5087}"
NAME="${MAHJONG_COMPARISON_NAME:-webgl-comparison}"
if [[ ! "$NAME" =~ ^[a-z0-9-]+$ ]]; then
    printf 'MAHJONG_COMPARISON_NAME must be a simple artifact directory name.\n' >&2
    exit 2
fi
WORK="$ROOT/artifacts/$NAME"
case "$PORT" in
    ''|*[!0-9]*) printf 'MAHJONG_COMPARISON_PORT must be a loopback port number.\n' >&2; exit 2 ;;
esac
if ((PORT < 1024 || PORT > 65535 || PORT == 5086)); then
    printf 'Choose an unprivileged port other than the protected Unity preview port 5086.\n' >&2
    exit 2
fi
mkdir -p "$WORK/work" "$WORK/frontend/autotable" "$ROOT/.work"
export TMPDIR="$ROOT/.work" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export DOTNET_CLI_HOME="$WORK/dotnet-home"
BUILD_SHA="$(git -C "$ROOT" rev-parse HEAD)"
if [[ -n "$(git -C "$ROOT" status --porcelain --untracked-files=normal)" ]]; then BUILD_SHA="$BUILD_SHA-dirty"; fi
export BUILD_SHA
(
    cd "$ROOT/src/frontend/autotable-src"
    npm run typecheck
    MAHJONG_WEBGL_COMPARISON=1 npm run build
)
dotnet publish "$ROOT/src/backend/src/Mahjong.Autotable.Api/Mahjong.Autotable.Api.csproj" \
    -c Release --no-restore --disable-build-servers -p:UseSharedCompilation=false -p:UseAppHost=false \
    -o "$WORK/backend/src/Mahjong.Autotable.Api"
cp -a "$ROOT/src/frontend/autotable/." "$WORK/frontend/autotable/"
export MAHJONG_COMPARISON_WORK="$WORK"
python3 - <<'PY'
import hashlib
import json
import os
from pathlib import Path
import urllib.request
from urllib.parse import urlparse

work = Path(os.environ["MAHJONG_COMPARISON_WORK"])
reference = os.environ.get("MAHJONG_UNITY_REFERENCE_URL", "")
identity = {"artifactKind": "experimental-webgl2-comparison", "engine": "webgl2",
            "browserSource": os.environ["BUILD_SHA"], "packaging": "native-host-not-container-qualified",
            "selection": "webgl2 default; renderer=unity needs separately verified genuine Unity artifacts",
            "unityReference": None}
if reference:
    parsed = urlparse(reference)
    if parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or parsed.username or parsed.password:
        raise SystemExit("Unity reference must be an explicitly selected public loopback HTTP URL.")
    base = reference.rstrip("/") + "/autotable/unity/"
    raw = urllib.request.urlopen(base + "renderer-build.json", timeout=30).read()
    manifest = json.loads(raw)
    if (manifest["artifactKind"] != "unity-webgl" or manifest["sourceDirty"]
            or manifest["developmentBuild"] or manifest["contractVersion"] != "1.0.0"):
        raise SystemExit("A clean, non-development genuine Unity reference is required.")
    target = work / "frontend/autotable/unity"
    target.mkdir(parents=True, exist_ok=True)
    for kind in ("loader", "framework", "data", "wasm"):
        item = manifest[kind]
        relative = Path(item["path"])
        if relative.is_absolute() or ".." in relative.parts:
            raise SystemExit("Unsafe Unity artifact path.")
        request = urllib.request.Request(base + item["path"], headers={"Accept-Encoding": "identity"})
        data = urllib.request.urlopen(request, timeout=90).read()
        if len(data) != item["bytes"] or hashlib.sha256(data).hexdigest() != item["sha256"]:
            raise SystemExit("Unity artifact integrity mismatch: " + kind)
        path = target / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    (target / "renderer-build.json").write_bytes(raw)
    identity["unityReference"] = {"rendererSourceRevision": manifest["rendererSourceRevision"],
                                  "manifestSha256": hashlib.sha256(raw).hexdigest(),
                                  "origin": base, "originalManifestUnmodified": True}
    (work / "unity-reference-origin.json").write_text(json.dumps({
        **identity["unityReference"], "scope": "Genuine separately sourced Unity player, comparison browser/native backend; not the original runtime image",
    }, indent=2) + "\n")
elif (work / "frontend/autotable/unity/renderer-build.json").exists():
    raise SystemExit("Existing reference assets require explicit MAHJONG_UNITY_REFERENCE_URL to reverify their origin.")
(work / "frontend/autotable/comparison-build.json").write_text(json.dumps(identity, indent=2) + "\n")
print(json.dumps(identity, indent=2))
PY
FIXTURE="$WORK/fixture-$(python3 -c 'import uuid; print(uuid.uuid4().hex)')"
mkdir -p "$FIXTURE"
export ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS="http://127.0.0.1:$PORT"
export Persistence__Provider=Sqlite ConnectionStrings__Sqlite="Data Source=$FIXTURE/game.db;Pooling=False"
export Authentication__JwtSigningKeys__0="$(openssl rand -base64 48)"
export Auth__JwtSigningKeys__0="$(openssl rand -base64 48)"
export Logging__LogLevel__Default=Warning Sentry__Dsn= DOTNET_EnableDiagnostics=0
printf 'Experimental comparison: http://127.0.0.1:%s/autotable/\n' "$PORT"
printf 'Fresh synthetic fixture: %s\n' "$FIXTURE"
cd "$WORK/backend/src/Mahjong.Autotable.Api"
exec dotnet Mahjong.Autotable.Api.dll
