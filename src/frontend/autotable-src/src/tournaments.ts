// Phase J Wave 10 → Phase K Wave 1 — Tournaments tab.
//
// Owns the Tournaments tab inside the lobby strip.  Feature-detects
// Bishop's tournament endpoints — when the backend is unavailable
// (404 on `GET /api/tournaments`) we render a "Coming soon"
// placeholder so the rest of the lobby keeps working.
//
// Phase K Wave 1 additions:
//   • Single-elim bracket is rendered as an interactive SVG instead
//     of the Wave-10 <pre> dump.  Match cells are clickable to expand
//     an inline detail row; a "Watch finals" link surfaces on the
//     final-round match once it completes.
//   • Round-robin + Swiss tournaments render a sortable standings
//     table whose column headers cycle the sort key on click.
//   • Subscribes to Bishop's `TournamentMatchCompleted` hub event
//     (propagated from Bishop's GameCompleted hook) so the bracket /
//     standings refresh in-place without polling.
//
// Wire contract (matches Bishop's draft + Phase K Wave 1 evolution):
//   GET    /api/tournaments                 → 200 { tournaments: [...] } | 404
//   GET    /api/tournaments/{id}            → 200 { tournament, bracket, standings, matches? } | 404
//   POST   /api/tournaments                 → 201 { tournament }
//   POST   /api/tournaments/{id}/register   → 204
//   POST   /api/tournaments/{id}/unregister → 204
//   POST   /api/tournaments/{id}/start      → 204
//   Hub:   ChangshaHub#TournamentMatchCompleted({ tournamentId, matchId, … })
//
// `installTournamentsPanel()` is idempotent and re-runs the probe on
// each activation so the placeholder can self-heal once the backend
// ships.

import { openReplayForGame } from './replay-launcher';
import { setElHidden, showEl, hideEl } from './dom-utils';
import { pickBracketRenderer, resolveFormatKey } from './bracket-renderer';
import type { DoubleElimLayout } from './bracket-renderer';

// ── Wire types ──────────────────────────────────────────────────────

interface TournamentSummary {
  id: string;
  name: string;
  format: string;
  status: 'open' | 'running' | 'complete' | string;
  playersRegistered: number;
  maxPlayers: number;
  startsAtUtc?: string | null;
  viewerRegistered?: boolean;
  viewerCanStart?: boolean;
}

/**
 * Wire shape for a single bracket match.  Bishop's controller is
 * still settling, so we tolerate multiple field names in the
 * normalizer below — what matters is that we get round / matchIndex
 * / status / players + an optional gameId for the replay link.
 */
export interface BracketMatch {
  id: string;
  round: number;
  matchIndex: number; // 0-based ordinal within the round
  player1: BracketSlot | null;
  player2: BracketSlot | null;
  winnerPlayerId: string | null;
  status: 'pending' | 'in-progress' | 'complete' | string;
  /** Backing game once the match is dispatched to a table. */
  gameId: string | null;
  score1?: number | null;
  score2?: number | null;
}

export interface BracketSlot {
  playerId: string | null;
  displayName: string;
  avatarColor?: string | null;
}

export interface StandingsRow {
  rank: number;
  playerId: string;
  displayName: string;
  wins: number;
  losses: number;
  draws: number;
  points: number;
  buchholz?: number | null;
}

interface TournamentDetail {
  tournament: TournamentSummary;
  /** Single-elim bracket; empty for round-robin / Swiss formats. */
  matches: BracketMatch[];
  standings: StandingsRow[];
  /**
   * Phase K Wave 4 — Registered players list, used to render the
   * seeding panel in sparse mode (some players haven't been placed
   * into round-1 match slots yet).  Optional because Bishop's
   * pre-Wave-4 detail payload doesn't carry it.
   */
  players?: BracketSlot[];
  /**
   * Phase K Wave 8 — Bishop's double-elim partition.  When present,
   * the renderer uses the server-authored split for the
   * winners/losers brackets + grand-final/reset-match.  Absent on
   * single-elim / Swiss / round-robin formats and on mid-deploy
   * responses that still ship the flat `matches[]` shape.
   */
  layout?: DoubleElimLayout | null;
  viewerRegistered?: boolean;
  viewerCanStart?: boolean;
}

// ── Module state ────────────────────────────────────────────────────

interface State {
  installed: boolean;
  hubSubscribed: boolean;
  available: boolean | null;
  selected: string | null;
  detail: TournamentDetail | null;
  expandedMatchId: string | null;
  standingsSort: StandingsSort;
  standingsAsc: boolean;
  // Phase K Wave 2 — admin role probe + drag-drop seeding state.
  isAdmin: boolean;
  adminProbed: boolean;
  dragSeedFromMatchId: string | null;
}

const state: State = {
  installed: false,
  hubSubscribed: false,
  available: null,
  selected: null,
  detail: null,
  expandedMatchId: null,
  standingsSort: 'rank',
  standingsAsc: true,
  isAdmin: false,
  adminProbed: false,
  dragSeedFromMatchId: null,
};

type StandingsSort = 'rank' | 'player' | 'wins' | 'losses' | 'draws' | 'points';

// ── DOM helpers ─────────────────────────────────────────────────────

function $(id: string): HTMLElement | null {
  return document.getElementById(id);
}

// ── HTTP helpers ────────────────────────────────────────────────────

async function probe(): Promise<boolean> {
  try {
    const resp = await fetch('/api/tournaments', {
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
    });
    if (resp.status === 404) return false;
    if (!resp.ok) return false;
    return true;
  } catch {
    return false;
  }
}

async function fetchList(): Promise<TournamentSummary[]> {
  try {
    const resp = await fetch('/api/tournaments', {
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
    });
    if (!resp.ok) return [];
    const body = await resp.json() as unknown;
    if (Array.isArray(body)) return body as TournamentSummary[];
    if (body !== null && typeof body === 'object'
        && Array.isArray((body as Record<string, unknown>).tournaments)) {
      return (body as Record<string, unknown>).tournaments as TournamentSummary[];
    }
    return [];
  } catch {
    return [];
  }
}

async function fetchDetail(id: string): Promise<TournamentDetail | null> {
  try {
    const resp = await fetch(`/api/tournaments/${encodeURIComponent(id)}`, {
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
    });
    if (!resp.ok) return null;
    const raw = await resp.json() as unknown;
    return normalizeDetail(raw);
  } catch {
    return null;
  }
}

async function doPost(path: string, body?: unknown): Promise<boolean> {
  const r = await doPostStatus(path, body);
  return r.ok;
}

/**
 * Phase K Wave 4 — Status-aware POST.  Returns `{ ok, status }` so
 * callers can branch on the specific HTTP code (e.g. 400 → schema
 * validation toast).  Network errors map to `{ ok: false, status: 0 }`.
 */
interface PostResult {
  ok: boolean;
  status: number;
}

async function doPostStatus(path: string, body?: unknown): Promise<PostResult> {
  try {
    const resp = await fetch(path, {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    return { ok: resp.ok, status: resp.status };
  } catch {
    return { ok: false, status: 0 };
  }
}

// ── Phase K Wave 2 — Admin role probe + seeding helpers ────────────

interface MeResponse {
  authenticated?: boolean;
  Authenticated?: boolean;
  claims?: { role?: string; roles?: string[] } | Record<string, unknown>;
  Claims?: { role?: string; roles?: string[] } | Record<string, unknown>;
  role?: string;
  Role?: string;
  roles?: string[];
  Roles?: string[];
}

function isAdminPayload(raw: unknown): boolean {
  if (raw === null || typeof raw !== 'object') return false;
  const me = raw as MeResponse;
  const claims = me.claims ?? me.Claims;
  if (claims !== undefined && claims !== null && typeof claims === 'object') {
    const c = claims as Record<string, unknown>;
    if (typeof c.role === 'string' && c.role.toLowerCase() === 'admin') return true;
    if (Array.isArray(c.roles) && c.roles.some((r) => String(r).toLowerCase() === 'admin')) return true;
  }
  const role = me.role ?? me.Role;
  if (typeof role === 'string' && role.toLowerCase() === 'admin') return true;
  const roles = me.roles ?? me.Roles;
  if (Array.isArray(roles) && roles.some((r) => String(r).toLowerCase() === 'admin')) return true;
  return false;
}

async function probeAdmin(): Promise<boolean> {
  if (state.adminProbed) return state.isAdmin;
  state.adminProbed = true;
  try {
    const r = await fetch('/api/auth/me', {
      method: 'GET',
      credentials: 'include',
      headers: { Accept: 'application/json' },
    });
    if (!r.ok) return false;
    const body = await r.json() as unknown;
    state.isAdmin = isAdminPayload(body);
    return state.isAdmin;
  } catch {
    return false;
  }
}

/**
 * Phase K Wave 2 → Wave 4 — POST the new seed order to Bishop's
 * seeding endpoint.  Wave-4 surfaces sparse seeding (some players
 * unseeded) — the wire shape stays `{ seeds: [{ playerId, seedNumber
 * }, …] }`, but unseeded slots send `seedNumber: 0`.  Bishop's server
 * interprets 0 as "preserve as unseeded".
 *
 * Returns `{ ok, status }` so the caller can surface a 400-specific
 * toast ("Tournament must have unique sequential seeds 1..N.") when
 * the server's body validation fails.
 *
 * Wire: `POST /api/tournaments/{id}/seed`
 *       body `{ seeds: [{ playerId, seedNumber }, ...] }`.
 */
interface SeedEntry {
  playerId: string;
  seedNumber: number;
}

async function postSeed(tournamentId: string, entries: SeedEntry[]): Promise<PostResult> {
  const payload = { seeds: entries };
  return doPostStatus(`/api/tournaments/${encodeURIComponent(tournamentId)}/seed`, payload);
}

// ── Normalisers ─────────────────────────────────────────────────────

function normalizeDetail(raw: unknown): TournamentDetail | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const summaryRaw = (o.tournament ?? o) as Record<string, unknown>;
  const tournament: TournamentSummary = {
    id: typeof summaryRaw.id === 'string' ? summaryRaw.id : '',
    name: typeof summaryRaw.name === 'string' ? summaryRaw.name : 'Tournament',
    format: typeof summaryRaw.format === 'string' ? summaryRaw.format : 'single-elim',
    status: typeof summaryRaw.status === 'string' ? summaryRaw.status : 'open',
    playersRegistered: typeof summaryRaw.playersRegistered === 'number'
      ? summaryRaw.playersRegistered : 0,
    maxPlayers: typeof summaryRaw.maxPlayers === 'number' ? summaryRaw.maxPlayers : 0,
    startsAtUtc: typeof summaryRaw.startsAtUtc === 'string' ? summaryRaw.startsAtUtc : null,
    viewerRegistered: typeof summaryRaw.viewerRegistered === 'boolean'
      ? summaryRaw.viewerRegistered : undefined,
    viewerCanStart: typeof summaryRaw.viewerCanStart === 'boolean'
      ? summaryRaw.viewerCanStart : undefined,
  };

  const matches = normalizeMatches(o.matches ?? o.bracket);
  const standings = normalizeStandings(o.standings ?? o.leaderboard);
  // Phase K Wave 9 — Canonical Bishop W8 wire shape (per
  // `docs/contracts/bracket-api.md`):
  //
  //   { layout: { winnersBracket, losersBracket,
  //               grandFinal: { match, resetMatch } } }
  //
  // W6→W8 we accepted three wrapper-key aliases (`layout`,
  // `doubleElimLayout`, `bracketLayout`) and per-field
  // synonyms — pragmatic at the time but it masked the W6 Bishop
  // contract drift that landed us here.  W9 hard-requires the
  // canonical key.  `normalizeDoubleElimLayout` returns null on
  // any other shape, and `BracketRenderer` will emit a
  // `bracket-shape-error` testid + `console.error` instead of
  // falling through to a heuristic partition.
  const layout = normalizeDoubleElimLayout(o.layout ?? null);
  // Phase K Wave 4 — Sparse-mode seeding needs the registered player
  // list independent of round-1 match slots.  Tolerate a handful of
  // wire vocabularies (`players`, `registered`, `participants`) so we
  // don't need a Bishop wire-shape commit to land first.
  const players = normalizeSlots(
    o.players ?? o.Players ?? o.registered ?? o.Registered ?? o.participants);

  return {
    tournament,
    matches,
    standings,
    players,
    layout,
    viewerRegistered: typeof o.viewerRegistered === 'boolean'
      ? o.viewerRegistered : tournament.viewerRegistered,
    viewerCanStart: typeof o.viewerCanStart === 'boolean'
      ? o.viewerCanStart : tournament.viewerCanStart,
  };
}

function normalizeDoubleElimLayout(raw: unknown): DoubleElimLayout | null {
  // Phase K Wave 9 — Hard-canonical wire shape.  Only accepts the
  // exact field names Bishop documents in W8 (`docs/contracts/
  // bracket-api.md`).  Any other vocabulary returns null so
  // `BracketRenderer` can surface the discrepancy via the
  // `bracket-shape-error` testid; we explicitly do NOT fall back
  // to W6→W8 heuristics that masked Bishop contract drift.
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const winners = normalizeMatches(o.winnersBracket);
  const losers = normalizeMatches(o.losersBracket);
  const grandRaw = (o.grandFinal ?? null) as Record<string, unknown> | null;
  let match: BracketMatch | null = null;
  let resetMatch: BracketMatch | null = null;
  if (grandRaw !== null && typeof grandRaw === 'object') {
    const matchArr = normalizeMatches([grandRaw.match]);
    if (matchArr.length > 0) match = matchArr[0];
    const resetSource = grandRaw.resetMatch;
    if (resetSource !== null && resetSource !== undefined) {
      const resetArr = normalizeMatches([resetSource]);
      if (resetArr.length > 0) resetMatch = resetArr[0];
    }
  }
  if (winners.length === 0
      && losers.length === 0
      && match === null
      && resetMatch === null) {
    return null;
  }
  return {
    winnersBracket: winners,
    losersBracket: losers,
    grandFinal: { match, resetMatch },
  };
}

function normalizeSlots(raw: unknown): BracketSlot[] {
  if (!Array.isArray(raw)) return [];
  const out: BracketSlot[] = [];
  for (const r of raw) {
    const slot = normalizeSlot(r);
    if (slot !== null) out.push(slot);
  }
  return out;
}

function normalizeMatches(raw: unknown): BracketMatch[] {
  if (!Array.isArray(raw)) return [];
  const out: BracketMatch[] = [];
  for (let i = 0; i < raw.length; i++) {
    const r = raw[i] as unknown;
    if (r === null || typeof r !== 'object') continue;
    const o = r as Record<string, unknown>;
    const id = typeof o.id === 'string' && o.id !== ''
      ? o.id
      : `m-${i}`;
    const round = typeof o.round === 'number' ? o.round : 1;
    const matchIndex = typeof o.matchIndex === 'number'
      ? o.matchIndex
      : (typeof o.index === 'number' ? o.index : i);
    out.push({
      id,
      round,
      matchIndex,
      player1: normalizeSlot(o.player1 ?? slotFromIds(o, 1)),
      player2: normalizeSlot(o.player2 ?? slotFromIds(o, 2)),
      winnerPlayerId: typeof o.winnerPlayerId === 'string' ? o.winnerPlayerId : null,
      status: typeof o.status === 'string' ? o.status : 'pending',
      gameId: typeof o.gameId === 'string' && o.gameId !== '' ? o.gameId : null,
      score1: typeof o.score1 === 'number' ? o.score1 : null,
      score2: typeof o.score2 === 'number' ? o.score2 : null,
    });
  }
  return out;
}

function slotFromIds(o: Record<string, unknown>, n: 1 | 2): unknown {
  const idKey = `player${n}Id`;
  const nameKey = `player${n}Name`;
  if (typeof o[idKey] !== 'string') return null;
  return {
    playerId: o[idKey],
    displayName: typeof o[nameKey] === 'string' ? o[nameKey] : (o[idKey] as string),
  };
}

function normalizeSlot(raw: unknown): BracketSlot | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const playerId = typeof o.playerId === 'string'
    ? o.playerId
    : (typeof o.id === 'string' ? o.id : null);
  const displayName = typeof o.displayName === 'string' && o.displayName !== ''
    ? o.displayName
    : (typeof o.name === 'string' && o.name !== ''
      ? o.name
      : (playerId !== null ? playerId.slice(0, 8) : '—'));
  return {
    playerId,
    displayName,
    avatarColor: typeof o.avatarColor === 'string' ? o.avatarColor : null,
  };
}

function normalizeStandings(raw: unknown): StandingsRow[] {
  if (!Array.isArray(raw)) return [];
  const out: StandingsRow[] = [];
  for (let i = 0; i < raw.length; i++) {
    const r = raw[i] as unknown;
    if (r === null || typeof r !== 'object') continue;
    const o = r as Record<string, unknown>;
    const playerId = typeof o.playerId === 'string' && o.playerId !== ''
      ? o.playerId
      : (typeof o.id === 'string' ? o.id : `p-${i}`);
    const wins = typeof o.wins === 'number' ? o.wins : 0;
    const losses = typeof o.losses === 'number' ? o.losses : 0;
    const draws = typeof o.draws === 'number' ? o.draws : 0;
    const points = typeof o.points === 'number'
      ? o.points
      : (wins * 3 + draws); // Standard 3/1/0 Swiss scoring fallback.
    out.push({
      rank: typeof o.rank === 'number' ? o.rank : i + 1,
      playerId,
      displayName: typeof o.displayName === 'string' && o.displayName !== ''
        ? o.displayName
        : (typeof o.name === 'string' && o.name !== '' ? o.name : playerId.slice(0, 8)),
      wins,
      losses,
      draws,
      points,
      buchholz: typeof o.buchholz === 'number' ? o.buchholz : null,
    });
  }
  return out;
}

// ── Rendering ───────────────────────────────────────────────────────

function renderEmpty(): void {
  const placeholder = $('tournaments-placeholder');
  const list = $('tournament-list');
  const detail = $('tournament-detail');
  const form = $('tournament-create-form');
  if (placeholder !== null) showEl(placeholder);
  if (list !== null) hideEl(list);
  if (detail !== null) hideEl(detail);
  if (form !== null) hideEl(form);
}

function renderListShell(): void {
  const placeholder = $('tournaments-placeholder');
  const list = $('tournament-list');
  const form = $('tournament-create-form');
  if (placeholder !== null) hideEl(placeholder);
  if (list !== null) showEl(list);
  if (form !== null) showEl(form);
}

function renderList(rows: TournamentSummary[]): void {
  const list = $('tournament-list');
  if (list === null) return;
  list.replaceChildren();
  if (rows.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'lobby-tab-tournaments-empty';
    empty.textContent = 'No tournaments yet — create one below.';
    list.appendChild(empty);
    return;
  }
  rows.forEach((row, i) => {
    const card = document.createElement('div');
    card.className = 'tournament-row';
    card.setAttribute('role', 'listitem');
    card.setAttribute('data-testid', `tournament-row-${i}`);
    card.setAttribute('data-tournament-id', row.id);

    const name = document.createElement('div');
    name.className = 'tournament-row-name';
    name.textContent = row.name;

    const meta = document.createElement('div');
    meta.className = 'tournament-row-meta';
    meta.textContent = `${row.format} · ${row.playersRegistered}/${row.maxPlayers} · ${row.status}`;

    const status = document.createElement('span');
    status.className = 'tournament-registration-status';
    status.setAttribute('data-testid', 'tournament-registration-status');
    status.textContent = row.viewerRegistered === true ? 'Registered'
      : row.status === 'open' ? 'Open'
      : row.status;

    const actions = document.createElement('div');
    actions.className = 'tournament-row-actions';
    const open = row.status === 'open' || row.status === 'registration-open';
    if (open && row.viewerRegistered !== true) {
      const reg = document.createElement('button');
      reg.type = 'button';
      reg.className = 'btn btn-success btn-sm';
      reg.setAttribute('data-testid', 'tournament-register-btn');
      reg.textContent = 'Register';
      reg.addEventListener('click', async (ev) => {
        ev.stopPropagation();
        const ok = await doPost(`/api/tournaments/${encodeURIComponent(row.id)}/register`);
        if (ok) void refreshList();
      });
      actions.appendChild(reg);
    }
    if (open && row.viewerCanStart === true) {
      const startBtn = document.createElement('button');
      startBtn.type = 'button';
      startBtn.className = 'btn btn-warning btn-sm';
      startBtn.setAttribute('data-testid', 'tournament-start-btn');
      startBtn.textContent = 'Start';
      startBtn.addEventListener('click', async (ev) => {
        ev.stopPropagation();
        const ok = await doPost(`/api/tournaments/${encodeURIComponent(row.id)}/start`);
        if (ok) void refreshList();
      });
      actions.appendChild(startBtn);
    }

    card.appendChild(name);
    card.appendChild(meta);
    card.appendChild(status);
    card.appendChild(actions);
    card.addEventListener('click', () => {
      void openDetail(row.id);
    });
    list.appendChild(card);
  });
}

async function openDetail(id: string): Promise<void> {
  state.selected = id;
  state.expandedMatchId = null;
  // Phase K Wave 2 — probe admin once per session so the bracket can
  // expose drag-drop seeding when the viewer is authorised + the
  // tournament is still open for seeding.
  void probeAdmin().then(() => {
    // Re-render once the probe resolves so the seed handles can
    // appear without waiting for another user click.
    if (state.detail !== null && state.detail.tournament.id === id) {
      rerenderBracket();
    }
  });
  const detail = await fetchDetail(id);
  state.detail = detail;
  const detailEl = $('tournament-detail');
  const listEl = $('tournament-list');
  const form = $('tournament-create-form');
  if (detail === null || detailEl === null) {
    if (detailEl !== null) hideEl(detailEl);
    return;
  }
  if (listEl !== null) hideEl(listEl);
  if (form !== null) hideEl(form);
  showEl(detailEl);

  const nameEl = $('tournament-detail-name');
  if (nameEl !== null) nameEl.textContent = detail.tournament.name;
  const metaEl = $('tournament-detail-meta');
  if (metaEl !== null) {
    metaEl.textContent = `${detail.tournament.format} · ${detail.tournament.status} · `
      + `${detail.tournament.playersRegistered}/${detail.tournament.maxPlayers}`;
  }

  rerenderBracket();
  rerenderStandings();

  const registerBtn = $('tournament-register') as HTMLButtonElement | null;
  const unregisterBtn = $('tournament-unregister') as HTMLButtonElement | null;
  const startBtn = $('tournament-start') as HTMLButtonElement | null;
  const open = detail.tournament.status === 'open' || detail.tournament.status === 'registration-open';
  if (registerBtn !== null) setElHidden(registerBtn, !open || detail.viewerRegistered === true);
  if (unregisterBtn !== null) setElHidden(unregisterBtn, !open || detail.viewerRegistered !== true);
  if (startBtn !== null) setElHidden(startBtn, !open || detail.viewerCanStart !== true);
}

function rerenderBracket(): void {
  const host = $('tournament-bracket');
  if (host === null) return;
  host.replaceChildren();
  const detail = state.detail;
  if (detail === null) return;

  // Phase K Wave 6 — Strategy dispatch.  The bracket-renderer module
  // picks the format-appropriate view (single-elim SVG, Swiss W/L/D
  // table, double-elim winners+losers split).  Backwards-compatible:
  // the single-elim wrapper still delegates to `buildBracketSvg`
  // below, so the SVG layout / connector geometry is unchanged.
  const fmt = (detail.tournament.format || '').toLowerCase();
  const formatKey = resolveFormatKey(fmt);
  const isElim = formatKey === 'single-elim' || formatKey === 'double-elim';

  // Admin seeding panel — single-elim only (double-elim seeds are
  // computed from the winners bracket round 1 like single-elim; the
  // existing panel works there as well).
  if (isElim) {
    const seedingPanel = buildSeedingPanel(detail);
    if (seedingPanel !== null) {
      host.appendChild(seedingPanel);
    }
  }

  const renderer = pickBracketRenderer(fmt);
  const rendered = renderer.render({
    format: fmt,
    matches: detail.matches,
    standings: detail.standings,
    players: detail.players,
    tournamentId: detail.tournament.id,
    singleElimSvg: () => buildBracketSvg(detail),
    // Phase K Wave 8 — Pass Bishop's server-authored partition
    // through to the DoubleElimRenderer.  `null` when the wire
    // ships only the flat `matches[]` (mid-deploy / legacy).
    layout: detail.layout ?? null,
  });
  host.appendChild(rendered);

  if (formatKey === 'single-elim' && detail.matches.length > 0) {
    const expanded = buildExpandedRow(detail);
    if (expanded !== null) host.appendChild(expanded);
  }
}

// ── Phase K Wave 2 → Wave 4 — Admin drag-drop seeding panel ────────

/**
 * Phase K Wave 4 — Sparse-mode row.  Each entry tracks a registered
 * player, a flag for whether they are currently seeded (drives the
 * `#N` vs `—` rank label), and the optimistic-render seed number.
 * When the user drops a row above the "Unseeded" divider (i.e.
 * within the first `seededCount` slots) it becomes seeded; dropping
 * below the divider returns it to unseeded.
 */
interface SeedRow {
  slot: BracketSlot;
  seeded: boolean;
}

const SPARSE_VALIDATION_TOAST = 'Tournament must have unique sequential seeds 1..N.';

function buildSeedingPanel(detail: TournamentDetail): HTMLDivElement | null {
  if (!state.isAdmin) return null;
  const fmt = (detail.tournament.format || '').toLowerCase();
  const isElim = fmt.includes('elim') || fmt.includes('bracket');
  if (!isElim) return null;
  const status = (detail.tournament.status || '').toLowerCase();
  const isOpen = status === 'open' || status === 'registration-open';
  if (!isOpen) return null;

  // Phase K Wave 4 — Build the unified row list.  Seeded players come
  // from round-1 match slots (in slot-order); unseeded come from the
  // registered-players list (`detail.players`) minus everyone already
  // placed into a seeded slot.
  const round1 = detail.matches
    .filter(m => m.round === 1)
    .sort((a, b) => a.matchIndex - b.matchIndex);

  const seededSlots: BracketSlot[] = [];
  for (const m of round1) {
    if (m.player1 !== null && m.player1.playerId !== null) seededSlots.push(m.player1);
    if (m.player2 !== null && m.player2.playerId !== null) seededSlots.push(m.player2);
  }
  const seededIds = new Set(seededSlots.map(s => s.playerId).filter((p): p is string => p !== null));

  const rows: SeedRow[] = seededSlots.map(slot => ({ slot, seeded: true }));
  for (const slot of detail.players ?? []) {
    if (slot.playerId !== null && !seededIds.has(slot.playerId)) {
      rows.push({ slot, seeded: false });
    }
  }
  if (rows.length < 2) return null;

  const wrap = document.createElement('div');
  wrap.className = 'tournament-seeding-panel';
  wrap.setAttribute('data-testid', 'tournament-seeding-panel');
  wrap.setAttribute('role', 'region');
  wrap.setAttribute('aria-label', 'Tournament seeding');

  const header = document.createElement('div');
  header.className = 'tournament-seeding-header';
  header.textContent = 'Seeding (drag or use arrow keys on a handle to reorder; press Enter on a handle to edit a seed number directly)';
  wrap.appendChild(header);

  // Phase K Wave 5 — Visually-hidden aria-live region used by the
  // keyboard reorder + edit flow.  Drag-drop is unannounced (mouse
  // users get visual feedback); keyboard moves and edits are
  // announced via `aria-live="polite"` so screen-reader users hear
  // the new position before the row repaints.
  const liveRegion = document.createElement('div');
  liveRegion.className = 'tournament-seeding-live';
  liveRegion.setAttribute('data-testid', 'seed-live-region');
  liveRegion.setAttribute('role', 'status');
  liveRegion.setAttribute('aria-live', 'polite');
  liveRegion.setAttribute('aria-atomic', 'true');
  // Inline visually-hidden styling so we don't need to ship a
  // dedicated stylesheet change for a single panel.
  liveRegion.style.position = 'absolute';
  liveRegion.style.width = '1px';
  liveRegion.style.height = '1px';
  liveRegion.style.padding = '0';
  liveRegion.style.margin = '-1px';
  liveRegion.style.overflow = 'hidden';
  liveRegion.style.clip = 'rect(0,0,0,0)';
  liveRegion.style.whiteSpace = 'nowrap';
  liveRegion.style.border = '0';
  wrap.appendChild(liveRegion);

  const announce = (msg: string): void => {
    liveRegion.textContent = msg;
  };

  const list = document.createElement('ol');
  list.className = 'tournament-seeding-list';
  list.setAttribute('role', 'list');

  let lastSavedRows = cloneRows(rows);

  const actions = document.createElement('div');
  actions.className = 'tournament-seeding-actions';

  // Phase K Wave 5 — When the user reorders via keyboard we need to
  // restore focus to the new handle position after rerender(), since
  // `list.replaceChildren()` blows away the original element.  The
  // identifier we use is the playerId (stable across rerenders).
  let pendingFocusPlayerId: string | null = null;

  const focusHandleByPlayerId = (playerId: string): void => {
    // Iterate rather than use `querySelector` with a CSS attribute
    // selector — playerIds are server-generated and may legitimately
    // contain characters (e.g. `=`, `+`, `:`) that require CSS
    // escaping.  A linear scan over the live row set is O(N) on the
    // tournament size (≤ 64 in practice) and side-steps the escape
    // pitfall entirely.
    const rowEls = list.querySelectorAll<HTMLElement>('.tournament-seeding-row');
    for (const rowEl of Array.from(rowEls)) {
      if (rowEl.getAttribute('data-player-id') === playerId) {
        const handle = rowEl.querySelector<HTMLElement>('.tournament-seeding-handle');
        if (handle !== null) handle.focus();
        return;
      }
    }
  };

  const rerender = (): void => {
    list.replaceChildren();
    let seedRank = 0;
    let unseededDividerRendered = false;
    rows.forEach((row, i) => {
      if (!row.seeded && !unseededDividerRendered) {
        const divider = document.createElement('li');
        divider.className = 'tournament-seeding-divider';
        divider.setAttribute('data-testid', 'tournament-seeding-unseeded-divider');
        divider.setAttribute('role', 'separator');
        divider.textContent = 'Unseeded';
        // Drop-onto-divider promotes the row into the unseeded zone.
        divider.addEventListener('dragover', (ev) => { ev.preventDefault(); });
        divider.addEventListener('drop', (ev) => {
          ev.preventDefault();
          if (ev.dataTransfer === null) return;
          const fromIdx = parseInt(ev.dataTransfer.getData('text/plain'), 10);
          if (!isFinite(fromIdx)) return;
          moveRow(rows, fromIdx, i);
          rows[i].seeded = false;
          recomputeSeededFlags(rows);
          rerender();
          void persistSeeds();
        });
        list.appendChild(divider);
        unseededDividerRendered = true;
      }
      if (row.seeded) seedRank += 1;

      const li = document.createElement('li');
      li.className = row.seeded
        ? 'tournament-seeding-row tournament-seeding-row-seeded'
        : 'tournament-seeding-row tournament-seeding-row-unseeded';
      li.draggable = true;
      li.setAttribute('data-testid', `tournament-seed-row-${i}`);
      li.setAttribute('data-seed-index', String(i));
      li.setAttribute('data-player-id', row.slot.playerId ?? '');
      li.setAttribute('data-seeded', row.seeded ? 'true' : 'false');
      li.setAttribute('role', 'listitem');
      li.setAttribute('aria-grabbed', 'false');

      const rank = document.createElement('span');
      rank.className = 'tournament-seeding-rank';
      // Phase K Wave 4 — sparse-mode "—" placeholder for unseeded
      // rows so admins see at a glance which players still need a
      // seed before the tournament can start.
      rank.textContent = row.seeded ? `#${seedRank}` : '—';
      li.appendChild(rank);

      const name = document.createElement('span');
      name.className = 'tournament-seeding-name';
      name.textContent = row.slot.displayName;
      li.appendChild(name);

      const handle = document.createElement('span');
      handle.className = 'tournament-seeding-handle';
      handle.textContent = '⋮⋮';
      // Phase K Wave 5 — Keyboard-accessible reorder.  The handle was
      // `aria-hidden` in Wave 4 because drag-drop was the only
      // interaction model; Wave 5 promotes it to a focusable button
      // so keyboard users can reorder rows without a pointing device.
      // Vasquez's Wave-5 spec gates on a stable per-player testid so
      // post-reorder lookups don't race against the index-based name
      // (`tournament-seed-row-{i}` shifts whenever the row moves).
      handle.removeAttribute('aria-hidden');
      handle.setAttribute('tabindex', '0');
      handle.setAttribute('role', 'button');
      if (row.slot.playerId !== null && row.slot.playerId !== '') {
        handle.setAttribute('data-testid', `seed-row-${row.slot.playerId}`);
      }
      handle.setAttribute(
        'aria-label',
        row.seeded
          ? `Seed handle for ${row.slot.displayName}, currently #${seedRank}. ` +
            'Use Arrow Up or Arrow Down to reorder; press Enter to edit seed number.'
          : `Seed handle for ${row.slot.displayName}, currently unseeded. ` +
            'Use Arrow Up or Arrow Down to reorder; press Enter to assign a seed number.',
      );
      handle.addEventListener('keydown', (ev) => {
        // Ignore key events with modifiers — keep room for the
        // browser's own keyboard shortcuts (e.g. ⌘↑/⌃↑ to scroll).
        if (ev.altKey || ev.ctrlKey || ev.metaKey || ev.shiftKey) return;
        switch (ev.key) {
          case 'ArrowUp': {
            ev.preventDefault();
            if (i === 0) {
              announce(`${row.slot.displayName} is already at the top.`);
              return;
            }
            moveRow(rows, i, i - 1);
            recomputeSeededFlags(rows);
            const targetSeeded = rows[i - 1].seeded;
            pendingFocusPlayerId = row.slot.playerId;
            announce(
              targetSeeded
                ? `Moved ${row.slot.displayName} up to seed ${i}.`
                : `Moved ${row.slot.displayName} up to position ${i + 1} (unseeded).`,
            );
            rerender();
            void persistSeeds();
            return;
          }
          case 'ArrowDown': {
            ev.preventDefault();
            if (i === rows.length - 1) {
              announce(`${row.slot.displayName} is already at the bottom.`);
              return;
            }
            moveRow(rows, i, i + 1);
            recomputeSeededFlags(rows);
            pendingFocusPlayerId = row.slot.playerId;
            const targetSeeded = rows[i + 1].seeded;
            announce(
              targetSeeded
                ? `Moved ${row.slot.displayName} down to seed ${i + 2}.`
                : `Moved ${row.slot.displayName} down to position ${i + 2} (unseeded).`,
            );
            rerender();
            void persistSeeds();
            return;
          }
          case 'Enter':
          case ' ': {
            ev.preventDefault();
            openSeedKeyboardPrompt(row, i);
            return;
          }
        }
      });
      li.appendChild(handle);

      li.addEventListener('dragstart', (ev) => {
        if (ev.dataTransfer === null) return;
        ev.dataTransfer.setData('text/plain', String(i));
        ev.dataTransfer.effectAllowed = 'move';
        li.classList.add('tournament-seeding-row-dragging');
        li.setAttribute('aria-grabbed', 'true');
      });
      li.addEventListener('dragend', () => {
        li.classList.remove('tournament-seeding-row-dragging');
        li.setAttribute('aria-grabbed', 'false');
      });
      li.addEventListener('dragover', (ev) => {
        ev.preventDefault();
        if (ev.dataTransfer !== null) ev.dataTransfer.dropEffect = 'move';
        li.classList.add('tournament-seeding-row-over');
      });
      li.addEventListener('dragleave', () => {
        li.classList.remove('tournament-seeding-row-over');
      });
      li.addEventListener('drop', (ev) => {
        ev.preventDefault();
        li.classList.remove('tournament-seeding-row-over');
        if (ev.dataTransfer === null) return;
        const fromIdx = parseInt(ev.dataTransfer.getData('text/plain'), 10);
        const toIdx = i;
        if (!isFinite(fromIdx) || fromIdx === toIdx) return;
        moveRow(rows, fromIdx, toIdx);
        // Dropping onto a seeded target promotes the dropped row to
        // seeded.  Dropping onto an unseeded target leaves it unseeded.
        rows[toIdx].seeded = row.seeded;
        recomputeSeededFlags(rows);
        rerender();
        void persistSeeds();
      });

      list.appendChild(li);
    });

    if (pendingFocusPlayerId !== null) {
      const target = pendingFocusPlayerId;
      pendingFocusPlayerId = null;
      // Use rAF so focus lands after the browser commits the new
      // layout — important when the row moved across the unseeded
      // divider (the divider's render shifts everything by 1px).
      window.requestAnimationFrame(() => focusHandleByPlayerId(target));
    }
  };

  // Phase K Wave 5 — Inline modal for "Edit seed number" prompt.  We
  // intentionally avoid the browser `prompt()` builtin: it blocks
  // the main thread, is unstyleable, and Playwright treats it as a
  // dialog the spec must `accept()` — all hostile to the toolchain.
  // The inline dialog carries `role="dialog"` + `aria-modal="true"`
  // and traps focus until the user submits or cancels.
  const openSeedKeyboardPrompt = (row: SeedRow, fromIdx: number): void => {
    // Close any existing prompt before opening a new one.
    const existing = wrap.querySelector('[data-testid="seed-keyboard-prompt"]');
    if (existing !== null) existing.remove();

    const seededCount = rows.filter(r => r.seeded).length;
    const maxSeed = Math.max(seededCount, 1);

    const dialog = document.createElement('div');
    dialog.className = 'tournament-seeding-prompt';
    dialog.setAttribute('data-testid', 'seed-keyboard-prompt');
    dialog.setAttribute('role', 'dialog');
    dialog.setAttribute('aria-modal', 'true');
    dialog.setAttribute('aria-label', `Edit seed number for ${row.slot.displayName}`);

    const labelText = `Enter new seed for ${row.slot.displayName} ` +
      `(1..${maxSeed} to seed; 0 to leave unseeded):`;
    const label = document.createElement('label');
    label.className = 'tournament-seeding-prompt-label';
    label.textContent = labelText;
    const inputId = `seed-prompt-input-${row.slot.playerId ?? fromIdx}`;
    label.htmlFor = inputId;
    dialog.appendChild(label);

    const input = document.createElement('input');
    input.type = 'number';
    input.id = inputId;
    input.className = 'tournament-seeding-prompt-input';
    input.setAttribute('data-testid', 'seed-keyboard-prompt-input');
    input.min = '0';
    input.max = String(rows.length);
    input.step = '1';
    input.value = row.seeded
      ? String(rows.slice(0, fromIdx + 1).filter(r => r.seeded).length)
      : '0';
    dialog.appendChild(input);

    const error = document.createElement('div');
    error.className = 'tournament-seeding-prompt-error';
    error.setAttribute('data-testid', 'seed-keyboard-prompt-error');
    error.setAttribute('role', 'alert');
    error.style.display = 'none';
    dialog.appendChild(error);

    const btnRow = document.createElement('div');
    btnRow.className = 'tournament-seeding-prompt-actions';

    const ok = document.createElement('button');
    ok.type = 'button';
    ok.className = 'btn btn-primary btn-sm';
    ok.setAttribute('data-testid', 'seed-keyboard-prompt-ok');
    ok.textContent = 'Apply';

    const cancel = document.createElement('button');
    cancel.type = 'button';
    cancel.className = 'btn btn-secondary btn-sm';
    cancel.setAttribute('data-testid', 'seed-keyboard-prompt-cancel');
    cancel.textContent = 'Cancel';

    btnRow.appendChild(ok);
    btnRow.appendChild(cancel);
    dialog.appendChild(btnRow);

    wrap.appendChild(dialog);
    input.focus();
    input.select();

    const close = (restoreFocus: boolean): void => {
      dialog.remove();
      if (restoreFocus) {
        pendingFocusPlayerId = row.slot.playerId;
        // No rerender pending — focus directly.
        if (row.slot.playerId !== null && row.slot.playerId !== '') {
          focusHandleByPlayerId(row.slot.playerId);
        }
        pendingFocusPlayerId = null;
      }
    };

    const submit = (): void => {
      const raw = input.value.trim();
      const n = parseInt(raw, 10);
      if (!Number.isFinite(n) || n < 0 || n > rows.length) {
        error.textContent = `Seed must be 0..${rows.length}.`;
        error.style.display = '';
        return;
      }
      // Reorder rows so the picked row lands at the requested seed
      // slot.  n === 0 demotes to unseeded (push past the divider);
      // n in 1..seededCount inserts at position n-1 with seeded=true.
      const movingPlayerId = row.slot.playerId;
      const targetSeeded = n > 0;
      // Determine the actual destination index in `rows`.
      let destIdx: number;
      if (n === 0) {
        // Drop past the last seeded row (or wherever the divider sits).
        // After removing the moved row, append it at the first
        // unseeded position (or end).
        rows.splice(fromIdx, 1);
        const firstUnseeded = rows.findIndex(r => !r.seeded);
        destIdx = firstUnseeded === -1 ? rows.length : firstUnseeded;
        rows.splice(destIdx, 0, { slot: row.slot, seeded: false });
      } else {
        rows.splice(fromIdx, 1);
        // After removal, the n-th seed slot is the (n-1)-th *seeded*
        // row.  Find the index in `rows` of the (n-1)-th currently-
        // seeded entry; if there aren't enough, append at the
        // boundary.
        const seededIndices: number[] = [];
        rows.forEach((r, idx) => { if (r.seeded) seededIndices.push(idx); });
        if (seededIndices.length < n - 1) {
          // Insert at the boundary so it becomes the new last seed.
          destIdx = seededIndices.length === 0 ? 0 : seededIndices[seededIndices.length - 1] + 1;
        } else if (n === seededIndices.length + 1) {
          destIdx = seededIndices.length === 0 ? 0 : seededIndices[seededIndices.length - 1] + 1;
        } else {
          destIdx = seededIndices[n - 1];
        }
        rows.splice(destIdx, 0, { slot: row.slot, seeded: true });
      }
      recomputeSeededFlags(rows);

      if (movingPlayerId !== null && movingPlayerId !== '') {
        pendingFocusPlayerId = movingPlayerId;
      }
      dialog.remove();
      announce(
        targetSeeded
          ? `${row.slot.displayName} is now seed #${n}.`
          : `${row.slot.displayName} is now unseeded.`,
      );
      rerender();
      void persistSeeds();
    };

    ok.addEventListener('click', submit);
    cancel.addEventListener('click', () => close(true));
    dialog.addEventListener('keydown', (ev) => {
      if (ev.key === 'Enter') {
        ev.preventDefault();
        submit();
      } else if (ev.key === 'Escape') {
        ev.preventDefault();
        close(true);
      }
    });
  };

  // Phase K Wave 4 — Persist after each drop.  Wire shape includes
  // every registered player; seeded entries carry `seedNumber: 1..N`,
  // unseeded carry `seedNumber: 0`.  On HTTP failure we restore the
  // last-known-good ordering + re-render; on 400 we surface the
  // body-validation toast Bishop's controller returns ("Tournament
  // must have unique sequential seeds 1..N.").
  const persistSeeds = async (): Promise<void> => {
    const entries = buildSeedEntries(rows);
    const result = await postSeed(detail.tournament.id, entries);
    if (result.ok) {
      lastSavedRows = cloneRows(rows);
      const { showToast } = await import('./toast');
      showToast('Seeding saved.', 'success', 2400);
      return;
    }
    rows.splice(0, rows.length, ...cloneRows(lastSavedRows));
    rerender();
    const statusEl = document.createElement('span');
    statusEl.className = 'tournament-seeding-status tournament-seeding-status-error';
    statusEl.setAttribute('data-testid', 'tournament-seeding-status');
    statusEl.textContent = 'Failed to save seeding — reverted.';
    actions.appendChild(statusEl);
    window.setTimeout(() => statusEl.remove(), 4000);
    const { showToast } = await import('./toast');
    if (result.status === 400) {
      showToast(SPARSE_VALIDATION_TOAST, 'error');
    } else {
      showToast('Failed to save seeding — reverted to last saved order.', 'error');
    }
  };

  rerender();
  wrap.appendChild(list);

  const save = document.createElement('button');
  save.type = 'button';
  save.className = 'btn btn-primary btn-sm tournament-seeding-save';
  save.setAttribute('data-testid', 'tournament-seeding-save');
  save.textContent = 'Save seeding';
  save.addEventListener('click', async () => {
    save.disabled = true;
    const entries = buildSeedEntries(rows);
    const result = await postSeed(detail.tournament.id, entries);
    save.disabled = false;
    if (result.ok) {
      lastSavedRows = cloneRows(rows);
      const { showToast } = await import('./toast');
      showToast('Seeding saved.', 'success', 2400);
      void openDetail(detail.tournament.id);
    } else {
      const statusEl = document.createElement('span');
      statusEl.className = 'tournament-seeding-status tournament-seeding-status-error';
      statusEl.setAttribute('data-testid', 'tournament-seeding-status');
      statusEl.textContent = 'Failed to save seeding.';
      actions.appendChild(statusEl);
      window.setTimeout(() => statusEl.remove(), 4000);
      const { showToast } = await import('./toast');
      if (result.status === 400) {
        showToast(SPARSE_VALIDATION_TOAST, 'error');
      } else {
        showToast('Failed to save seeding.', 'error');
      }
    }
  });
  actions.appendChild(save);

  wrap.appendChild(actions);
  return wrap;
}

function cloneRows(rows: SeedRow[]): SeedRow[] {
  return rows.map(r => ({ slot: r.slot, seeded: r.seeded }));
}

function moveRow(rows: SeedRow[], fromIdx: number, toIdx: number): void {
  const [moved] = rows.splice(fromIdx, 1);
  rows.splice(toIdx, 0, moved);
}

// Ensure the seeded rows form a contiguous prefix.  Re-classifies
// stragglers so the rendered "Unseeded" divider always lands at a
// stable boundary regardless of where the user dropped a row.
function recomputeSeededFlags(rows: SeedRow[]): void {
  let inSeededRun = true;
  for (const r of rows) {
    if (inSeededRun && r.seeded) continue;
    if (!r.seeded) { inSeededRun = false; continue; }
    // A seeded row that follows an unseeded row: demote it so the
    // divider stays a single boundary.
    r.seeded = false;
    inSeededRun = false;
  }
}

function buildSeedEntries(rows: SeedRow[]): SeedEntry[] {
  const entries: SeedEntry[] = [];
  let seed = 0;
  for (const r of rows) {
    if (r.slot.playerId === null || r.slot.playerId === '') continue;
    if (r.seeded) {
      seed += 1;
      entries.push({ playerId: r.slot.playerId, seedNumber: seed });
    } else {
      // Phase K Wave 4 — sparse marker: seedNumber 0 tells Bishop's
      // controller "this player is registered but unseeded".
      entries.push({ playerId: r.slot.playerId, seedNumber: 0 });
    }
  }
  return entries;
}

// ── Bracket SVG ─────────────────────────────────────────────────────

const CELL_W = 180;
const CELL_H = 56;
const ROUND_GAP = 32;
const MATCH_GAP = 12;
const SVG_PAD_X = 12;
const SVG_PAD_Y = 12;

function buildBracketSvg(detail: TournamentDetail): SVGSVGElement {
  // Group matches by round, descending into a sorted-by-matchIndex
  // layout.  Match positions stack from top to bottom; downstream
  // round-N matches anchor between their two parent round-(N-1)
  // matches (standard single-elim layout).
  const byRound = new Map<number, BracketMatch[]>();
  for (const m of detail.matches) {
    const r = byRound.get(m.round) ?? [];
    r.push(m);
    byRound.set(m.round, r);
  }
  const rounds = Array.from(byRound.keys()).sort((a, b) => a - b);
  for (const r of rounds) {
    byRound.get(r)!.sort((a, b) => a.matchIndex - b.matchIndex);
  }
  const finalRound = rounds.length === 0 ? 1 : rounds[rounds.length - 1];

  // Total height anchored on the first round (which has the most
  // matches).  Each subsequent round halves its match count and
  // doubles the vertical stride.
  const firstRoundCount = (byRound.get(rounds[0])?.length ?? 1);
  const totalHeight = firstRoundCount * (CELL_H + MATCH_GAP) - MATCH_GAP + SVG_PAD_Y * 2;
  const totalWidth = rounds.length * (CELL_W + ROUND_GAP) - ROUND_GAP + SVG_PAD_X * 2;

  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('class', 'tournament-bracket-svg');
  svg.setAttribute('data-testid', 'tournament-bracket-svg');
  svg.setAttribute('viewBox', `0 0 ${totalWidth} ${totalHeight}`);
  svg.setAttribute('role', 'img');
  svg.setAttribute('aria-label',
    `Single-elimination bracket: ${rounds.length} rounds, `
    + `${detail.matches.length} matches`);

  // Compute the centre y-coordinate of each (round, matchIndex) cell.
  const yByRoundMatch = new Map<string, number>();
  rounds.forEach((round, rIdx) => {
    const matches = byRound.get(round) ?? [];
    const stride = totalHeight / matches.length;
    matches.forEach((m, mIdx) => {
      const y = SVG_PAD_Y + stride * mIdx + (stride - CELL_H) / 2;
      yByRoundMatch.set(`${round}-${mIdx}`, y);
      drawConnectors(svg, round, mIdx, rounds, rIdx, yByRoundMatch, byRound);
      const x = SVG_PAD_X + rIdx * (CELL_W + ROUND_GAP);
      svg.appendChild(buildMatchCell(m, x, y, round === finalRound, detail.tournament.id));
    });
  });

  return svg;
}

function drawConnectors(
  svg: SVGSVGElement,
  round: number,
  mIdx: number,
  rounds: number[],
  rIdx: number,
  yByRoundMatch: Map<string, number>,
  byRound: Map<number, BracketMatch[]>,
): void {
  // Connector lines go from the right edge of the parent matches in
  // round-(N-1) to the left edge of the current round-N match.  We
  // draw them lazily as we encounter the child (current) cell.  When
  // we don't have parents (round 0) there's nothing to draw.
  if (rIdx === 0) return;
  const prevRound = rounds[rIdx - 1];
  const prevMatches = byRound.get(prevRound) ?? [];
  if (prevMatches.length === 0) return;
  const myX = SVG_PAD_X + rIdx * (CELL_W + ROUND_GAP);
  const myY = (yByRoundMatch.get(`${round}-${mIdx}`) ?? 0) + CELL_H / 2;
  // Two parent matches feed each child: 2*mIdx and 2*mIdx + 1.
  for (const parentIdx of [mIdx * 2, mIdx * 2 + 1]) {
    if (parentIdx >= prevMatches.length) continue;
    const parentY = (yByRoundMatch.get(`${prevRound}-${parentIdx}`) ?? 0) + CELL_H / 2;
    const parentXRight = SVG_PAD_X + (rIdx - 1) * (CELL_W + ROUND_GAP) + CELL_W;
    const midX = parentXRight + ROUND_GAP / 2;
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('class', 'tournament-bracket-connector');
    path.setAttribute(
      'd',
      `M ${parentXRight} ${parentY} `
      + `L ${midX} ${parentY} `
      + `L ${midX} ${myY} `
      + `L ${myX} ${myY}`,
    );
    path.setAttribute('fill', 'none');
    svg.appendChild(path);
  }
}

function buildMatchCell(
  m: BracketMatch,
  x: number,
  y: number,
  isFinalRound: boolean,
  tournamentId: string,
): SVGGElement {
  const group = document.createElementNS('http://www.w3.org/2000/svg', 'g');
  group.setAttribute('class', `tournament-bracket-match tournament-bracket-match-${m.status}`);
  group.setAttribute('data-testid', `tournament-bracket-match-${m.round}-${m.matchIndex}`);
  group.setAttribute('data-match-id', m.id);
  group.setAttribute('transform', `translate(${x}, ${y})`);
  group.setAttribute('tabindex', '0');
  group.setAttribute('role', 'button');
  group.setAttribute('aria-label',
    `Round ${m.round} match ${m.matchIndex + 1}: `
    + `${m.player1?.displayName ?? 'TBD'} vs ${m.player2?.displayName ?? 'TBD'} — ${m.status}`);

  // Background rect
  const bg = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
  bg.setAttribute('class', 'tournament-bracket-match-bg');
  bg.setAttribute('width', String(CELL_W));
  bg.setAttribute('height', String(CELL_H));
  bg.setAttribute('rx', '4');
  group.appendChild(bg);

  // Divider line between the two players
  const divider = document.createElementNS('http://www.w3.org/2000/svg', 'line');
  divider.setAttribute('class', 'tournament-bracket-match-divider');
  divider.setAttribute('x1', '0');
  divider.setAttribute('x2', String(CELL_W));
  divider.setAttribute('y1', String(CELL_H / 2));
  divider.setAttribute('y2', String(CELL_H / 2));
  group.appendChild(divider);

  appendSlotLabel(group, m.player1, 0, m.winnerPlayerId, m.score1 ?? null);
  appendSlotLabel(group, m.player2, CELL_H / 2, m.winnerPlayerId, m.score2 ?? null);

  // Expand pin
  const pin = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
  pin.setAttribute('class', 'tournament-bracket-match-expand');
  pin.setAttribute('data-testid', `tournament-bracket-match-${m.round}-${m.matchIndex}-expand`);
  pin.setAttribute('x', String(CELL_W - 14));
  pin.setAttribute('y', '4');
  pin.setAttribute('width', '10');
  pin.setAttribute('height', '10');
  pin.setAttribute('rx', '2');
  pin.setAttribute('role', 'button');
  pin.setAttribute('aria-label', 'Toggle match detail');
  group.appendChild(pin);

  const onActivate = (ev: Event): void => {
    ev.stopPropagation();
    state.expandedMatchId = state.expandedMatchId === m.id ? null : m.id;
    rerenderBracket();
  };
  group.addEventListener('click', onActivate);
  group.addEventListener('keydown', (ev) => {
    if (ev instanceof KeyboardEvent && (ev.key === 'Enter' || ev.key === ' ')) {
      onActivate(ev);
    }
  });
  pin.addEventListener('click', onActivate);

  // Phase K Wave 1 — final-round complete match exposes a Watch finals
  // button.  Anchored inside the cell so the bracket stays compact;
  // duplicated as a regular HTML link below in the detail strip so
  // it's discoverable by keyboard / screen readers.
  if (isFinalRound && m.status === 'complete' && m.gameId !== null) {
    const fg = document.createElementNS('http://www.w3.org/2000/svg', 'g');
    fg.setAttribute('class', 'tournament-bracket-finals-pin');
    fg.setAttribute('transform', `translate(${CELL_W - 64}, ${CELL_H - 18})`);
    fg.setAttribute('data-testid', `tournament-watch-finals-${tournamentId}`);
    fg.setAttribute('role', 'button');
    fg.setAttribute('aria-label', 'Watch finals replay');
    fg.setAttribute('tabindex', '0');
    const finalsBg = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
    finalsBg.setAttribute('width', '56');
    finalsBg.setAttribute('height', '14');
    finalsBg.setAttribute('rx', '3');
    finalsBg.setAttribute('class', 'tournament-bracket-finals-bg');
    fg.appendChild(finalsBg);
    const finalsTxt = document.createElementNS('http://www.w3.org/2000/svg', 'text');
    finalsTxt.setAttribute('x', '28');
    finalsTxt.setAttribute('y', '10');
    finalsTxt.setAttribute('text-anchor', 'middle');
    finalsTxt.setAttribute('class', 'tournament-bracket-finals-txt');
    finalsTxt.textContent = '▶ Watch finals';
    fg.appendChild(finalsTxt);
    const openFinals = (ev: Event): void => {
      ev.stopPropagation();
      const gid = m.gameId;
      if (gid === null || gid === '') return;
      void openReplayForGame(gid, { finals: true });
    };
    fg.addEventListener('click', openFinals);
    fg.addEventListener('keydown', (ev) => {
      if (ev instanceof KeyboardEvent && (ev.key === 'Enter' || ev.key === ' ')) {
        openFinals(ev);
      }
    });
    group.appendChild(fg);
  }

  return group;
}

function appendSlotLabel(
  parent: SVGGElement,
  slot: BracketSlot | null,
  yOffset: number,
  winnerPlayerId: string | null,
  score: number | null,
): void {
  const isWinner = slot !== null
    && slot.playerId !== null
    && winnerPlayerId !== null
    && slot.playerId === winnerPlayerId;
  const t = document.createElementNS('http://www.w3.org/2000/svg', 'text');
  t.setAttribute('x', '8');
  t.setAttribute('y', String(yOffset + CELL_H / 4 + 5));
  t.setAttribute('class',
    `tournament-bracket-match-slot${isWinner ? ' tournament-bracket-match-slot-winner' : ''}`);
  t.textContent = truncate(slot?.displayName ?? 'TBD', 18);
  parent.appendChild(t);

  if (score !== null) {
    const s = document.createElementNS('http://www.w3.org/2000/svg', 'text');
    s.setAttribute('x', String(CELL_W - 22));
    s.setAttribute('y', String(yOffset + CELL_H / 4 + 5));
    s.setAttribute('text-anchor', 'end');
    s.setAttribute('class', 'tournament-bracket-match-score');
    s.textContent = String(score);
    parent.appendChild(s);
  }
}

function truncate(s: string, n: number): string {
  return s.length <= n ? s : `${s.slice(0, n - 1)}…`;
}

function buildExpandedRow(detail: TournamentDetail): HTMLDivElement | null {
  if (state.expandedMatchId === null) return null;
  const m = detail.matches.find(x => x.id === state.expandedMatchId) ?? null;
  if (m === null) return null;
  const wrap = document.createElement('div');
  wrap.className = 'tournament-bracket-detail';
  wrap.setAttribute('role', 'region');
  wrap.setAttribute('aria-label', `Match ${m.matchIndex + 1} detail`);
  const title = document.createElement('div');
  title.className = 'tournament-bracket-detail-title';
  title.textContent = `Round ${m.round} · Match ${m.matchIndex + 1} · ${m.status}`;
  wrap.appendChild(title);

  const body = document.createElement('div');
  body.className = 'tournament-bracket-detail-body';
  const p1 = m.player1?.displayName ?? 'TBD';
  const p2 = m.player2?.displayName ?? 'TBD';
  const score = (m.score1 !== null && m.score1 !== undefined
    && m.score2 !== null && m.score2 !== undefined)
    ? `(${m.score1}–${m.score2})`
    : '';
  body.textContent = `${p1} vs ${p2} ${score}`;
  wrap.appendChild(body);

  if (m.gameId !== null && m.gameId !== '') {
    const link = document.createElement('button');
    link.type = 'button';
    link.className = 'btn btn-info btn-sm tournament-bracket-detail-replay';
    link.setAttribute('data-testid', `tournament-watch-finals-${detail.tournament.id}`);
    link.textContent = '▶ Watch replay';
    link.addEventListener('click', () => {
      const gid = m.gameId;
      if (gid === null || gid === '') return;
      // Phase K Wave 2 — completed match replay → finals-style deep link.
      void openReplayForGame(gid, { finals: true });
    });
    wrap.appendChild(link);
  }
  return wrap;
}

// Phase K Wave 6 — `buildMatchesList` (the legacy flat list renderer)
// was retired when `bracket-renderer.ts` took over the round-robin /
// Swiss view.  The Swiss strategy renders a round-by-round table
// instead of the flat single-list view; nothing else referenced this
// function so it's been removed.


// ── Standings table ─────────────────────────────────────────────────

function rerenderStandings(): void {
  const host = $('tournament-standings');
  if (host === null) return;
  host.replaceChildren();
  const detail = state.detail;
  if (detail === null || detail.standings.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'tournament-standings-empty';
    empty.textContent = 'Standings will appear once the first match completes.';
    host.appendChild(empty);
    return;
  }

  const fmt = (detail.tournament.format || '').toLowerCase();
  const showBuchholz = fmt.includes('swiss');

  const table = document.createElement('table');
  table.className = 'tournament-standings-table';
  table.setAttribute('data-testid', 'tournament-standings-table');
  table.setAttribute('role', 'table');

  const head = document.createElement('thead');
  const headRow = document.createElement('tr');
  const columns: Array<{ key: StandingsSort; label: string; numeric: boolean }> = [
    { key: 'rank',   label: '#',      numeric: true },
    { key: 'player', label: 'Player', numeric: false },
    { key: 'wins',   label: 'W',      numeric: true },
    { key: 'losses', label: 'L',      numeric: true },
    { key: 'draws',  label: 'D',      numeric: true },
    { key: 'points', label: 'Pts',    numeric: true },
  ];
  for (const col of columns) {
    const th = document.createElement('th');
    th.className = `tournament-standings-col tournament-standings-col-${col.key}`;
    th.setAttribute('scope', 'col');
    th.setAttribute('role', 'columnheader');
    th.setAttribute('tabindex', '0');
    th.textContent = col.label;
    if (col.key === state.standingsSort) {
      th.classList.add('tournament-standings-col-active');
      th.setAttribute('aria-sort', state.standingsAsc ? 'ascending' : 'descending');
    } else {
      th.setAttribute('aria-sort', 'none');
    }
    const toggle = (): void => {
      if (state.standingsSort === col.key) {
        state.standingsAsc = !state.standingsAsc;
      } else {
        state.standingsSort = col.key;
        // Default direction: ascending for rank/player, descending for stats.
        state.standingsAsc = !col.numeric || col.key === 'rank';
      }
      rerenderStandings();
    };
    th.addEventListener('click', toggle);
    th.addEventListener('keydown', (ev) => {
      if (ev.key === 'Enter' || ev.key === ' ') {
        ev.preventDefault();
        toggle();
      }
    });
    headRow.appendChild(th);
  }
  if (showBuchholz) {
    const th = document.createElement('th');
    th.className = 'tournament-standings-col tournament-standings-col-buchholz';
    th.textContent = 'Buchholz';
    th.setAttribute('scope', 'col');
    headRow.appendChild(th);
  }
  head.appendChild(headRow);
  table.appendChild(head);

  const body = document.createElement('tbody');
  const sorted = sortStandings(detail.standings);
  sorted.forEach((row, idx) => {
    const tr = document.createElement('tr');
    tr.className = 'tournament-standings-row';
    tr.setAttribute('data-testid', `tournament-standings-row-${idx}`);
    tr.setAttribute('data-player-id', row.playerId);
    appendCell(tr, String(row.rank));
    appendCell(tr, row.displayName);
    appendCell(tr, String(row.wins));
    appendCell(tr, String(row.losses));
    appendCell(tr, String(row.draws));
    appendCell(tr, String(row.points));
    if (showBuchholz) {
      appendCell(tr, row.buchholz === null || row.buchholz === undefined
        ? '–' : String(row.buchholz));
    }
    body.appendChild(tr);
  });
  table.appendChild(body);
  host.appendChild(table);
}

function appendCell(row: HTMLElement, text: string): void {
  const td = document.createElement('td');
  td.className = 'tournament-standings-cell';
  td.textContent = text;
  row.appendChild(td);
}

function sortStandings(rows: ReadonlyArray<StandingsRow>): StandingsRow[] {
  const copy = rows.slice();
  const sign = state.standingsAsc ? 1 : -1;
  copy.sort((a, b) => {
    switch (state.standingsSort) {
      case 'rank':   return sign * (a.rank - b.rank);
      case 'player': return sign * a.displayName.localeCompare(b.displayName);
      case 'wins':   return sign * (a.wins - b.wins);
      case 'losses': return sign * (a.losses - b.losses);
      case 'draws':  return sign * (a.draws - b.draws);
      case 'points': return sign * (a.points - b.points);
      default:       return 0;
    }
  });
  return copy;
}

// ── Hub subscription ────────────────────────────────────────────────

interface HubEventPayload {
  tournamentId?: string;
  matchId?: string;
  status?: string;
}

/**
 * Best-effort hub subscription.  We dynamic-import `hub.ts` so the
 * SignalR connection isn't paid for unless the Tournaments tab is
 * actually opened.  Failures are silent (the panel falls back to
 * polling on re-activation).
 */
async function ensureHubSubscription(): Promise<void> {
  if (state.hubSubscribed) return;
  state.hubSubscribed = true; // optimistic — we only want one subscription
  // Phase K Wave 8 — Vasquez's `bracket-live-update.spec.ts` drives a
  // synthetic `TournamentBracketUpdated` via this window hook (the
  // SignalR layer is mocked in the spec).  The hook is idempotent
  // and safe to install before the subscription completes — the
  // handler runs the same refresh path either way.
  installPublishTournamentBracketUpdateHook();
  try {
    const { getHubConnection, onHubConnected } = await import('./hub');
    const wire = (conn: { on: (m: string, cb: (...args: unknown[]) => void) => void }): void => {
      const handler = (payload: unknown): void => {
        const p = (payload ?? {}) as HubEventPayload;
        // Refresh the standings + bracket when the currently-selected
        // tournament receives a match-completed event.
        if (state.selected !== null
            && (p.tournamentId === undefined || p.tournamentId === state.selected)) {
          void refreshSelectedDetail();
        } else {
          // Refresh the list even when we're not viewing — the row's
          // status badge may need to flip.
          void refreshList();
        }
      };
      conn.on('TournamentMatchCompleted', handler);
      conn.on('TournamentMatchCompletedV1', handler);
      // Phase K Wave 8 — Bishop ships a finer-grained
      // `TournamentBracketUpdated` push whenever ANY bracket cell
      // mutates (seed shuffle, match dispatched, score reported,
      // grand-final reset spawned).  The handler is identical to
      // match-completed: refresh the active detail / list.  Both
      // events coexist so consumers that only care about completes
      // (e.g. notifications) keep their filter.
      conn.on('TournamentBracketUpdated', handler);
    };
    const conn = await getHubConnection();
    wire(conn);
    onHubConnected((c) => wire(c));
  } catch {
    state.hubSubscribed = false; // allow retry on next activation
  }
}

let publishHookInstalled = false;
function installPublishTournamentBracketUpdateHook(): void {
  if (publishHookInstalled) return;
  publishHookInstalled = true;
  const w = window as unknown as {
    __publishTournamentBracketUpdate?: (payload: unknown) => void;
  };
  w.__publishTournamentBracketUpdate = (payload: unknown): void => {
    const p = (payload ?? {}) as HubEventPayload;
    if (state.selected !== null
        && (p.tournamentId === undefined || p.tournamentId === state.selected)) {
      void refreshSelectedDetail();
    } else {
      void refreshList();
    }
  };
}

async function refreshSelectedDetail(): Promise<void> {
  if (state.selected === null) return;
  const detail = await fetchDetail(state.selected);
  if (detail === null) return;
  state.detail = detail;
  rerenderBracket();
  rerenderStandings();
}

async function refreshList(): Promise<void> {
  const rows = await fetchList();
  renderListShell();
  renderList(rows);
}

// ── Wiring ──────────────────────────────────────────────────────────

function wireDetailButtons(): void {
  const back = $('tournament-detail-back');
  if (back !== null) {
    back.addEventListener('click', () => {
      const detailEl = $('tournament-detail');
      if (detailEl !== null) hideEl(detailEl);
      state.selected = null;
      state.detail = null;
      void refreshList();
    });
  }
  const register = $('tournament-register');
  if (register !== null) {
    register.addEventListener('click', async () => {
      if (state.selected === null) return;
      const ok = await doPost(`/api/tournaments/${encodeURIComponent(state.selected)}/register`);
      if (ok) void openDetail(state.selected);
    });
  }
  const unregister = $('tournament-unregister');
  if (unregister !== null) {
    unregister.addEventListener('click', async () => {
      if (state.selected === null) return;
      const ok = await doPost(`/api/tournaments/${encodeURIComponent(state.selected)}/unregister`);
      if (ok) void openDetail(state.selected);
    });
  }
  const start = $('tournament-start');
  if (start !== null) {
    start.addEventListener('click', async () => {
      if (state.selected === null) return;
      const ok = await doPost(`/api/tournaments/${encodeURIComponent(state.selected)}/start`);
      if (ok) void openDetail(state.selected);
    });
  }
}

function wireCreateForm(): void {
  const form = $('tournament-create-form') as HTMLFormElement | null;
  if (form === null) return;
  form.addEventListener('submit', async (e: SubmitEvent) => {
    e.preventDefault();
    const name = ($('tournament-create-name') as HTMLInputElement | null)?.value.trim() ?? '';
    const format = ($('tournament-create-format') as HTMLSelectElement | null)?.value ?? 'round-robin';
    const maxRaw = ($('tournament-create-max-players') as HTMLInputElement | null)?.value ?? '8';
    const maxPlayers = Math.max(4, Math.min(64, parseInt(maxRaw, 10) || 8));
    if (name === '') return;
    const ok = await doPost('/api/tournaments', { name, format, maxPlayers });
    if (ok) {
      const input = $('tournament-create-name') as HTMLInputElement | null;
      if (input !== null) input.value = '';
      void refreshList();
    }
  });
}

export function installTournamentsPanel(): void {
  if (state.installed) return;
  state.installed = true;
  wireDetailButtons();
  wireCreateForm();
  void probe().then((available) => {
    state.available = available;
    if (!available) {
      renderEmpty();
      return;
    }
    void refreshList();
    void ensureHubSubscription();
  });
}

/** Called by lobby tab activation so the panel can refresh on each
 *  Tournaments-tab click.  No-op when the endpoint is unavailable. */
export function refreshTournamentsPanel(): void {
  if (!state.installed) return;
  if (state.available !== true) {
    void probe().then((available) => {
      state.available = available;
      if (!available) {
        renderEmpty();
        return;
      }
      void refreshList();
      void ensureHubSubscription();
    });
    return;
  }
  void refreshList();
  void ensureHubSubscription();
}
