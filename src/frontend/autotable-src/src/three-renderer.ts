// Phase K Wave 5 — Three.js renderer chunk.
//
// Wave 4 left a single 886 kB `scene-shell.<hash>.js` chunk that the
// user had to download before the WebGL canvas could composite.
// three.js alone is ~575 kB minified — that put a hard floor under
// any further `scene-shell` shrink, which Wave 4's memo logged as the
// Wave-5 followup.
//
// Wave 5 peels everything that statically imports `three` out of the
// `scene-shell` graph and into THIS module, which is now dynamic-
// imported by `scene-shell.ts` once the canvas mount is requested.
// Net effect:
//
//   • `scene-shell.<hash>.js` — thin coordinator (~5 kB).  No
//     static three.js import; just publishes `scene-shell-ready` and
//     awaits the `three-renderer` chunk.
//   • `three-renderer.<hash>.js` (this file) — three.js + AssetLoader
//     + Game + World + MainView + ClientUi + everything else that
//     pulls a `Vector3` / `Quaternion` / `Mesh`.  ~600 kB, fully
//     lazy — pays only on game-URL navigation.
//
// Surface contract (consumed by `scene-shell.ts`):
//   • `mountThreeRenderer()` — instantiates `AssetLoader`, awaits the
//     asset load, boots `Game`, publishes `window.game` + `window.three`
//     for debug callers, and resolves to `{ game, client }`.
//     Idempotent — re-entry resolves to the existing singletons.
//     Side-effect: mints `data-testid="three-renderer-ready"` so
//     Vasquez's Wave-5 specs can wait on the heavy chunk specifically
//     (the `scene-shell-ready` testid still wraps the whole flow).

import { AssetLoader } from './asset-loader';
import { Game } from './game';
import type { Client } from './client';

interface RendererHandles {
  game: Game;
  client: Client;
}

let mountedHandles: RendererHandles | null = null;
let mountPromise: Promise<RendererHandles> | null = null;

export async function mountThreeRenderer(): Promise<RendererHandles> {
  if (mountedHandles !== null) return mountedHandles;
  if (mountPromise !== null) return mountPromise;

  mountPromise = (async (): Promise<RendererHandles> => {
    const assetLoader = new AssetLoader();
    await assetLoader.loadAll();

    const game = new Game(assetLoader);
    // Phase K Wave 4 → Wave 5 → Wave 6 → Wave 7 — Expose `game` on
    // `window` for debug callers (E2E specs, manual console
    // poking).  Wave 6 introduced a `?debug=three` lazy-import
    // hook that exposed the full `three` namespace; under Vite
    // that dynamic import conflicts with the static `from 'three'`
    // imports in this module's static graph (rollup raises a
    // warning + collapses the chunk back).  Wave 7 retires the
    // hook — debug callers can `import('three')` from devtools
    // directly if needed (the chunk is already loaded by the time
    // the debug query string is read).
    Object.assign(window, { game });
    game.start();

    const client = game.client;
    mountedHandles = { game, client };
    markRendererReady();
    return mountedHandles;
  })();

  return mountPromise;
}

function markRendererReady(): void {
  if (document.body.getAttribute('data-three-renderer-ready') === 'true') return;
  document.body.setAttribute('data-three-renderer-ready', 'true');
  const marker = document.createElement('div');
  marker.setAttribute('data-testid', 'three-renderer-ready');
  marker.setAttribute('aria-hidden', 'true');
  marker.style.display = 'none';
  document.body.appendChild(marker);
  window.dispatchEvent(new CustomEvent('mahjong:three-renderer-ready'));
}
