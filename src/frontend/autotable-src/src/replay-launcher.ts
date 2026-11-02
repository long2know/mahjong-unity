// Phase J Wave 7 — Replay launcher.
//
// Single entry point for "open the replay viewer for game X".  Handles:
//   • Try `GET /api/games/{gameId}/replay` (Bishop's Wave-7 endpoint).
//   • On 404, fall back to the in-memory client.gameComplete payload
//     (the Wave-3 behaviour).
//   • Open the existing 2D replay screen with the resolved event list.
//
// The launcher routes through a module-level callback (`launcher`) that
// `game-ui.ts` wires at boot time — that lets us keep the Replay class
// unaware of the server fetch logic while still letting any surface
// (leaderboard row, profile page row, post-game modal) trigger a
// replay by passing only a `gameId`.

import { type HandResultEntry } from './types';

export interface ServerReplayEvent {
  turn: number;
  phase: string;
  actor: number;        // seat 0..3, or -1 for system events
  action: string;
  tilesJson: string;
  timestampUtc: string;
}

export interface ServerReplayResponse {
  gameId: string;
  events: ReadonlyArray<ServerReplayEvent>;
  handHistory?: ReadonlyArray<HandResultEntry>;
}

export type ReplayLauncher = (payload: ServerReplayResponse, options?: { finals?: boolean }) => void;

let launcher: ReplayLauncher | null = null;

/** game-ui.ts calls this at boot once the Replay singleton exists. */
export function registerReplayLauncher(fn: ReplayLauncher): void {
  launcher = fn;
}

/**
 * Open the replay viewer for `gameId`.  Feature-detects Bishop's
 * Wave-7 server endpoint; on 404 / network error falls back to an
 * empty event list so the viewer at least renders its shell.
 *
 * Phase K Wave 2 — `options.finals` carries the "auto-scroll to the
 * final hand" intent.  Tournament finals links + the `?finals=true`
 * URL deep-link both route through this entry point so the behaviour
 * stays consistent.
 */
export async function openReplayForGame(
  gameId: string,
  options?: { finals?: boolean },
): Promise<void> {
  if (gameId === '' || launcher === null) return;
  const finals = options?.finals === true;
  // Stamp `?finals=true` on the URL when the caller asked for it so
  // a shared / bookmarked replay link reopens at the final hand.
  if (finals) {
    try {
      const url = new URL(window.location.href);
      url.searchParams.set('finals', 'true');
      window.history.replaceState(null, '', url.toString());
    } catch { /* file:// or sandboxed contexts — best effort */ }
  }
  try {
    const resp = await fetch(
      `/api/games/${encodeURIComponent(gameId)}/replay`,
      {
        credentials: 'same-origin',
        headers: { 'Accept': 'application/json' },
      },
    );
    if (resp.status === 404) {
      // Endpoint not implemented yet — render an empty viewer with a
      // placeholder so the user sees we tried.
      launcher({ gameId, events: [], handHistory: [] }, { finals });
      return;
    }
    if (!resp.ok) {
      launcher({ gameId, events: [], handHistory: [] }, { finals });
      return;
    }
    const body = (await resp.json()) as unknown;
    const normalized = normalizeServerReplay(gameId, body);
    launcher(normalized, { finals });
  } catch {
    launcher({ gameId, events: [], handHistory: [] }, { finals });
  }
}

/** Read the `finals=true` flag from the current URL.  Used by replay.ts
 *  to auto-scroll to the final hand when a deep link lands. */
export function readFinalsFlagFromUrl(): boolean {
  try {
    const params = new URLSearchParams(window.location.search);
    return params.get('finals') === 'true';
  } catch {
    return false;
  }
}

/**
 * Phase K Wave 12 — Open the replay viewer with a pre-fetched
 * payload, bypassing the `/api/games/{gameId}/replay` legacy hop.
 *
 * Hicks's `?action=replay&replayId=<id>` dispatch (W12) talks to
 * Bishop's id-addressable `GET /api/replays/{replayId}` endpoint and
 * already holds the response body when this helper runs — feeding
 * that JSON through `normalizeServerReplay()` reuses the existing
 * W7 wire-shape contract without duplicating the normalisation
 * logic in `action-router.ts`.
 *
 * Hand-off contract:
 *   • `replayId` is the canonical id passed to the launcher (which
 *     stamps it as `gameId` on the normalised payload, since the
 *     viewer surface still keys on the legacy `gameId` field).
 *   • `body` is the raw JSON from Bishop's W12 endpoint.  The Bishop
 *     wire shape wraps the actual play-by-play in a `payload`
 *     sub-object alongside metadata fields
 *     (`replayId`/`gameId`/`completedAt`/`variant`/`turnCount`/
 *     `ingestedAt`/`expiresAt`).  We unwrap `body.payload` before
 *     normalisation when present so the `events` + `handHistory`
 *     arrays land in the same shape `normalizeServerReplay()` already
 *     consumes for the W7 legacy `/api/games/{gameId}/replay`
 *     endpoint.  If `body.payload` is absent (caller passed the
 *     already-unwrapped play-by-play), we fall through to the legacy
 *     top-level shape — tolerant in both directions.
 *   • If no launcher has been registered (game-ui.ts hasn't booted
 *     yet), the call no-ops.  The W12 action-router lazy-imports
 *     this module on the `?action=replay` boot path, so under
 *     normal flow the launcher has been wired by the time we land
 *     here.
 */
export function openReplayPayload(
  replayId: string,
  body: unknown,
  options?: { finals?: boolean },
): void {
  if (launcher === null || replayId === '') return;
  const finals = options?.finals === true;
  const playByPlay = unwrapBishopEnvelope(body);
  const normalized = normalizeServerReplay(replayId, playByPlay);
  launcher(normalized, { finals });
}

/**
 * Phase K Wave 12 — If `body` is Bishop's `/api/replays/{id}`
 * envelope (object with a nested `payload` object), return
 * `body.payload`; otherwise return `body` unchanged.  Lets us
 * accept both Bishop's W12 shape and the legacy W7 top-level
 * shape without duplicating the normaliser.
 */
function unwrapBishopEnvelope(body: unknown): unknown {
  if (body === null || typeof body !== 'object') return body;
  const o = body as Record<string, unknown>;
  const payload = o.payload;
  if (payload !== null && typeof payload === 'object' && !Array.isArray(payload)) {
    return payload;
  }
  return body;
}

function normalizeServerReplay(gameId: string, raw: unknown): ServerReplayResponse {
  const o = (raw !== null && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
  const events: ServerReplayEvent[] = [];
  const rawEvents = Array.isArray(o.events) ? o.events : [];
  for (const e of rawEvents) {
    if (e === null || typeof e !== 'object') continue;
    const ev = e as Record<string, unknown>;
    events.push({
      turn: typeof ev.turn === 'number' ? ev.turn : 0,
      phase: typeof ev.phase === 'string' ? ev.phase : '',
      actor: typeof ev.actor === 'number' ? ev.actor : -1,
      action: typeof ev.action === 'string' ? ev.action : '',
      tilesJson: typeof ev.tilesJson === 'string'
        ? ev.tilesJson
        : (typeof ev.tiles === 'string' ? ev.tiles : ''),
      timestampUtc: typeof ev.timestampUtc === 'string'
        ? ev.timestampUtc
        : (typeof ev.timestamp === 'string' ? ev.timestamp : ''),
    });
  }
  // Optional handHistory (Phase J Wave 3 shape).
  let handHistory: HandResultEntry[] | undefined;
  if (Array.isArray(o.handHistory)) {
    handHistory = [];
    for (const h of o.handHistory) {
      if (h === null || typeof h !== 'object') continue;
      const hh = h as Record<string, unknown>;
      handHistory.push({
        winner: typeof hh.winner === 'number' ? hh.winner : -1,
        type: (hh.type === 'Hu' || hh.type === 'Draw' || hh.type === 'ZhaHu')
          ? hh.type
          : 'Draw',
        score: Array.isArray(hh.score) ? hh.score as HandResultEntry['score'] : [],
        hand: Array.isArray(hh.hand) ? hh.hand as number[] : [],
        nextBanker: typeof hh.nextBanker === 'number' ? hh.nextBanker : 0,
      });
    }
  }
  return { gameId, events, handHistory };
}

/** Parse a tilesJson payload from a server event.  Best-effort. */
export function parseTilesJson(json: string): number[] {
  if (json === '' || json === undefined) return [];
  try {
    const v = JSON.parse(json) as unknown;
    if (Array.isArray(v)) {
      const out: number[] = [];
      for (const t of v) {
        if (typeof t === 'number' && isFinite(t)) out.push(t);
      }
      return out;
    }
  } catch { /* ignore */ }
  return [];
}
