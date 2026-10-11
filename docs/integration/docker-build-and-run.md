# Docker build and run

**Tester APPROVE: October 11, 2026, 01:01:59 UTC
(October 10, 18:01:59 PDT).** Legitimate host Hub activation, actual **99/99
EditMode tests**, production-settings WebGL and scoped native gameplay in a
healthy Docker runtime are verified. The final review retains the previously
approved Changsha and mobile-layout scopes; it is not a new exhaustive rerun.
The reviewed runtime used **`sourceDirty: true`, `developmentBuild: false`**.
The final committed **99-test clean WebGL rebuild and strict validator now
pass**, with **`sourceDirty: false`, `developmentBuild: false`**. All four source
CI jobs are green. Canonical clean-image packaging, native runtime/player
checks and the live 5086 preview upgrade also passed, delivering the first
working local iteration. Physical iOS/Android
and touchscreen double-tap discard are qualification limits of the first
working iteration, not current completion blockers.

## Prerequisites and pin

Use an actual Git checkout and Docker with a running Linux-container daemon,
**Buildx and Compose**, plus the host's Bash or PowerShell shell. Docker packages
Unity, Python/Git, Node, and .NET build tools; no host Editor, Node, .NET SDK, or
Python installation is required for the default packaging flow. Host Git is
optional for the public build label; renderer provenance uses Git in the build
container.

**Docker does not eliminate licensing.** The host Editor's existing Hub
activation was accepted; it does not establish licensing compatibility in the
GameCI container. Use the verified host route below without reactivation.
For a different build environment, separately confirm legitimate compatible
licensing; do not invent a manual `.ulf` or paid/free headless activation route.
Read [licensing and provenance](licensing-and-provenance.md).

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
GameCI receipt records registry-metadata inspection only, not an Editor image
pull or container build. Its compressed layers total about 6.94 GiB; allow
additional space for extraction/import caches. The Unity builder is amd64; choosing another
hosting platform does not make the Unity builder native on that platform.
An operator-invoked Docker build can pull that pinned image when it is not
cached. This is tool packaging, not container activation. The successful
Editor execution documented next used the separately installed host Editor.

## Verified host Editor

The actual installed Editor is:

```text
/data/source/mahjong-unity/artifacts/test-results/unity-u2-archive-route/6000.3.24f1/Editor/Unity
```

Its adjacent WebGLSupport module and binary match the verified archive. The
old `unity-u2-archive-route/vendor` path is a preserved compatibility symlink
to the version-named directory. Hub's manually registered inventory still
labels the version `vendor`; neither GUI registration nor the future install
root was changed. Do not use a newly invented path or substitute an installed
6.6 Editor for the project's **6.3 / 6000.3.24f1** pin.

This host has Python 3, Git, the exact Editor and WebGLSupport installed.
From `/data/source/mahjong-unity`, match the already-activated portable Hub's
environment **only for the invoked process**:

```bash
env HOME="/data/programs/Unity/UnityHubSetup-x86_64.AppImage.home" \
  XDG_CONFIG_HOME="/data/programs/Unity/UnityHubSetup-x86_64.AppImage.home/.config" \
  XDG_DATA_HOME="/data/programs/Unity/UnityHubSetup-x86_64.AppImage.home/.local/share" \
  XDG_CACHE_HOME="/data/programs/Unity/UnityHubSetup-x86_64.AppImage.home/.cache" \
  UNITY_EDITOR="$PWD/artifacts/test-results/unity-u2-archive-route/6000.3.24f1/Editor/Unity" \
  bash scripts/build-unity-webgl.sh --test-and-build
```

`env` leaves the caller's normal HOME/XDG values unchanged. The producer sets
its own project-local tool scratch under
`unity/MahjongRenderer/BuildCache/tool-work/`. No reactivation, license export,
account/token copying, or whole-home container mount is needed for this
already accepted host route.

The approved **99-test** Editor/build receipt is
`artifacts/test-results/relay-native-target-repair/editor-rebuild-receipt.json`
(completed **October 10, 2026, 23:54:48 UTC**). It records successful actual
EditMode execution and IL2CPP WebGL production. The unchanged producer's
BuildReport gate requires `BuildResult.Succeeded`, WebGL, zero errors and
nonzero output. The final approval is
`artifacts/test-results/migration-player-final-relay-review.json`.

Real output is under `artifacts/unity-webgl/`: `renderer-build.json` and
`Build/unity-webgl.{loader.js,framework.js,wasm,data}`. Each actual manifest
records its build ID, source revision/tree hash and artifact hashes. The final
review's `actualArtifact` identifies the exact tested local iteration; do not
reuse that identity to claim a later clean build. Production build settings
alone do not make a dirty-source artifact a clean release.

The final approved source/CI/tests are committed at
`684f67837eb9fbef0475162702f5d3542d87b949`. Its actual clean **99-test** rebuild
completed: the manifest's `builtAtUtc` is **2026-10-11T01:07:03.6390440Z**,
`rendererSourceRevision` is that commit, and `sourceDirty` / `developmentBuild`
are both **false**. The strict validator passed without a dirty-source option;
all 99 cases in `artifacts/test-results/unity-editmode.xml` passed. The reviewed
renderer-input tree is preserved. Use the actual manifest/completion evidence;
never edit cleanliness flags or synthesize output.

Canonical clean-image packaging, native health/player checks and the preview
upgrade are complete. The final operational receipts below record actual clean
runtime proof, distinct from the preserved earlier broad gameplay scopes.

The producer executes `Mahjong.Renderer.Editor.WebGlBuild.Build`. Standard
logs are `artifacts/test-results/unity-webgl-build.log` / `unity-tests.log`;
the actual test XML is `artifacts/test-results/unity-editmode.xml`.
These outputs/receipts are local ignored files, not included in a fresh source
checkout. Path names alone never prove execution; do not create placeholders.
Actual player loading/rendered frames, native actions, server echoes and
Docker-runtime health now have scoped approval. Physical iOS/Android,
touchscreen double-tap discard and performance remain separate.

The final review links preserved earlier qualifications and their exact
receipts. These are prior approved scopes, not all newly rerun against the final
relay iteration. Earlier repair/build receipts are historical, not the final
clean-build identity.

## Build: default container wrapper

This remains the default behavior of `build.sh` / `build.ps1`, not the route
used for the successful host build above. Container licensing compatibility
must be checked separately; pulling the pinned image or observing host Hub
acceptance is not that check.

If using this alternative, privately configure **one existing compatible
input**:

- `UNITY_LICENSE_FILE`: existing absolute `.ulf` file outside the checkout,
  not a symlink.
- `UNITY_LICENSE_DIR`: a narrow, existing, privately provisioned Unity licensing
  directory outside the checkout—not an account/home directory.

Values are **paths, never license contents**. These are inputs for an already
provisioned container-compatible license, not a manual activation recipe.
The wrappers mount the input read-only with no host-path auto-creation. They
do not accept credentials or activate Unity; file existence is not proof that
licensed execution will work. Do not copy the portable Hub home into a mount.

```bash
# Only after compatible private licensing is confirmed for this container.
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

## Existing artifacts and validation

`--skip-unity-build` (`-SkipUnityBuild`) consumes **existing real** artifacts;
the Docker validator still runs. `--allow-source-dirty` (`-AllowSourceDirty`)
or `ALLOW_SOURCE_DIRTY=1` is explicit **local qualification only**. It allows
the recorded renderer-input dirty state, not missing artifacts, development
builds, wrong pins, hashes, or WebAssembly magic.

```bash
# Consume the actual final clean outputs:
BUILDX_BUILDER=default ALLOW_SOURCE_DIRTY=0 bash build.sh \
  --skip-unity-build --tag mahjong-unity:local
# Disclosed dirty-source local qualification only, never release:
bash build.sh --skip-unity-build --allow-source-dirty --tag mahjong-unity:local
```

The browser separately rejects dirty-source artifacts by default. For explicit
local inspection, append **`unityLocalBuild=1` to the existing table URL**,
preserving its room/variant/join parameters. This opt-in works only on
`localhost`, `127.0.0.1`, or `[::1]`; it is not a server authorization or a
release qualification. The upgraded user preview is already clean and loads
without that parameter. Development builds remain
rejected even with the opt-in.

For direct diagnostics in an existing Python environment:

```bash
# Strict check for the actual final clean Editor rebuild:
PYTHONDONTWRITEBYTECODE=1 ALLOW_SOURCE_DIRTY=0 python3 scripts/verify-unity-build.py \
  artifacts/unity-webgl/renderer-build.json
# Previously approved dirty-source local iteration only:
PYTHONDONTWRITEBYTECODE=1 python3 scripts/verify-unity-build.py \
  artifacts/unity-webgl/renderer-build.json --allow-source-dirty
```

Validation checks frozen pins/schema, safe relative paths, nonzero sizes,
SHA-256, WebAssembly magic, and the complete artifact tree. The strict check
rejects any dirty-source manifest. The local override does
not waive development-build, pin, hash or file checks; its success proves
integrity only, not activation, camera output, or browser/mobile gameplay.

### Verified local runtime and player

`artifacts/test-results/migration-player-final-relay-review.json` records the
exact reviewed image, actual healthy API/database and Docker healthcheck,
genuine hosted loader/framework/wasm/data with correct MIME, and **47 matching
frontend asset hashes**. Runtime hardening remained non-root/read-only,
capabilities dropped and no-new-privileges. This is actual runtime evidence,
not merely a successful image build.

All four relay modes passed **8 native tile/stick commits with one target write
and server echo each**, plus **16 F / Shift+F / right-click / R routes**.
Previously approved Changsha manual/automatic pickup/discard, count-only wire,
chat, privacy, spectator and reconnect scopes remain accepted. Phone-layout
evidence retains **294/294 hand centers across 21 configurations**, DPR
**1/2/3**, portrait/landscape and chat collapsed/open, with formerly blocked
lower-row native select/discard passing. The final review explicitly preserves
these earlier scopes rather than calling them a full new rerun.

Final clean operational evidence:
`artifacts/test-results/final-clean-runtime/summary.json` and
`artifacts/test-results/final-clean-runtime/preview-upgrade-receipt.json`,
recorded **October 11, 2026, 02:12:40 UTC**. Runtime source commit:
`684f67837eb9fbef0475162702f5d3542d87b949`. The immutable image is
**`mahjong-unity:final-684f678`** /
`sha256:cb420dcd543212ea1d29e9d9e8b44a16ef559bf110a1f6c6b0b03df931b39a90`;
served manifest SHA-256:
`3730e734f0a9c599e565a2fbb40efe06e1988b9509b6f27b206048087eaf8c43`,
with **`sourceDirty: false`, `developmentBuild: false`**.
Clean isolated Changsha C# select/discard and THREE_PLAYER tile/stick/keyboard
checks passed. After preview cutover, actual C# rendering, `frameRendered` and
native discard/server-wire proof passed without dirty opt-in.

The live user preview is **`http://127.0.0.1:5086/autotable/`**, running healthy
as UID/GID 1000:1000 with a read-only root filesystem and `unless-stopped`
restart policy. Existing protected private key/configuration and database
inodes/mounts were preserved; all test helpers were removed. Its host-side
private operator state remains outside the `/data` checkout. Do not replace it
with the new-install commands below or broaden permissions. Named-room seat
ownership is retained; a seeded URL does not grant an already occupied seat
to a new browser. Use normal join/seat controls or a different table.

## Start the hosting image

For a **new installation**, after building the intended image, privately set
`OPERATOR_ENV_FILE` to an operator configuration file in a protected directory
outside the checkout. Runtime signing configuration is separate from Unity
licensing. These commands are not a replacement for the existing user preview:

```bash
: "${OPERATOR_ENV_FILE:?Set a protected operator env-file path outside the checkout}"
bash scripts/compose-bootstrap.sh --env-file "$OPERATOR_ENV_FILE"
MAHJONG_IMAGE=mahjong-unity:local docker compose --env-file "$OPERATOR_ENV_FILE" \
  -p mahjong-unity up -d --no-build
docker compose --env-file "$OPERATOR_ENV_FILE" -p mahjong-unity ps
docker compose --env-file "$OPERATOR_ENV_FILE" -p mahjong-unity exec -T mahjong \
  curl -fsS http://127.0.0.1:8080/health
```

Bootstrap uses restrictive creation permissions and preserves an existing
configured signing key; it does not print the key. Keep operator configuration
private and stable across restarts. Do not place it under a world-writable
parent directory, including this host's `/data` checkout. PowerShell operators may provide existing stable
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

Earlier Docker missing-artifact gates and the wrapper with private licensing
inputs unset exited **1** as expected. Those negative checks neither describe
the now-successful host Editor nor imply runtime image success, a GameCI pull
or container activation.

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
source/context approval is not runtime-container or gameplay qualification.

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
These source checks are not player/browser acceptance.

U1's [source receipt](../../unity/MahjongRenderer/Documentation/source-evidence.json)
records metadata/schema/scene guards, **5 JS source tests** and **26-file C#9
syntax parsing**. That earlier source snapshot is not Unity API/shader
compilation. The separate actual host receipt above now records **99 EditMode
passes and genuine WebGL BuildPlayer output**. Source tools remain optional
diagnostics, not extra host prerequisites for Docker packaging.

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
and `browser-social-receipt.json`, with matching probe logs. These successful
shell/social executions are not a claim of Unity player or final release
approval.

Local host Chrome's `SIGTRAP` launch failure remains an environment limitation;
Chromium in the pinned official container worked. Phone-sized/emulated viewports
are **not real iOS/Android device evidence**, Unity board/touch input tests, or
mobile-performance qualification. These probes leave the table visibly
unavailable and do not qualify the separately built Unity player, runtime-Docker
gameplay, or the separate license/dependency redistribution gates.

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

The final scoped CI review additionally approved mandatory **6 renderer-shape/
relay-authority regressions + 1 genuine-player packaging source case** and
their named failure logs. Evidence:
`artifacts/test-results/player-regression-ci-wiring/receipt.json` and the final
review's `ciWiringReview`. All four source CI jobs are now green on the final
committed inputs. This is not a licensed CI Editor-build claim; the real
Editor/build qualification used the accepted host activation.

These mandatory jobs are source checks, not Unity assembly/API/shader
compilation, EditMode, WebGL, native browser or device qualification. The
separate local host result above is not a licensed CI-build receipt.
The real Editor/build job stays an explicit `workflow_dispatch` opt-in behind
the `licensed-unity-build`
environment, dependent on all four source jobs. The optional native published
API/browser checks remain separate; no skipped browser check is counted as a
browser pass. Workflow wiring and passing source jobs are not evidence of a
licensed CI Editor/build execution.

## Troubleshooting and validation limits

- **Host cannot see its accepted activation:** use the exact Editor and
  per-process portable Hub HOME/XDG environment above; do not persistently
  change normal HOME or export license contents. If licensing is still
  rejected, use the supported Unity/operator process, not manual bypasses.
- **Missing manifest, hashes, or files:** generate real outputs; do not add
  placeholders, rename compressed files, or disable the artifact gate.
- **Dirty-source rejection:** expected for earlier disclosed local artifacts,
  not the verified final clean manifest. Retain strict validation and consume
  the real rebuilt outputs; do not change flags to disguise dirty source.
- **Permission error:** match access narrowly for the private input and mounted
  cache/output paths; never use `chmod 777` or a whole-home mount.
- **Lobby visible, table unavailable:** expected without a real compatible
  build. Health, DOM readiness, static/mocked checks, and a valid manifest are
  not proof of a rendered playable table.
- **Local gameplay is approved, not an unrestricted release:** the final
  review supplies actual native/player/server/Docker scope. Clean post-commit
  build, strict validation and all four source CI jobs passed. Canonical
  clean-image packaging, native runtime checks and the live preview upgrade
  also passed. The first working local iteration is delivered; see PR #10 for
  delivery history. Physical iOS/Android, touchscreen double-tap
  discard, exhaustive gesture/target coverage and performance limit this
  first working iteration; they are not new completion blockers. Root/additions
  licensing/dependency notices remain separate redistribution gates.
