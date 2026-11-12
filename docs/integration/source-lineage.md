# What “Autotable-derived” means in Mahjong Web

There is **no external Autotable installation, npm package, engine download or
required remote Autotable service**. The browser build resolves in-repository
modules and ordinary locked npm packages; the server builds its .NET project
and ordinary NuGet dependencies. Default requests go to this application's own
`/autotable/`, `/autotable/ws`, `/api/` and hub addresses.

Those retained names preserve URLs, wire shapes, database namespaces and existing
deployments. They are not a runtime link to the original project.

## A. Original upstream lineage

`src/frontend/autotable-src/UPSTREAM_SHA` records
`pwmarcz/autotable@8b81d92aa37997dcfbcc6724d3bd3f694f9cc53a`.
The upstream code notice is retained verbatim in `COPYING`.
The actual default browser still uses modified/derived:

- `src/base-client.ts`, `client.ts`, `client-ui.ts`: collections, transport and
  tabletop client/UI scaffolding.
- `src/world.ts`, `setup.ts`, `setup-slots.ts`, `setup-deal.ts`: table/model
  setup, slot topology, presentation and collaborative relay operations.
- `src/slot.ts`, `thing.ts`, `movement.ts`, `types.ts`, `utils.ts`: model,
  movement, coordinate/math conventions and supporting semantics.

Changing the draw calls did not replace these modules or erase their ancestry.
The new renderer's `renderer/gpu/` primitives retain/modify in-repository
WebGL work from the owner's earlier fork. They are not imported from the NFL
project and are not represented as a clean-room engine.

Tile pictures are **separately sourced CC0 FluffyStuff artwork**, not the
excluded noncommercial upstream atlas. See `art/source/FluffyStuff/LICENSE.md`
and `art/tiles/import-provenance.json`. Original label/cue/plastic/felt code
and ordinary third-party libraries have separate provenance.

## B. The owner's previous application

The imported application baseline is
`long2know/mahjong-autotable@d10d351e543aa3e341960de9800828b6c0662a82`.
`contracts/source-provenance.json` preserves the immutable import inventory.

`src/backend/src/Mahjong.Autotable.Api/` contains the owner's existing .NET
API, persistence, multiplayer/relay, Changsha runtime/scoring and lobby/chat
services. Importing it from that project does **not** establish that the C#
implementation was authored by `pwmarcz/autotable`; the two lineages must not
be conflated. This transition keeps those working in-repository services and
their established message contracts, not an external upstream server.

## C. Removed implementation

The active checkout no longer contains:

- Unity project/C#/shaders/packages/scenes, Editor/build container/activation
  tooling, IL2CPP/player manifests or required prebuilt runtime artifacts.
- `UnityHost`, the generated-loader/SendMessage/.jslib callback route, the
  comparison selector/opt-in flag, or a Unity source/qualification CI job.
- The unreachable old Three renderer (`asset-loader`, `main-view`, `object-view`,
  `mouse-ui`, `thing-group`, `center`, `selection-box`, `three-renderer`,
  custom outline) and unused old WebGL demos/animation/atlas runtime.

Portable CC0 artwork, the topology fixture, and used GPU helpers were moved
out before deleting those trees. Authority/frame/action/layout/privacy/focus
tests remain and exercise the sole WebGL path. Removed tests were specifically
for the deleted Unity producer/player APIs and old Three renderer; Docker,
artifact closure, source/digest publishing and default-graph tests replace
those build-specific gates.

The preserved import contract is historical at
`contracts/history/migration-contract.json`; it is not a current build input.
Historical Unity/comparison implementation and receipts remain in Git/PR
history. Required attribution is retained independently from runtime code.

A complete rewrite of the remaining model/client is a different product change,
not needed to remove external dependencies or Unity. It would not retroactively
make existing derived code clean-room.
