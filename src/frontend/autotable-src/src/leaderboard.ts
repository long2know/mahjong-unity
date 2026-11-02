// Phase J Wave 6 — Leaderboard view module.
//
// Owns the lobby's "Leaderboard" tab.  Polls Bishop's
// `GET /api/leaderboard` REST endpoint with the user's chosen sort +
// min-games filter + page index, and renders the result as a paged
// table.
//
// Lifecycle:
//   • startLeaderboardPolling() — install on tab activate; kicks off
//     a 30-second auto-refresh loop.
//   • stopLeaderboardPolling() — uninstall on tab deactivate so the
//     endpoint isn't hammered while the user is on a different tab.
//
// ── Wire contract assumed (Bishop, Phase J Wave 6) ──────────────────
//
//   GET /api/leaderboard?sort=<key>&minGames=<n>&page=<n>&pageSize=50
//     → 200 {
//         rows: [{
//           rank: 1,
//           playerId: "<uuid>",
//           displayName: "Player A",
//           avatarColor: "#c0392b",
//           gamesPlayed: 50,
//           gamesWon: 25,
//           winRate: 0.5,
//           totalScore: 12500,
//           highestScore: 580,
//           longestStreak: 7
//         }, ...],
//         page: 0,
//         pageSize: 50,
//         totalCount: 153,
//         minGames: 5,
//         sort: "gamesWon"
//       }
//
// `sort` values: gamesWon | totalScore | winRate | longestStreak |
// highestScore.  Server is responsible for picking the canonical order
// when the field is omitted (default is gamesWon DESC).
//
// Rate-limit awareness (Apone, Phase J Wave 6): the poll fires at
// most once every 30 s while the tab is active and on every explicit
// user action (sort change, min-games change, page next/prev).  In a
// burst (e.g. the user paging quickly) we still cap at one in-flight
// request via AbortController to avoid stacking calls.

import { EventEmitter } from 'events';
import { setElHidden } from './dom-utils';

// ── Public types ────────────────────────────────────────────────────

export type LeaderboardSort =
  | 'gamesWon'
  | 'totalScore'
  | 'winRate'
  | 'longestStreak'
  | 'highestScore';

export interface LeaderboardRow {
  rank: number;
  playerId: string;
  displayName: string;
  avatarColor: string;
  gamesPlayed: number;
  gamesWon: number;
  winRate: number;        // 0..1
  totalScore: number;
  highestScore: number;
  longestStreak: number;
  /** Phase K Wave 1 — ELO rating for the active season (null when
   *  the row came from the legacy stats endpoint). */
  rating?: number | null;
  /** Phase K Wave 1 — rating change vs. the prior season (null when
   *  the player has no prior season on record). */
  ratingDelta?: number | null;
}

export interface LeaderboardPage {
  rows: ReadonlyArray<LeaderboardRow>;
  page: number;
  pageSize: number;
  totalCount: number;
  sort: LeaderboardSort;
  minGames: number;
  /** Phase K Wave 1 — when the page was sourced from the ratings
   *  endpoint, carries the resolved season label. */
  season?: string | null;
}

// Phase K Wave 1 — rated leaderboard data sources.
export type LeaderboardMode = 'stats' | 'rating';
export type LeaderboardSeason = 'current' | 'last' | 'all';

export interface SeasonOption {
  value: LeaderboardSeason;
  label: string;
}

export const SEASON_OPTIONS: ReadonlyArray<SeasonOption> = [
  { value: 'current', label: 'Current season' },
  { value: 'last',    label: 'Last season' },
  { value: 'all',     label: 'All seasons' },
];

export const RATINGS_ENDPOINT = '/api/ratings/leaderboard';
const RATING_MODE_LS_KEY = 'mahjong.leaderboard.rating.v1';
const RATING_SEASON_LS_KEY = 'mahjong.leaderboard.rating.season.v1';

// ── Constants ───────────────────────────────────────────────────────

export const LEADERBOARD_ENDPOINT = '/api/leaderboard';
export const LEADERBOARD_PAGE_SIZE = 50;
export const LEADERBOARD_AUTO_REFRESH_MS = 30_000;
export const DEFAULT_MIN_GAMES = 5;
export const DEFAULT_SORT: LeaderboardSort = 'gamesWon';

export const SORT_OPTIONS: ReadonlyArray<{ value: LeaderboardSort; label: string }> = [
  { value: 'gamesWon',      label: 'Games Won' },
  { value: 'totalScore',    label: 'Total Score' },
  { value: 'winRate',       label: 'Win Rate' },
  { value: 'longestStreak', label: 'Longest Streak' },
  { value: 'highestScore',  label: 'Highest Score' },
];

// ── Module state ────────────────────────────────────────────────────

const events = new EventEmitter();
let cache: LeaderboardPage | null = null;
let lastError: string | null = null;
let pollTimer: number | null = null;
let inflight: AbortController | null = null;
let active = false;

const state: {
  sort: LeaderboardSort;
  minGames: number;
  page: number;
  mode: LeaderboardMode;
  season: LeaderboardSeason;
  ratingsAvailable: boolean | null;
} = {
  sort: DEFAULT_SORT,
  minGames: DEFAULT_MIN_GAMES,
  page: 0,
  mode: loadInitialMode(),
  season: loadInitialSeason(),
  ratingsAvailable: null,
};

function loadInitialMode(): LeaderboardMode {
  try {
    const v = window.localStorage.getItem(RATING_MODE_LS_KEY);
    return v === 'rating' ? 'rating' : 'stats';
  } catch {
    return 'stats';
  }
}

function loadInitialSeason(): LeaderboardSeason {
  try {
    const v = window.localStorage.getItem(RATING_SEASON_LS_KEY);
    if (v === 'current' || v === 'last' || v === 'all') return v;
  } catch { /* ignore */ }
  return 'current';
}

function persistMode(): void {
  try {
    window.localStorage.setItem(RATING_MODE_LS_KEY, state.mode);
  } catch { /* ignore */ }
}

function persistSeason(): void {
  try {
    window.localStorage.setItem(RATING_SEASON_LS_KEY, state.season);
  } catch { /* ignore */ }
}

// ── Normalisers ─────────────────────────────────────────────────────

function isSort(v: string): v is LeaderboardSort {
  return v === 'gamesWon' || v === 'totalScore' || v === 'winRate'
      || v === 'longestStreak' || v === 'highestScore';
}

function normalizeRow(raw: unknown): LeaderboardRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  if (typeof o.playerId !== 'string' || o.playerId === '') return null;
  const num = (v: unknown, dflt: number): number =>
    typeof v === 'number' && isFinite(v) ? v : dflt;
  // Bishop's backend ships verbose field names
  // (`highestSingleGameScore`, `longestWinStreak`); tolerate the short
  // form too so the wire shape can evolve without a frontend bump.
  const highestScore = num(
    o.highestScore !== undefined ? o.highestScore : o.highestSingleGameScore, 0);
  const longestStreak = num(
    o.longestStreak !== undefined ? o.longestStreak : o.longestWinStreak, 0);
  const winRate = (() => {
    const wr = num(o.winRate, NaN);
    if (isFinite(wr) && wr >= 0 && wr <= 1) return wr;
    const gp = num(o.gamesPlayed, 0);
    const gw = num(o.gamesWon, 0);
    return gp > 0 ? gw / gp : 0;
  })();
  return {
    rank: num(o.rank, 0),
    playerId: o.playerId,
    displayName: typeof o.displayName === 'string' && o.displayName !== ''
      ? o.displayName
      : `Player ${o.playerId.slice(0, 6)}`,
    avatarColor: typeof o.avatarColor === 'string' ? o.avatarColor : '#2980b9',
    gamesPlayed: num(o.gamesPlayed, 0),
    gamesWon: num(o.gamesWon, 0),
    winRate,
    totalScore: num(o.totalScore, 0),
    highestScore,
    longestStreak,
    // Phase K Wave 1 — tolerate alternative wire names for the rating
    // surface so Bishop's controller can evolve without a frontend
    // bump. `eloRating` is the legacy chess-style label, `rating` is
    // the canonical form, and `ratingDelta`/`ratingChange`/`delta`
    // all map to the per-season change.
    rating: (() => {
      if (typeof o.rating === 'number' && isFinite(o.rating)) return o.rating;
      if (typeof o.eloRating === 'number' && isFinite(o.eloRating)) return o.eloRating;
      if (typeof o.elo === 'number' && isFinite(o.elo)) return o.elo;
      return null;
    })(),
    ratingDelta: (() => {
      const candidates = [o.ratingDelta, o.ratingChange, o.delta, o.eloDelta];
      for (const c of candidates) {
        if (typeof c === 'number' && isFinite(c)) return c;
      }
      return null;
    })(),
  };
}

function normalizePage(raw: unknown): LeaderboardPage | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const rawRows = Array.isArray(o.rows) ? o.rows : [];
  const rows: Array<LeaderboardRow> = [];
  for (const r of rawRows) {
    const n = normalizeRow(r);
    if (n !== null) rows.push(n);
    if (rows.length >= LEADERBOARD_PAGE_SIZE) break;
  }
  // Bishop's response uses `total` for the post-filter count.  We
  // tolerate `totalCount` too in case the field name evolves.
  const totalCount = typeof o.total === 'number'
    ? o.total
    : (typeof o.totalCount === 'number' ? o.totalCount : rows.length);
  const page = state.page;
  const pageSize = LEADERBOARD_PAGE_SIZE;
  const sort = state.sort;
  const minGames = state.minGames;
  const season = typeof o.season === 'string' ? o.season : null;
  return { rows, page, pageSize, totalCount, sort, minGames, season };
}

// ── Fetch helpers ───────────────────────────────────────────────────

function buildUrl(): string {
  const params = new URLSearchParams();
  params.set('minGames', String(state.minGames));
  // Bishop's controller takes `limit` + `offset` (not page + pageSize).
  params.set('limit', String(LEADERBOARD_PAGE_SIZE));
  params.set('offset', String(state.page * LEADERBOARD_PAGE_SIZE));
  if (state.mode === 'rating') {
    params.set('season', state.season);
    // The ratings endpoint sorts by rating internally; surface our
    // own sort key too so clients that fall back can still slice the
    // legacy stats columns.
    params.set('sort', state.sort);
    return `${RATINGS_ENDPOINT}?${params.toString()}`;
  }
  params.set('sort', state.sort);
  return `${LEADERBOARD_ENDPOINT}?${params.toString()}`;
}

function emitState(): void {
  events.emit('update', { page: cache, error: lastError });
}

async function fetchOnce(): Promise<void> {
  if (inflight !== null) inflight.abort();
  const ctrl = new AbortController();
  inflight = ctrl;
  try {
    const resp = await fetch(buildUrl(), {
      credentials: 'same-origin',
      signal: ctrl.signal,
      headers: { 'Accept': 'application/json' },
    });
    if (resp.status === 404 && state.mode === 'rating') {
      // Ratings endpoint not deployed yet — fall back to the stats
      // endpoint silently so the toggle "degrades" rather than
      // failing.  The toggle UI separately surfaces an "Unavailable"
      // badge via `getRatingsAvailable()`.
      state.ratingsAvailable = false;
      state.mode = 'stats';
      persistMode();
      emitState();
      return await fetchOnce();
    }
    if (!resp.ok) {
      lastError = `HTTP ${resp.status}`;
      emitState();
      return;
    }
    if (state.mode === 'rating') {
      state.ratingsAvailable = true;
    }
    const body = (await resp.json()) as unknown;
    const page = normalizePage(body);
    if (page === null) {
      lastError = 'malformed response';
      emitState();
      return;
    }
    cache = page;
    lastError = null;
    emitState();
  } catch (e) {
    if ((e as DOMException)?.name === 'AbortError') return;
    lastError = (e as Error)?.message ?? 'network error';
    emitState();
  } finally {
    if (inflight === ctrl) inflight = null;
  }
}

// ── Public API ──────────────────────────────────────────────────────

export function getCachedPage(): LeaderboardPage | null {
  return cache;
}

export function getLastError(): string | null {
  return lastError;
}

export function getSort(): LeaderboardSort {
  return state.sort;
}

export function getMinGames(): number {
  return state.minGames;
}

export function getPage(): number {
  return state.page;
}

/** Subscribe to fetch updates.  Returns an unsubscribe handle. */
export function onUpdate(
  handler: (s: { page: LeaderboardPage | null; error: string | null }) => void,
): () => void {
  events.on('update', handler);
  handler({ page: cache, error: lastError });
  return () => events.off('update', handler);
}

/** Start the 30-second auto-refresh loop.  Idempotent. */
export function startLeaderboardPolling(): void {
  if (active) return;
  active = true;
  void fetchOnce();
  pollTimer = window.setInterval(() => { void fetchOnce(); }, LEADERBOARD_AUTO_REFRESH_MS);
}

/** Stop the loop + cancel any in-flight request. */
export function stopLeaderboardPolling(): void {
  active = false;
  if (pollTimer !== null) {
    window.clearInterval(pollTimer);
    pollTimer = null;
  }
  if (inflight !== null) {
    inflight.abort();
    inflight = null;
  }
}

export function isPolling(): boolean {
  return active;
}

/**
 * Update the sort key and immediately re-fetch.  Resets `page` to 0
 * because the row positions can shift wildly with a new sort.
 */
export function setSort(sort: LeaderboardSort): void {
  if (state.sort === sort) return;
  state.sort = sort;
  state.page = 0;
  void fetchOnce();
}

/**
 * Update the min-games filter and re-fetch (page reset to 0 because
 * the filtered set shrinks/grows).  Clamps to a non-negative integer.
 */
export function setMinGames(minGames: number): void {
  const n = Math.max(0, Math.floor(minGames));
  if (state.minGames === n) return;
  state.minGames = n;
  state.page = 0;
  void fetchOnce();
}

/** Advance to the next page if there is one.  No-op at the end. */
export function nextPage(): void {
  if (cache === null) return;
  const lastPage = Math.max(0, Math.ceil(cache.totalCount / LEADERBOARD_PAGE_SIZE) - 1);
  if (state.page >= lastPage) return;
  state.page += 1;
  void fetchOnce();
}

/** Step back one page.  No-op at page 0. */
export function prevPage(): void {
  if (state.page <= 0) return;
  state.page -= 1;
  void fetchOnce();
}

/** One-shot manual refresh (e.g. on tab activate). */
export function refreshLeaderboard(): Promise<void> {
  return fetchOnce();
}

// Phase K Wave 1 — mode + season controls.

export function getMode(): LeaderboardMode {
  return state.mode;
}

export function getSeason(): LeaderboardSeason {
  return state.season;
}

export function getRatingsAvailable(): boolean | null {
  return state.ratingsAvailable;
}

export function setMode(mode: LeaderboardMode): void {
  if (state.mode === mode) return;
  state.mode = mode;
  state.page = 0;
  persistMode();
  void fetchOnce();
}

export function setSeason(season: LeaderboardSeason): void {
  if (state.season === season) return;
  state.season = season;
  state.page = 0;
  persistSeason();
  void fetchOnce();
}

// ── Renderer ────────────────────────────────────────────────────────
//
// Renders the cached page into the host element + wires the sort,
// min-games, and paging controls to the module state.  Called by
// lobby.ts when the Leaderboard tab is installed; re-renders on every
// onUpdate event.

function el<K extends keyof HTMLElementTagNameMap>(
  tag: K, className?: string, testid?: string,
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  if (className !== undefined) node.className = className;
  if (testid !== undefined) node.setAttribute('data-testid', testid);
  return node;
}

function fmtInt(n: number): string {
  if (!isFinite(n)) return '–';
  return Math.round(n).toLocaleString();
}

function fmtPct(n: number): string {
  if (!isFinite(n)) return '–';
  return `${(n * 100).toFixed(1)}%`;
}

function fmtSigned(n: number): string {
  if (!isFinite(n)) return '–';
  const r = Math.round(n);
  if (r > 0) return `+${r.toLocaleString()}`;
  return r.toLocaleString();
}

/** Initials helper — first + last name token's initials, uppercased. */
function initialsFromName(name: string): string {
  const trimmed = name.trim();
  if (trimmed === '') return '?';
  const parts = trimmed.split(/\s+/);
  if (parts.length === 1) return parts[0].charAt(0).toUpperCase();
  return (parts[0].charAt(0) + parts[parts.length - 1].charAt(0)).toUpperCase();
}

/**
 * Install the leaderboard surface — wires the sort/filter/page
 * controls and subscribes to the fetch update stream.  Idempotent.
 * Does NOT start polling; call startLeaderboardPolling() separately
 * when the Leaderboard tab is activated.
 */
let renderInstalled = false;
export function installLeaderboardSurface(): void {
  if (renderInstalled) return;
  const tableHost = document.getElementById('leaderboard-table');
  if (tableHost === null) return;
  renderInstalled = true;

  const sortSelect = document.getElementById(
    'leaderboard-sort-select') as HTMLSelectElement | null;
  const minGamesInput = document.getElementById(
    'leaderboard-min-games-input') as HTMLInputElement | null;
  const prevBtn = document.getElementById(
    'leaderboard-prev-page') as HTMLButtonElement | null;
  const nextBtn = document.getElementById(
    'leaderboard-next-page') as HTMLButtonElement | null;
  const summaryEl = document.getElementById('leaderboard-paging-summary');
  const errorEl = document.getElementById('leaderboard-error');
  const emptyEl = document.getElementById('leaderboard-empty');
  const loadingEl = document.getElementById('leaderboard-loading');

  if (sortSelect !== null) {
    sortSelect.replaceChildren();
    for (const opt of SORT_OPTIONS) {
      const o = document.createElement('option');
      o.value = opt.value;
      o.textContent = opt.label;
      sortSelect.appendChild(o);
    }
    sortSelect.value = state.sort;
    sortSelect.addEventListener('change', () => {
      const v = sortSelect.value;
      if (isSort(v)) setSort(v);
    });
  }

  if (minGamesInput !== null) {
    minGamesInput.value = String(state.minGames);
    const commit = (): void => {
      const n = parseInt(minGamesInput.value, 10);
      if (!isNaN(n) && n >= 0) setMinGames(n);
    };
    minGamesInput.addEventListener('change', commit);
    minGamesInput.addEventListener('blur', commit);
  }

  if (prevBtn !== null) prevBtn.addEventListener('click', () => prevPage());
  if (nextBtn !== null) nextBtn.addEventListener('click', () => nextPage());

  // Phase K Wave 1 — rated leaderboard toggle + season picker.  Both
  // controls live in the leaderboard header (HTML scaffold in
  // index.html) so the renderer can read them on every refresh.
  const ratingToggle = document.getElementById(
    'leaderboard-rating-toggle') as HTMLInputElement | null;
  const seasonSelect = document.getElementById(
    'leaderboard-season-select') as HTMLSelectElement | null;
  const ratingStatus = document.getElementById('leaderboard-rating-status');
  if (ratingToggle !== null) {
    ratingToggle.checked = state.mode === 'rating';
    ratingToggle.addEventListener('change', () => {
      setMode(ratingToggle.checked ? 'rating' : 'stats');
      // Surface the season picker only in rating mode.
      if (seasonSelect !== null) {
        setElHidden(seasonSelect, state.mode !== 'rating');
        const lbl = seasonSelect.closest<HTMLElement>('label');
        if (lbl !== null) setElHidden(lbl, state.mode !== 'rating');
      }
    });
  }
  if (seasonSelect !== null) {
    seasonSelect.replaceChildren();
    for (const opt of SEASON_OPTIONS) {
      const o = document.createElement('option');
      o.value = opt.value;
      o.textContent = opt.label;
      seasonSelect.appendChild(o);
    }
    seasonSelect.value = state.season;
    seasonSelect.addEventListener('change', () => {
      const v = seasonSelect.value;
      if (v === 'current' || v === 'last' || v === 'all') setSeason(v);
    });
    const lbl = seasonSelect.closest<HTMLElement>('label');
    if (state.mode !== 'rating') {
      setElHidden(seasonSelect, true);
      if (lbl !== null) setElHidden(lbl, true);
    }
  }

  const render = (): void => {
    if (errorEl !== null) {
      if (lastError !== null) {
        setElHidden(errorEl, false);
        errorEl.textContent = `Failed to load leaderboard: ${lastError}`;
      } else {
        setElHidden(errorEl, true);
        errorEl.textContent = '';
      }
    }
    if (ratingStatus !== null) {
      // Phase K Wave 1 — Bishop. When the ratings endpoint 404s
      // `fetchOnce()` flips `state.mode` back to 'stats' and pins
      // `ratingsAvailable=false`, so the banner condition must NOT
      // re-check `state.mode === 'rating'` (otherwise the fallback
      // mutation immediately suppresses the banner). The banner is
      // gated on `ratingsAvailable === false` alone — that flag is
      // only set after a real user-driven rating-mode fetch failed.
      if (state.ratingsAvailable === false) {
        setElHidden(ratingStatus, false);
        ratingStatus.textContent = 'Ratings unavailable — showing stats.';
      } else {
        setElHidden(ratingStatus, true);
        ratingStatus.textContent = '';
      }
    }
    const page = cache;
    if (loadingEl !== null) {
      setElHidden(loadingEl, !(page === null && lastError === null));
    }
    if (page === null) {
      // Clear out any stale rows so a transient error doesn't keep
      // showing old data.
      tableHost.replaceChildren();
      if (emptyEl !== null) setElHidden(emptyEl, true);
      if (summaryEl !== null) summaryEl.textContent = '';
      if (prevBtn !== null) prevBtn.disabled = true;
      if (nextBtn !== null) nextBtn.disabled = true;
      return;
    }
    renderTable(tableHost, page);
    if (emptyEl !== null) {
      setElHidden(emptyEl, page.rows.length !== 0);
    }
    if (summaryEl !== null) {
      summaryEl.textContent = formatSummary(page);
    }
    if (prevBtn !== null) prevBtn.disabled = page.page <= 0;
    if (nextBtn !== null) {
      const lastPage = Math.max(0, Math.ceil(page.totalCount / LEADERBOARD_PAGE_SIZE) - 1);
      nextBtn.disabled = page.page >= lastPage;
    }
  };

  onUpdate(render);
  render();
}

function formatSummary(page: LeaderboardPage): string {
  const total = page.totalCount;
  if (total === 0) return 'Showing 0 of 0';
  const start = page.page * LEADERBOARD_PAGE_SIZE + 1;
  const end = Math.min(total, start + page.rows.length - 1);
  return `Showing ${start}-${end} of ${total}`;
}

function renderTable(host: HTMLElement, page: LeaderboardPage): void {
  host.replaceChildren();
  const isRating = state.mode === 'rating';
  const table = el('table', `leaderboard-grid${isRating ? ' leaderboard-grid-rating' : ''}`);
  table.setAttribute('role', 'table');
  const head = el('thead');
  const headRow = el('tr');
  const headers: Array<{ label: string; sort: LeaderboardSort | null; cls?: string }> = [
    { label: 'Rank',          sort: null,            cls: 'lb-col-rank' },
    { label: 'Player',        sort: null,            cls: 'lb-col-player' },
  ];
  if (isRating) {
    headers.push({ label: 'Rating', sort: null, cls: 'lb-col-num' });
    headers.push({ label: 'Δ',      sort: null, cls: 'lb-col-num' });
  }
  headers.push(
    { label: 'Games',         sort: null,            cls: 'lb-col-num' },
    { label: 'Wins',          sort: 'gamesWon',      cls: 'lb-col-num' },
    { label: 'Win %',         sort: 'winRate',       cls: 'lb-col-num' },
    { label: 'Total',         sort: 'totalScore',    cls: 'lb-col-num' },
    { label: 'Highest',       sort: 'highestScore',  cls: 'lb-col-num' },
    { label: 'Streak',        sort: 'longestStreak', cls: 'lb-col-num' },
    { label: 'Profile',       sort: null,            cls: 'lb-col-action' },
  );
  for (const h of headers) {
    const th = el('th', h.cls);
    th.setAttribute('scope', 'col');
    if (h.sort !== null && h.sort === page.sort) {
      th.classList.add('lb-sort-active');
      th.setAttribute('aria-sort', 'descending');
    }
    th.textContent = h.label;
    headRow.appendChild(th);
  }
  head.appendChild(headRow);
  table.appendChild(head);

  const body = el('tbody');
  page.rows.forEach((row, idx) => {
    const tr = el('tr', 'leaderboard-row', `leaderboard-row-${idx}`);
    tr.setAttribute('data-rank', String(row.rank));
    tr.setAttribute('data-player-id', row.playerId);

    const rankCell = el('td', 'lb-col-rank lb-cell-rank');
    rankCell.textContent = String(row.rank);
    tr.appendChild(rankCell);

    const playerCell = el('td', 'lb-col-player lb-cell-player');
    const avatar = el('span', 'lb-avatar');
    avatar.style.backgroundColor = row.avatarColor;
    avatar.textContent = initialsFromName(row.displayName);
    avatar.setAttribute('aria-hidden', 'true');
    const name = el('span', 'lb-name');
    name.textContent = row.displayName;
    playerCell.appendChild(avatar);
    playerCell.appendChild(name);
    tr.appendChild(playerCell);

    if (isRating) {
      const ratingCell = el('td', 'lb-col-num lb-cell-num lb-cell-rating');
      ratingCell.textContent = row.rating === null || row.rating === undefined
        ? '—'
        : fmtInt(row.rating);
      tr.appendChild(ratingCell);

      const deltaCell = el('td', 'lb-col-num lb-cell-num lb-cell-rating-delta');
      deltaCell.setAttribute('data-testid', `leaderboard-rating-delta-${idx}`);
      const delta = row.ratingDelta ?? null;
      if (delta === null || !isFinite(delta) || Math.round(delta) === 0) {
        deltaCell.textContent = delta === null ? '—' : '0';
        deltaCell.classList.add('lb-delta-zero');
      } else if (delta > 0) {
        deltaCell.textContent = `▲ ${fmtInt(delta)}`;
        deltaCell.classList.add('lb-delta-up');
        deltaCell.setAttribute('aria-label', `Up ${Math.round(delta)} from prior season`);
      } else {
        deltaCell.textContent = `▼ ${fmtInt(Math.abs(delta))}`;
        deltaCell.classList.add('lb-delta-down');
        deltaCell.setAttribute('aria-label', `Down ${Math.abs(Math.round(delta))} from prior season`);
      }
      tr.appendChild(deltaCell);
    }

    appendNumCell(tr, fmtInt(row.gamesPlayed));
    appendNumCell(tr, fmtInt(row.gamesWon));
    appendNumCell(tr, fmtPct(row.winRate));
    appendNumCell(tr, fmtSigned(row.totalScore));
    appendNumCell(tr, fmtInt(row.highestScore));
    appendNumCell(tr, fmtInt(row.longestStreak));

    // Phase J Wave 7 — per-row "View" button opens the player's profile
    // page (read-only when looking at someone else's profile), which in
    // turn lists their recent games with replay links.  This is the
    // gateway the spec requires for "leaderboard → replay" navigation
    // without baking a per-row gameId into the leaderboard payload.
    const viewCell = el('td', 'lb-col-action lb-cell-action');
    const viewBtn = document.createElement('button');
    viewBtn.type = 'button';
    viewBtn.className = 'lb-view-btn';
    viewBtn.textContent = 'View';
    viewBtn.setAttribute('aria-label', `View profile for ${row.displayName}`);
    viewBtn.setAttribute('data-testid', `leaderboard-view-${idx}`);
    viewBtn.dataset.playerId = row.playerId;
    viewBtn.addEventListener('click', (ev) => {
      ev.preventDefault();
      ev.stopPropagation();
      window.dispatchEvent(new CustomEvent('mahjong:open-profile-page', {
        detail: {
          playerId: row.playerId,
          displayName: row.displayName,
          avatarColor: row.avatarColor,
          readOnly: true,
        },
      }));
    });
    viewCell.appendChild(viewBtn);
    tr.appendChild(viewCell);

    body.appendChild(tr);
  });
  table.appendChild(body);
  host.appendChild(table);
}

function appendNumCell(row: HTMLElement, value: string): void {
  const td = el('td', 'lb-col-num lb-cell-num');
  td.textContent = value;
  row.appendChild(td);
}
