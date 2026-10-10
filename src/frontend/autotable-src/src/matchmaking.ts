// Phase J Wave 5 — Public matchmaking lobby module.
//
// Polls Bishop's `GET /api/matchmaking/lobby` endpoint every 5 s while
// the "Public Games" tab is visible.  Each entry renders as a card
// with a Join button.  The "Join Random" button invokes the SignalR
// hub's non-seating `FindJoinableGame` RPC; the host's "Make public" toggle invokes
// `SetGamePublic`.
//
// Tab activation / deactivation is driven by lobby.ts (Public Games
// vs My Game).  We expose start/stop helpers so the poll only runs
// while the user is actually looking at the list.
//
// ── Wire contract (Bishop, Phase J Wave 5) ──────────────────────────
//
//   GET /api/matchmaking/lobby   (MatchmakingController.cs)
//     → 200 { games: PublicGame[] }
//
//   PublicGame (LobbyGameDto.cs) = {
//     gameId: string;
//     publicName: string | null;
//     creatorDisplayName: string;
//     seatedCount: number;
//     maxSeats: number;
//     variant: string;
//     createdAt: string;       // ISO-8601 UTC
//   }
//
//   SignalR invoke 'FindJoinableGame'(variant?)
//     → { matched: true, gameId }
//     → { matched: false }
//
//   SignalR invoke 'SetGamePublic'(gameId, isPublic, publicName?)
//     → { success, isPublic, publicName }
//     Throws HubException when the caller isn't the host or the game
//     has left the Seating phase.

import { EventEmitter } from 'events';

import { invokeHub } from './hub';
import { buildRoomJoinUrl } from './room-join-url';

export interface PublicGame {
  gameId: string;
  publicName: string | null;
  creatorDisplayName: string;
  seatedCount: number;
  botCount: number;
  openHumanSeats: number;
  maxSeats: number;
  variant: string;
  createdAt: string;
}

export const MATCHMAKING_POLL_MS = 5000;
export const MAX_PUBLIC_GAMES_RENDERED = 50;

const events = new EventEmitter();
let cache: ReadonlyArray<PublicGame> = [];
let lastError: string | null = null;
let pollTimer: number | null = null;
let inflight: AbortController | null = null;
let active = false;

function emitState(): void {
  events.emit('update', { games: cache, error: lastError });
}

function normalizePublicGame(g: unknown): PublicGame {
  if (g === null || typeof g !== 'object') throw new Error('Invalid public table response.');
  const o = g as Record<string, unknown>;
  if (typeof o.gameId !== 'string' || o.gameId === ''
      || typeof o.seatedCount !== 'number' || !Number.isInteger(o.seatedCount)
      || typeof o.botCount !== 'number' || !Number.isInteger(o.botCount)
      || typeof o.openHumanSeats !== 'number' || !Number.isInteger(o.openHumanSeats)
      || o.seatedCount < 0 || o.botCount < 0 || o.openHumanSeats < 0
      || o.seatedCount > 4 || o.botCount > 4 || o.openHumanSeats > 4) {
    throw new Error('Public table availability does not match the multiplayer contract.');
  }
  buildRoomJoinUrl(o.gameId);
  const publicName = typeof o.publicName === 'string' ? o.publicName : null;
  const creatorDisplayName = typeof o.creatorDisplayName === 'string' ? o.creatorDisplayName : 'Unknown';
  const seatedCount = o.seatedCount;
  const maxSeats = typeof o.maxSeats === 'number' ? o.maxSeats : 4;
  const variant = typeof o.variant === 'string' ? o.variant : 'changsha';
  const createdAt = typeof o.createdAt === 'string' ? o.createdAt : new Date().toISOString();
  return {
    gameId: o.gameId, publicName, creatorDisplayName, seatedCount,
    botCount: o.botCount, openHumanSeats: o.openHumanSeats, maxSeats, variant, createdAt,
  };
}

async function pollOnce(): Promise<void> {
  if (inflight !== null) {
    inflight.abort();
  }
  const ctrl = new AbortController();
  inflight = ctrl;
  try {
    const resp = await fetch('/api/matchmaking/lobby', {
      credentials: 'same-origin',
      signal: ctrl.signal,
    });
    if (!resp.ok) {
      lastError = `HTTP ${resp.status}`;
      emitState();
      return;
    }
    const body = (await resp.json()) as unknown;
    const raw = body !== null && typeof body === 'object' ? (body as { games?: unknown }).games : null;
    if (!Array.isArray(raw)) throw new Error('Public table response omitted games.');
    const games: Array<PublicGame> = [];
    for (const g of raw.slice(0, MAX_PUBLIC_GAMES_RENDERED)) {
      games.push(normalizePublicGame(g));
    }
    if (ctrl.signal.aborted) return;
    cache = games;
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

/** Start the 5-second poll loop.  Idempotent. */
export function startPolling(): void {
  if (active) return;
  active = true;
  void pollOnce();
  pollTimer = window.setInterval(() => { void pollOnce(); }, MATCHMAKING_POLL_MS);
}

/** Stop the poll loop and cancel any in-flight request. */
export function stopPolling(): void {
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

export function getCachedGames(): ReadonlyArray<PublicGame> {
  return cache;
}

export function getLastError(): string | null {
  return lastError;
}

/** Subscribe to poll updates.  Returns an unsubscribe handle. */
export function onUpdate(
  handler: (state: { games: ReadonlyArray<PublicGame>; error: string | null }) => void,
): () => void {
  events.on('update', handler);
  handler({ games: cache, error: lastError });
  return () => events.off('update', handler);
}

/** One-shot manual refresh (e.g. on tab activate). */
export function refresh(): Promise<void> {
  return pollOnce();
}

// ── Action helpers ─────────────────────────────────────────────────

export interface JoinRandomResult {
  gameId: string;
}

/**
 * Ask the SignalR hub for any joinable public game.  Returns null
 * when the hub responds `{ matched: false }`.  Throws on transport
 * errors so the caller can surface an inline error toast.
 */
export async function joinRandom(variant?: string): Promise<JoinRandomResult | null> {
  const result = await invokeHub<unknown>('FindJoinableGame', variant ?? null);
  if (result === null || typeof result !== 'object') throw new Error('Invalid matchmaking response.');
  const o = result as Record<string, unknown>;
  if (o.matched === false) return null;
  if (o.matched !== true || typeof o.gameId !== 'string') throw new Error('Invalid matchmaking response.');
  buildRoomJoinUrl(o.gameId);
  return { gameId: o.gameId };
}

/**
 * Flip the current game's "public" flag via the SignalR hub.  Returns
 * the server's `{ success, isPublic, publicName }` payload so the UI
 * can echo the final state (which may differ from the requested
 * publicName if the server normalised / truncated it).  Caller is
 * responsible for passing the gameId of a game they actually host —
 * Bishop's hub rejects unauthorised flips with a HubException.
 */
export interface SetGamePublicResult {
  success: true;
  gameId: string;
  isPublic: boolean;
  publicName: string | null;
}

export async function setGamePublic(args: {
  gameId: string;
  isPublic: boolean;
  publicName?: string;
}): Promise<SetGamePublicResult> {
  const result = await invokeHub<unknown>(
    'SetGamePublic',
    args.gameId,
    args.isPublic,
    args.publicName ?? null,
  );
  if (result === null || typeof result !== 'object') {
    throw new Error('Invalid SetGamePublic response.');
  }
  const o = result as Record<string, unknown>;
  if (o.success !== true || typeof o.gameId !== 'string' || typeof o.isPublic !== 'boolean'
      || (o.publicName !== null && typeof o.publicName !== 'string')) {
    throw new Error(typeof o.reason === 'string' ? o.reason : 'Server did not confirm the public-table change.');
  }
  if (active) void pollOnce();
  return {
    success: true,
    gameId: o.gameId,
    isPublic: o.isPublic,
    publicName: o.publicName,
  };
}

// ── Navigation helper ──────────────────────────────────────────────

/**
 * Cards and random selection use existing-only admission, never a hub seat
 * that would be discarded during navigation.
 */
export function navigateToGame(gameId: string): void {
  window.location.replace(buildRoomJoinUrl(gameId));
}
