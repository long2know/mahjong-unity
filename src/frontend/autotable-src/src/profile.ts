// Phase J Wave 5 — Player profile module.
//
// Owns the in-memory player profile cache (display name + avatar colour
// + the running per-player career stats Bishop persists on the
// backend).  Exposes a tiny API the lobby / profile drawer / move-log
// / end-of-game modal can lean on without each surface re-fetching
// state.
//
// ── Wire contract (Bishop, Phase J Wave 5) ──────────────────────────
//
// Profile traffic flows over Bishop's SignalR ChangshaHub
// (`/hubs/changsha`).  See ChangshaHub.cs:117–131 for the canonical
// shapes:
//
//   • Server → Client event 'ProfileLoaded' fires once on connect (and
//     after every UpdateProfile RPC the client makes).  Payload shape:
//
//       {
//         playerId, displayName, avatarColor,
//         createdAt, lastSeenAt,
//         stats: {
//           gamesPlayed, gamesWon,
//           totalScore, highestSingleGameScore,
//           longestWinStreak, currentWinStreak,
//           lastGameAt
//         }
//       }
//
//   • Client → Server invoke 'UpdateProfile'(displayName, avatarColor?)
//     returns the same DTO.
//
// Identity note: `playerId` in the profile DTO is the SignalR
// `Context.ConnectionId` (PlayerProfile.cs:11–15).  This is a
// different identifier from the autotable WS playerId used in the
// `nicks` / `seats` collections — we treat the two as parallel.  The
// move-log / lobby chips read `client.nicks` (which each tab writes
// to with its own profile.displayName on profile change), so the
// cross-identity mismatch never surfaces in the UI.
//
// ── Stats snapshot for delta rendering ──────────────────────────────
//
// `snapshotStatsForGame()` captures the current stats so the
// post-game modal can render a before/after delta.  Called from
// client.ts on the `connect` event (per-game lifecycle); read by
// stats.ts:formatStatsDelta() after the game completes.

import { EventEmitter } from 'events';

import {
  getHubConnection,
  invokeHub,
  onHubConnectionCreated,
} from './hub';

// ── Public types ─────────────────────────────────────────────────────

/**
 * Career stats the frontend cares about.  Field names are
 * normalised — Bishop's wire DTO uses `longestWinStreak` /
 * `currentWinStreak` / `highestSingleGameScore`, we map them to
 * shorter names here so the lobby / post-game modal don't have to
 * lug the verbose wire shape around.
 */
export interface PlayerStats {
  gamesPlayed: number;
  gamesWon: number;
  longestStreak: number;
  currentStreak: number;
  highestScore: number;
}

export interface PlayerProfile {
  playerId: string;
  displayName: string;
  avatarColor: string;
  stats: PlayerStats;
}

// ── Constants ────────────────────────────────────────────────────────

// Preset avatar colours surfaced in the profile drawer.  Chosen so
// each is legibly dark enough to host the white initials text.
export const AVATAR_COLOR_PRESETS: ReadonlyArray<string> = [
  '#c0392b', // red
  '#e67e22', // orange
  '#f1c40f', // yellow
  '#2ecc71', // green
  '#16a085', // teal
  '#2980b9', // blue
  '#8e44ad', // purple
  '#34495e', // slate
];

export const DEFAULT_PROFILE: PlayerProfile = {
  playerId: '',
  displayName: '',
  avatarColor: AVATAR_COLOR_PRESETS[5],
  stats: { gamesPlayed: 0, gamesWon: 0, longestStreak: 0, currentStreak: 0, highestScore: 0 },
};

const LS_KEY_PROFILE_CACHE = 'mahjong.profile.cache.v1';

// Directive: 1–32 chars, no leading/trailing whitespace.  Matches
// PlayerProfileService.UpdateDisplayNameAsync.
export const DISPLAY_NAME_MIN = 1;
export const DISPLAY_NAME_MAX = 32;

// #rgb / #rrggbb (case-insensitive).  Bishop's backend only accepts
// #RRGGBB so we expand 3-char shortcuts before sending.
const HEX_COLOR_RE = /^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$/;

const DISPLAY_NAME_DEBOUNCE_MS = 500;

// ── Module state ─────────────────────────────────────────────────────

const events = new EventEmitter();
let current: PlayerProfile | null = null;
let preGameSnapshot: PlayerStats | null = null;

// ── Validation helpers ──────────────────────────────────────────────

export function validateDisplayName(raw: string): { value: string | null; error: string | null } {
  const trimmed = raw.trim();
  if (trimmed !== raw) {
    return { value: null, error: 'No leading or trailing whitespace.' };
  }
  if (trimmed.length < DISPLAY_NAME_MIN) {
    return { value: null, error: 'Display name cannot be empty.' };
  }
  if (trimmed.length > DISPLAY_NAME_MAX) {
    return { value: null, error: `Display name must be ${DISPLAY_NAME_MAX} characters or fewer.` };
  }
  return { value: trimmed, error: null };
}

export function validateAvatarColor(raw: string): boolean {
  return HEX_COLOR_RE.test(raw);
}

// ── localStorage cache (best-effort; private mode tolerated) ────────

function loadCache(): PlayerProfile | null {
  try {
    const raw = window.localStorage.getItem(LS_KEY_PROFILE_CACHE);
    if (raw === null) return null;
    const j = JSON.parse(raw) as Partial<PlayerProfile>;
    if (typeof j.playerId !== 'string') return null;
    return {
      playerId: j.playerId,
      displayName: typeof j.displayName === 'string' ? j.displayName : '',
      avatarColor:
        typeof j.avatarColor === 'string' && validateAvatarColor(j.avatarColor)
          ? j.avatarColor
          : DEFAULT_PROFILE.avatarColor,
      stats: normalizeStats((j as { stats?: unknown }).stats),
    };
  } catch {
    return null;
  }
}

function writeCache(p: PlayerProfile): void {
  try {
    window.localStorage.setItem(LS_KEY_PROFILE_CACHE, JSON.stringify(p));
  } catch {
    /* private mode / quota — skip */
  }
}

function normalizeStats(s: unknown): PlayerStats {
  const base = { ...DEFAULT_PROFILE.stats };
  if (s !== null && typeof s === 'object') {
    const o = s as Record<string, unknown>;
    // Wire-name → internal mapping (Bishop's verbose shape ⟶ ours).
    if (typeof o.gamesPlayed === 'number') base.gamesPlayed = o.gamesPlayed;
    if (typeof o.gamesWon === 'number') base.gamesWon = o.gamesWon;
    if (typeof o.longestWinStreak === 'number') base.longestStreak = o.longestWinStreak;
    if (typeof o.currentWinStreak === 'number') base.currentStreak = o.currentWinStreak;
    if (typeof o.highestSingleGameScore === 'number') base.highestScore = o.highestSingleGameScore;
    // Tolerate the short form too in case Bishop ships an alias.
    if (typeof o.longestStreak === 'number') base.longestStreak = o.longestStreak;
    if (typeof o.currentStreak === 'number') base.currentStreak = o.currentStreak;
    if (typeof o.highestScore === 'number') base.highestScore = o.highestScore;
  }
  return base;
}

function normalizeProfile(raw: unknown, fallbackPlayerId: string): PlayerProfile {
  const o = (raw !== null && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
  const playerId =
    typeof o.playerId === 'string' && o.playerId !== '' ? o.playerId : fallbackPlayerId;
  const displayName = typeof o.displayName === 'string' ? o.displayName : '';
  const avatarColor =
    typeof o.avatarColor === 'string' && validateAvatarColor(o.avatarColor)
      ? o.avatarColor
      : DEFAULT_PROFILE.avatarColor;
  const stats = normalizeStats(o.stats);
  return { playerId, displayName, avatarColor, stats };
}

// Stable per-playerId default colour for the placeholder profile
// shown before the hub responds.  djb2 over the autotable WS
// playerId so the placeholder colour matches the existing lobby chip
// strip colour.
function defaultColorForId(playerId: string): string {
  if (playerId === '') return DEFAULT_PROFILE.avatarColor;
  let hash = 5381;
  for (let i = 0; i < playerId.length; i++) {
    hash = ((hash << 5) + hash) + playerId.charCodeAt(i);
    hash &= 0xffffffff;
  }
  const idx = Math.abs(hash) % AVATAR_COLOR_PRESETS.length;
  return AVATAR_COLOR_PRESETS[idx];
}

// ── Public API ──────────────────────────────────────────────────────

export function getProfile(): PlayerProfile | null {
  return current;
}

/**
 * Boot-time cache hydration.  Safe to call at lobby init — does NOT
 * touch SignalR.  If the localStorage cache holds a previous-session
 * profile, we seed `current` from it so the lobby chip + onProfile
 * subscribers see a populated state before (or even without) the hub
 * ever connecting.  Idempotent — bails out if a profile is already
 * loaded.  Phase J Wave 6 — required because the hub only connects
 * once the user enters a game (via the WS `connect` event), so
 * post-onboarding reloads would otherwise show "Profile" until the
 * first game starts.
 */
export function hydrateProfileFromCacheIfAvailable(): PlayerProfile | null {
  if (current !== null) return current;
  const cached = loadCache();
  if (cached === null) return null;
  setCurrent(cached);
  return cached;
}

/**
 * Subscribe to profile updates.  Returns an unsubscribe function.
 * Fires whenever the cached profile is replaced (cache hydration +
 * initial ProfileLoaded + UpdateProfile round-trip).  If a profile
 * is already cached, the handler fires synchronously once before
 * returning.
 */
export function onProfile(handler: (profile: PlayerProfile) => void): () => void {
  events.on('profile', handler);
  if (current !== null) handler(current);
  return () => events.off('profile', handler);
}

/**
 * Bring the profile online for `playerId`.  Hydrates from
 * localStorage immediately (so the UI is populated before the hub
 * connects) and opens / re-uses the SignalR connection.  Bishop's
 * hub emits 'ProfileLoaded' on connect — we register the listener
 * here so the event is wired before the connection completes.
 *
 * Returns the cached/synthesized profile right away.  The hub-side
 * truth arrives asynchronously via the 'ProfileLoaded' event and
 * replaces the cached value.
 */
export async function loadProfile(playerId: string): Promise<PlayerProfile> {
  // Synthesise a placeholder until the hub responds.
  if (current === null || current.playerId !== playerId) {
    const cached = loadCache();
    if (cached !== null && (cached.playerId === playerId || playerId === '' || playerId === 'offline')) {
      setCurrent(cached);
    } else {
      setCurrent(synthesizeProfile(playerId));
    }
  }

  if (playerId === '' || playerId === 'offline') {
    return current!;
  }

  try {
    const conn = await getHubConnection();
    // Idempotent: 'ProfileLoaded' is a server-driven event so the
    // listener only needs to be installed once.  Track via a module
    // flag so reconnects don't double-subscribe.
    installProfileLoadedListener();
    // Bishop's hub fires 'ProfileLoaded' on connect automatically;
    // if we re-connected and missed it, do a fresh round-trip by
    // calling UpdateProfile with the current display name (no-op).
    if (current !== null && current.displayName !== '') {
      try {
        const dto = await conn.invoke<unknown>(
          'UpdateProfile',
          current.displayName,
          null,
        );
        setCurrent(normalizeProfile(dto, playerId));
      } catch {
        /* hub round-trip failed — keep the local cached state */
      }
    }
  } catch {
    /* Hub start failed — UI uses the cached/synth profile */
  }
  return current!;
}

let profileLoadedInstalled = false;
function installProfileLoadedListener(): void {
  if (profileLoadedInstalled) return;
  profileLoadedInstalled = true;
  onHubConnectionCreated((c) => {
    c.on('ProfileLoaded', (dto: unknown) => {
      const p = normalizeProfile(dto, current?.playerId ?? '');
      setCurrent(p);
    });
  });
}

function synthesizeProfile(playerId: string): PlayerProfile {
  // Default display name = first 6 chars of the player id (visible
  // until Bishop's ProfileLoaded event lands).
  const stub = playerId === '' || playerId === 'offline'
    ? 'Guest'
    : `Player ${playerId.slice(0, 6)}`;
  return {
    playerId,
    displayName: stub,
    avatarColor: defaultColorForId(playerId),
    stats: { ...DEFAULT_PROFILE.stats },
  };
}

function setCurrent(p: PlayerProfile): void {
  current = p;
  writeCache(p);
  events.emit('profile', p);
}

// ── Mutations ───────────────────────────────────────────────────────

let pendingDisplayName: string | null = null;
let displayNameTimer: number | null = null;

export function setDisplayName(name: string): { error: string | null } {
  const { value, error } = validateDisplayName(name);
  if (error !== null) return { error };
  if (current === null) {
    // Phase K Wave 22+ — Hicks (e2e wave 27).  The profile-page edit
    // surfaces can be reached before the SignalR hub has populated a
    // ProfileLoaded event (the lobby never connects until the user
    // joins a game).  Synthesise a guest placeholder so the optimistic
    // local update + LS cache write below land — the hub round-trip,
    // when it happens, will replace `current` via setCurrent().
    setCurrent(synthesizeProfile(''));
  }
  // Optimistic local update so subscribers (lobby chips, move-log)
  // see the new name immediately.  The UpdateProfile invoke below
  // confirms with the server.
  setCurrent({ ...current!, displayName: value! });
  pendingDisplayName = value!;
  if (displayNameTimer !== null) window.clearTimeout(displayNameTimer);
  displayNameTimer = window.setTimeout(() => {
    displayNameTimer = null;
    const flush = pendingDisplayName;
    pendingDisplayName = null;
    if (flush !== null) void sendUpdateProfile({ displayName: flush });
  }, DISPLAY_NAME_DEBOUNCE_MS);
  return { error: null };
}

export function setAvatarColor(hex: string): { error: string | null } {
  if (!validateAvatarColor(hex)) {
    return { error: 'Avatar colour must be a hex code like #ff0000.' };
  }
  if (current === null) {
    // Phase K Wave 22+ — Hicks (e2e wave 27).  Same rationale as
    // setDisplayName above — synthesise a guest profile so the edit
    // path is never blocked by a pre-hub null current.
    setCurrent(synthesizeProfile(''));
  }
  const normalized = hex.toLowerCase();
  setCurrent({ ...current!, avatarColor: normalized });
  void sendUpdateProfile({ avatarColor: normalized });
  return { error: null };
}

export function resetProfile(): void {
  if (current === null) return;
  const synth = synthesizeProfile(current.playerId);
  // Keep the server-side stats — Reset only touches the visible
  // fields.
  setCurrent({ ...synth, stats: current.stats });
  void sendUpdateProfile({ displayName: synth.displayName, avatarColor: synth.avatarColor });
}

async function sendUpdateProfile(body: { displayName?: string; avatarColor?: string }): Promise<void> {
  if (current === null) return;
  // Bishop's hub takes (displayName, avatarColor?) — neither can be
  // null on the wire, so pass the currently-cached value if the
  // caller didn't include that field.
  const displayName = body.displayName ?? current.displayName;
  const avatarColor = body.avatarColor ?? null;
  const expandedColor = avatarColor === null ? null : expandHex(avatarColor).toUpperCase();
  try {
    const dto = await invokeHub<unknown>('UpdateProfile', displayName, expandedColor);
    setCurrent(normalizeProfile(dto, current.playerId));
  } catch {
    /* network error — keep the optimistic local state */
  }
}

// ── Pre-game stats snapshot (for post-game delta rendering) ─────────

export function snapshotStatsForGame(): void {
  preGameSnapshot = current === null ? null : { ...current.stats };
}

export function getPreGameSnapshot(): PlayerStats | null {
  return preGameSnapshot === null ? null : { ...preGameSnapshot };
}

/**
 * Re-request the profile from the hub (typically after a game
 * completes so the post-game modal sees freshly-updated stats).
 * Bishop's hub re-broadcasts 'ProfileLoaded' as part of any
 * UpdateProfile RPC, so we invoke that with the current display
 * name (no-op) to trigger a fresh snapshot.
 */
export async function refreshProfile(): Promise<PlayerProfile | null> {
  if (current === null) return null;
  try {
    const dto = await invokeHub<unknown>(
      'UpdateProfile',
      current.displayName,
      null,
    );
    setCurrent(normalizeProfile(dto, current.playerId));
  } catch {
    /* keep cached value */
  }
  return current;
}

// ── UI install helpers ─────────────────────────────────────────────
//
// Phase K Wave 21 — Hicks (bundle-audit §3.6).  The full drawer +
// chip-toggle wiring has been extracted to `./profile-drawer.ts` so
// the lobby cold-path eager bundle no longer pays for the drawer's
// DOM-installation graph (~6 KB raw).  This module retains the
// data layer + a single `flushPendingDisplayName()` helper the
// drawer's Save button calls to flush the in-flight debounce.

/**
 * Flush any pending debounced `setDisplayName` write — used by the
 * lazy-loaded `./profile-drawer` Save button so the displayName
 * round-trip lands immediately rather than waiting for the
 * `DISPLAY_NAME_DEBOUNCE_MS` timer.  No-op when nothing is pending.
 */
export function flushPendingDisplayName(): void {
  if (displayNameTimer !== null) {
    window.clearTimeout(displayNameTimer);
    displayNameTimer = null;
    const flush = pendingDisplayName;
    pendingDisplayName = null;
    if (flush !== null) void sendUpdateProfile({ displayName: flush });
  }
}

// ── Re-export init helpers used by lobby/client ────────────────────

/**
 * Hook the profile module into the SignalR hub.  Called once at app
 * boot from index.ts → initLobby.  Wires the 'ProfileLoaded' event
 * listener so Bishop's first push lands in the cache.
 */
export function initProfileHubBindings(): void {
  installProfileLoadedListener();
}

// Private helper used by sendUpdateProfile to canonicalize 3-char hex
// shortcuts before they hit Bishop's wire (which only accepts #RRGGBB).
// The full drawer-side `expandHex` ships in `./profile-drawer.ts` so this
// duplicate stays tightly scoped to the data layer's one call site.
function expandHex(hex: string): string {
  if (/^#[0-9a-fA-F]{3}$/.test(hex)) {
    const r = hex.charAt(1);
    const g = hex.charAt(2);
    const b = hex.charAt(3);
    return `#${r}${r}${g}${g}${b}${b}`;
  }
  return hex;
}
