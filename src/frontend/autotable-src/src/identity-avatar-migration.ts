// Phase K Wave 22 — Hicks (Frontend), bundle-audit §3.7.
//
// Lazy-loaded avatar-migration modal.  Extracted from `identity.ts`
// so the eager bundle no longer carries the modal grid + confirm
// path; only users whose cached avatar matches the legacy `#808080`
// sentinel (sniffed synchronously by index.ts before any lobby code
// runs) pay for this chunk.
//
// Pre-Wave-22 the install path was already dynamic-imported from
// index.ts (`scheduleAvatarMigrationLazyMount` → `import('./identity')
// .then(m => m.installAvatarMigrationModalIfNeeded())`), but because
// identity.ts is also statically imported by lobby.ts the modal code
// still landed in `autotable-src-eager`.  Splitting it into its own
// file lets Rollup hoist it into its own lazy chunk.

import {
  AVATAR_COLOR_PRESETS,
  getProfile,
  onProfile,
  setAvatarColor as setProfileAvatarColor,
  validateAvatarColor,
} from './profile';
import { getIdentity, onIdentity } from './identity';
import { showEl, hideEl } from './dom-utils';

// The legacy hex `#808080` is the sentinel for "not yet customised".
// Anyone whose persisted avatarColor matches (case-insensitive) is
// shown the modal once their profile loads.  Picking + confirming
// persists via the existing setAvatarColor() Hub RPC.
const LEGACY_AVATAR_COLOR = '#808080';

// Hicks (e2e wave 27).  The identity cache is the canonical source
// of truth for the legacy avatar sentinel — it's what the index.ts
// lazy-mount probe reads to decide whether to install this module
// at all.  Sniffing it directly (in addition to subscribing to the
// profile + identity event channels) lets the modal surface on
// fresh visits where neither the SignalR profile nor the cookie-
// bound identity has hydrated yet (e.g. lobby-only visits before
// any game RPC).
const LS_KEY_IDENTITY_CACHE = 'mahjong.identity.cache.v1';

function readCachedAvatarColor(): string | null {
  try {
    const raw = window.localStorage.getItem(LS_KEY_IDENTITY_CACHE);
    if (raw === null) return null;
    const j = JSON.parse(raw) as { avatarColor?: unknown };
    if (typeof j.avatarColor !== 'string') return null;
    return j.avatarColor;
  } catch {
    return null;
  }
}

// Vasquez's Wave-10 testid contract uses friendly colour names
// (e.g. `avatar-migration-pick-emerald`) rather than hex strings.
// Keep this array index-aligned with profile.ts AVATAR_COLOR_PRESETS.
const AVATAR_MIGRATION_NAMES: ReadonlyArray<string> = [
  'red',
  'orange',
  'yellow',
  'emerald',
  'teal',
  'blue',
  'purple',
  'slate',
];

let migrationModalInstalled = false;
let migrationSelected: string | null = null;

function isLegacyAvatarColor(color: string | null | undefined): boolean {
  if (color === null || color === undefined) return false;
  return color.toLowerCase() === LEGACY_AVATAR_COLOR;
}

function renderMigrationGrid(): void {
  const grid = document.getElementById('migrate-avatar-grid');
  const confirmBtn = document.getElementById('migrate-avatar-confirm') as HTMLButtonElement | null;
  if (grid === null) return;
  grid.replaceChildren();
  migrationSelected = null;
  if (confirmBtn !== null) confirmBtn.disabled = true;
  AVATAR_COLOR_PRESETS.forEach((hex, i) => {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'migrate-avatar-swatch';
    btn.style.backgroundColor = hex;
    btn.setAttribute('data-color', hex);
    const name = AVATAR_MIGRATION_NAMES[i] ?? `preset-${i}`;
    btn.setAttribute('data-testid', `avatar-migration-pick-${name}`);
    btn.setAttribute('role', 'radio');
    btn.setAttribute('aria-checked', 'false');
    btn.setAttribute('aria-label', `Avatar colour ${name}`);
    btn.title = `${name} (${hex})`;
    btn.addEventListener('click', () => {
      migrationSelected = hex;
      for (const sw of grid.querySelectorAll<HTMLButtonElement>('.migrate-avatar-swatch')) {
        const matches = sw.getAttribute('data-color')?.toLowerCase() === hex.toLowerCase();
        sw.classList.toggle('migrate-avatar-swatch-selected', matches);
        sw.setAttribute('aria-checked', matches ? 'true' : 'false');
      }
      if (confirmBtn !== null) confirmBtn.disabled = false;
    });
    grid.appendChild(btn);
  });
}

function showMigrationModal(): void {
  const modal = document.getElementById('migrate-avatar-modal');
  if (modal === null) return;
  renderMigrationGrid();
  showEl(modal);
  modal.setAttribute('aria-hidden', 'false');
}

function hideMigrationModal(): void {
  const modal = document.getElementById('migrate-avatar-modal');
  if (modal === null) return;
  hideEl(modal);
  modal.setAttribute('aria-hidden', 'true');
}

export function installAvatarMigrationModalIfNeeded(): void {
  if (migrationModalInstalled) return;
  const modal = document.getElementById('migrate-avatar-modal');
  const confirmBtn = document.getElementById('migrate-avatar-confirm') as HTMLButtonElement | null;
  const dismissBtn = document.getElementById('migrate-avatar-dismiss') as HTMLButtonElement | null;
  if (modal === null || confirmBtn === null) return;
  migrationModalInstalled = true;

  confirmBtn.addEventListener('click', () => {
    if (migrationSelected === null) return;
    if (!validateAvatarColor(migrationSelected)) return;
    confirmBtn.disabled = true;
    const result = setProfileAvatarColor(migrationSelected);
    if (result.error !== null) {
      // Re-enable so the user can retry.
      confirmBtn.disabled = false;
      return;
    }
    hideMigrationModal();
  });

  // Wave 10 — dismiss button per Vasquez's `avatar-migration-dismiss`
  // contract.  Lets the user defer the choice; the modal will re-show
  // on the next profile load while avatarColor remains the legacy
  // sentinel (so the prompt is recurring, not blocking).
  if (dismissBtn !== null) {
    dismissBtn.addEventListener('click', () => {
      hideMigrationModal();
    });
  }

  let shownAtLeastOnce = false;
  const evaluate = (): void => {
    // Hicks (e2e wave 27).  Whichever source surfaces the legacy
    // sentinel first wins; once the modal has been shown, an async
    // arrival of a fresh non-legacy identity/profile does NOT silently
    // close it — the user must explicitly Pick or Dismiss.  This is
    // critical for the lobby-only visit path where the LS cache says
    // legacy but a server-side identity reissue may otherwise race
    // ahead and auto-hide a prompt the user never saw.
    const profile = getProfile();
    const identity = getIdentity();
    const profileColor = profile !== null ? profile.avatarColor : null;
    const identityColor = identity !== null ? identity.avatarColor : null;
    const cachedColor = readCachedAvatarColor();

    if (isLegacyAvatarColor(profileColor) || isLegacyAvatarColor(identityColor)
        || isLegacyAvatarColor(cachedColor)) {
      showMigrationModal();
      shownAtLeastOnce = true;
      return;
    }
    if (!shownAtLeastOnce && (profile !== null || identity !== null)) {
      hideMigrationModal();
    }
  };

  // Listen for profile and identity updates.  Either channel arriving
  // (or already-arrived) will trigger evaluate(); the initial call
  // handles the LS-cache-only path on lobby-only visits.
  onProfile(evaluate);
  onIdentity(evaluate);
  evaluate();
}
