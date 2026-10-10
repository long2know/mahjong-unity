# mahjong-unity

**First source iteration—not a playable browser release.** This repository
imports the established .NET game/backend and replaces table presentation with
Unity renderer source behind a browser adapter. Docker-managed build tooling is
included, but **no licensed Unity execution, compiled WebGL output, or real
browser/mobile/container gameplay qualification has been delivered**. Missing
Unity artifacts mean a visible **table unavailable** state, not a fake or legacy
renderer fallback. The source/dev lobby may remain available independently.

## What is preserved

- Behavioral baseline:
  `long2know/mahjong-autotable@d10d351e543aa3e341960de9800828b6c0662a82`.
- All five modes: **Changsha, four-player, three-player, Bamboo, and Minefield**.
  Changsha retains server authority; the others retain their existing
  collaborative relay semantics.
- Existing .NET rules/runtime, APIs, raw WebSocket **`/autotable/ws`**, and
  SignalR **`/hubs/changsha`**. Unity does not replace the transport or evaluate
  game rules.
- Browser lobby, identity/reconnect flow, room/rule controls, and chat. Existing
  room-chat gating to bound Changsha rooms is retained—not newly promised for
  every relay mode.

The imported source and regression fixtures are authoritative. Linked rule
guides and inspect-only Unity/Japanese-rule examples do not authorize changes
to that baseline.

## Source delivered

| Surface | Location and boundary |
| --- | --- |
| Existing server and rule tests | `src/backend/`; preserved .NET implementation |
| Browser shell/adapter | `src/frontend/autotable-src/`; retained Client/World/Setup/Movement with a data-only renderer seam |
| Unity renderer source | `unity/MahjongRenderer`; contract pin **6000.3.24f1**, procedural artwork, flat/perspective and rectangular/separate-hand presentation source |
| Frozen renderer protocol | `contracts/renderer-v1.schema.json`, **1.0.0**; complete frame/layout snapshots, opaque entity actions, rendered-frame readiness |
| Build/hosting tooling | `build.sh` / `build.ps1`, Docker Unity builder, real-Editor producer, artifact validator, separate ASP.NET hosting image |
| Provenance/notices | `contracts/source-provenance.json`, inherited frontend `COPYING`, `THIRD_PARTY_NOTICES.txt` |

Source capability is **not** mobile performance, touch, privacy, geometry, or
gameplay evidence. Those checks require the actual renderer build and devices.
Renderer v1 also has explicit UI limits: sticky click-to-grab is disabled
(press/drag/release only), and the legacy GPU benchmark is retired. This is not
a claim of complete legacy renderer-input or benchmark parity.

## Next build/run path

The default packaging flow requires Docker with a Linux-container daemon,
Buildx/Compose, a checkout and host shell—not a host Unity Editor, Node, .NET
SDK, or Python installation. **Legitimate Unity activation is a separate
prerequisite**, not supplied by pulling an image. No supported Personal-only,
Hub-free activation method has been established for this iteration.

After privately provisioning compatible licensing outside the checkout and
setting `UNITY_LICENSE_FILE` or `UNITY_LICENSE_DIR`:

```bash
bash build.sh --tag mahjong-unity:local
bash scripts/compose-bootstrap.sh
MAHJONG_IMAGE=mahjong-unity:local docker compose -p mahjong-unity up -d --no-build
```

PowerShell build counterpart: `./build.ps1 -Tag mahjong-unity:local`.
The default build runs actual Unity tests/build in Docker, verifies artifacts,
then builds Node/.NET in Docker. It fails loudly when the prerequisites or real
outputs are missing. Bootstrap preserves existing private signing
configuration; do not commit `.env` or license material.

For a new installation, open `http://127.0.0.1:8950/autotable/`. Existing
operators must retain their Compose project/volume identity; do not remove
persistent data to troubleshoot a renderer failure.

- [Docker build/run, cache/output paths, optional host Editor, troubleshooting](docs/integration/docker-build-and-run.md)
- [Renderer contract, lifecycle, privacy and presentation boundaries](docs/integration/renderer-v1.md)
- [Licensing, excluded assets and source provenance](docs/integration/licensing-and-provenance.md)

## Validation and remaining gates

**October 10, 2026 (UTC): final specialist source receipts verified; Tester's
first-iteration integration approval remains pending.**

| Area | Verified result—not production qualification |
| --- | --- |
| Backend/hosting | **90 focused .NET + 33 tooling tests**, plus Tester's **57 room/relay/reconnect/continuation tests**, no failures/skips. Actual API Release publish and localhost Development/test-database smoke: health, SignalR negotiation, raw WS open/close, missing-Unity 404s without HTML fallback, and clean shutdown. Not a Docker runtime/gameplay test. |
| Browser adapter | **38 preserved + 40 renderer-specific Node tests**, rerun after independent revision, plus **2 actual Game startup/auto-resize regressions**. Strict primary-graph TypeScript check and an actual asset-free Vite build passed. Real Client/World/wire/gesture and Vite/source tests with explicitly simulated browser/runtime services—not a Unity player. |
| Real browser shell | **6 Chromium-container cases** passed: desktop/phone-sized lobby and missing-build routes, invitations/join, chat receive/reload, and responsive viewport routing. Real localhost API and served bundle; no Unity player, rendered board input, actual iOS/Android device, touch or mobile-performance qualification. |
| Unity source | **5 JavaScript source tests**, metadata/schema/scene checks, and C#9 syntax parsing of **26 real files** for editor/player branches with zero syntax errors. No Unity API/shader compilation or EditMode execution. |
| Docker context | Tester approved the independent `.dockerignore` revision after **9 source/context gates**. Actual scratch-export filename probes verify filtering, not a production Unity build. |

Evidence: `artifacts/test-results/backend-summary.json`,
`artifacts/test-results/backend-final-compatibility/backend-final-compatibility.trx`,
`src/frontend/autotable-src/.unity-validation/f1-source-evidence.json`, and
[`unity/MahjongRenderer/Documentation/source-evidence.json`](unity/MahjongRenderer/Documentation/source-evidence.json).
Runtime/test logs under `artifacts/` and `.unity-validation/` are local ignored
outputs. The **373-module** browser source graph and `renderer-module-graph.json`
are not Unity WebGL artifacts. These are targeted checks, not full-suite,
performance, or device evidence.

The original F1 receipt is the author's source snapshot, not integration
approval. Tester's actual-caller review found two startup/resize bugs; their
independent revisions passed the regression and affected suites. Current
revision evidence is `artifacts/test-results/lead-browser-caller-evidence.json`
and `lead-browser-{integration-final,source-tests,typecheck,build}.log`.
The real-browser receipts are
`artifacts/test-results/migration-browser-actual/browser-unavailable-receipt.json`
and `browser-social-receipt.json`. The lock-matched Playwright **1.60.0**
Linux/amd64 container worked despite the local host Chrome `SIGTRAP` launch
failure. The revised browser runner's focused evidence review remains pending.
See [repeatable browser-shell commands and prerequisites](docs/integration/docker-build-and-run.md#real-chromium-container-shell-checks).

For an already-provisioned development/CI Node environment (not an additional
host prerequisite for Docker packaging), the existing manifest exposes:

```bash
cd src/frontend/autotable-src
npm run typecheck
npm run build
npm run test:unity
```

`test:unity` is the passing **Node source** suite, not Unity Editor tests.
See the [exact source-check commands and receipts](docs/integration/docker-build-and-run.md#verified-source-checks).

Missing-license and missing-artifact commands were executed and failed loudly
with exit 1. The actual Unity build remains an owner-controlled activation gate;
builder/image preparation is not a licensed build or genuine player output.
These six browser-shell cases do not close that gate or qualify Unity-rendered
board/input, physical mobile devices/touch/performance, multiplayer gameplay,
or the runtime hosting container's gameplay. Do not substitute source/mock
tests, shell-browser success, DOM readiness, localhost transport smoke, or file
integrity for those checks.

**Redistribution is also gated:** the owner has not selected a root/original
additions license, and a complete shipped dependency notice inventory is still
required. The inherited MIT code notice excludes images/sounds and does not
license all fork/backend code or dependencies. Noncommercial inherited tile
art, Majsoul sample resources and bundled sample assets are excluded. Authorized
local source work can proceed without claiming a production release license.
