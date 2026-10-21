# Renderer v1 integration

This documents the accepted **renderer contract** and Tester's approved scoped
local browser/Docker gameplay. Actual **99/99 EditMode tests** and genuine
production-settings WebGL passed. The final committed **clean 99-test rebuild
and strict validator also passed**. The earlier broad native review used
disclosed dirty source; final canonical clean-image packaging, native runtime
checks and the live preview upgrade have now passed. The contract authority is
[`contracts/renderer-v1.schema.json`](../../contracts/renderer-v1.schema.json),
with source mappings in
[`contracts/migration-contract.json`](../../contracts/migration-contract.json).
Both freeze `contractVersion: "1.0.0"`. Do not add fields or reinterpret backend
messages to accommodate a renderer.

Messages are UTF-8 JSON with camelCase fields, explicit nullable values, and
finite numbers. Unity uses pinned `com.unity.nuget.newtonsoft-json` **3.2.1**;
the schema is not a permissive raw-server DTO.

## Compatibility boundary

```text
Existing .NET rules/runtime, relay, APIs, SignalR
                  ↕
Browser lobby/chat + retained Client / ClientUi / World / Setup / Movement
                  ↕
renderer-sink → renderer-frame / renderer-actions / unity-host
                  ↕
Real Unity WebGL instance: MahjongBridge
```

Browser adapter source lives under
`src/frontend/autotable-src/src/renderer/`; the Unity project is
`unity/MahjongRenderer`. Unity draws and picks; it opens **no** WebSocket,
SignalR, authentication, or game-rule connection.

All five existing modes remain: `CHANGSHA`, `FOUR_PLAYER`, `THREE_PLAYER`,
`BAMBOO`, and `MINEFIELD`. Changsha keeps its server-authoritative path; the
other four keep existing collaborative relay behavior. The browser owns
same-origin **`/autotable/ws`**, APIs, and **`/hubs/changsha`**. `/ws/autotable`
is not an alias. Bare `/autotable/` is the lobby. Existing room-chat gating
to bound Changsha rooms is preserved, not expanded to relay modes.

## Receiver and lifecycle

The browser uses `unityInstance.SendMessage("MahjongBridge", method, jsonString)`.

| Browser → Unity method | Purpose |
| --- | --- |
| `Configure` | Bind the instance, contract, and build identity. |
| `Clear` | Bind a new session epoch and immediately revoke old state/input. |
| `ApplyLayout` | Complete slot catalog for one `layoutRevision`. |
| `ApplyFrame` | Atomic full render snapshot, not a delta. |
| `SetViewport` | Measured CSS/buffer size, DPR, safe insets, and hand band. |
| `SetPlayback` | Suspend/resume visual work without stopping browser networking. |
| `ReceiveActionResult` | Return `submitted`, `local`, `ignored`, or `rejected`. |

The WebGL `.jslib` callback is **`window.MahjongUnityBridge.emit(jsonString)`**.
Only the frozen `ready`, `frameRendered`, `action`, and `error` envelopes cross
back to the browser.

1. Install the callback, fetch `/autotable/unity/renderer-build.json`, validate
   its supported pins and relative same-origin artifact paths, then load its
   real loader and call `createUnityInstance`.
2. Send `Configure` after instance creation; wait for matching C# `ready`.
3. Send `Clear`, latest viewport, full layout, then newest coherent frame.
   Pending visual updates coalesce; they are not a queue of stale game actions.
4. A frame becomes visibly ready only after the Unity cameras have rendered it
   and emitted matching `frameRendered`. Parsing JSON is not a rendered-frame
   acknowledgment. `unity-renderer-ready` / `scene-shell-ready` depend on that
   event; `game-shell-ready` means only the earlier DOM shell is available.
5. Disconnect, room/authority change, or protocol failure clears the old
   session. Teardown detaches callbacks/listeners, drops queued frames, and
   awaits `unityInstance.Quit()`.

`instanceId`, `sessionId`, `layoutRevision`, and monotonic per-session `frameId`
must match. These are adapter identities/counters, **not** server versions.
Complete frames replace all entities; absence removes an entity. Mismatched,
stale, malformed, or unsupported messages fail closed. Visual publication is
coalesced at at most 30 Hz; existing input/network processing is not throttled.
Only one full frame may be awaiting its matching `frameRendered` receipt.
Newer complete frames and layouts replace the browser's pending publication
instead of filling the native player's message queue.

The pinned native player pauses when its window loses focus. The browser
therefore suspends visual publication for both hidden documents and visible
but unfocused windows. Networking and authoritative model updates continue;
focus/visibility restoration sends the newest viewport and snapshot, not an
idle backlog. Startup and rendered-frame response deadlines count only active
visible/focused playback. An active player that stops acknowledging frames for
30 seconds fails visibly and revokes input, with **Reload table** available.

The actual-player `actual-idle-visible-unfocused-memory` case uses a private
Xvfb display and disables Playwright's always-focused override before changing
OS focus. It checks native heap bounds, no full-frame publication during the
pause, the newest rendered frame on resume, and a real tile action after the
old action contexts expire. This is not physical-device evidence.

## Privacy and actions

Unity sees renderer `entityId`s, not source wire keys, physical tile IDs,
credentials, raw updates, or the hidden-back pool. Concealed faces use
`faceIndex: null`, never a guessed tile face. Before the first entitled
Changsha snapshot and after authority loss, private entities/hand data are
cleared; pre-baked local placeholder tiles are not exported.

`actorSeat` and spectator `viewSeat` are separate. Following a seat or changing
the camera cannot grant a hand. `localHand.entityIds` supplies the projected
own-hand presentation order; it is not authority to infer other hand entities.
`slotName` stays the backend-semantic address, while the entity pose may include
the retained model's held/shifted presentation.

The browser validates an action against the current instance/session, frame
context, live entity eligibility, authority, and gesture. It maps an eligible
opaque renderer entity to the existing browser action pipeline. No offline
mutation queue, new rule RPC, or optimistic Changsha identity is introduced.
`submitted` means sent through that pipeline, **not** accepted by the server.
Rule-command game IDs retain their baseline source; do not replace a runtime
game ID with a public room alias.

## Presentation capability and evidence

The source Display controls expose **Board layout** (`Automatic (compact table)`,
`Classic square`, `Rectangular`) and **Local hand layout**
(`Automatic (compact table)`, `In table`, `Separate local hand`) and **Camera
view** (`Automatic (portrait fit)`, `Perspective`, `Flat`). Automatic is a
browser preference, not a new renderer enum. Legacy perspective controls and
the P shortcut still persist an explicit view, separate from game authority.

Both layout preferences default to `auto` in `mahjong.settings.v1`. The
implemented resolver uses the **canvas CSS dimensions**: Changsha uses a fitted
traditional table on desktop and phones; relay modes use rectangular below
768 px and classic otherwise. Below 768 px or 520 px high, the automatic local
hand is separate. Automatic camera is flat in narrow portrait and perspective
otherwise. Older saved perspective choices migrate without being overridden.
Explicit choices
persist and override that automatic fit. Resize republishes presentation; it
does not change the variant, actor seat, or concealed-hand entitlement.

`Presentation.camera` is an optional, nullable local view override:
`{ scale, yaw, pitch, roll }`. Scale is **0.5..2.5**; yaw and roll are bounded to
**-180..180 degrees**; pitch is **20..90 degrees** or `null` to keep the original
view's angle. Missing/null camera retains the exact original automatic fit.
Manual scaling can deliberately crop the table, but the perspective camera stays
above the table and outside its geometry. Flat view ignores pitch; explicitly
adjusting pitch selects perspective. Only the table camera moves, never source
coordinates, tile faces/entitlements, seat authority or the separate hand camera.
This additive presentation field does not change native game wire messages.
Its exact contract/schema hashes are recorded as an extension in source
provenance; the original one-time import hashes and baseline inventory remain
unchanged.

The settings drawers share zoom, yaw, pitch and roll sliders plus Reset view.
The four preferences persist in the existing local settings blob. Pinch zoom,
two-finger drag orbit and twist roll work on the board, not the hand/chrome.
Mouse wheel zoom and Alt-left/middle-drag orbit preserve ordinary left-drag and
right-click; Shift with a camera drag rolls. Focused-canvas arrows tilt/turn,
Shift-Left/Right roll, +/- zoom and Home resets. Typing and browser shortcuts are
not claimed by camera keyboard input.

Camera contacts cancel active game drags and remain blocked through the final
old finger's release until a fresh ordinary contact or game keystroke. The browser returns the
normal rejected/not-actionable result for game intents during that interval;
harmless hover and cancellation can settle without an action-result loop or
blocking Escape. C# independently
invalidates input when any camera axis changes and rejects picking against a
camera override until that exact frame has been committed to the scene.
The local `CancelInput` receiver clears native pointer and double-click state at
gesture start, even when the fingers/mouse never move; cancellation is not a
session/authority change or a fabricated viewport resize.

**October 15, 2026 traditional-layout correction:** the previous independent
lane-packing experiment incorrectly placed walls outside the hands and closed
gaps left by drawn stacks. Changsha rectangular presentation now preserves every
canonical wall pose, tier and empty draw position. Four fixed discard rivers sit
inside the wall ring; melds sit alongside each player's remaining hand outside
it. The separate local hand includes that player's melds instead of duplicating
them on the table. Public tiles retain their natural proportions, and a steeper
oblique view keeps the side rivers readable without moving the wall.
The approved tile artwork, bevels, felt, rail and shadows remain unchanged.
Header, claim, chat, move-log and install chrome reserve their actual visible
footprint. Explicit classic layout retains the original source poses.

Ordinary unmoved touch release discards an authorized tile once; mouse retains
selection/double-click. Movement, cancellation, long press, multi-touch, resize
and authority loss cannot accidentally discard. The actual pinned Editor
passed **144/144** cases, including canonical wall/depletion identity, bounded
rivers, hand/meld adjacency, portrait/Kong hand widths, public-face CSS sizes,
camera bounds and native pointer guards. The earlier mobile correction's
compiled browser probe passed **20** cases: 360/390/500-width single taps
at DPR1/2/3, progressed public piles, chat/orientation/explicit preferences,
canceled gestures, desktop input and count-only manual pickup. Separate real
native retakes preserved four relay variants, spectator privacy, chat and
reconnect. These are Chromium emulation results, not physical-device claims.
The native API smoke checks public listing and chat, then makes its room private
before testing owner reconnect: a public seating room with no remaining live
human intentionally retires when its sole owner disconnects.

Repeat the mobile probe against a genuine clean runtime image and the matching
actual Editor receipt, rather than overwriting an older build's evidence:

```sh
python3 tests/migration/run_runtime_player_integration.py \
  --image <qualified-clean-runtime-image> \
  --editor-receipt artifacts/test-results/<current-clean-editor>/receipt.json \
  --mobile-playability --port 5099 \
  --output-name migration-player-mobile
```

| v1 capability | Source boundary | Verified local scope and limits |
| --- | --- | --- |
| `perspective` / `flat` | Unity camera presentation; browser settings, no rule change | Actual rendered frames/camera and native picking scopes approved; no performance guarantee |
| `classic` / `rectangular` | Fit/reposition groups without stretching tiles or changing semantic slots | Real all-mode startup and retained portrait/landscape layout scopes; not exhaustive held-pose coverage |
| `in-table` / `separate` hand | One canvas, independent orthographic local-hand band; no duplicate local entities in the table camera | Changsha entitlement/concealment and 294/294 hand centers across 21 browser layouts; formerly blocked lower-row native select/discard passed |
| Resize, orientation, DPR, safe areas | Canvas measurements; pixel ratio capped at 2; viewport changes cancel active gestures | Retained browser DPR1/2/3, portrait/landscape and chat collapsed/open results, not physical phone rotation/performance |
| Relay extras | Sticks/markers, Kita, tray/payment/riichi, extra/open-wall slots remain in scope | Four modes' 8 native tile/stick target commits and server echoes approved; not every extra/target-cell gesture |

These are actual browser/native-input scopes, not physical iOS/Android evidence.
Do not infer a new semantic target slot or claim exhaustive cross-group/raycast
coverage. The review records a nonblocking payment-cell caveat: exact center
aim at `payment.0@1` missed in FOUR_PLAYER/MINEFIELD, while exposed alternative
real payment cells committed. A calculated geometric witness is not a native
hit receipt.

**Known v1 UI limits:** sticky click-to-grab is visibly disabled; the supported
input source is press/drag/release. The legacy GPU benchmark is visibly retired
because Unity owns rendering. No sticky field, synthetic Unity benchmark, or
hidden legacy fallback is added to the frozen contract. Do not claim complete
legacy renderer-input parity or performance qualification.

## Verified Editor/build, player and source evidence

On **October 10, 2026**, the actual **6000.3.24f1 / 4e7b9b5b6244** host
Editor accepted existing portable Hub activation, imported packages, compiled
C#/APIs and the IL2CPP WebGL player, and passed **99/99 EditMode tests** after
independent repairs.
The unchanged producer enforced a successful WebGL BuildReport with zero
errors and nonzero output. Evidence:
`artifacts/test-results/relay-native-target-repair/editor-rebuild-receipt.json`.
Tester **APPROVE**, recorded **October 11, 2026, 01:01:59 UTC
(October 10, 18:01:59 PDT)**, is
`artifacts/test-results/migration-player-final-relay-review.json`. Its exact
artifact/runtime identities and preserved earlier evidence, not a hardcoded
current hash, define the approved iteration.

Genuine loader/framework/wasm/data and hosted MIME/hashes were verified.
The earlier broad native-review runtime records
**`sourceDirty: true`, `developmentBuild: false`**.
Read each produced `artifacts/unity-webgl/renderer-build.json` for its actual
build identity and cleanliness. The validator's
`--allow-source-dirty` is explicit local qualification only; the browser's
loopback-only `unityLocalBuild=1` opt-in is likewise not a release gate bypass.
Final source/CI/tests are committed at
`684f67837eb9fbef0475162702f5d3542d87b949`; its actual **99-test clean rebuild**
produced the manifest at **October 11, 2026, 01:07:03 UTC**, with that source
revision and **`sourceDirty: false`, `developmentBuild: false`**. Strict
validation passed without an override; the reviewed renderer-input tree is
unchanged. All four source CI jobs are green. Canonical clean-image packaging,
native health/player checks and the live preview upgrade also passed.
See the [verified host command and artifact checks](docker-build-and-run.md#verified-host-editor).

The final review approves actual C# readiness/rendered frames and native
gameplay: **8 tile/stick commits with one server echo each** across the four
relay modes, plus **16 F / Shift+F / right-click / R routes**. Previously approved
Changsha manual/automatic pickup/discard, count-only pickup wire, chat,
privacy/spectator/reconnect and the **21-layout / 294-hand-center** scope are
retained—not relabeled as a new full rerun.

Final clean runtime evidence is
`artifacts/test-results/final-clean-runtime/summary.json` and
`artifacts/test-results/final-clean-runtime/preview-upgrade-receipt.json`.
The **`mahjong-unity:final-684f678`** preview is healthy at
**`http://127.0.0.1:5086/autotable/`**, without `unityLocalBuild`.
Clean isolated Changsha native discard and THREE_PLAYER tile/stick/keyboard
passed; post-upgrade actual C# rendering, `frameRendered` and native discard
also passed. Earlier all-mode/mobile scopes remain preserved, not a new full
rerun. Physical iOS/Android, touchscreen double-tap discard and exhaustive
gesture/target coverage remain advisory limits of this first working iteration.

F1's final receipt records **38 preserved + 40 renderer-specific Node tests**,
strict primary-graph TypeScript checking, and an asset-free Vite build
(`src/frontend/autotable-src/.unity-validation/f1-source-evidence.json`).
Real Client/World/Slot/Thing/Movement and source-wire/gesture paths are exercised;
host callbacks are explicitly mocked. The 373-module graph and source mutations
are not evidence that a Unity camera or device pointer rendered/ran.

The author's F1 receipt is not integration approval. Tester's real Game caller
found an initial animation sentinel escaping into the first frame and a
reentrant resize overwriting the separate-hand viewport. Independent revisions
settle the retained animations before publication and prevent superseded
measurements from overwriting/sending newer viewports. The **2 actual-caller
regressions**, **78 affected Node tests**, strict typecheck and Vite build passed
after revision. Supplemental source evidence verifies correct hand bands,
backing buffers and strictly newer transmitted resize revisions; its runtime
handshake is explicitly simulated
(`artifacts/test-results/lead-browser-caller-evidence.json`). These are source
regressions, not player/browser acceptance.

U1's [source receipt](../../unity/MahjongRenderer/Documentation/source-evidence.json)
records metadata/schema/scene guards, **5 JS source tests**, and C#9 parsing of
**26 files** for editor/player branches with zero syntax errors. That earlier
syntax snapshot alone is not Unity assembly/API/shader compilation, Editor
import, EditMode tests, or a BuildPlayer result. The separate actual host
receipt above supplies real Editor/build evidence and the separate final review
supplies scoped player/runtime acceptance. Physical-device/performance limits
are separate from local delivery; the actual post-commit clean Editor build,
clean runtime checks and live preview upgrade are complete.

## Failure behavior

Missing manifest/artifacts, failed loading/handshake, or invalid renderer input
must produce a visible **table unavailable** state. The source/dev lobby and
available DOM controls may remain accessible, but there is **no legacy GPU,
JavaScript-canvas, or synthetic Unity fallback**. Lobby-only survival is not
player success.

The hosting image requires actual Unity artifacts and fails its build when they
are absent or invalid. Manifest/hash/WebAssembly checks establish file
integrity, not legitimate activation, successful camera output, multiplayer
gameplay, or browser/mobile readiness. See [Docker build and run](docker-build-and-run.md).
