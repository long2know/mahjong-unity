// Establish the signed guest cookie before either transport opens.
// Profile, chat and presence share the server's stable player ID,
// not a per-tab connection ID. Cached identity is display-only;
// a failed POST leaves verification unavailable and retryable.
//
// ── First-visit detection ──────────────────────────────────────────
//
// Bishop's `mahjong_pid` cookie is `HttpOnly`, so JavaScript cannot
// read its value via `document.cookie` — the sniff in `readCookie`
// will always return null in the browser.  We therefore use a
// localStorage flag (`mahjong.identity.onboarded.v1`) as the
// authoritative "user has been here before" signal:
//
//   • Cookie sniff returns null AND LS flag missing  → first visit
//                                                       (show card)
//   • LS flag present                                → silent restore
//                                                       (skip card)
//   • Server sends `isNewProfile: false`             → silent restore
//
// The cookie sniff is still wired up as a fallback / future-proof
// signal: when the backend stops using HttpOnly (or the test harness
// reads through a server middleware that strips the flag) the sniff
// becomes the primary detector.
//
// ── Wire contract (Bishop, Phase J Wave 6) ──────────────────────────
//
//   POST /api/identity
//     credentials: include   (so Set-Cookie: mahjong_pid=… lands)
//     → 200 OK
//       {
//         playerId: "<32-char hex>",
//         displayName: "Player-AABBCC",
//         avatarColor: "#1E88E5",
//         createdAt: "<iso>",
//         lastSeenAt: "<iso>"
//       }
//
// (Optional future field) `isNewProfile: boolean` — when present
// overrides the localStorage / cookie heuristic; the backend can
// detect "row just inserted" from EF Core's tracking.

import { EventEmitter } from 'events';

import {
  AVATAR_COLOR_PRESETS,
  validateAvatarColor,
} from './profile';
import { showEl, hideEl } from './dom-utils';

// ── Public types ─────────────────────────────────────────────────────

export interface Identity {
  playerId: string;
  displayName: string;
  avatarColor: string;
  /** True when the cookie wasn't present at boot (this is a brand-new visitor). */
  isFirstVisit: boolean;
  /** ISO timestamp of first identity creation; surfaced as
   *  "Member since" on the Wave-7 profile page.  Optional because
   *  pre-Wave-7 caches won't carry it. */
  createdAt?: string;
}

// ── Constants ────────────────────────────────────────────────────────

export const IDENTITY_ENDPOINT = '/api/identity';
export const IDENTITY_COOKIE_NAME = 'mahjong_pid';

const LS_KEY_IDENTITY_CACHE = 'mahjong.identity.cache.v1';
const LS_KEY_ONBOARDED = 'mahjong.identity.onboarded.v1';

// ── Module state ─────────────────────────────────────────────────────

const events = new EventEmitter();
let current: Identity | null = null;
let cookieAtBoot: string | null = null;
let bootPromise: Promise<Identity | null> | null = null;
let verifiedPlayerId: string | null = null;

export interface IdentityBootstrapState {
  status: 'idle' | 'loading' | 'ready' | 'unavailable';
  error: string | null;
}

let bootstrapState: IdentityBootstrapState = { status: 'idle', error: null };

// ── Helpers ──────────────────────────────────────────────────────────

/**
 * Read a cookie value by name (or null if absent).  We split on `; `
 * which is what `document.cookie` serialises to in every browser.
 */
export function readCookie(name: string): string | null {
  try {
    const all = document.cookie;
    if (all === '') return null;
    const parts = all.split(';');
    for (const raw of parts) {
      const trimmed = raw.trim();
      const eq = trimmed.indexOf('=');
      if (eq === -1) {
        if (trimmed === name) return '';
        continue;
      }
      if (trimmed.slice(0, eq) === name) {
        return decodeURIComponent(trimmed.slice(eq + 1));
      }
    }
    return null;
  } catch {
    return null;
  }
}

function loadCache(): Identity | null {
  try {
    const raw = window.localStorage.getItem(LS_KEY_IDENTITY_CACHE);
    if (raw === null) return null;
    const j = JSON.parse(raw) as Partial<Identity>;
    if (typeof j.playerId !== 'string' || j.playerId === '') return null;
    return {
      playerId: j.playerId,
      displayName: typeof j.displayName === 'string' ? j.displayName : '',
      avatarColor:
        typeof j.avatarColor === 'string' && validateAvatarColor(j.avatarColor)
          ? j.avatarColor
          : AVATAR_COLOR_PRESETS[5],
      isFirstVisit: false,
      createdAt: typeof j.createdAt === 'string' ? j.createdAt : undefined,
    };
  } catch {
    return null;
  }
}

function writeCache(id: Identity): void {
  try {
    window.localStorage.setItem(
      LS_KEY_IDENTITY_CACHE,
      JSON.stringify({
        playerId: id.playerId,
        displayName: id.displayName,
        avatarColor: id.avatarColor,
        createdAt: id.createdAt,
      }),
    );
  } catch {
    /* private mode / quota — skip */
  }
}

function isOnboardingComplete(): boolean {
  try {
    return window.localStorage.getItem(LS_KEY_ONBOARDED) === 'true';
  } catch {
    return false;
  }
}

function markOnboardingComplete(): void {
  try {
    window.localStorage.setItem(LS_KEY_ONBOARDED, 'true');
  } catch {
    /* skip */
  }
}

function normalizeIdentity(raw: unknown, isFirstVisit: boolean): Identity {
  const o = (raw !== null && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
  if (typeof o.playerId !== 'string' || o.playerId.trim() === '') {
    throw new Error('Identity endpoint returned an invalid player identity.');
  }
  const playerId = o.playerId;
  const displayName = typeof o.displayName === 'string' ? o.displayName : '';
  const avatarColor =
    typeof o.avatarColor === 'string' && validateAvatarColor(o.avatarColor)
      ? o.avatarColor
      : AVATAR_COLOR_PRESETS[5];
  // Server hint overrides the cookie sniff when present.
  const serverIsNew = typeof o.isNewProfile === 'boolean' ? o.isNewProfile : null;
  // Phase J Wave 7 — surface the createdAt timestamp so the profile
  // page can show "Member since {date}".  The endpoint returns it as
  // an ISO string; we accept either camelCase or PascalCase.
  const createdAt =
    typeof o.createdAt === 'string' && o.createdAt !== ''
      ? o.createdAt
      : (typeof o.CreatedAt === 'string' && o.CreatedAt !== '' ? o.CreatedAt : undefined);
  return {
    playerId,
    displayName,
    avatarColor,
    isFirstVisit: serverIsNew !== null ? serverIsNew : isFirstVisit,
    createdAt,
  };
}

function setCurrent(id: Identity): void {
  current = id;
  writeCache(id);
  events.emit('identity', id);
}

// ── Public API ───────────────────────────────────────────────────────

export function getIdentity(): Identity | null {
  return current;
}

/** A display cache is never evidence that the signed cookie was established. */
export function getVerifiedIdentity(): Identity | null {
  return bootstrapState.status === 'ready' && current?.playerId === verifiedPlayerId
    ? current : null;
}

export function getIdentityBootstrapState(): IdentityBootstrapState {
  return bootstrapState;
}

export function onIdentityBootstrap(handler: (value: IdentityBootstrapState) => void): () => void {
  events.on('bootstrap', handler);
  handler(bootstrapState);
  return () => events.off('bootstrap', handler);
}

function setBootstrapState(status: IdentityBootstrapState['status'], error: string | null = null): void {
  bootstrapState = { status, error };
  events.emit('bootstrap', bootstrapState);
}

export function onIdentity(handler: (id: Identity) => void): () => void {
  events.on('identity', handler);
  if (current !== null) handler(current);
  return () => events.off('identity', handler);
}

/**
 * Bootstrap the identity.  Idempotent — concurrent callers share the
 * same in-flight POST. Failed attempts return null and can be retried.
 * Cached identity remains available for display, never for transport bootstrap.
 *
 * The "first-visit" decision is made *before* the POST: if the
 * `mahjong_pid` cookie isn't on the jar at boot, the Set-Cookie
 * response we receive is the very first issue of the identity.
 */
export async function bootstrapIdentity(): Promise<Identity | null> {
  const verified = getVerifiedIdentity();
  if (verified !== null) return verified;
  if (bootPromise !== null) return bootPromise;
  cookieAtBoot = readCookie(IDENTITY_COOKIE_NAME);
  const firstVisitGuess = cookieAtBoot === null || cookieAtBoot === '';
  // Install the flight before publishing any synchronous subscriber event.
  // A loading/identity listener may re-enter bootstrapIdentity immediately.
  const attempt = Promise.resolve().then(async (): Promise<Identity | null> => {
    try {
      const resp = await fetch(IDENTITY_ENDPOINT, {
        method: 'POST',
        credentials: 'include',
        headers: { 'Accept': 'application/json' },
      });
      if (!resp.ok) {
        throw new Error(`Identity bootstrap failed (HTTP ${resp.status}).`);
      }
      const body = (await resp.json()) as unknown;
      const id = normalizeIdentity(body, firstVisitGuess);
      verifiedPlayerId = id.playerId;
      setCurrent(id);
      setBootstrapState('ready');
      return id;
    } catch (error) {
      verifiedPlayerId = null;
      const cached = loadCache();
      if (current === null && cached !== null) {
        setCurrent({ ...cached, isFirstVisit: false });
      }
      setBootstrapState('unavailable', error instanceof Error ? error.message : String(error));
      return null;
    }
  });
  bootPromise = attempt;
  setBootstrapState('loading');
  try {
    return await attempt;
  } finally {
    if (bootPromise === attempt) bootPromise = null;
  }
}

/** Pre-boot cookie sniff (null if bootstrapIdentity hasn't started yet). */
export function getCookieAtBoot(): string | null {
  return cookieAtBoot;
}

// ── Onboarding card ─────────────────────────────────────────────────
//
// First-visit users see a small card pinned at the top of the lobby
// asking for their preferred display name + avatar colour.  The card
// is dismissed when the user clicks Continue or Skip.  On subsequent
// visits the card is suppressed via the LS_KEY_ONBOARDED flag.
//
// Phase K Wave 22 — Hicks bundle-audit §3.7.  The card-install path
// (`installOnboardingCard`, ~7 KB minified) lives in the lazy
// `identity-onboarding` chunk; the only eager surface left is this
// decision predicate + a tiny show/hide helper.  Lobby.ts dynamic-
// imports the installer.  Skipping the install for returning users
// keeps the chunk off the cold path entirely.

/**
 * Decide whether the onboarding card should be shown for this visit.
 * Returns true when:
 *   • the `mahjong_pid` cookie was absent at boot, AND
 *   • the user has not previously completed onboarding
 *     (LS flag absent or false).
 *
 * Server `isNewProfile` flag (when present) overrides the cookie sniff
 * — if the backend says "this is a brand-new row" we trust it.
 */
export function shouldShowOnboarding(): boolean {
  if (isOnboardingComplete()) return false;
  if (current !== null && current.isFirstVisit) return true;
  // Pre-boot path: rely on the cookie sniff alone.
  if (current === null) {
    return cookieAtBoot === null || cookieAtBoot === '';
  }
  return false;
}

/**
 * Re-evaluate visibility (called by lobby after installOnboardingCard
 * + after bootstrapIdentity resolves, in case the identity arrived
 * *after* the lobby mounted).
 */
export function refreshOnboardingVisibility(): void {
  const card = document.getElementById('onboarding-card');
  if (card === null) return;
  if (shouldShowOnboarding()) {
    showEl(card);
    card.setAttribute('aria-hidden', 'false');
  } else {
    hideEl(card);
    card.setAttribute('aria-hidden', 'true');
  }
}

// ── Lazy-module hooks (Phase K Wave 22) ─────────────────────────────
//
// `identity-onboarding.ts` (the lazy installer chunk) needs to mutate
// the `current` Identity + commit the LS_KEY_ONBOARDED flag, both of
// which are private to this module.  Rather than promote `current` /
// `setCurrent` / `markOnboardingComplete` to the eager public API,
// we expose two narrow shim functions that the lazy module calls
// and nothing else does.

/**
 * Apply onboarding-form values (displayName + avatarColor) to the
 * cached Identity, mirroring the original inline assignment in the
 * pre-Wave-22 installOnboardingCard.  No-op when the Identity hasn't
 * loaded yet (the lazy path always installs after bootstrapIdentity).
 */
export function applyOnboardingProfile(
  displayName: string,
  avatarColor: string,
): void {
  if (current === null) return;
  if (!validateAvatarColor(avatarColor)) return;
  setCurrent({ ...current, displayName, avatarColor });
}

/** Wave-22 lazy-module shim — commit the LS_KEY_ONBOARDED flag. */
export function markOnboardingCompleteExported(): void {
  markOnboardingComplete();
}
