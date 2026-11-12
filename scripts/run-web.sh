#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
PORT="${MAHJONG_PORT:-5087}"
NAME="${MAHJONG_ARTIFACT_NAME:-webgl-only}"
[[ "$PORT" =~ ^[0-9]+$ && "$NAME" =~ ^[a-z0-9-]+$ ]] || { printf 'Invalid port or artifact name.\n' >&2; exit 2; }
if ((PORT < 1024 || PORT > 65535 || PORT == 5086)); then
    printf 'Choose an unprivileged loopback port other than protected rollback port 5086.\n' >&2; exit 2
fi
WORK="$ROOT/artifacts/$NAME"
mkdir -p "$WORK/logs" "$WORK/frontend/autotable" "$ROOT/.work"
export TMPDIR="$ROOT/.work" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_HOME="$WORK/dotnet-home"
BUILD_SHA="$(git -C "$ROOT" rev-parse HEAD)"
if [[ -n "$(git -C "$ROOT" status --porcelain --untracked-files=normal)" ]]; then BUILD_SHA="$BUILD_SHA-dirty"; fi
export BUILD_SHA
(cd "$ROOT/src/frontend/autotable-src" && npm run typecheck && npm run build)
dotnet publish "$ROOT/src/backend/src/Mahjong.Autotable.Api/Mahjong.Autotable.Api.csproj" \
    -c Release --no-restore --disable-build-servers -p:UseSharedCompilation=false -p:UseAppHost=false \
    -o "$WORK/backend/src/Mahjong.Autotable.Api"
# Refuse a reused export that would misrepresent a player-free default build.
if [[ -d "$WORK/frontend/autotable/unity" ]]; then
    printf 'Use a fresh artifact directory; retired player files exist in this output.\n' >&2; exit 1
fi
cp -a "$ROOT/src/frontend/autotable/." "$WORK/frontend/autotable/"
FIXTURE="$WORK/fixture-$(python3 -c 'import uuid; print(uuid.uuid4().hex)')"
mkdir -p "$FIXTURE"
export ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS="http://127.0.0.1:$PORT"
export Persistence__Provider=Sqlite ConnectionStrings__Sqlite="Data Source=$FIXTURE/game.db;Pooling=False"
export Authentication__JwtSigningKeys__0="$(openssl rand -base64 48)"
export Auth__JwtSigningKeys__0="$(openssl rand -base64 48)"
export Logging__LogLevel__Default=Warning Sentry__Dsn= DOTNET_EnableDiagnostics=0 Security__CspStrict=true
printf 'Mahjong Web: http://127.0.0.1:%s/autotable/\n' "$PORT"
printf 'Synthetic fixture only: %s\n' "$FIXTURE"
cd "$WORK/backend/src/Mahjong.Autotable.Api"
exec dotnet Mahjong.Autotable.Api.dll
