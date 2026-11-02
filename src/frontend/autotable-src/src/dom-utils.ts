// Phase K Wave 2 — DOM-only visibility helpers (split from utils.ts).
//
// `utils.ts` imports three.js for `Vector3` / `Quaternion`, which drags
// the whole renderer into any chain that pulls in `setElHidden` /
// `showEl` / `hideEl`.  The lobby chain (lobby, profile, leaderboard,
// settings, profile-page, identity, tour, history, chat, audit,
// tournaments) only ever needs the DOM helpers — moving them here
// peels three out of the eager lobby bundle so Wave-2 hits its
// <500 kB lobby budget.
//
// Bootstrap ships `[hidden] { display: none !important; }`, so toggling
// visibility via `el.style.display = '...'` cannot override the
// `hidden` attribute.  These helpers flip the attribute (which is what
// HTML5 + assistive tech expect) while also clearing any prior inline
// `display` value so the element's own CSS class (or the user-agent
// default) takes over.

export function setElHidden(el: HTMLElement, hidden: boolean): void {
  el.hidden = hidden;
  if (!hidden) {
    el.style.display = '';
  }
}

export function showEl(el: HTMLElement): void {
  setElHidden(el, false);
}

export function hideEl(el: HTMLElement): void {
  setElHidden(el, true);
}
