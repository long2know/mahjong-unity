# Mahjong Web browser client

The default entry loads one browser-native WebGL2 host. There is no engine
selector, build opt-in, Unity bridge/loader, fallback renderer or prebuilt player.
`npm run build` emits `../autotable/` directly from this source and local artwork.

The retained Client/World/Setup/Slot/Thing/Movement and DOM lobby/chat/action
controls are derived source, not a required external Autotable package/service.
`COPYING` and `UPSTREAM_SHA` remain authoritative notices for upstream code;
the .NET API comes from the owner's separate baseline project.

- `renderer/renderer-sink.ts`: data-only model rendering seam.
- `renderer/renderer-frame.ts`: viewer-safe full frame/layout projection,
  session identities and private source lookups.
- `renderer/renderer-actions.ts`: stale/duplicate/authority/gesture validation
  through the existing model/transport path.
- `renderer/webgl-host.ts`: DOM/viewport/focus lifecycle and GPU-fence receipts.
- `renderer/webgl-scene.ts`, `webgl-layout.ts`, `webgl-input.ts`: instanced
  drawing, physical tile finish, four felt regions, camera fit and native input.
- `renderer/gpu/`: small in-repository shader/matrix/instancing/picking helpers.
- `art/tiles/`: locally bundled CC0 face atlases; original source/license and
  historical input receipt retained under `art/source/` and `import-provenance.json`.

Run `npm run typecheck`, `npm run test:renderer`, and `npm run build`.
The default build records `web-build.json` (engine/source/file hashes) and the
actual `renderer-module-graph.json`. These are browser artifacts, not player
manifests. Actual gameplay/device evidence is separate from unit/source checks.
