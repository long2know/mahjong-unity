// Phase J Wave 4 — Reconnect token manager.
//
// Layered on top of the Wave-2 exponential-backoff reconnect loop in
// client-ui.ts.  The token lets us preserve a player's session across:
//
//   1. A page refresh / browser crash mid-game (URL-based rejoin).
//   2. A clean close-and-reopen within ~5 min (localStorage rejoin).
//
// Wire contract (deliberately opaque to the server — Bishop hasn't
// shipped a token system yet):
//
//   • Stored at  localStorage["mahjong:lastSession:" + gameId]
//   • URL form   ?rejoin=<token>   (token = base64url-encoded JSON)
//   • Payload    { v:1, gameId, playerId, seat, connectionId?, savedAt }
//
// Backend treats the rejoin URL as a hint: it accepts the implied seat
// when the seat is still empty in the recorded gameId, rejects when
// taken.  The frontend interprets failure (banner stays red after every
// reconnect attempt) as "your previous session has ended" and falls
// through to the normal lobby flow.
//
// Module is intentionally framework-free + side-effect free at module
// scope (no auto-wiring) so unit tests can mock localStorage / URL
// without booting the whole bundle.

export const SESSION_KEY_PREFIX = 'mahjong:lastSession:';

// Tokens older than this on read are silently discarded.  Five-minute
// window matches the directive's "reasonable window (~5 min)" acceptance
// criterion.  Bumping this is safe because the backend re-validates by
// trying to seat the player and will reject a stale seat-taken case.
export const TOKEN_TTL_MS = 5 * 60 * 1000;

// Schema version — bump on breaking changes to the payload shape so a
// pre-version-N token from an older bundle gets discarded cleanly instead
// of mis-parsed.
const TOKEN_VERSION = 1;

// Best-effort cap to prevent localStorage abuse on hand-typed tokens.
// A real payload is ~120 bytes; the cap rejects obvious garbage early.
const MAX_TOKEN_LENGTH = 2048;

export interface SessionToken {
  v: number;
  gameId: string;
  playerId: string;
  seat: number | null;
  // SignalR connection id when known.  Wave-2 / Wave-4 backends don't
  // surface one; the frontend ships the playerId instead and the field
  // stays here for future Bishop integrations.
  connectionId?: string | null;
  savedAt: number;
}

export interface RejoinUrlInfo {
  token: string;
  decoded: SessionToken;
}

// base64url encode/decode helpers — bundled here so the module has zero
// dependencies on the rest of the codebase.
function b64UrlEncode(s: string): string {
  // btoa accepts a binary string; encodeURIComponent → bytes round-trip
  // keeps Unicode safe in the payload.
  const b64 = btoa(unescape(encodeURIComponent(s)));
  return b64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function b64UrlDecode(s: string): string | null {
  try {
    const b64 = s.replace(/-/g, '+').replace(/_/g, '/');
    // Re-pad so atob accepts the URL-safe variant.
    const pad = b64.length % 4 === 0 ? '' : '='.repeat(4 - (b64.length % 4));
    return decodeURIComponent(escape(atob(b64 + pad)));
  } catch {
    return null;
  }
}

export function encodeToken(token: SessionToken): string {
  return b64UrlEncode(JSON.stringify(token));
}

export function decodeToken(raw: string): SessionToken | null {
  if (typeof raw !== 'string' || raw.length === 0 || raw.length > MAX_TOKEN_LENGTH) {
    return null;
  }
  const json = b64UrlDecode(raw);
  if (json === null) return null;
  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch {
    return null;
  }
  if (typeof parsed !== 'object' || parsed === null) return null;
  const obj = parsed as Record<string, unknown>;
  if (typeof obj.v !== 'number' || obj.v !== TOKEN_VERSION) return null;
  if (typeof obj.gameId !== 'string' || obj.gameId.length === 0) return null;
  if (typeof obj.playerId !== 'string' || obj.playerId.length === 0) return null;
  if (typeof obj.savedAt !== 'number' || !isFinite(obj.savedAt)) return null;
  const seat = obj.seat;
  if (!(seat === null || (typeof seat === 'number' && seat >= -1 && seat <= 3))) {
    return null;
  }
  let connectionId: string | null | undefined;
  if (obj.connectionId === undefined) {
    connectionId = undefined;
  } else if (obj.connectionId === null) {
    connectionId = null;
  } else if (typeof obj.connectionId === 'string') {
    connectionId = obj.connectionId;
  } else {
    return null;
  }
  return {
    v: TOKEN_VERSION,
    gameId: obj.gameId,
    playerId: obj.playerId,
    seat: seat as number | null,
    connectionId,
    savedAt: obj.savedAt,
  };
}

// Persist the active session so that a refresh / re-open within the TTL
// can rejoin without bouncing through the lobby.  Idempotent: safe to
// call from onConnect and on every seat change.
export function saveSession(payload: Omit<SessionToken, 'v' | 'savedAt'>): SessionToken | null {
  const token: SessionToken = {
    v: TOKEN_VERSION,
    gameId: payload.gameId,
    playerId: payload.playerId,
    seat: payload.seat,
    connectionId: payload.connectionId,
    savedAt: Date.now(),
  };
  try {
    window.localStorage.setItem(
      SESSION_KEY_PREFIX + payload.gameId,
      JSON.stringify(token));
  } catch {
    // Privacy mode / quota — silently ignore.  The Wave-2 reconnect
    // loop still works inside the same page, just the cross-tab /
    // cross-refresh handoff degrades.
  }
  return token;
}

export function readSession(gameId: string): SessionToken | null {
  try {
    const raw = window.localStorage.getItem(SESSION_KEY_PREFIX + gameId);
    if (raw === null) return null;
    const parsed = JSON.parse(raw) as Record<string, unknown>;
    if (typeof parsed !== 'object' || parsed === null) return null;
    if (parsed.v !== TOKEN_VERSION) return null;
    if (typeof parsed.savedAt !== 'number') return null;
    if (Date.now() - parsed.savedAt > TOKEN_TTL_MS) {
      // Stale — clean up the dead entry so it doesn't accumulate.
      clearSession(gameId);
      return null;
    }
    if (typeof parsed.gameId !== 'string' || typeof parsed.playerId !== 'string') {
      return null;
    }
    const seat = parsed.seat;
    if (!(seat === null || (typeof seat === 'number' && seat >= -1 && seat <= 3))) {
      return null;
    }
    return {
      v: TOKEN_VERSION,
      gameId: parsed.gameId,
      playerId: parsed.playerId,
      seat: seat as number | null,
      connectionId: (typeof parsed.connectionId === 'string'
        ? parsed.connectionId
        : (parsed.connectionId === null ? null : undefined)),
      savedAt: parsed.savedAt,
    };
  } catch {
    return null;
  }
}

export function clearSession(gameId: string): void {
  try {
    window.localStorage.removeItem(SESSION_KEY_PREFIX + gameId);
  } catch {
    // ignore
  }
}

// Build the "Copy rejoin link" URL: current page URL with ?rejoin=<token>
// appended (replacing any previous rejoin param).  We intentionally
// preserve every other query param (gameId, variant, seat preference, …)
// so a returning click lands on the same picker state the user had
// before the disconnect.
export function buildRejoinUrl(token: SessionToken, baseUrl?: string): string {
  const base = baseUrl ?? window.location.href;
  let url: URL;
  try {
    url = new URL(base);
  } catch {
    url = new URL(window.location.href);
  }
  url.searchParams.set('rejoin', encodeToken(token));
  return url.toString();
}

// Parse ?rejoin=<token> off the page URL.  Returns null when the param
// is absent, malformed, or expired.  Callers should clear the param
// from the page URL after consuming it so a second refresh doesn't
// re-trigger the rejoin path with a stale token.
export function parseRejoinFromUrl(search?: string): RejoinUrlInfo | null {
  const q = new URLSearchParams(search ?? window.location.search);
  const raw = q.get('rejoin');
  if (raw === null || raw === '') return null;
  const decoded = decodeToken(raw);
  if (decoded === null) return null;
  if (Date.now() - decoded.savedAt > TOKEN_TTL_MS) return null;
  return { token: raw, decoded };
}

// Strip the ?rejoin= param from the address bar after we've consumed it,
// leaving every other query param intact.  Uses history.replaceState so
// the back button still works for whatever route the user came from.
export function clearRejoinFromUrl(): void {
  try {
    const url = new URL(window.location.href);
    if (!url.searchParams.has('rejoin')) return;
    url.searchParams.delete('rejoin');
    const query = url.searchParams.toString();
    const newUrl = url.pathname + (query ? '?' + query : '') + url.hash;
    window.history.replaceState(undefined, '', newUrl);
  } catch {
    // ignore
  }
}

// Apply a decoded rejoin token to the page URL so the existing
// client-ui boot path picks up the right gameId + seat.  We DON'T
// remove the lobby panel here — the caller is expected to short-circuit
// the lobby-on-first-load gate when a rejoin landed.
export function applyTokenToUrl(decoded: SessionToken): void {
  try {
    const url = new URL(window.location.href);
    url.searchParams.set('gameId', decoded.gameId);
    if (decoded.seat !== null) {
      url.searchParams.set('seat', String(decoded.seat));
    }
    // Drop the rejoin param after applying so subsequent reloads aren't
    // stuck in a rejoin loop if the rejoin succeeds.
    url.searchParams.delete('rejoin');
    const query = url.searchParams.toString();
    const newUrl = url.pathname + (query ? '?' + query : '') + url.hash;
    window.history.replaceState(undefined, '', newUrl);
  } catch {
    // ignore — boot path will fall through to the lobby.
  }
}
