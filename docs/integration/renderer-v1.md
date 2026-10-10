# Renderer v1 integration

This is the accepted **source contract**, not evidence of a compiled or playable
Unity browser build. The authority is
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

The source Display controls expose **Board layout** (`Automatic (mobile fit)`,
`Classic square`, `Rectangular`) and **Local hand layout**
(`Automatic (mobile fit)`, `In table`, `Separate local hand`). Automatic is a
browser preference, not a new renderer enum. The existing perspective settings
and P shortcut remain separate from game authority.

Both layout preferences default to `auto` in `mahjong.settings.v1`. The
implemented resolver uses the **canvas CSS width**: below 768 px it selects
rectangular/separate; at 768 px or above, classic/in-table. Explicit choices
persist and override that automatic fit. Resize republishes presentation; it
does not change the variant, actor seat, or concealed-hand entitlement.

| v1 capability | Source boundary | Evidence still required |
| --- | --- | --- |
| `perspective` / `flat` | Unity camera presentation; browser settings, no rule change | Real WebGL camera/picking checks |
| `classic` / `rectangular` | Fit/reposition slot groups without stretching tiles or changing semantic slots | All five variants, held/shifted transforms, landscape/portrait geometry |
| `in-table` / `separate` hand | One canvas, independent orthographic local-hand band; no duplicate local entities in the table camera | Entitled hand ordering, drawn tile, remote concealment, touch targets |
| Resize, orientation, DPR, safe areas | Browser measures the canvas; pixel ratio capped at 2; viewport changes cancel active gestures | Real mobile rotation, chat scrolling, pointer/touch alignment |
| Relay extras | Sticks/markers, Kita, tray/payment/riichi, extra/open-wall slots remain in scope | Real interactive relay regression |

Layout helpers and renderer/browser source are not device evidence.
Rectangular cross-group held/drag transforms remain a runtime qualification
item; do not infer a new target slot or rewrite the semantic descriptor to hide
an unverified remap.

**Known v1 UI limits:** sticky click-to-grab is visibly disabled; the supported
input source is press/drag/release. The legacy GPU benchmark is visibly retired
because Unity owns rendering. No sticky field, synthetic Unity benchmark, or
hidden legacy fallback is added to the frozen contract. Do not claim complete
legacy renderer-input parity or performance qualification.

## Verified source evidence

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
(`artifacts/test-results/lead-browser-caller-evidence.json`). Browser/overall
source-gate approval remains pending.

U1's [source receipt](../../unity/MahjongRenderer/Documentation/source-evidence.json)
records metadata/schema/scene guards, **5 JS source tests**, and C#9 parsing of
**26 files** for editor/player branches with zero syntax errors. That is not
Unity assembly/API/shader compilation, Editor import, EditMode tests, or a
BuildPlayer result. The source-iteration integration gate still requires
Tester's approval; runtime/device/performance qualification remains absent.

## Failure behavior

Missing manifest/artifacts, failed loading/handshake, or invalid renderer input
must produce a visible **table unavailable** state. The source/dev lobby and
available DOM controls may remain accessible, but there is **no legacy GPU,
JavaScript-canvas, or synthetic Unity fallback** and no completed-game claim.

The hosting image requires actual Unity artifacts and fails its build when they
are absent or invalid. Manifest/hash/WebAssembly checks establish file
integrity, not legitimate activation, successful camera output, multiplayer
gameplay, or browser/mobile readiness. See [Docker build and run](docker-build-and-run.md).
