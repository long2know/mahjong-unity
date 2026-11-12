# Viewer-safe WebGL renderer contract

The browser retains Client/World and publishes data-only `RendererLayout` and
`RendererFrame` snapshots through `BufferedRendererSink` and
`RendererFrameAdapter` to the sole `WebglHost`. The schema is
`contracts/renderer-v1.schema.json`, version **1.0.0**. Its frame/action fields
remain compatible; obsolete engine-manifest/handshake definitions were removed.

`instanceId`, `sessionId`, `layoutRevision` and monotonic `frameId` correlate
publication and input. Private tile identities, hidden faces, credentials and
raw backend/runtime versions never become renderer entities. Full frames
replace all entities; missing entities disappear. Opaque renderer actions pass
through current eligibility/authority/gesture checks before using the retained
model/transport. A server submission is not an optimistic server acceptance.

Only one GPU submission is pending. A real WebGL2 fence completes before an
engine-labelled `mahjong:renderer-frame` / `frameRendered` receipt commits
readiness. `webgl-renderer-ready` and `scene-shell-ready` mean an actual rendered
frame; DOM bootstrap readiness is separate. There is no C# callback or engine
loader. Unsupported WebGL, startup/deadline failure and context loss revoke
input with an explicit reload state, not another renderer.

Focus/visibility suspends visual publication while networking/model updates
continue. New full frames coalesce, and resuming uses the newest state. Resize,
presentation/camera changes and multi-contact camera gestures cancel stale
input. Canonical AABB/entity/slot picking is separate from bevels, contact
shadows, cues and the four noninteractive felt rivers. Separate-hand geometry
and camera remain independent from board navigation.

Teardown cancels scheduled work and gestures, disconnects observers, deletes
GPU buffers/VAOs/programs/textures/fences and releases the context. No engine
WASM heap or prebuilt player is allocated.
