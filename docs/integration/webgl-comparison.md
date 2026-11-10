# Experimental WebGL2 renderer comparison

This branch's comparison build defaults to a real browser WebGL2 renderer.
An ordinary build still defaults to Unity and excludes the custom GPU graph.
There is no automatic fallback between engines. No Unity Editor build is needed
when the C# renderer is unchanged.

## Run locally

Use the existing locked frontend dependencies and .NET 10 SDK:

```sh
cd src/frontend/autotable-src
npm ci --no-audit --no-fund
cd ../../..
dotnet restore src/backend/src/Mahjong.Autotable.Api/Mahjong.Autotable.Api.csproj
bash scripts/run-webgl-comparison.sh
```

Open `http://127.0.0.1:5087/autotable/` and create a disposable game normally.
The foreground process binds only loopback, uses a new synthetic SQLite fixture
and signing values per run, and does not touch an operator database/config.
Set `MAHJONG_COMPARISON_PORT` to choose another free unprivileged port; port 5086
is deliberately excluded. This native-host runner is **not Docker packaging
qualification**; the normal production Docker/Unity producer gates are unchanged.

To compare the genuine existing Unity player on the same isolated backend:

```sh
MAHJONG_UNITY_REFERENCE_URL=http://127.0.0.1:5086 \
  bash scripts/run-webgl-comparison.sh
```

That optional step makes public **asset GETs only**, verifies every artifact's
declared size/hash, and preserves the original Unity manifest/source identity.
Repeat the explicit reference URL when reusing an existing staged reference;
the runner refuses to advertise an undeclared old Unity payload.
Append `renderer=unity` to the comparison game's URL to select it. Without those
genuine artifacts, Unity selection fails explicitly. `renderer=webgl2` selects the
custom renderer explicitly; it is also this comparison build's default.
`comparison-build.json` identifies the browser build and any separate Unity
origin. Combining them does not create a new qualified Unity build/image.

## Controls and checks

The existing camera settings, wheel zoom, Alt-left/middle drag, two-finger
orbit/pinch/twist and focused-canvas arrows/+/−/Home remain available.
Shift-right drag additionally pans the custom board; Reset view clears that pan.
The separate hand is independent of board camera changes. Ordinary touch
discards once; desktop double click discards, right click flips an eligible relay
tile, and ordinary drag uses the existing eligible slot/action route.

```sh
cd src/frontend/autotable-src
npm run typecheck
node --test tests/node/webgl-*.test.mjs tests/node/unity-host.test.mjs \
  tests/node/unity-actions.test.mjs tests/node/unity-frame.test.mjs \
  tests/node/unity-camera-controls.test.mjs
cd ../../..
mkdir -p .work
TMPDIR="$PWD/.work" node tests/migration/browser-webgl-comparison.mjs \
  http://127.0.0.1:5087
```

The browser check uses installed host Chrome and real input on disposable
fixture rooms. Its ignored `artifacts/webgl-comparison/` outputs distinguish
WebGL2 GPU-fence receipts from genuine C# Unity receipts. Software GPU,
emulation, JavaScript heap, WASM memory and frame-timing scopes must remain
explicit; neither source bundle size nor reserved WASM capacity is a runtime
performance comparison. This remains an experimental renderer, not a declaration
of production visual/device/feature parity.
