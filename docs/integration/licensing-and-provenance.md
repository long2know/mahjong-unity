# Notices and source provenance

The original Autotable code notice (`src/frontend/autotable-src/COPYING`) and
upstream revision marker (`UPSTREAM_SHA`) are retained unchanged. Their presence
records derived source; it is not an external runtime/install requirement.
The owner's .NET baseline and new contributions have distinct provenance and
must not be blanket-attributed or relicensed as upstream Autotable.

`contracts/source-provenance.json` is the historical immutable baseline import
receipt, not a current dependency manifest. The former audit contract is at
`contracts/history/migration-contract.json`; neither triggers a network checkout
or imports an old renderer during build.

The current tile atlases are local at
`src/frontend/autotable-src/art/tiles/{Faces,FacesLabelled}.png`. They were moved
byte-for-byte out of the removed engine project. Their original CC0 source and
creator license are retained at `art/source/FluffyStuff/`; the historical
artwork/derivative hashes are in `art/tiles/import-provenance.json`. Paths and
engine budgets inside that receipt describe its original generation, not a
claim that the old engine remains installed.

The excluded noncommercial tile/model/font/media assets have not been restored.
The original root contributions retain the repository owner's existing license
disposition; no new blanket license is selected by this migration.

The runtime image retains the original code notice, source receipt,
FluffyStuff license and generated npm/NuGet notices under `/licenses/`.
Node/.NET/base-image dependencies retain their individual terms. Unity,
Editor/activation tooling and their runtime packages are no longer build or
distribution inputs.
