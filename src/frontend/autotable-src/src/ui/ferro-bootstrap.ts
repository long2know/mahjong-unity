// Ferro — bootstrap.
//
// Hooks into Hicks's trunk via the public `mahjong:three-renderer-ready`
// event dispatched by three-renderer.ts (which also publishes
// `window.game`).  We avoid touching any of Hicks's trunk modules —
// the only trunk surface we depend on is the event + the global.
//
// Trigger contract:
//   • Game pages (non-empty `window.location.search`) trigger
//     `bootstrapGame()` → mounts three-renderer → fires the event.
//   • Lobby-only sessions never fire the event, so this bootstrap
//     is a no-op (the overlays never attach).
//
// Re-entry safe — `attach()` on each overlay is idempotent.

import { ClaimWindowOverlay } from './claim-window-overlay';
import { WinScreenPolish } from './win-screen-polish';
import './variant-picker';
import './dark-listbox';

type AnyGame = { client?: unknown };

let attached = false;

function attachOverlays(): void {
  if (attached) return;
  const game = (window as unknown as { game?: AnyGame }).game;
  if (game === undefined) return;
  if (game.client === undefined) return;
  attached = true;
  try {
    // The cast hops over Hicks's Client type — we only depend on the
    // public collection shape (`on('update', ...)`, `get(key)`, `set(key, ...)`).
    new ClaimWindowOverlay(game as never).attach();
  } catch (err) {
    // eslint-disable-next-line no-console
    console.warn('[ferro] claim-window-overlay attach failed:', err);
  }
  try {
    new WinScreenPolish(game as never).attach();
  } catch (err) {
    // eslint-disable-next-line no-console
    console.warn('[ferro] win-screen-polish attach failed:', err);
  }
}

// Primary signal — three-renderer.ts dispatches this once `window.game`
// is published and `game.start()` has been called.
window.addEventListener('mahjong:three-renderer-ready', () => {
  attachOverlays();
}, { once: true });

// Defensive fallback — three-renderer-ready may already have fired
// before this chunk lands (if the bootstrap import races the renderer
// chunk).  Poll briefly for `window.game` so we don't miss it.
let polls = 0;
const pollHandle = window.setInterval(() => {
  polls += 1;
  if (attached) {
    window.clearInterval(pollHandle);
    return;
  }
  const game = (window as unknown as { game?: AnyGame }).game;
  if (game !== undefined && game.client !== undefined) {
    window.clearInterval(pollHandle);
    attachOverlays();
    return;
  }
  // Give up after ~30s — lobby-only sessions never publish `window.game`
  // and we don't want to hold an interval forever.
  if (polls > 300) {
    window.clearInterval(pollHandle);
  }
}, 100);
