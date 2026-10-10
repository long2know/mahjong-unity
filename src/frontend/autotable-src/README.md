# Mahjong Unity browser client

This directory contains the imported browser/model baseline pinned in `UPSTREAM_SHA`, plus the Unity renderer migration. The ASP.NET backend and its wire/rules remain outside this directory; `server/protocol.ts` is retained only for browser type imports.

## Active renderer boundary

- `src/game.ts` and `src/scene-shell.ts` start the real Client/World and retained lobby, DOM controls and effects independently of Unity availability.
- `src/renderer/renderer-sink.ts` buffers math-only World render data.
- `src/renderer/renderer-frame.ts` publishes coherent full v1 frames/layouts with session-scoped opaque identities, current viewer entitlements and private source lookups.
- `src/renderer/renderer-actions.ts` validates semantic input and calls retained World/Slot/Movement/Client operations. Claim, own-turn and hand-result continuation remain browser DOM controls with their original runtime contexts.
- `src/renderer/unity-host.ts` loads only `/autotable/unity/renderer-build.json` and the validated Unity-generated loader. It verifies contract/editor/provenance fields, safe same-origin paths, MIME, byte lengths, SHA-256 and wasm magic.

The seven receiver methods target `MahjongBridge`: `Configure`, `Clear`, `ApplyLayout`, `ApplyFrame`, `SetViewport`, `SetPlayback`, `ReceiveActionResult`. Callbacks use `window.MahjongUnityBridge.emit`. Only a matching C# `FrameRendered` receipt sets `unity-renderer-ready` and `scene-shell-ready`; `game-shell-ready` and `scene-effects-ready` are DOM-only markers.

Missing, incompatible, corrupt or unsupported builds are visible unavailable states. No Three/WebGL/Canvas renderer is instantiated as a fallback. Historical GPU source files are unreachable from the active entry and the Vite graph rejects their reintroduction.

## Display and input

Perspective remains the default and the P shortcut follows the existing checkbox. Board layout and Local hand layout persist in `mahjong.settings.v1`. Automatic uses rectangular/separate below 768 measured CSS pixels, otherwise classic/in-table. Explicit choices override automatic fitting. Resize/orientation/visual viewport/DPR/insets changes update the real canvas buffer at capped DPR 2 and cancel active gestures before changed picking transforms.

Unity v1 uses press/drag/release. The inherited sticky checkbox is visibly disabled because the frozen contract has no sticky configuration. The legacy benchmark switch is explicitly retired. Camera look-down/zoom and tile labels remain browser preferences; spectator viewpoints never grant actor authority.

Development Unity builds are always rejected. A dirty-source non-development artifact may be inspected only with explicit `?unityLocalBuild=1` on localhost/127.0.0.1/IPv6 loopback; it remains unqualified for release.

## Source validation

Run from this directory:

```sh
npm ci --no-audit --no-fund
node --test tests/node/room-startup-readiness.test.mjs tests/node/hand-result-render-and-wire.test.mjs tests/node/sc2-slot-owner.invariants.test.mjs tests/node/perspective-settings.test.mjs
npm run test:unity
npm run typecheck
npm run build
```

Vite output is `../autotable/`. `renderer-module-graph.json` records the actual browser source graph, not a Unity build. Unit host fixtures simulate loader/bridge callbacks explicitly; they do not prove Unity compilation, rendered graphics or touch picking. Real Unity editor/player/browser/mobile validation requires the legitimate licensed build and remains separate.

Original DOM result labels, the app icon and source-synthesized sound replace excluded atlas/images/font/PDF/static audio assets. `COPYING` and `UPSTREAM_SHA` are retained and copied to browser output. The original upstream code notice does not settle licensing of fork additions; repository notices/decisions govern redistribution.
