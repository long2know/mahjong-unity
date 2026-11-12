# Mahjong Web

Browser-native **WebGL2** mahjong with perspective/flat views, ivory beveled
tiles, inset teal/amber backs, four subtle discard rivers, responsive rectangular
boards and an independently fixed local hand.

The default app and Docker image build entirely from TypeScript/artwork and
.NET source. **No Unity Editor, player, activation, WebAssembly player, prebuilt
engine bundle or external Autotable installation/service is required.**
Unsupported WebGL2 and GPU context loss fail visibly; there is no engine fallback.

All five established modes remain: **Changsha, four-player, three-player, Bamboo
and Minefield**. Existing lobby/chat, identity/reconnect and APIs are retained.
The UI lives directly at the configured public mount, the raw relay at `/ws`,
and SignalR at `/hubs/changsha`, all relative to that mount.

## Run from source

Requires Node 20+ and .NET 10; host Chrome is used by the real-browser checks.

```sh
cd src/frontend/autotable-src
npm ci --no-audit --no-fund
cd ../../..
dotnet restore src/backend/src/Mahjong.Autotable.Api/Mahjong.Autotable.Api.csproj
bash scripts/run-web.sh
```

Open **http://127.0.0.1:5087/** and create a table normally.
This foreground runner uses fresh synthetic SQLite/signing state under ignored
`artifacts/webgl-only/`. It does not import an operator database/configuration or
download another renderer. Set `MAHJONG_PORT` for another free loopback port and
`MAHJONG_ARTIFACT_NAME` to separate qualification outputs. Port 5086 remains
excluded from this fixture runner while the previous deployment is a rollback.

## Build and self-host

Only Docker with Buildx/Compose is required for the container build:

```sh
bash build.sh --tag mahjong-web:local
# PowerShell: ./build.ps1 -Tag mahjong-web:local
./scripts/compose-bootstrap.sh
docker compose up -d --build
```

New Compose installations default to **http://127.0.0.1:8950/**.
Production uses a persistent `/data` volume, non-root UID/GID 1000, read-only
root filesystem, dropped capabilities and no-new-privileges.

**Existing deployments:** retain your Compose project/volume identity and stable
signing configuration. A repository rename is not permission to replace a
database or start an empty project-scoped volume. `MAHJONG_IMAGE` remains the
image override. The old `ghcr.io/long2know/mahjong-unity:latest` is a historical
rollback image, not silently redirected; the new publishing path uses
`ghcr.io/long2know/mahjong-web` and requires separate candidate/digest acceptance.
No registry promotion occurs during a source build or PR CI.

To host below a path instead of site root, set runtime **`BASE_URL=/mahjong`**
and open **https://games.long2know.com/mahjong/** behind your proxy. Only the
trailing slash is canonicalized; no extra application suffix is added.
No image rebuild is needed. See [runtime mount/nginx configuration](docs/integration/base-url.md)
for nested/escaped paths, WebSocket forwarding, trusted proxies and healthchecks.

## Controls

- Touch once to discard an eligible tile; double-click with a mouse.
- Right-click/F/R flips an eligible collaborative-table tile. Drag uses the
  existing semantic slot/action route.
- Wheel/pinch zoom; Alt-left/middle drag or two fingers orbit/tilt; twist rolls.
  Shift-right drag pans. Camera contacts cancel tile gestures.
- Focus the canvas for arrows, Shift-left/right roll, +/- zoom and Home reset.
  Settings expose the same camera and board/hand layout preferences.
- The separate hand remains fixed while the table camera moves. Hidden or
  unfocused windows suspend visual publication, retain only the newest frame,
  and continue normal networking.

## Source lineage, not an external dependency

The browser still uses derived `Client`, `World`, `Setup`, `Slot`, `Thing` and
`Movement` code and relay/table semantics. The .NET game/API/hosting implementation
was imported from **Stephen's `long2know/mahjong-autotable` project**; that fact
does not identify all of it as original `pwmarcz/autotable` code.
The WebGL renderer is now in `src/frontend/autotable-src/src/renderer/`, including
small retained/modified math, instancing and picking primitives.

This is not a clean-room rewrite. Retained copyright/asset notices and import
receipts document ancestry; they are not runtime installations. See
[the precise retained/removed source inventory](docs/integration/source-lineage.md)
and [notices/provenance](docs/integration/licensing-and-provenance.md).

## Checks and build identity

```sh
cd src/frontend/autotable-src
npm run typecheck
npm run test:renderer
npm run build
cd ../../..
mkdir -p .work
TMPDIR="$PWD/.work" node tests/migration/browser-webgl.mjs http://127.0.0.1:5087
```

`web-build.json` records actual emitted-file sizes/hashes and source identity.
`renderer-module-graph.json` records the real default WebGL graph. Build guards
reject engine/WASM-player artifacts and legacy renderer imports.
`tests/migration/run_source_ci.py` runs exact source/tool/backend inventories.
PR CI additionally builds the normal Docker image and exercises an isolated
hardened synthetic runtime without registry publication.

Real-browser output distinguishes GPU-fence-completed frames, actual pointer/
touch actions, server outcomes, fixed-hand PNG evidence and explicit failure
paths. Software-GPU and emulated-phone timings are not physical-device FPS or
total-memory guarantees. The former Unity/comparison snapshots and measurements
remain historical, not validation of the current default source.

- [Renderer contract](docs/integration/renderer-v1.md)
- [Docker/run guidance](docs/integration/docker-build-and-run.md)
- [Candidate publishing/protected promotion](docs/ghcr-publishing.md)
