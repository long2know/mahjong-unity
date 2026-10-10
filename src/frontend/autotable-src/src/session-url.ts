// #153 (Ferro) — Default-game URL helpers.
//
// A bare / default navigation (e.g. `/autotable/` or `/autotable/?variant=
// changsha`) carries no concrete `?gameId=`.  Before this module the connect
// path fell back to the shared legacy sentinel `changsha-default`, so every
// fresh visitor JOINed one long-lived room and inherited its stale
// seat/turn/deal state (Hudson's #153 diagnosis: "restart → Take Seat →
// nothing progresses").  It also omitted `dealMode` / `botCount` from the
// handshake, so the runtime silently ran the Manual ceremony while the lobby
// UI implied Auto.
//
// This helper mints a fresh, non-colliding game id and produces an explicit,
// honest "New Game" URL that carries the same defaults the lobby's Apply &
// Start emits (variant / dealMode / botCount / botDifficulty / handCount).
// The connect path redirects a default navigation through this URL so the
// whole page (renderer config display AND the WS handshake) reads one
// consistent, authoritative source of truth.
//
// A URL that already carries a concrete `?gameId=` is a deliberate
// reload / reconnect / shared-room join and is left untouched (creator-wins
// semantics + reconnect are preserved).

// The legacy shared sentinel.  A default navigation must never silently
// resolve to this; only an explicit user-typed / deep-linked gameId may.
import { DEFAULT_BASE_UNIT } from './base-unit';

export const DEFAULT_GAME_ID = 'changsha-default';
export const NEW_GAME_INTENT_PARAM = 'createGame';

// New-game defaults — kept in lock-step with lobby.ts DEFAULTS so the bare
// Connect path and the lobby's Apply & Start land on identical config.
export const NEW_GAME_DEFAULTS = {
  variant: 'changsha',
  dealMode: 'auto',
  botCount: 3,
  botDifficulty: 'Hard',
  handCount: 4,
  baseUnit: DEFAULT_BASE_UNIT,
} as const;

export function hasNewGameIntent(search: string, gameId: string): boolean {
  const params = new URLSearchParams(search);
  return (params.get('variant') ?? NEW_GAME_DEFAULTS.variant).toLowerCase() === 'changsha'
    && readConcreteGameId(search) === gameId
    && params.getAll(NEW_GAME_INTENT_PARAM).length === 1
    && params.get(NEW_GAME_INTENT_PARAM) === gameId;
}

// Frontend-only intent, bound to the minted alias. The WS still uses normal NEW.
export function setNewGameIntent(params: URLSearchParams, fresh: boolean): void {
  const gameId = params.get('gameId');
  if (fresh && gameId !== null && gameId !== ''
      && (params.get('variant') ?? NEW_GAME_DEFAULTS.variant).toLowerCase() === 'changsha') {
    params.set(NEW_GAME_INTENT_PARAM, gameId);
  } else {
    params.delete(NEW_GAME_INTENT_PARAM);
  }
}

// Mint a fresh `changsha-<8 hex>` game id.  Prefers crypto.randomUUID, then
// crypto.getRandomValues, then Math.random — never throws on legacy / non-
// secure contexts.  Mirrors lobby.ts mintFreshGameId so both entry points
// produce the same id shape.
export function mintFreshGameId(): string {
  let hex = '';
  try {
    const c = (window as { crypto?: Crypto }).crypto;
    if (c !== undefined && typeof c.randomUUID === 'function') {
      hex = c.randomUUID().replace(/-/g, '').slice(0, 8);
    } else if (c !== undefined && typeof c.getRandomValues === 'function') {
      const buf = new Uint8Array(4);
      c.getRandomValues(buf);
      hex = Array.from(buf, b => b.toString(16).padStart(2, '0')).join('');
    }
  } catch { /* fall through to Math.random fallback */ }
  if (hex === '') {
    hex = Math.floor(Math.random() * 0x100000000).toString(16).padStart(8, '0');
  }
  return `changsha-${hex}`;
}

// Read a concrete, non-empty gameId from a query string.  Returns null when
// the param is absent or blank.  The legacy `changsha-default` sentinel only
// counts as "concrete" when it is literally present in the URL (an explicit
// join), never as an implicit fallback.
export function readConcreteGameId(search: string): string | null {
  const raw = new URLSearchParams(search).get('gameId');
  if (raw === null) return null;
  const trimmed = raw.trim();
  return trimmed === '' ? null : trimmed;
}

// True when the current navigation already targets a concrete game (deliberate
// reload / reconnect / shared-room join).
export function hasConcreteGameId(search: string): boolean {
  return readConcreteGameId(search) !== null;
}

// Stuck-turn fix (Hicks) — game-defining config used to decide whether an
// "Apply & Start" / variant switch is a *reconfiguration* (⇒ mint a fresh,
// isolated gameId) or an *unchanged reload* (⇒ reuse the gameId so a deliberate
// reconnect to the same concrete game is preserved).
//
// Why this matters (Ripley Design Review defect 1 / Hudson stale-gameId reuse):
// changing variant/config while a concrete `?gameId=` is on the URL used to
// keep that gameId, so Apply re-JOINed the SAME already-running runtime game
// (whose seats may be owned by other/absent connections) — the server ignores
// the new config for an existing game, stranding the viewer in a stalled hand
// with no open seat.  Reconfiguration must therefore start a new game.
//
// Seat choice is intentionally NOT part of this comparison — it is a per-viewer
// join preference, not a property of the game.
export interface GameDefiningConfig {
  variant: string;
  dealMode?: string;        // Changsha-only; ignored for other variants
  botCount: number;
  botDifficulty?: string;   // only meaningful when botCount > 0
  handCount: number;
  seed?: number | null;
  baseUnit?: number;
}

interface CanonicalConfig {
  variant: string;
  dealMode: string;
  botCount: number;
  botDifficulty: string;
  handCount: number;
  seed: number | null;
  baseUnit: number;
}

function canonicalizeConfig(src: GameDefiningConfig): CanonicalConfig {
  const variant = src.variant || NEW_GAME_DEFAULTS.variant;
  const botCount = src.botCount;
  return {
    variant,
    // dealMode only affects Changsha; blank it out elsewhere so a Riichi
    // reload is never spuriously treated as a reconfiguration.
    dealMode: variant === 'changsha' ? (src.dealMode || NEW_GAME_DEFAULTS.dealMode) : '',
    botCount,
    botDifficulty: botCount > 0 ? (src.botDifficulty || NEW_GAME_DEFAULTS.botDifficulty) : '',
    handCount: src.handCount,
    seed: src.seed ?? null,
    baseUnit: variant === 'changsha' ? (src.baseUnit ?? DEFAULT_BASE_UNIT) : DEFAULT_BASE_UNIT,
  };
}

function canonicalConfigFromSearch(search: string): CanonicalConfig {
  const p = new URLSearchParams(search);
  const variant = p.get('variant') || NEW_GAME_DEFAULTS.variant;
  const botCount = p.get('bots') === 'false' ? 0 : p.has('botCount')
    ? Number(p.get('botCount'))
    : NEW_GAME_DEFAULTS.botCount;
  const handCount = p.has('handCount')
    ? Number(p.get('handCount'))
    : NEW_GAME_DEFAULTS.handCount;
  const seedRaw = p.get('seed');
  return canonicalizeConfig({
    variant,
    dealMode: p.get('dealMode') ?? undefined,
    botCount,
    botDifficulty: p.get('botDifficulty') ?? undefined,
    handCount,
    seed: seedRaw === null || seedRaw.trim() === '' ? null : Number(seedRaw),
    baseUnit: p.has('baseUnit') ? Number(p.get('baseUnit')) : DEFAULT_BASE_UNIT,
  });
}

// True when `cfg` differs from the game-defining config already encoded on the
// current URL (defaults normalised on both sides so an omitted param never
// reads as a change).
export function gameConfigDiffersFromUrl(search: string, cfg: GameDefiningConfig): boolean {
  const a = canonicalConfigFromSearch(search);
  const b = canonicalizeConfig(cfg);
  return (
    a.variant !== b.variant ||
    a.dealMode !== b.dealMode ||
    a.botCount !== b.botCount ||
    a.botDifficulty !== b.botDifficulty ||
    a.handCount !== b.handCount ||
    a.seed !== b.seed ||
    a.baseUnit !== b.baseUnit
  );
}

// Decide the gameId an Apply/variant-switch should target:
//   • No concrete gameId on the URL            ⇒ mint a fresh one (New Game).
//   • Concrete gameId + config CHANGED         ⇒ mint a fresh one (fresh game;
//     never silently re-open a stranger's in-progress game).
//   • Concrete gameId + config UNCHANGED       ⇒ reuse it (deliberate reconnect
//     to the same concrete game is preserved).
// `mint` is injectable so the contract test is deterministic.
export function resolveApplyGameId(
  search: string,
  cfg: GameDefiningConfig,
  mint: () => string = mintFreshGameId,
): string {
  const current = readConcreteGameId(search);
  if (current === null) return mint();
  if (gameConfigDiffersFromUrl(search, cfg)) return mint();
  return current;
}

// Build an explicit, honest "New Game" URL for a default navigation: mint a
// fresh gameId and fill in any config params the current URL is missing with
// the shared New Game defaults.  Params the user already set on the URL are
// preserved verbatim so a partial deep-link (e.g. `?dealMode=manual`) keeps
// the user's intent while still gaining a fresh, isolated gameId.
export function buildFreshGameUrl(
  pathname: string,
  search: string,
  gameId: string = mintFreshGameId(),
  seat?: number | null,
): string {
  const p = new URLSearchParams(search);
  p.delete('join');
  p.set('gameId', gameId);
  if (!p.has('variant')) p.set('variant', NEW_GAME_DEFAULTS.variant);
  // dealMode is Changsha-only; only stamp it for the Changsha variant so
  // Riichi deep-links stay tidy (they ignore dealMode server-side).
  if (p.get('variant') === 'changsha' && !p.has('dealMode')) {
    p.set('dealMode', NEW_GAME_DEFAULTS.dealMode);
  }
  if (p.get('variant') === 'changsha' && !p.has('baseUnit')) {
    p.set('baseUnit', String(DEFAULT_BASE_UNIT));
  }
  if (!p.has('botCount')) p.set('botCount', String(NEW_GAME_DEFAULTS.botCount));
  // botDifficulty is meaningless with zero bots — only stamp it when bots
  // will actually fill seats.
  const botCount = Number(p.get('botCount'));
  if (botCount > 0 && !p.has('botDifficulty')) {
    p.set('botDifficulty', NEW_GAME_DEFAULTS.botDifficulty);
  }
  if (!p.has('handCount')) p.set('handCount', String(NEW_GAME_DEFAULTS.handCount));
  // New Game UX P0 — SEAT HANDOFF. When the caller owns a real seat (0..3),
  // stamp `?seat=` so the fresh empty game auto-takes THAT chair from one click
  // (the WS handshake forwards it; the backend seats the human there, auto-seats
  // bots, and — in Auto — auto-deals to a playable hand). `-1` (explicit
  // spectator) is honoured; when `seat` is undefined/null the URL's existing
  // `seat` (if any) is preserved verbatim.
  if (seat !== undefined && seat !== null && (seat >= 0 || seat === -1)) {
    p.set('seat', String(seat));
  }
  setNewGameIntent(p, true);
  return `${pathname}?${p.toString()}`;
}

// New Game UX P0 — resolve WHICH seat the one-click New Game hands off to the
// fresh game, so the user keeps the SAME chair with no re-pick / extra clicks:
//   • the caller's LIVE seat when currently seated (0..3, or -1 explicit
//     spectator);
//   • else the pre-disconnect seat (a New Game clicked while disconnected still
//     re-takes the same chair);
//   • else null ⇒ no owned seat, let the fresh game assign / spectate.
// Pure ⇒ browser-free contract-tested; `newGame()` feeds the result to
// `buildFreshGameUrl(..., seat)`.
export function resolveHandoffSeat(
  liveSeat: number | null | undefined,
  reconnectSeat: number | null | undefined,
): number | null {
  if (liveSeat !== null && liveSeat !== undefined) return liveSeat;
  if (reconnectSeat !== null && reconnectSeat !== undefined) return reconnectSeat;
  return null;
}
