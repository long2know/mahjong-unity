# Docker build and run

**Source-iteration status:** the repository contains the real-Editor producer,
Docker wrapper, artifact validator, and separate hosting Dockerfile. No licensed
Editor execution, generated WebGL build, or container gameplay qualification has
been demonstrated. Actual .NET publish/localhost transport smoke and source
suites passed; they are not a playable image. The commands below are the
implemented **next execution path**, not a record that a playable image has
already been built.

## Prerequisites and pin

Use an actual Git checkout and Docker with a running Linux-container daemon,
**Buildx and Compose**, plus the host's Bash or PowerShell shell. Docker packages
Unity, Python/Git, Node, and .NET build tools; no host Editor, Node, .NET SDK, or
Python installation is required for the default packaging flow. Host Git is
optional for the public build label; renderer provenance uses Git in the build
container.

**Legitimate, compatible Unity activation must be provisioned separately.**
Docker does not eliminate licensing. In particular, no supported Personal-only,
Hub-free activation method has been established here. Read
[licensing and provenance](licensing-and-provenance.md) before provisioning.

The build-only image is pinned in
[`docker/unity-builder.Dockerfile`](../../docker/unity-builder.Dockerfile) and
[`docker/unity-builder-image.json`](../../docker/unity-builder-image.json):

```text
unityci/editor:ubuntu-6000.3.24f1-webgl-3.2.2
linux/amd64
sha256:f3c6661a8c5bc975df168f114eb3882766546b2ad09765664f5b2bb186857ddb
Unity 6000.3.24f1 / revision 4e7b9b5b6244 / Web Build Support
```

The digest is the **platform manifest**, not the multi-platform index. The
receipt records registry-metadata inspection only: no Editor image pull or
execution. Its compressed layers total about 6.94 GiB; allow additional space
for extraction/import caches. The Unity builder is amd64; choosing another
hosting platform does not make the Unity builder native on that platform.
An operator-invoked Docker build can pull that pinned image when it is not
cached. This is tool packaging, not activation or evidence that this source
iteration already executed the Editor.

## Build: default container path

Privately set **one** of these inputs:

- `UNITY_LICENSE_FILE`: existing absolute `.ulf` file outside the checkout,
  not a symlink.
- `UNITY_LICENSE_DIR`: a narrow, existing, privately provisioned Unity licensing
  directory outside the checkout—not an account/home directory.

Values are **paths, never license contents**. The wrappers mount the input
read-only with no host-path auto-creation. They do not accept credentials or
activate Unity; file existence is not proof that licensed execution will work.

```bash
# Replace this path with the privately provisioned, compatible license file.
export UNITY_LICENSE_FILE="/absolute/private/Unity_lic.ulf"
bash build.sh --tag mahjong-unity:local
```

PowerShell counterpart: `./build.ps1 -Tag mahjong-unity:local`, with the same
private input configured in the environment.

The default wrappers run actual Unity **EditMode tests + WebGL build**, verify
fresh outputs, then use the root Dockerfile to build/load the hosting image.
Node 20/Vite and .NET 10 restore/publish run in Docker stages. Failed tests,
missing licensing/module/project, failed BuildReport, or absent/invalid
artifacts stop the flow; nothing is deployed or pushed automatically.

For Unity-only work:

```bash
bash scripts/build-unity-docker.sh --test
bash scripts/build-unity-docker.sh --test-and-build
```

This wrapper selects the `unity-build` profile in
[`docker-compose.unity-build.yml`](../../docker-compose.unity-build.yml), through
the repo entrypoint. It passes a project-local empty env file, so Unity builds
do not load the operator's production `.env`. Invoke the wrapper rather than
constructing private Compose mounts manually.

| Persistent project path | Purpose |
| --- | --- |
| `unity/MahjongRenderer/Library/` | Editor import cache |
| `unity/MahjongRenderer/BuildCache/tool-work/` | Producer tool scratch |
| `artifacts/unity-builder-home/` | Builder HOME/client state; treat as private |
| `artifacts/tool-work/` | Wrapper scratch and empty Compose env file |
| `artifacts/test-results/` | Actual test XML and Editor logs when executed |
| `artifacts/unity-webgl/` | Actual WebGL output and `renderer-build.json` |

Only the Unity project and project `artifacts/` are writable workspace mounts;
scripts, contracts, Git metadata, and the license input are read-only. Bash
defaults the builder UID/GID to the host user; PowerShell defaults to 1000:1000,
with `UNITY_BUILD_UID` / `UNITY_BUILD_GID` available for deliberate matching.
Resolve access to those specific paths, not with global permission changes.
Raw Editor logs/client state must be reviewed/redacted before sharing.

The builder root filesystem remains read-only. A private, disposable `/tmp`
tmpfs (`rw,nosuid,nodev,mode=1777,size=512m`) provides writable UPM Unix-socket
and filesystem-backed licensing IPC space; `TMPDIR` under `artifacts/` does not
replace literal `/tmp`. This runtime allowance neither activates Unity nor
qualifies an Editor/WebGL build.

## Existing artifacts and optional host Editor

`--skip-unity-build` (`-SkipUnityBuild`) consumes **existing real** artifacts;
the Docker validator still runs. `--allow-source-dirty` (`-AllowSourceDirty`)
or `ALLOW_SOURCE_DIRTY=1` is explicit **local qualification only**. It allows
the recorded renderer-input dirty state, not missing artifacts, development
builds, wrong pins, hashes, or WebAssembly magic.

```bash
bash build.sh --skip-unity-build --tag mahjong-unity:local
# Only for disclosed local dirty-source qualification:
bash build.sh --skip-unity-build --allow-source-dirty --tag mahjong-unity:local
```

The browser separately rejects dirty-source artifacts by default. For explicit
local inspection, append **`unityLocalBuild=1` to the existing table URL**,
preserving its room/variant/join parameters. This opt-in works only on
`localhost`, `127.0.0.1`, or `[::1]`; it is not a server authorization or a
release qualification. Development builds remain rejected even with this
opt-in.

An already-provisioned host Editor is an **optional alternative**, not required
for Docker packaging. That environment needs Python 3, Git, the exact licensed
Editor, and its WebGLSupport module:

```bash
UNITY_EDITOR="/absolute/path/to/6000.3.24f1/Editor/Unity" \
  bash scripts/build-unity-webgl.sh --test-and-build
```

The producer executes `Mahjong.Renderer.Editor.WebGlBuild.Build`. Successful
execution writes the manifest and real `.loader.js`, `.framework.js`, `.wasm`,
and `.data` files under `artifacts/unity-webgl/`. Logs are
`artifacts/test-results/unity-webgl-build.log` / `unity-tests.log`; EditMode
results are `unity-editmode.xml`. These files are **not present execution
evidence** merely because their producer paths exist.

For direct diagnostics in an existing Python environment:

```bash
python3 scripts/verify-unity-build.py artifacts/unity-webgl/renderer-build.json
```

Validation checks frozen pins/schema, safe relative paths, nonzero sizes,
SHA-256, WebAssembly magic, and the complete artifact tree. Its success proves
integrity only, not activation, camera output, or browser/mobile gameplay.

## Start the hosting image

After a real image builds, provision stable runtime signing configuration
separately from the Unity license:

```bash
bash scripts/compose-bootstrap.sh
MAHJONG_IMAGE=mahjong-unity:local docker compose -p mahjong-unity up -d --no-build
docker compose -p mahjong-unity ps
docker compose -p mahjong-unity exec -T mahjong \
  curl -fsS http://127.0.0.1:8080/health
```

Bootstrap uses restrictive creation permissions and preserves an existing
configured signing key; it does not print the key. Keep `.env` private and
stable across restarts. PowerShell operators may provide existing stable
`JWT_SIGNING_KEY` configuration privately to Compose instead of using the Bash
bootstrap. For a separate operator file, bootstrap supports
`--env-file /absolute/private/operator.env`; pass that same file with
`docker compose --env-file` when starting. Do not print resolved secret config.

For a **new** installation, the commands fix the Compose project name to
`mahjong-unity`. Existing operators must retain their existing project/volume
identity: changing `-p` silently selects a different named volume. The
`mahjong-data` volume persists `/data/mahjong-autotable.db`; stopping Compose
without deleting volumes preserves it. The runtime stays non-root UID/GID
1000:1000, with `tini`, production signing-key checks, and `/health`
(`/api/health` fallback). Optional PostgreSQL/SQL Server overlays remain in the
repo; configure their credentials/terms privately rather than rely on
development defaults.

Open `http://127.0.0.1:8950/autotable/`. Default binding is loopback; port and
address use `MAHJONG_HOST_PORT` / `MAHJONG_BIND_ADDRESS`. A proxy must retain
same-origin `/autotable/` static assets, **`/autotable/ws`** WebSocket upgrades,
`/api/...`, and `/hubs/changsha`. The ASP.NET runtime serves validated Unity
output at `/autotable/unity/`; no separate Nginx/Editor runtime is introduced.
Use the same optional public `BUILD_SHA` at build and startup when matching
UI/health identity matters—Compose can override the image's build label.
Remote/LAN browser and device URLs need a secure context (normally HTTPS);
plain LAN HTTP is not equivalent to loopback. The browser adapter requires
WebAssembly, WebGL 2, ResizeObserver, and WebCrypto for artifact verification.

The contract uses **uncompressed, single-threaded** WebGL: `.wasm` needs
`application/wasm`, `.data` `application/octet-stream`, and no false gzip/br
encoding or HTML fallback. Revalidate manifest/loader/unversioned assets.
Strict/custom CSP without WebAssembly permission is incompatible; report that
configuration rather than weaken security globally or add COOP/COEP blindly.

## Verified source checks

Final receipts record **90 focused .NET + 33 tooling tests**,
`dotnet publish` Release output, and actual localhost Development/test-database
health/SignalR/raw-WS/404/clean-shutdown smoke
(`artifacts/test-results/backend-summary.json`). This is not production
identity deployment, a real runtime Docker smoke, or an unfiltered backend
suite. Tester additionally ran **57 room/relay/reconnect/continuation tests**
(`artifacts/test-results/migration-backend-wire.log` and
`migration-backend/migration-wire.trx`).

The repeatable tooling runner preserves the existing Hosting test modules and
redirects their owned fixtures to project-local work; it does not read live
operator configuration:

```bash
PYTHONDONTWRITEBYTECODE=1 python3 tests/migration/run_backend_tools.py
```

For an already-restored/built .NET test environment, the additional 57-case
selector is:

```bash
dotnet test src/backend/tests/Mahjong.Autotable.Api.Tests/Mahjong.Autotable.Api.Tests.csproj \
  --no-build --no-restore --disable-build-servers -p:UseSharedCompilation=false \
  --filter 'FullyQualifiedName~.Autotable.ExistingRoomJoinTests|FullyQualifiedName~.Autotable.AutotableWsRelayTests|FullyQualifiedName~.Autotable.AutotableConfigJoinSemanticsTests|FullyQualifiedName~.Autotable.HandResultAckWsTests|FullyQualifiedName~.Autotable.AutotableHandshakeAndDealTriggerContractTests' \
  --logger 'trx;LogFileName=migration-wire.trx' \
  --results-directory artifacts/test-results/migration-backend
```

These are local development/CI diagnostics, not additional host requirements
for Docker packaging or claims that remote GitHub Actions ran.

The actual Docker missing-artifact build/gate and the wrapper with private
licensing inputs unset exited **1** as expected. No runtime image success,
GameCI pull, activation, or Unity output is implied by those negative checks.

The unchanged source-only Docker filter repro is executable from the repo root:

```bash
PYTHONDONTWRITEBYTECODE=1 python3 tests/migration/test_source_gate.py \
  ArtifactAndContextTests.test_real_docker_context_filter_keeps_outputs_notices_and_excludes_private_names -v
```

Tester approved the independent `.dockerignore` revision after all **9
source/context gates** passed. A scratch context uses empty filename probes and
the actual root ignore rules; no fixture is a Unity build or credential.
Supplemental export evidence at
`artifacts/test-results/lead-dockerignore-context-evidence.json` also checks
non-Unity API/test/tool-work/cache/log/private artifact paths. Tester's
overall first-iteration integration approval is still pending.

The actual Game startup and automatic resize regressions are:

```bash
node --test tests/migration/browser-source-integration.test.mjs
```

Both passed after independent revisions to `game.ts` / `unity-host.ts`; no
retained animation implementation or frozen schema changed. First-frame
publication is valid before the first animation tick, and the source resize
caller retains the correct hand band/buffer with strictly newer revisions.
Browser services/runtime transport facades are explicit test simulations,
not an Editor/player/device run.

For an already-provisioned Node development/CI environment:

```bash
cd src/frontend/autotable-src
npm run typecheck
npm run build
npm run test:unity
node --test tests/node/room-startup-readiness.test.mjs \
  tests/node/hand-result-render-and-wire.test.mjs \
  tests/node/sc2-slot-owner.invariants.test.mjs \
  tests/node/perspective-settings.test.mjs
```

F1's final hashed receipt is
`src/frontend/autotable-src/.unity-validation/f1-source-evidence.json`:
**40 renderer-specific + 38 preserved Node tests**, strict primary-graph
typechecking, and asset-free Vite build passed. Host callbacks are explicitly
mocked; real Client/World/source wire/gesture and Vite/mutation checks do not
render a Unity player. The original author receipt is not integration approval:
after the startup/resize revisions, the **78 affected Node tests**, strict
typecheck and Vite build were rerun successfully. Current logs are
`artifacts/test-results/lead-browser-{source-tests,typecheck,build}.log`;
`lead-browser-integration-final.log` and `lead-browser-caller-evidence.json`
record the actual-caller regressions and supplemental outbound viewport checks.
Browser/overall source approval remains pending.

U1's [source receipt](../../unity/MahjongRenderer/Documentation/source-evidence.json)
records metadata/schema/scene guards, **5 JS source tests** and **26-file C#9
syntax parsing**, not Unity API/shader compilation. Its authored EditMode tests
and genuine BuildPlayer output remain unexecuted. These source tools are
optional diagnostics, not extra host prerequisites for Docker packaging.

### Real Chromium-container shell checks

**Six actual Chromium-container cases passed** against the published localhost
API and served Vite bundle: two desktop/phone-sized lobby-only and missing-build
route cases, plus four social cases covering lobby controls, invitations/join,
chat send/receive/reload, and desktop/portrait/landscape viewport routing while
the table is unavailable. No Unity files, loader fallback, or rendered-frame
receipt was fabricated.

Use a prepared **local source qualification environment** with Python 3, Git,
a Linux-container Docker daemon, Node 20/npm with the installed lockfile-matched
Playwright packages and Vite bundle, and .NET 10 SDK/runtime with the actual
Release API published at `artifacts/api/`. The runner starts and stops its own
isolated localhost API. These are test-environment prerequisites, **not new
host production-tool requirements** for the Docker packaging/hosting flow.
No host Chrome installation is required.

The runner requires the cached, lock-matched official Playwright **1.60.0**
image on **linux/amd64**. Explicitly pull the exact image first if absent;
the runner inspects it and uses **`--pull=never`**, not an automatic pull:

```bash
docker pull --platform linux/amd64 \
  mcr.microsoft.com/playwright:v1.60.0-noble@sha256:83192064c7510f7ee73dd63dc5f22a5e01a92c81a2e6a9c715d9e3fe55471fd9
python3 tests/migration/run_browser_integration.py
python3 tests/migration/run_browser_integration.py --probe browser-social.mjs
```

The default is `browser-unavailable.mjs` (two cases); `browser-social.mjs`
contains the four social/routing cases. Persistent results and source hashes
are at
`artifacts/test-results/migration-browser-actual/browser-unavailable-receipt.json`
and `browser-social-receipt.json`, with matching probe logs. The revised
runner's focused evidence rereview is pending; these successful executions
are not a claim of final release approval.

Local host Chrome's `SIGTRAP` launch failure remains an environment limitation;
Chromium in the pinned official container worked. Phone-sized/emulated viewports
are **not real iOS/Android device evidence**, Unity board/touch input tests, or
mobile-performance qualification. These probes leave the table visibly
unavailable and do not close the owner's actual Unity activation/build gate or
the separate license/dependency redistribution gates.

### Portable source CI

`.github/workflows/migration-build.yml` runs mandatory source jobs on `dev` and
`main` pull requests/pushes. These jobs do **not** require Unity licensing.
The exact commands, source hashes, tool versions, test counts and output
hashes are recorded in `artifacts/test-results/migration-ci/<job>/receipt.json`;
named logs, backend TRX and the Vite source-module graph are uploaded even on
failure. Baseline stores, tool fixtures, API publish files and private
operator/Editor inputs are not uploaded.

The portable runner uses Python 3, Git, Node 20/npm and .NET 10. The
`source-tools` job also requires Bash, native PowerShell, a running Docker
daemon, Buildx and Compose. Missing tools, failed public pin fetches and any
skipped mandatory tests **fail** the job. CI provisions Node/.NET explicitly
and installs only the existing lockfile/manifests:

```bash
python3 tests/migration/run_source_ci.py source-tools --install-dependencies
python3 tests/migration/run_source_ci.py backend --install-dependencies
python3 tests/migration/run_source_ci.py frontend-source --install-dependencies
python3 tests/migration/run_source_ci.py unity-source --install-dependencies
```

For already-installed/restored local backend/frontend dependencies, omit
`--install-dependencies`; all source tests/builds still execute, without
`dotnet test --no-build`. For `unity-source`, always pass
`--install-dependencies`: each invocation uses a fresh scratch intermediate
directory for the C# syntax build and must restore its assets, even when the
normal project cache exists. Restore the existing dependency manifests if a
missing dependency is reported.
The source-tools runner always fetches the exact public baseline commit
`d10d351e543aa3e341960de9800828b6c0662a82` into its owned, ignored
`artifacts/test-results/migration-work/` Git object store and sets
`MIGRATION_BASELINE_GIT_STORE` for the actual immutable-blob tests. No historical
session path or cached source mirror is required.

Coverage is **33 tooling + 9 import/ignore/real-Docker-context cases**,
**147 combined backend/hosting/room/relay/continuation cases**, **80 retained,
Unity-adapter and actual Game startup/settled-viewport source cases**, distinct
strict primary TypeScript and Vite builds, and Unity metadata/frozen-contract,
**5 JS bridge cases + 26-file C#9 editor/player syntax parsing**. Workflow
contract tests also reject conditional source skips, widened evidence uploads,
missing prerequisites and skipped actual immutable-object assertions.

These are source checks, not Unity assembly/API/shader compilation, EditMode,
WebGL, native browser or device qualification. The real Editor/build job stays
an explicit `workflow_dispatch` opt-in behind the `licensed-unity-build`
environment, dependent on all four source jobs. The optional native published
API/browser checks remain separate; no skipped browser check is counted as a
browser pass. This wiring is not evidence that the focused follow-up has
already executed remotely.

## Troubleshooting and validation limits

- **No valid licensing / activation failure:** remain blocked. Use the
  supported Unity/operator process, not manual Personal bypasses.
- **Missing manifest, hashes, or files:** generate real outputs; do not add
  placeholders, rename compressed files, or disable the artifact gate.
- **Permission error:** match access narrowly for the private input and mounted
  cache/output paths; never use `chmod 777` or a whole-home mount.
- **Lobby visible, table unavailable:** expected without a real compatible
  build. Health, DOM readiness, static/mocked checks, and a valid manifest are
  not proof of a rendered playable table.
- **Before claiming completion:** execute the real Editor tests/build, then
  Unity-rendered browser/input, real mobile, multiplayer, and runtime-container
  gameplay checks. The six shell/social Chromium cases above do not satisfy
  those unqualified checks. Root/additions license selection and dependency
  notices remain separate redistribution gates.
