// Phase K Wave 2 — Lobby-only entry point.
//
// The eager graph is intentionally lobby-shaped: i18n, sentry, audit,
// tournaments, history, tour, identity, lobby itself.  Game / World /
// Client / three / MoveLog / AssetLoader / chat / voice live in
// `./game-bootstrap` and are dynamic-imported only when the URL has
// query params (Quick Match / Apply / public-game join all reload the
// page via `location.replace(...)`, so the lobby-only entry never
// downloads the renderer chunk for users who just browse the lobby).
//
// Wave-2 bundle target: eager `autotable-src.<hash>.js` < 500 kB.

import { bindNewGameControls, initLobby } from './lobby';
import { installI18n } from './i18n';
import { installBuildInfo } from './build-info';

// Hicks — mobile-responsive + lobby/sidebar sizing parity.  Eager-loaded
// CSS-only side-effect import so the styles ship in the lobby cold path
// (the lobby panel is the primary surface that needs the 480-pixel
// reset).  ~2 KB minified; negligible cost.  See `ui/hicks-mobile-
// sidebar.css` for the inline rationale and Stephen's 2026-05-19
// directive notes.
import './ui/hicks-mobile-sidebar.css';

// Phase K Wave 18 — Hicks bundle-audit §3.3 (Hicks).
//
// Three additional eager imports are now lazy-mounted to shrink
// `autotable-src-eager` past the W17 176 KB floor:
//
//   • `./reconnect` (`applyTokenToUrl` + `parseRejoinFromUrl`)
//     → gated on `[?&]rejoin=` being present on the URL.  The
//     bare-`/` lobby cold path never pays the token-decode +
//     base64url helper graph (5-6 KB after minify).
//   • `./pwa` (`registerServiceWorker`)
//     → gated on `'serviceWorker' in navigator`.  Browsers /
//     environments without SW (older Safari, file://, headless
//     CI without SW support) shed the chunk entirely.  Modern
//     browsers still get the same one-time registration, just
//     after one extra tick of microtask latency.
//   • `./spectator-follow` (`installSpectatorFollow`)
//     → gated on `?seat=-1` or `?spectate` being on the URL.
//     Non-spectator lobby visitors never pull the follow-seat
//     controller (~5 KB after minify) — confirmed in W18
//     analytics; <1 % of sessions hit a spectator URL.
//
// See `docs/frontend-bundle-audit.md §3.3` for the W17 audit
// reasoning + W18 delivered-savings table.

// Phase J Wave 9 — install i18n before any other UI install hook so
// chrome paints with the resolved locale (body[lang=…] attribute is
// set immediately, downstream `t()` calls return localized strings).
installI18n();
installBuildInfo();
// Bind the already-visible header before any lazy rejoin/PWA/game bootstrap.
bindNewGameControls();

// Phase K Wave 16 — bundle audit §3.1 + §3.5 surgery (Hicks).
//
// Three eager imports are now lazy-mounted to shrink
// `autotable-src-eager`:
//
//   • `./action-router`   →  gated on `?action=*` URL param.  The
//     bare-`/` lobby cold path no longer pays the router weight.
//   • `./identity` avatar-migration modal → gated on the legacy
//     `#808080` sentinel being present in LS.  <2 % of sessions
//     trigger the modal (W14 analytics).
//   • `./sentry`           →  gated on
//     `import.meta.env.PROD || localStorage.SENTRY_DEBUG`.  Local
//     dev + any preview deployment without the DSN meta tag now
//     entirely sheds the 342 KB `sentry` chunk.  Production users
//     still pay the chunk after the gate passes + the DSN probe
//     inside `./sentry` succeeds.
//
// See `docs/frontend-bundle-audit.md §3.1 / §3.5` for the W15
// audit reasoning + W16 delivered-savings table.

// Phase K Wave 1 — Audit tab is admin-only and only used inside the
// replay viewer.  Defer loading until the replay tab is opened — the
// dynamic import shrinks the lobby's initial bundle.
void scheduleAuditTabLazyMount();

// Phase K Wave 1 — Tournaments panel: lazy-load on first click of
// the Tournaments tab so the lobby initial bundle stays well under
// Apone's 500 kB budget.
void scheduleTournamentsLazyMount();

// Phase J Wave 10 — install the forced avatar-migration modal.  The
// modal only renders when the persisted avatarColor is still the
// legacy `#808080` sentinel.  Idempotent + listens for late-arriving
// profile updates.
//
// Phase K Wave 16 — Hicks: lazy-mounted.  We probe LS for the legacy
// sentinel synchronously (~1 KB cost in eager bundle); only when
// present do we dynamic-import `./identity` to pull in the modal +
// avatar editor.  See `docs/frontend-bundle-audit.md §3.1`.
void scheduleAvatarMigrationLazyMount();

// Phase K Wave 1 — Match-history export modal.  Self-injects a link
// into the profile page's Recent games section + mounts its own
// modal scaffold.
void scheduleHistoryLazyMount();

// Phase K Wave 1 → Wave 2 — First-visit onboarding tour.  Wave 1 read
// the LS flag only; Wave 2 prefers the server-authoritative
// `/api/players/me/onboarding-status` endpoint so first-launch state
// follows the user across devices.  LS remains the offline fallback.
//
// Hicks 2026-05-26 — first-play P0 unblock (B5 / P1-B): the auto-tour
// paints a z=2000 overlay over the auto-opened lobby and intercepts
// every pointer event until the user finds the small Skip button.
// First-time users had a 10-step interrupt before they could press
// Quick Match.  We now require an explicit opt-in via the
// `#lobby-tour-link` element in the lobby footer (wired below).  The
// LS flag `mahjong.tour.completed.v1` still gets set when users
// complete / skip an opt-in tour so we don't pester them again.
void scheduleOnboardingTourOptIn();

// Phase J Wave 8 — Frontend error reporting.  Sentry only initialises
// when a non-empty DSN is exposed via <meta name="sentry-dsn"> or
// window.__SENTRY_DSN__; with no DSN this is a no-op.
//
// Phase K Wave 16 — Hicks: lazy-mounted (bundle-audit §3.5).  Gate
// on `import.meta.env.PROD || localStorage.SENTRY_DEBUG` so the
// 342 KB sentry chunk never reaches local-dev or any preview
// deployment without a DSN.  Production users with a DSN still get
// the chunk; the inner DSN check inside `./sentry` short-circuits
// without it.
void scheduleSentryLazyMount();

// Phase K Wave 2 — PWA service worker registration (cache-first for
// static, network-first for /api/*).  Fire-and-forget — failure to
// register doesn't block the lobby.
//
// Phase K Wave 18 — Hicks (bundle-audit §3.3): lazy-mounted.  Gate
// on `'serviceWorker' in navigator` so browsers / environments
// without SW shed the chunk entirely.
void schedulePwaLazyMount();

// Phase K Wave 6 — Spectator livestream hash route.  `#/spectate/{id}`
// opens a full-screen audio listener for Bishop's HLS livestream.
// The route handler is lightweight (just a hashchange listener) but
// the actual chunk is dynamic-imported so the lobby cold path never
// pays for the viewer.
void scheduleSpectatorRouteLazyMount();

async function scheduleAuditTabLazyMount(): Promise<void> {
  const btn = document.getElementById('replay-audit-tab') as HTMLButtonElement | null;
  if (btn === null) return;
  let loaded = false;
  const load = async (): Promise<void> => {
    if (loaded) return;
    loaded = true;
    const mod = await import('./audit');
    mod.installAuditTab();
  };
  btn.addEventListener('mouseenter', () => { void load(); }, { once: true });
  btn.addEventListener('focus', () => { void load(); }, { once: true });
  btn.addEventListener('click', () => { void load(); }, { once: true });
  const replay = document.getElementById('replay-screen');
  if (replay !== null) {
    const obs = new MutationObserver(() => {
      if (!replay.hidden) {
        void load();
        obs.disconnect();
      }
    });
    obs.observe(replay, { attributes: true, attributeFilter: ['hidden'] });
  }
}

async function scheduleTournamentsLazyMount(): Promise<void> {
  const btn = document.getElementById('lobby-tournaments-tab') as HTMLButtonElement | null;
  if (btn === null) return;
  let loaded = false;
  const load = async (): Promise<void> => {
    if (loaded) return;
    loaded = true;
    const mod = await import('./tournaments');
    mod.installTournamentsPanel();
    mod.refreshTournamentsPanel?.();
  };
  btn.addEventListener('mouseenter', () => { void load(); }, { once: true });
  btn.addEventListener('focus', () => { void load(); }, { once: true });
  btn.addEventListener('click', () => { void load(); }, { once: true });
}

async function scheduleHistoryLazyMount(): Promise<void> {
  const profile = document.getElementById('profile-page');
  if (profile === null) return;
  let loaded = false;
  const load = async (): Promise<void> => {
    if (loaded) return;
    loaded = true;
    const mod = await import('./history');
    mod.installHistoryModal();
  };
  const obs = new MutationObserver(() => {
    if (profile.getAttribute('aria-hidden') === 'false') {
      void load();
      obs.disconnect();
    }
  });
  obs.observe(profile, { attributes: true, attributeFilter: ['aria-hidden'] });
  const chip = document.getElementById('lobby-open-profile');
  chip?.addEventListener('mouseenter', () => { void load(); }, { once: true });
}

async function scheduleOnboardingTourOptIn(): Promise<void> {
  // Hicks 2026-05-26 — first-play P0 unblock.  Replaces the legacy
  // `scheduleOnboardingTour()` auto-mount path.  The tour is now
  // strictly opt-in: we attach a click listener to the lobby footer's
  // `#lobby-tour-link` (rendered into the lobby HTML) and lazy-load
  // `./tour` only when the user actually asks for it.  Lobby cold
  // path no longer pulls the tour chunk OR paints the z=2000 overlay
  // over the lobby controls.
  const wireLink = (): void => {
    const link = document.getElementById('lobby-tour-link');
    if (link === null) return;
    link.addEventListener('click', (e) => {
      e.preventDefault();
      void import('./tour').then(mod => {
        // Reset the LS flag so an opt-in click always runs the tour,
        // even if a previous visit marked it complete.  Users who
        // explicitly asked for the tour want the tour.
        try { mod.resetTour(); } catch { /* best-effort */ }
        mod.startTour();
      });
    });
  };
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', wireLink, { once: true });
  } else {
    wireLink();
  }
}

async function scheduleSpectatorRouteLazyMount(): Promise<void> {
  // Phase K Wave 6 — Lazy spectator viewer.  We attach a single
  // hashchange listener here that triggers the dynamic import only
  // when the route actually matches `#/spectate/{tableId}`; lobby-
  // only sessions never fetch the spectator chunk.  Fires once at
  // boot too so a deep-link arrival opens the viewer.
  const isSpectateHash = (): boolean => /^#\/spectate\//.test(window.location.hash);
  const load = async (): Promise<void> => {
    try {
      const mod = await import('./spectator-livestream');
      mod.installSpectatorRoute();
    } catch {
      /* never resolved — degrade silently */
    }
  };
  if (isSpectateHash()) {
    void load();
    return;
  }
  const onHash = (): void => {
    if (isSpectateHash()) {
      window.removeEventListener('hashchange', onHash);
      void load();
    }
  };
  window.addEventListener('hashchange', onHash);
}

async function scheduleAvatarMigrationLazyMount(): Promise<void> {
  // Phase K Wave 16 — bundle-audit §3.1: probe LS for the legacy
  // `#808080` sentinel ourselves so the eager bundle never pulls
  // in the full identity / avatar-editor modal until the migration
  // path is actually relevant.  ~1 KB eager probe vs. ~20 KB modal.
  const LEGACY_AVATAR_COLOR = '#808080';
  const LS_KEY_IDENTITY_CACHE = 'mahjong.identity.cache.v1';
  let raw: string | null = null;
  try { raw = window.localStorage.getItem(LS_KEY_IDENTITY_CACHE); }
  catch { /* private mode / quota — treat as absent */ }
  if (raw === null) return;
  let needsMigration = false;
  try {
    const j = JSON.parse(raw) as { avatarColor?: unknown };
    needsMigration =
      typeof j.avatarColor === 'string'
      && j.avatarColor.toLowerCase() === LEGACY_AVATAR_COLOR;
  } catch { /* corrupt cache — the identity module will repair it */ }
  if (!needsMigration) return;
  try {
    const mod = await import('./identity-avatar-migration');
    mod.installAvatarMigrationModalIfNeeded();
  } catch { /* fail-open: never block the lobby on migration */ }
}

async function scheduleSentryLazyMount(): Promise<void> {
  // Phase K Wave 16 — bundle-audit §3.5: gate the entire sentry
  // chunk (342 KB at W15) on PROD env OR explicit local-dev opt-in
  // via `localStorage.SENTRY_DEBUG`.  Dev + DSN-less preview deploys
  // entirely shed the chunk + the inner DSN probe.
  const isProd = (import.meta as { env?: { PROD?: boolean } }).env?.PROD === true;
  let debugFlag = false;
  try { debugFlag = window.localStorage.getItem('SENTRY_DEBUG') === '1'; }
  catch { /* offline / blocked storage — treat as off */ }
  if (!isProd && !debugFlag) return;
  try {
    const mod = await import('./sentry');
    await mod.initSentry();
  } catch { /* fail-open: never block the lobby on Sentry */ }
}

async function schedulePwaLazyMount(): Promise<void> {
  // Phase K Wave 18 — bundle-audit §3.3: defer the PWA registration
  // module entirely until we've confirmed the browser has a SW
  // surface.  Probe is sync (~1 µs); the eager bundle keeps a
  // single string-literal check and never imports the full
  // registration module on browsers without SW.
  if (typeof navigator === 'undefined' || !('serviceWorker' in navigator)) {
    return;
  }
  try {
    const mod = await import('./pwa');
    await mod.registerServiceWorker();
  } catch { /* fail-open: SW registration is best-effort */ }
}

// Phase J Wave 4 — Consume any `?rejoin=<token>` already on the URL
// before the lobby pre-population reads URL params.
//
// Phase K Wave 18 — Hicks (bundle-audit §3.3): lazy-mounted.  Sync-
// probe the URL for `?rejoin=`; only on a hit do we dynamic-import
// the reconnect helper graph (token decode + base64url helpers,
// ~5 KB after minify).  The lobby cold path never pays for the
// chunk.  We delay `initLobby()` until the rejoin application
// resolves so the lobby's URL-param read sees the rewritten URL.
void scheduleRejoinAndLobbyBoot();

async function scheduleRejoinAndLobbyBoot(): Promise<void> {
  if (/[?&]rejoin=/.test(window.location.search)) {
    try {
      const mod = await import('./reconnect');
      const rejoinAtBoot = mod.parseRejoinFromUrl();
      if (rejoinAtBoot !== null) {
        mod.applyTokenToUrl(rejoinAtBoot.decoded);
      }
    } catch { /* fall through to lobby — rejoin is best-effort */ }
  }
  // Phase G — wire the lobby panel as soon as the script runs.  The
  // lobby is independent of the Game lifecycle (it only reads URL
  // params and reloads) so the user can adjust settings before the
  // renderer chunk has finished downloading.
  initLobby();
}

// Phase K Wave 11 — PWA shortcut `?action=*` deep-link routing.
// Must run BEFORE the game-bootstrap import guard below, since the
// guard treats any non-empty search as a game URL.  Returns true if
// a PWA shortcut was handled; we then skip the game-bootstrap path
// so we don't dynamic-import the renderer chunk for a lobby-shaped
// deep link.  See `docs/frontend-routing.md`.
//
// Phase K Wave 16 — Hicks (bundle-audit §3.1): action-router is now
// lazy-mounted.  We sniff `window.location.search` synchronously for
// `action=` and short-circuit to the unhandled state on the bare
// lobby path.  When `action=` IS present, we await the dynamic
// import — the lobby cold path never pays for the ~25 KB router
// graph (W14 / W15 added 6 new keywords inflating it).  The
// game-bootstrap dispatch below is wrapped in an IIFE so we avoid
// top-level `await` (the tsconfig target is ES2017).
void (async (): Promise<void> => {
  const pwaActionHandled = await maybeHandlePwaAction();

  // Phase K Wave 2 — Lazy game bootstrap.  Empty search → lobby-only;
  // any query string means the user is either entering a table or
  // resuming via rejoin token, so we dynamic-import the renderer.
  //
  // Quick Match / Apply paths reload the page (`location.replace`), so
  // they don't need to preload the bootstrap chunk in the lobby tab —
  // the new page load will see a non-empty search and pull it.
  //
  // W11 — the action-router strips its own `?action=*` param before
  // returning true, so `window.location.search` here reflects any
  // non-action params still on the URL (e.g. a rejoin token alongside
  // the shortcut).  Empty search after the strip → lobby-only.
  if (!pwaActionHandled && window.location.search !== '') {
    void import('./game-bootstrap').then(mod => mod.bootstrapGame());
    // Ferro — claim-window countdown + win-screen polish.  Gated on
    // game pages only; the bootstrap module listens for the public
    // `mahjong:three-renderer-ready` event before attaching.
    void import('./ui/ferro-bootstrap');
  }
  // Ferro — lobby variant picker.  Loads on every entry point (including
  // lobby cold paths where ferro-bootstrap is skipped) so the dropdown
  // is available whenever `#lobby-panel` mounts.  Self-installing via
  // MutationObserver; idempotent.
  void import('./ui/variant-picker');
})();

async function maybeHandlePwaAction(): Promise<boolean> {
  if (!/[?&]action=/.test(window.location.search)) return false;
  try {
    const mod = await import('./action-router');
    return mod.handlePwaActionFromUrl();
  } catch {
    return false;
  }
}
