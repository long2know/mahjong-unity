// Phase K Wave 11 — `?action=*` deep-link routing for PWA shortcuts.
//
// The W10 manifest declares three shortcut entries that dispatch users
// at the autotable origin with a deep-link query parameter:
//
//   `/?action=new-game`   → start a new mahjong table
//   `/?action=spectate`   → open the spectator livestream picker
//   `/?action=tournament` → activate the tournament dashboard
//
// (The W10 manifest's `tournaments` plural alias is also honoured for
// backwards compatibility with any installed PWAs that picked it up
// before the W11 manifest rewrite landed.)
//
// Phase K Wave 12 adds a fourth keyword:
//
//   `/?action=replay&replayId=<guid>` → fetch + open the replay viewer
//
// Phase K Wave 13 extends `?action=spectate` with a `gameId` co-param
// that routes the spectator directly into a known game via Bishop's
// W12 handoff token endpoint:
//
//   `/?action=spectate&gameId=<guid>`
//     → POST /api/spectator/handoff
//     → on 200: navigate to `/spectate/{gameId}?token=<jwt>` + bootstrap
//       the spectator-livestream viewer for the game
//     → on 401: redirect to the lobby root (sign-in modal lives there)
//     → on 404 / any other error: "Game not found" toast
//
// The bare `?action=spectate` (no gameId) keeps the W11 / W12 lobby-
// tab fallback so the PWA shortcut still works as a "pick a game to
// watch" launcher when no specific game is targeted.
//
// Phase K Wave 14 adds three new keywords that wire deep-link
// surfaces against Bishop's W14 listing endpoints:
//
//   `/?action=bracket&tournamentId=<guid>`
//     → GET /api/tournaments/{tournamentId}/brackets
//     → on 200: lazy-imports `./bracket-listing` and mounts the
//       bracket-listing overlay (simple HTML grid of rounds /
//       matches; winner highlight + per-match status badges)
//     → on 404 / 5xx / network: "No brackets found" placeholder
//       inside the overlay
//
//   `/?action=replays`
//     → GET /api/replays  (metadata-only listing)
//     → on 200: lazy-imports `./replays-listing` and mounts the
//       replays-listing overlay (table of completedAt + variant +
//       turnCount + link to `?action=replay&replayId=<id>`)
//     → on 404 / 5xx / network: "Replays unavailable" placeholder
//
//   `/?action=admin-cost`
//     → admin pre-flight via /api/auth/me (mirrors audit.ts probe)
//     → on no-session → redirect to `/` so the sign-in modal mounts
//     → GET /api/commentary/cost/summary (admin-only on the backend)
//     → on 200: lazy-imports `./admin-cost` and mounts the cost
//       overlay (summary card "$X.XX / $Y.YY (Z%)" + byModel table)
//     → on 401 → redirect to `/` (sign-in modal at boot)
//     → on 403 → "Admins only" placeholder inside the overlay
//     → on 404 / 5xx → "Cost summary unavailable" placeholder
//
// Phase K Wave 15 adds a sister keyword to `admin-cost` for the
// Bishop W15 forecast endpoint:
//
//   `/?action=cost-forecast&days=<n>`
//     → admin pre-flight via /api/auth/me (shared with admin-cost)
//     → on no-session → redirect to `/` so the sign-in modal mounts
//     → GET /api/commentary/cost/forecast?days=<n>  (admin-only)
//     → on 200: lazy-imports `./admin-cost-forecast` and mounts the
//       forecast overlay (projected month-end + confidence +
//       days-of-data card; sub-3 kB chunk)
//     → on 400 → "Invalid forecast window" placeholder
//     → on 401 → redirect to `/`
//     → on 403 → "Admins only" placeholder
//     → on 404 / 5xx → "Cost forecast unavailable" placeholder
//     • `days` defaults to 30 when missing / fat-fingered; clamped
//       to `[1, 90]` to stay within Bishop's documented envelope.
//
// Bishop's W12 backend lane ships `GET /api/replays/{replayId}` (the
// new id-addressable replay endpoint, alongside the existing
// `GET /api/games/{gameId}/replay`).  This router intercepts the
// `?action=replay` URL, fetches the replay payload, navigates to
// `/replay/{replayId}`, and bootstraps the in-page replay viewer.
// 404 → user-facing error toast ("Replay not found").
//
// Without this router, the W2 boot guard in `index.ts` treats any
// non-empty `window.location.search` as a game-bootstrap trigger,
// imports the heavy renderer chunk, and tries to enter a non-existent
// table — yielding a console error and a black canvas.  The router
// intercepts the action keyword first, strips it from the URL via
// `history.replaceState` (so a refresh doesn't repeatedly re-fire the
// shortcut), routes the side-effect, and returns `true` to tell the
// caller to skip the game-bootstrap import.
//
// Hard-asserted by Vasquez's W11 spec — wiring lives here, the spec
// exercises the producer side via `?action=*` URL probes.  See
// `docs/frontend-routing.md` for the contract.
//
// Producer contract:
//   • `new-game`   → clicks `[data-action="new-game"]` once the DOM
//                    is ready (lobby's "New game" button has been
//                    annotated with the data-action selector in W11).
//   • `spectate`   → rewrites the URL to `/spectate`, then activates
//                    the lobby's public-games tab as the spectatable-
//                    games catalogue.  (A future wave can swap to a
//                    dedicated `/spectate` route when the SPA router
//                    lands; W11 keeps the visible page the lobby so
//                    users see something on cold launch.)
//   • `tournament` → rewrites the URL to `/tournament/list`, then
//                    activates the lobby's tournaments tab.
//   • `replay`     → reads `replayId=<guid>` co-param, fetches
//                    `/api/replays/{replayId}` (Bishop W12), rewrites
//                    the URL to `/replay/{replayId}`, and hands the
//                    payload to `openReplayForGame()`.  Missing /
//                    malformed replayId → "Replay not found" toast.

const SUPPORTED_ACTIONS = new Set([
  'new-game', 'spectate', 'tournament', 'tournaments', 'replay',
  // Phase K Wave 14 — new deep-link surfaces against Bishop's W14
  // listing endpoints.  See top-of-file comment + §4/§5/§6 of
  // `docs/frontend-routing.md` for the contract.
  'bracket', 'replays', 'admin-cost',
  // Phase K Wave 15 — admin cost forecast deep link against
  // Bishop's W15 `/api/commentary/cost/forecast` endpoint.  See
  // `docs/frontend-routing.md` §7 for the contract.
  'cost-forecast',
  // Phase K Wave 18 — operator admin panel deep link surfacing
  // Bishop's three W17 CRUD controllers (replay retention, JWKS
  // rotation, SignalR retention) as a single tabbed UI.  Admin-
  // only; see `docs/frontend-routing.md` §8 for the contract.
  'admin-panel',
]);

/**
 * Parse `?action=*` from the current URL.  Returns the normalised
 * action keyword or `null` if no recognized action is present.  Does
 * NOT modify the URL; callers strip the param via `clearActionParam()`
 * after dispatch so re-loads don't re-trigger the shortcut.
 */
export function parseActionFromUrl(): string | null {
  const params = new URLSearchParams(window.location.search);
  const raw = params.get('action');
  if (raw === null) return null;
  const norm = raw.trim().toLowerCase();
  if (!SUPPORTED_ACTIONS.has(norm)) return null;
  // Normalise the `tournaments` plural alias from the W10 manifest to
  // the W11 canonical `tournament` keyword so downstream code only
  // sees one form.
  return norm === 'tournaments' ? 'tournament' : norm;
}

/**
 * Remove the `?action=*` query parameter from the URL without
 * triggering a page reload.  Preserves any other query parameters the
 * caller may have on the URL (e.g. UTM tags) and falls back to a
 * no-op if `history.replaceState` is unavailable.
 */
export function clearActionParam(): void {
  try {
    const url = new URL(window.location.href);
    url.searchParams.delete('action');
    const next = url.pathname + (url.searchParams.toString() === '' ? '' : `?${url.searchParams.toString()}`) + url.hash;
    window.history.replaceState(window.history.state, '', next);
  } catch {
    /* legacy browsers without history API — leave URL intact */
  }
}

/**
 * Click `[data-action="new-game"]` once the DOM and the lobby's
 * tab-wiring are ready.  Idempotent — guarded by a single-fire flag.
 */
function dispatchNewGame(): void {
  const click = (): boolean => {
    const btn = document.querySelector<HTMLElement>('[data-action="new-game"]');
    if (btn === null) return false;
    btn.click();
    return true;
  };
  if (document.readyState === 'complete' || document.readyState === 'interactive') {
    if (click()) return;
  }
  // The button is rendered eagerly in `index.html`, but defensively
  // wait for DOMContentLoaded if we beat the parse.
  document.addEventListener('DOMContentLoaded', () => {
    // Best-effort retry — give the lobby tabs one microtask to bind
    // their event handlers before we synthesise the click.
    window.setTimeout(() => { click(); }, 0);
  }, { once: true });
}

/**
 * Rewrite the URL to `/spectate` (no reload) and activate the lobby's
 * public-games tab as the spectatable-games catalogue.  The full
 * spectate-by-id flow still lives at `#/spectate/{tableId}` (W6 hash
 * route).  This shortcut takes a user from a cold PWA launch into a
 * "pick a table to watch" state.
 *
 * Phase K Wave 13 — when a `gameId=<guid>` co-param is present, the
 * dispatch delegates to `dispatchSpectateWithGameId()` which mints
 * Bishop's W12 handoff JWT and routes directly into the spectator
 * viewer for that game.  The bare `?action=spectate` form keeps the
 * W11 / W12 lobby-tab fallback.
 */
function dispatchSpectate(): void {
  const params = new URLSearchParams(window.location.search);
  const rawGameId = (params.get('gameId') ?? '').trim();
  if (rawGameId !== '') {
    dispatchSpectateWithGameId(rawGameId);
    return;
  }

  try {
    const url = new URL(window.location.href);
    url.pathname = '/spectate';
    url.searchParams.delete('action');
    window.history.replaceState(window.history.state, '', url.pathname + url.search + url.hash);
  } catch {
    /* ignore */
  }
  const activate = (): boolean => {
    const tab = document.getElementById('lobby-public-games-tab') as HTMLButtonElement | null;
    if (tab === null) return false;
    tab.click();
    return true;
  };
  if (!activate()) {
    document.addEventListener('DOMContentLoaded', () => {
      window.setTimeout(() => { activate(); }, 0);
    }, { once: true });
  }
}

/**
 * Phase K Wave 13 — `?action=spectate&gameId=<guid>` dispatch.
 *
 * Mints Bishop's W12 short-lived spectator-handoff JWT (5-minute TTL,
 * scope `spectator:<gameId>`) via `POST /api/spectator/handoff`, then
 * navigates to the canonical `/spectate/{gameId}?token=<jwt>` URL and
 * bootstraps the existing W6 spectator-livestream viewer by setting
 * `window.location.hash = '#/spectate/<gameId>'` (the viewer's
 * `installSpectatorRoute()` hashchange listener owns the mount).
 *
 * The token is kept on the query-string so a refresh / share-link
 * round-trip carries the spectator credential along; a future wave
 * can extend `openSpectatorLivestream()` to read the token from the
 * URL and append `?token=<jwt>` to the HLS playlist fetch when the
 * caller doesn't have a session cookie (mobile webview / embedded
 * native client paths).  For W13 the existing cookie-auth playback
 * still works because the caller already has a session (the handoff
 * endpoint required one to mint the token).
 *
 * Failure modes:
 *   • 401 (no session) → navigate to lobby root (`/`); the sign-in
 *     modal is mounted at boot and the user can authenticate there.
 *   • 404 / 5xx / network error → "Game not found" toast.  We
 *     deliberately don't differentiate; the user-visible string is
 *     the same.
 *   • Malformed JSON response → same "Game not found" toast.
 *
 * The fetch + toast modules are lazy-imported to keep the
 * `?action=spectate&gameId=…` cold-launch off the eager lobby chunk.
 */
function dispatchSpectateWithGameId(gameId: string): void {
  // Strip the action + gameId params from the URL synchronously
  // before the network round-trip so a refresh during the fetch
  // doesn't re-fire the shortcut.  The path rewrite below (in the
  // success branch) will set the canonical `/spectate/{gameId}?token=…`.
  try {
    const url = new URL(window.location.href);
    url.searchParams.delete('action');
    url.searchParams.delete('gameId');
    window.history.replaceState(
      window.history.state,
      '',
      url.pathname + (url.searchParams.toString() === '' ? '' : `?${url.searchParams.toString()}`) + url.hash,
    );
  } catch {
    /* legacy browsers — best effort */
  }

  void fetchHandoffAndOpenSpectator(gameId);
}

async function fetchHandoffAndOpenSpectator(gameId: string): Promise<void> {
  let resp: Response;
  try {
    resp = await fetch(
      '/api/spectator/handoff',
      {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'application/json',
        },
        body: JSON.stringify({ gameId }),
      },
    );
  } catch {
    await showGameNotFoundToast();
    return;
  }

  if (resp.status === 401) {
    redirectToLobbyForSignIn();
    return;
  }

  if (!resp.ok) {
    await showGameNotFoundToast();
    return;
  }

  let body: { token?: unknown; expiresAt?: unknown } | null = null;
  try {
    body = await resp.json() as { token?: unknown; expiresAt?: unknown };
  } catch {
    await showGameNotFoundToast();
    return;
  }

  const token = typeof body?.token === 'string' ? body.token : '';
  if (token === '') {
    await showGameNotFoundToast();
    return;
  }

  // Rewrite the URL to `/spectate/{gameId}?token=<jwt>` and set the
  // hash to `#/spectate/{gameId}` so the W6 hashchange listener in
  // `spectator-livestream.installSpectatorRoute()` mounts the
  // viewer.  The token sits on the query-string for share-link
  // symmetry; the existing viewer ignores it (W13) but the URL is
  // the canonical reference for a spectator session.
  const encodedId = encodeURIComponent(gameId);
  const encodedToken = encodeURIComponent(token);
  const canonicalPath = `/spectate/${encodedId}`;
  const canonicalQuery = `?token=${encodedToken}`;
  const canonicalHash = `#/spectate/${encodedId}`;
  try {
    window.history.replaceState(
      window.history.state,
      '',
      canonicalPath + canonicalQuery + canonicalHash,
    );
  } catch {
    /* ignore */
  }

  // Bootstrap the spectator viewer.  The existing `installSpectator
  // Route()` fires its hashchange handler on install + on hash
  // change; calling `openSpectatorLivestream()` directly is the
  // most reliable path because the `replaceState()` call above does
  // not emit a `hashchange` event when the hash component is added
  // alongside a path rewrite.
  try {
    const mod = await import('./spectator-livestream');
    mod.installSpectatorRoute();
    await mod.openSpectatorLivestream({ tableId: gameId });
  } catch {
    await showGameNotFoundToast();
  }
}

function redirectToLobbyForSignIn(): void {
  // No dedicated `/login` route exists in the SPA — the sign-in
  // modal is mounted at boot under `installAuthUi()` and lives at
  // the lobby root.  Send the user there so they can authenticate
  // and re-try the spectator deep-link.
  try {
    const url = new URL(window.location.href);
    url.pathname = '/';
    url.search = '';
    url.hash = '';
    window.location.replace(url.toString());
  } catch {
    window.location.href = '/';
  }
}

async function showGameNotFoundToast(): Promise<void> {
  try {
    const { showToast } = await import('./toast');
    showToast('Game not found', 'error');
  } catch {
    // Toast module failed to load — fall back to console so the
    // failure isn't completely silent.  Production builds keep the
    // toast chunk eagerly available so this branch is exotic.
    // eslint-disable-next-line no-console
    console.warn('[action-router] spectator game not found, toast unavailable');
  }
}

/**
 * Rewrite the URL to `/tournament/list` (no reload) and activate the
 * lobby's tournaments tab.  The tournament module is W10-lazy-loaded
 * on tab click, so this routes the user into the bracket dashboard
 * without an extra interaction.
 */
function dispatchTournament(): void {
  try {
    const url = new URL(window.location.href);
    url.pathname = '/tournament/list';
    url.searchParams.delete('action');
    window.history.replaceState(window.history.state, '', url.pathname + url.search + url.hash);
  } catch {
    /* ignore */
  }
  const activate = (): boolean => {
    const tab = document.getElementById('lobby-tournaments-tab') as HTMLButtonElement | null;
    if (tab === null) return false;
    tab.click();
    return true;
  };
  if (!activate()) {
    document.addEventListener('DOMContentLoaded', () => {
      window.setTimeout(() => { activate(); }, 0);
    }, { once: true });
  }
}

/**
 * Phase K Wave 12 — `?action=replay&replayId=<guid>` dispatch.
 *
 * Reads the `replayId` co-param, fetches the replay payload from
 * Bishop's W12 `GET /api/replays/{replayId}` endpoint, rewrites the
 * URL to the canonical `/replay/{replayId}` path, and hands the
 * payload to the existing `openReplayForGame()` launcher (which
 * targets the in-page 2D replay viewer registered by `game-ui.ts`).
 *
 * Failure modes:
 *   • Missing / empty `replayId` co-param → "Replay not found" toast,
 *     URL param stripped, user lands on the bare lobby.
 *   • Endpoint 404 / network error → same toast.  We do NOT fall
 *     back to the legacy `GET /api/games/{gameId}/replay` (the W12
 *     contract is id-addressable; falling back to the game-id form
 *     would silently hide configuration drift).
 *   • Endpoint 200 → URL rewritten to `/replay/{replayId}`,
 *     `openReplayForGame()` invoked with the normalised payload.
 *
 * Both fetch + viewer wiring are lazy-imported to keep the
 * `?action=replay` cold-launch off the eager lobby chunk.  The
 * replay-launcher + toast modules are W3-/W7-already-emitted lazy
 * chunks shared with other surfaces (post-game modal, leaderboard
 * row, settings drawer), so no new chunk graph is added here.
 */
function dispatchReplay(): void {
  const params = new URLSearchParams(window.location.search);
  const rawId = (params.get('replayId') ?? '').trim();

  if (rawId === '') {
    clearActionParam();
    void showReplayNotFoundToast();
    return;
  }

  // Strip the action + replayId params from the URL synchronously
  // before the network round-trip so a refresh during the fetch
  // doesn't re-fire the shortcut.  The path rewrite below (in the
  // success branch) will set the canonical `/replay/{id}` URL.
  const replayId = rawId;
  try {
    const url = new URL(window.location.href);
    url.searchParams.delete('action');
    url.searchParams.delete('replayId');
    window.history.replaceState(
      window.history.state,
      '',
      url.pathname + (url.searchParams.toString() === '' ? '' : `?${url.searchParams.toString()}`) + url.hash,
    );
  } catch {
    /* legacy browsers — best effort */
  }

  void fetchAndOpenReplay(replayId);
}

async function fetchAndOpenReplay(replayId: string): Promise<void> {
  let resp: Response;
  try {
    resp = await fetch(
      `/api/replays/${encodeURIComponent(replayId)}`,
      {
        credentials: 'same-origin',
        headers: { 'Accept': 'application/json' },
      },
    );
  } catch {
    await showReplayNotFoundToast();
    return;
  }

  if (!resp.ok) {
    // 404 (replay missing) or 5xx (transient backend) — surface the
    // same toast.  We deliberately don't differentiate 404 vs 5xx in
    // the user-visible string; both are "we couldn't load this".
    await showReplayNotFoundToast();
    return;
  }

  let body: unknown;
  try {
    body = await resp.json();
  } catch {
    await showReplayNotFoundToast();
    return;
  }

  // Rewrite the URL to the canonical `/replay/{replayId}` so back/
  // forward + share-link symmetry land at a clean path post-dispatch.
  try {
    const url = new URL(window.location.href);
    url.pathname = `/replay/${encodeURIComponent(replayId)}`;
    window.history.replaceState(window.history.state, '', url.pathname + url.search + url.hash);
  } catch {
    /* ignore */
  }

  // Hand the payload to the existing replay launcher.  The launcher
  // module owns the viewer wiring (`registerReplayLauncher` is called
  // by game-ui.ts at boot); we just normalise the wire shape into
  // the launcher's expected interface and dispatch.
  try {
    const { openReplayPayload } = await import('./replay-launcher');
    openReplayPayload(replayId, body);
  } catch {
    await showReplayNotFoundToast();
  }
}

async function showReplayNotFoundToast(): Promise<void> {
  try {
    const { showToast } = await import('./toast');
    showToast('Replay not found', 'error');
  } catch {
    // Toast module failed to load — fall back to console so the
    // failure isn't completely silent.  Production builds keep the
    // toast chunk eagerly available so this branch is exotic.
    // eslint-disable-next-line no-console
    console.warn('[action-router] replay not found, toast unavailable');
  }
}

/**
 * Phase K Wave 14 — `?action=bracket&tournamentId=<guid>` dispatch.
 *
 * Strips the action + tournamentId from the URL, rewrites the path
 * to `/tournament/<id>/brackets`, and lazy-imports the bracket-
 * listing overlay which fetches `GET /api/tournaments/{id}/brackets`
 * and renders the rounds-grid.  Missing / malformed `tournamentId`
 * → "Tournament not found" toast.  The overlay handles its own
 * 404 / 5xx error states internally.
 */
function dispatchBracket(): void {
  const params = new URLSearchParams(window.location.search);
  const rawId = (params.get('tournamentId') ?? '').trim();
  if (rawId === '') {
    clearActionParam();
    void showTournamentNotFoundToast();
    return;
  }
  const tournamentId = rawId;
  try {
    const url = new URL(window.location.href);
    url.pathname = `/tournament/${encodeURIComponent(tournamentId)}/brackets`;
    url.searchParams.delete('action');
    url.searchParams.delete('tournamentId');
    window.history.replaceState(
      window.history.state,
      '',
      url.pathname + (url.searchParams.toString() === '' ? '' : `?${url.searchParams.toString()}`) + url.hash,
    );
  } catch { /* legacy browsers — best effort */ }

  void mountBracketListing(tournamentId);
}

async function mountBracketListing(tournamentId: string): Promise<void> {
  try {
    const mod = await import('./bracket-listing');
    await mod.openBracketListing(tournamentId);
  } catch {
    await showTournamentNotFoundToast();
  }
}

async function showTournamentNotFoundToast(): Promise<void> {
  try {
    const { showToast } = await import('./toast');
    showToast('Tournament not found', 'error');
  } catch {
    // eslint-disable-next-line no-console
    console.warn('[action-router] tournament not found, toast unavailable');
  }
}

/**
 * Phase K Wave 14 — `?action=replays` dispatch.
 *
 * No co-params — Bishop's `GET /api/replays` is the metadata-only
 * listing endpoint.  Lazy-imports `./replays-listing` and mounts
 * the overlay; the overlay handles 404 / 5xx / empty-list states
 * internally.  Rows link back to `?action=replay&replayId=<id>`,
 * the W12 deep-link.
 */
function dispatchReplays(): void {
  try {
    const url = new URL(window.location.href);
    url.pathname = '/replays';
    url.searchParams.delete('action');
    window.history.replaceState(
      window.history.state,
      '',
      url.pathname + (url.searchParams.toString() === '' ? '' : `?${url.searchParams.toString()}`) + url.hash,
    );
  } catch { /* ignore */ }

  void mountReplaysListing();
}

async function mountReplaysListing(): Promise<void> {
  try {
    const mod = await import('./replays-listing');
    await mod.openReplaysListing();
  } catch {
    // Fail silently — the overlay couldn't load.  A future wave can
    // surface a generic "Replays unavailable" toast here; W14 keeps
    // the cold path silent (overlay would have shown the error UX
    // if the chunk had loaded).
    // eslint-disable-next-line no-console
    console.warn('[action-router] replays-listing chunk failed to load');
  }
}

/**
 * Phase K Wave 14 — `?action=admin-cost` dispatch.
 *
 * Admin-only client.  Two-step gate:
 *
 *   1. Pre-flight `/api/auth/me` to check the session is authenticated.
 *      Mirrors the `audit.ts` admin probe.  On unauthenticated → redirect
 *      to `/` so `installAuthUi()` mounts the sign-in modal at boot.
 *   2. Lazy-import `./admin-cost` and mount the overlay, which fetches
 *      `GET /api/commentary/cost/summary`.  The overlay also handles
 *      401 (defensive redirect) and 403 (admins-only placeholder)
 *      internally so the gating is double-layered.
 *
 * We don't hard-check `role === 'admin'` here — the backend is the
 * source of truth via the 403 response.  The client-side probe only
 * gates the unauthenticated case so non-signed-in users are routed
 * to the sign-in modal instead of seeing the overlay flash up with
 * a "Admins only" placeholder.
 */
function dispatchAdminCost(): void {
  try {
    const url = new URL(window.location.href);
    url.pathname = '/admin/commentary-cost';
    url.searchParams.delete('action');
    window.history.replaceState(
      window.history.state,
      '',
      url.pathname + (url.searchParams.toString() === '' ? '' : `?${url.searchParams.toString()}`) + url.hash,
    );
  } catch { /* ignore */ }

  void gateAndMountAdminCost();
}

async function gateAndMountAdminCost(): Promise<void> {
  // Pre-flight: is there a session at all?  An unauthenticated user
  // hitting `?action=admin-cost` should land at the sign-in modal,
  // not at the cost overlay flashing "Admins only".
  let authed = false;
  try {
    const r = await fetch('/api/auth/me', {
      method: 'GET',
      credentials: 'same-origin',
      headers: { 'Accept': 'application/json' },
    });
    if (r.ok) {
      const body = await r.json() as { authenticated?: unknown; Authenticated?: unknown };
      const flag = body.authenticated ?? body.Authenticated;
      authed = flag === true;
    }
  } catch {
    // Network error on the probe — let the overlay path try; the
    // server might still 401 the cost endpoint, in which case the
    // overlay's 401 branch will redirect to `/`.
    authed = true;
  }
  if (!authed) {
    redirectToLobbyForSignIn();
    return;
  }

  try {
    const mod = await import('./admin-cost');
    await mod.openCommentaryCostPanel();
  } catch {
    // eslint-disable-next-line no-console
    console.warn('[action-router] admin-cost chunk failed to load');
  }
}

/**
 * Phase K Wave 15 — `?action=cost-forecast&days=<n>` dispatch.
 *
 * Sister surface to `?action=admin-cost` for Bishop's W15
 * `GET /api/commentary/cost/forecast?days=<n>` endpoint.  Same
 * gating posture as admin-cost: pre-flight `/api/auth/me` so
 * unauthenticated users land at the sign-in modal instead of
 * seeing a "Admins only" overlay flash, then lazy-import the
 * `./admin-cost-forecast` chunk which handles 401 / 403 / 400 /
 * 404 / 5xx + the happy path internally.
 *
 * Reads `days=<n>` from the same URL.  Missing / fat-fingered →
 * the chunk's `normaliseDays()` clamps to `[1, 90]` with a default
 * of 30, so the request never fires with an invalid window (which
 * the Bishop W15 backend would 400 anyway, but the client-side
 * clamp avoids an unnecessary round-trip).
 */
function dispatchCostForecast(): void {
  let daysRaw: string | null = null;
  try {
    const params = new URLSearchParams(window.location.search);
    daysRaw = params.get('days');
  } catch { /* ignore */ }

  try {
    const url = new URL(window.location.href);
    url.pathname = '/admin/commentary-cost/forecast';
    url.searchParams.delete('action');
    url.searchParams.delete('days');
    window.history.replaceState(
      window.history.state,
      '',
      url.pathname + (url.searchParams.toString() === '' ? '' : `?${url.searchParams.toString()}`) + url.hash,
    );
  } catch { /* ignore */ }

  void gateAndMountCostForecast(daysRaw);
}

async function gateAndMountCostForecast(daysRaw: string | null): Promise<void> {
  // Pre-flight: same admin probe as `?action=admin-cost`.  An
  // unauthenticated user lands at `/` so `installAuthUi()` mounts
  // the sign-in modal.
  let authed = false;
  try {
    const r = await fetch('/api/auth/me', {
      method: 'GET',
      credentials: 'same-origin',
      headers: { 'Accept': 'application/json' },
    });
    if (r.ok) {
      const body = await r.json() as { authenticated?: unknown; Authenticated?: unknown };
      const flag = body.authenticated ?? body.Authenticated;
      authed = flag === true;
    }
  } catch {
    // Network error on the probe — let the overlay path try; the
    // backend may still 401 the forecast endpoint, in which case the
    // overlay's 401 branch will redirect to `/`.
    authed = true;
  }
  if (!authed) {
    redirectToLobbyForSignIn();
    return;
  }

  try {
    const mod = await import('./admin-cost-forecast');
    const days = mod.normaliseDays(daysRaw);
    await mod.openCommentaryCostForecastPanel(days);
  } catch {
    // eslint-disable-next-line no-console
    console.warn('[action-router] admin-cost-forecast chunk failed to load');
  }
}

/**
 * Phase K Wave 18 — `?action=admin-panel` dispatch (Hicks).
 *
 * Operator admin surface for Bishop's three W17 CRUD controllers:
 *   • POST/GET/PUT/DELETE /api/admin/replays/retention
 *   • POST/GET/PUT/DELETE /api/admin/jwks-rotation/per-tenant
 *   • POST/GET/PUT/DELETE /api/admin/signalr/retention
 *
 * Same gating posture as `admin-cost`: pre-flight `/api/auth/me`
 * so unauthenticated users land at `/` (sign-in modal) instead of
 * watching the panel flash an "Admins only" placeholder.  The
 * admin-panel chunk handles 401 / 403 / 503 internally as well
 * (double-layered gate so a stale probe response can't mask the
 * server's authoritative role check).
 */
function dispatchAdminPanel(): void {
  try {
    const url = new URL(window.location.href);
    url.pathname = '/admin/policies';
    url.searchParams.delete('action');
    window.history.replaceState(
      window.history.state,
      '',
      url.pathname + (url.searchParams.toString() === '' ? '' : `?${url.searchParams.toString()}`) + url.hash,
    );
  } catch { /* ignore */ }

  void gateAndMountAdminPanel();
}

async function gateAndMountAdminPanel(): Promise<void> {
  // Pre-flight: same admin probe as `?action=admin-cost`.  An
  // unauthenticated user lands at `/` so `installAuthUi()` mounts
  // the sign-in modal.  Non-admins flow through to the panel,
  // which then renders the canonical "Admins only" placeholder.
  let authed = false;
  try {
    const r = await fetch('/api/auth/me', {
      method: 'GET',
      credentials: 'same-origin',
      headers: { 'Accept': 'application/json' },
    });
    if (r.ok) {
      const body = await r.json() as { authenticated?: unknown; Authenticated?: unknown };
      const flag = body.authenticated ?? body.Authenticated;
      authed = flag === true;
    }
  } catch {
    authed = true; // network blip — let the panel try, server is source of truth.
  }
  if (!authed) {
    redirectToLobbyForSignIn();
    return;
  }

  try {
    const mod = await import('./admin/admin-panel');
    await mod.openAdminPanel();
  } catch {
    // eslint-disable-next-line no-console
    console.warn('[action-router] admin-panel chunk failed to load');
  }
}

/**
 * Top-level entry point — call this once at boot before the game-
 * bootstrap import guard fires.  Returns `true` if a recognized
 * action was handled (caller should skip the game-bootstrap import);
 * returns `false` for the no-action / unrecognized-action case.
 */
export function handlePwaActionFromUrl(): boolean {
  const action = parseActionFromUrl();
  if (action === null) return false;

  switch (action) {
    case 'new-game':
      clearActionParam();
      dispatchNewGame();
      return true;
    case 'spectate':
      dispatchSpectate();
      return true;
    case 'tournament':
      dispatchTournament();
      return true;
    case 'replay':
      dispatchReplay();
      return true;
    case 'bracket':
      dispatchBracket();
      return true;
    case 'replays':
      dispatchReplays();
      return true;
    case 'admin-cost':
      dispatchAdminCost();
      return true;
    case 'cost-forecast':
      dispatchCostForecast();
      return true;
    case 'admin-panel':
      dispatchAdminPanel();
      return true;
    default:
      return false;
  }
}
