// New Game UX P0 (persistent control, RC-9) — the ONE convention that maps a
// clicked control to the authoritative one-click New Game action.
//
// The persistent, always-visible "New Game" control lives OUTSIDE the relay
// sidebar (Ferro's DOM) and is identified NOT by a hard-coded id but by the
// `data-action="new-game"` attribute — the same convention `action-router.ts`
// already keys on. `client-ui.ts` delegates a single document-level click
// handler through `isNewGameActivation`, so whatever persistent button Ferro
// mounts (any id, any location, even added dynamically) is wired to the
// authoritative fresh-game path with zero id coupling and never the legacy
// relay `#new-game` / local-reset path.
//
// `isNewGameActivation` is duck-typed on `closest` (rather than `instanceof
// Element`) purely so the predicate is unit-testable in a browser-free Node
// contract spec; real DOM click targets satisfy it unchanged.

export const NEW_GAME_ACTION_SELECTOR = '[data-action="new-game"]';

interface ClosestLike {
  closest?: (selectors: string) => unknown;
}

/**
 * True when a click's event target is (or is nested inside) a New Game control
 * — i.e. an element matching `[data-action="new-game"]`. The ancestor walk via
 * `closest` means a click on an icon/label INSIDE the button still counts.
 */
export function isNewGameActivation(target: EventTarget | null): boolean {
  if (target === null) return false;
  const el = target as ClosestLike;
  if (typeof el.closest !== 'function') return false;
  return el.closest(NEW_GAME_ACTION_SELECTOR) !== null;
}
