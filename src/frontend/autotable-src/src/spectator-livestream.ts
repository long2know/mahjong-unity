// Phase K Wave 6 — Spectator livestream viewer.
//
// Mounts a dedicated full-screen viewer for the spectate route
// (`#/spectate/{tableId}`).  Plays Bishop's HLS livestream
// (`GET /api/tables/{tableId}/livestream/playlist.m3u8`) through a
// native `<audio>` element, falling back to the HLS.js polyfill on
// browsers without native HLS support (everything except Safari).
//
// SignalR group `spectator:{tableId}` carries a member-count broadcast
// that drives the on-screen spectator-count badge; the existing
// `hub.ts` connection wrapper is re-used.
//
// Until Bishop's W6 livestream lands the m3u8 endpoint returns 404 /
// 503 — the viewer surfaces a "Phase L feature — not yet available"
// banner instead of trying to play silence.
//
// Testids:
//   • spectator-livestream-screen   — root container
//   • spectator-livestream-player   — the <audio> element
//   • spectator-count               — connected-spectator badge
//   • spectator-livestream-status   — status / error banner

import { getHubConnection } from './hub';

export interface SpectatorLivestreamOptions {
  tableId: string;
}

interface State {
  installed: boolean;
  screen: HTMLDivElement | null;
  audio: HTMLAudioElement | null;
  status: HTMLDivElement | null;
  count: HTMLSpanElement | null;
  tableId: string | null;
  hlsHandle: { destroy: () => void } | null;
  hubHandlerInstalled: boolean;
}

const state: State = {
  installed: false,
  screen: null,
  audio: null,
  status: null,
  count: null,
  tableId: null,
  hlsHandle: null,
  hubHandlerInstalled: false,
};

const PHASE_L_COPY = 'Phase L feature — not yet available.';
const PLAYLIST_PATH = (tableId: string): string =>
  `/api/tables/${encodeURIComponent(tableId)}/livestream/playlist.m3u8`;

/** Lazy entrypoint — opens the spectator viewer for `tableId`. */
export async function openSpectatorLivestream(opts: SpectatorLivestreamOptions): Promise<void> {
  ensureScreen();
  if (state.screen === null) return;
  state.tableId = opts.tableId;
  state.screen.hidden = false;
  setStatus(`Connecting to table ${opts.tableId.slice(0, 8)}…`, 'pending');
  paintTableLabel(opts.tableId);

  const url = PLAYLIST_PATH(opts.tableId);
  try {
    const head = await fetch(url, { method: 'HEAD', credentials: 'same-origin' });
    if (head.status === 404 || head.status === 503) {
      setStatus(PHASE_L_COPY, 'phase-l');
      return;
    }
    if (!head.ok) {
      setStatus(`Livestream unavailable (HTTP ${head.status}).`, 'error');
      return;
    }
  } catch {
    setStatus('Livestream unavailable — backend offline.', 'error');
    return;
  }

  await attachAudio(url);
  await subscribeSpectatorCount(opts.tableId);
}

/** Tear down the viewer + detach hub group / HLS handle. */
export function closeSpectatorLivestream(): void {
  if (state.hlsHandle !== null) {
    try { state.hlsHandle.destroy(); } catch { /* ignore */ }
    state.hlsHandle = null;
  }
  if (state.audio !== null) {
    try { state.audio.pause(); } catch { /* ignore */ }
    state.audio.removeAttribute('src');
    state.audio.load();
  }
  if (state.tableId !== null && state.hubHandlerInstalled) {
    void leaveSpectatorGroup(state.tableId);
  }
  if (state.screen !== null) {
    state.screen.hidden = true;
  }
  state.tableId = null;
}

// ── Internals ───────────────────────────────────────────────────────

function ensureScreen(): void {
  if (state.installed && state.screen !== null) return;
  const existing = document.querySelector<HTMLDivElement>('[data-testid="spectator-livestream-screen"]');
  if (existing !== null) {
    state.screen = existing;
    state.audio = existing.querySelector<HTMLAudioElement>('[data-testid="spectator-livestream-player"]');
    state.status = existing.querySelector<HTMLDivElement>('[data-testid="spectator-livestream-status"]');
    state.count = existing.querySelector<HTMLSpanElement>('[data-testid="spectator-count"]');
    state.installed = true;
    return;
  }

  const screen = document.createElement('div');
  screen.className = 'spectator-livestream-screen';
  screen.setAttribute('data-testid', 'spectator-livestream-screen');
  screen.setAttribute('role', 'region');
  screen.setAttribute('aria-label', 'Spectator livestream viewer');

  const header = document.createElement('div');
  header.className = 'spectator-livestream-header';
  const title = document.createElement('h2');
  title.className = 'spectator-livestream-title';
  title.textContent = '📻 Spectator livestream';
  header.appendChild(title);

  const tableLabel = document.createElement('span');
  tableLabel.className = 'spectator-livestream-table';
  tableLabel.setAttribute('data-testid', 'spectator-livestream-table-id');
  header.appendChild(tableLabel);

  const count = document.createElement('span');
  count.className = 'spectator-count';
  count.setAttribute('data-testid', 'spectator-count');
  count.setAttribute('aria-live', 'polite');
  count.textContent = '👥 –';
  header.appendChild(count);
  state.count = count;

  const close = document.createElement('button');
  close.type = 'button';
  close.className = 'btn btn-sm btn-secondary spectator-livestream-close';
  close.setAttribute('data-testid', 'spectator-livestream-close');
  close.textContent = 'Leave';
  close.addEventListener('click', () => {
    closeSpectatorLivestream();
    if (window.location.hash.startsWith('#/spectate/')) {
      window.location.hash = '';
    }
  });
  header.appendChild(close);
  screen.appendChild(header);

  const body = document.createElement('div');
  body.className = 'spectator-livestream-body';

  const audio = document.createElement('audio');
  audio.className = 'spectator-livestream-audio';
  audio.controls = true;
  audio.preload = 'none';
  audio.setAttribute('data-testid', 'spectator-livestream-player');
  audio.setAttribute('aria-label', 'Spectator audio stream');
  body.appendChild(audio);
  state.audio = audio;

  const status = document.createElement('div');
  status.className = 'spectator-livestream-status';
  status.setAttribute('data-testid', 'spectator-livestream-status');
  status.setAttribute('role', 'status');
  status.setAttribute('aria-live', 'polite');
  body.appendChild(status);
  state.status = status;

  screen.appendChild(body);
  document.body.appendChild(screen);
  state.screen = screen;
  state.installed = true;
}

function setStatus(text: string, state_: 'pending' | 'ready' | 'error' | 'phase-l'): void {
  if (state.status === null) return;
  state.status.textContent = text;
  state.status.setAttribute('data-state', state_);
}

function paintTableLabel(tableId: string): void {
  const el = state.screen?.querySelector<HTMLSpanElement>('[data-testid="spectator-livestream-table-id"]');
  if (el === null || el === undefined) return;
  el.textContent = `Table ${tableId}`;
}

async function attachAudio(url: string): Promise<void> {
  if (state.audio === null) return;
  const audio = state.audio;

  // Safari + iOS Safari have native HLS support — just set the
  // playlist URL directly and the player handles segment loading.
  const native = canPlayHlsNatively(audio);
  if (native) {
    audio.src = url;
    setStatus('Stream ready — press play.', 'ready');
    return;
  }

  // Everything else: load Hls.js on-demand.  Only fetched when the
  // user actually opens the spectator route AND the browser can't
  // play HLS natively — the lobby cold path never pulls this chunk.
  try {
    const HlsCtor = await loadHlsJs();
    if (HlsCtor === null || !HlsCtor.isSupported()) {
      setStatus('Live audio is not supported in this browser.', 'error');
      return;
    }
    const hls = new HlsCtor();
    hls.loadSource(url);
    hls.attachMedia(audio);
    state.hlsHandle = { destroy: () => hls.destroy() };
    setStatus('Stream ready — press play.', 'ready');
  } catch {
    setStatus('Failed to initialize HLS player.', 'error');
  }
}

interface HlsLike {
  loadSource(url: string): void;
  attachMedia(el: HTMLMediaElement): void;
  destroy(): void;
}

interface HlsConstructor {
  new(config?: unknown): HlsLike;
  isSupported(): boolean;
}

async function loadHlsJs(): Promise<HlsConstructor | null> {
  // Phase K Wave 7 — hls.js is now bundled and dynamic-imported.
  // W6 loaded it from `cdn.jsdelivr.net/npm/hls.js@1.5.13/dist/
  // hls.min.js` via a `<script>` tag.  That required a CSP
  // allowlist for `cdn.jsdelivr.net` in `script-src` (Bishop's
  // CspMiddleware), an extra DNS / TLS handshake on every cold
  // spectate, and an integrity gap (no SRI hash because Vite would
  // need to know the file at build time).
  //
  // W7 instead `import('hls.js/dist/hls.light.mjs')`s the polyfill;
  // rollup's `manualChunks` peels it into a sibling `hls.<hash>.js`
  // chunk that is fetched from our own origin only when a non-
  // Safari spectator clicks `#/spectate/{tableId}`.  Net effects:
  //
  //   • CSP requirements stay minimal — `script-src 'self'` is
  //     enough for the polyfill path (see
  //     docs/frontend-csp-requirements.md).
  //   • Spectator code-load uses the existing connection-keep-
  //     alive instead of a fresh CDN handshake.
  //   • SRI / supply-chain story improves (the polyfill ships
  //     with our signed deploy artefacts).
  //
  // The light build (no MP4 muxer, no transmuxing fallbacks for
  // legacy stream tracks) is sufficient for our backend's HLS
  // output, which is an audio-only AAC stream — the full build
  // would carry ~140 kB extra we cannot use.
  try {
    const mod = await import('hls.js/dist/hls.light.mjs');
    const Hls = (mod as { default?: HlsConstructor }).default ?? (mod as unknown as HlsConstructor);
    if (Hls === undefined || typeof (Hls as HlsConstructor).isSupported !== 'function') {
      return null;
    }
    return Hls as HlsConstructor;
  } catch {
    return null;
  }
}

function canPlayHlsNatively(audio: HTMLAudioElement): boolean {
  try {
    const probe = audio.canPlayType('application/vnd.apple.mpegurl');
    return probe !== '';
  } catch {
    return false;
  }
}

// ── SignalR spectator-count subscription ────────────────────────────

async function subscribeSpectatorCount(tableId: string): Promise<void> {
  try {
    const conn = await getHubConnection();
    if (!state.hubHandlerInstalled) {
      conn.on('SpectatorCountUpdated', (payload: unknown) => {
        const parsed = parseSpectatorPayload(payload, tableId);
        if (parsed !== null && state.count !== null) {
          state.count.textContent = `👥 ${parsed}`;
        }
      });
      state.hubHandlerInstalled = true;
    }
    // Best-effort: ask the hub to add us to the spectator group.
    // The hub method may not exist yet (Phase L) — invocation
    // failure is non-fatal.
    try {
      await conn.invoke('JoinSpectatorGroup', tableId);
    } catch {
      // Hub method missing or backend not ready — degrade silently.
    }
  } catch {
    // Hub unreachable — leave the spectator count at "–".
  }
}

async function leaveSpectatorGroup(tableId: string): Promise<void> {
  try {
    const conn = await getHubConnection();
    await conn.invoke('LeaveSpectatorGroup', tableId);
  } catch {
    // Best-effort — the server reaps the group membership on
    // disconnect anyway.
  }
}

function parseSpectatorPayload(payload: unknown, expectedTableId: string): number | null {
  if (typeof payload !== 'object' || payload === null) return null;
  const rec = payload as Record<string, unknown>;
  const tableId = typeof rec.tableId === 'string' ? rec.tableId : null;
  if (tableId !== null && tableId !== expectedTableId) return null;
  const count = typeof rec.count === 'number'
    ? rec.count
    : typeof rec.spectatorCount === 'number' ? rec.spectatorCount : null;
  if (count === null || !Number.isFinite(count)) return null;
  return Math.max(0, Math.floor(count));
}

// ── Hash route ──────────────────────────────────────────────────────

/**
 * Install the hash-route handler so any `#/spectate/{tableId}`
 * navigation opens the viewer.  Safe to call multiple times —
 * idempotent.  The handler also fires once at install time so a
 * fresh page-load with the hash already set opens the viewer
 * without a navigation event.
 */
export function installSpectatorRoute(): void {
  if ((window as unknown as { __spectatorRouteInstalled?: boolean }).__spectatorRouteInstalled === true) {
    return;
  }
  (window as unknown as { __spectatorRouteInstalled?: boolean }).__spectatorRouteInstalled = true;

  const onHashChange = (): void => {
    const m = /^#\/spectate\/([^/]+)/.exec(window.location.hash);
    if (m === null) {
      // Hash cleared — close any active viewer.
      if (state.tableId !== null) closeSpectatorLivestream();
      return;
    }
    const tableId = decodeURIComponent(m[1]);
    void openSpectatorLivestream({ tableId });
  };
  window.addEventListener('hashchange', onHashChange);
  // Fire once at install to catch deep-link arrivals.
  onHashChange();
}
