// Phase K Wave 21 — Hicks (Frontend, bundle-audit §3.6).
//
// Profile-drawer surface, split out of `./profile` so the eager
// lobby bundle no longer pays for the drawer's DOM-installation
// graph (~6 KB raw / ~2 KB minified at W21 close).  The drawer
// + toggle are now lazy-mounted from `./lobby` on first
// `lobby-open-profile` chip hover/focus/click, mirroring the
// W17 §3.2 lazy-mount patterns for `./profile-page` and
// `./settings-drawer`.
//
// What's HERE in W21:
//   • `installProfileDrawer()`  — runtime wiring (event listeners
//     + per-event re-render) for the legacy Wave-5 side drawer.
//   • `installProfileToggle()`  — toggle/chip click handler.
//   • `openProfileDrawer()` / `closeProfileDrawer()`  — public
//     open/close calls (the chip handler dispatches openDrawer;
//     the drawer's close button fires close).
//
// The shared *state* (`AVATAR_COLOR_PRESETS`, `getProfile()`,
// `onProfile()`, validators, mutators, plus the new
// `flushPendingDisplayName()` flush helper) lives in `./profile`.
// This module is intentionally DOM-only so the eager bundle can
// drop the entire surface unless the user shows intent to open
// their profile.
//
// Modern UI note: `./profile-page` (Wave 7+) intercepts the
// `lobby-open-profile` chip click in CAPTURE phase, so the
// drawer's installProfileToggle click handler is effectively
// dormant on modern paths.  The drawer's DOM listeners (close,
// save, name input, color picker, presets, custom color) still
// wire up so any third-party flow that calls `openProfileDrawer()`
// programmatically continues to work.

import {
  AVATAR_COLOR_PRESETS,
  flushPendingDisplayName,
  getProfile,
  onProfile,
  resetProfile,
  setAvatarColor,
  setDisplayName,
  validateAvatarColor,
  validateDisplayName,
} from './profile';
import { hideEl, showEl } from './dom-utils';

let drawerInstalled = false;
let savedNoteTimer: number | null = null;

function initialsFromName(name: string): string {
  const trimmed = name.trim();
  if (trimmed === '') return '?';
  const parts = trimmed.split(/\s+/);
  if (parts.length === 1) return parts[0].charAt(0).toUpperCase();
  return (parts[0].charAt(0) + parts[parts.length - 1].charAt(0)).toUpperCase();
}

function flashSaved(node: HTMLElement | null): void {
  if (node === null) return;
  showEl(node);
  node.textContent = 'Saved ✓';
  if (savedNoteTimer !== null) window.clearTimeout(savedNoteTimer);
  savedNoteTimer = window.setTimeout(() => {
    savedNoteTimer = null;
    hideEl(node);
  }, 1400);
}

function expandHex(hex: string): string {
  if (/^#[0-9a-fA-F]{3}$/.test(hex)) {
    const r = hex.charAt(1);
    const g = hex.charAt(2);
    const b = hex.charAt(3);
    return `#${r}${r}${g}${g}${b}${b}`;
  }
  return hex;
}

export function installProfileDrawer(): void {
  if (drawerInstalled) return;
  const drawer = document.getElementById('profile-drawer');
  if (drawer === null) return;
  drawerInstalled = true;

  const closeBtn = document.getElementById('profile-drawer-close') as HTMLButtonElement | null;
  const nameInput = document.getElementById('profile-display-name-input') as HTMLInputElement | null;
  const nameError = document.getElementById('profile-display-name-error');
  const presetsHost = document.getElementById('profile-avatar-presets');
  const customColor = document.getElementById('profile-avatar-color-custom') as HTMLInputElement | null;
  const previewAvatar = document.getElementById('profile-preview-avatar');
  const previewName = document.getElementById('profile-preview-name');
  const saveBtn = document.getElementById('profile-save') as HTMLButtonElement | null;
  const resetBtn = document.getElementById('profile-reset') as HTMLButtonElement | null;
  const savedNote = document.getElementById('profile-saved-note');

  if (presetsHost !== null) {
    presetsHost.replaceChildren();
    AVATAR_COLOR_PRESETS.forEach((hex, idx) => {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'profile-avatar-preset';
      btn.style.backgroundColor = hex;
      btn.setAttribute('data-color', hex);
      btn.setAttribute('data-testid', `profile-avatar-color-preset-${idx}`);
      btn.setAttribute('role', 'radio');
      btn.setAttribute('aria-checked', 'false');
      btn.setAttribute('aria-label', `Preset colour ${idx + 1}: ${hex}`);
      btn.title = hex;
      btn.addEventListener('click', () => {
        const { error } = setAvatarColor(hex);
        if (error === null) flashSaved(savedNote);
      });
      presetsHost.appendChild(btn);
    });
  }

  if (closeBtn !== null) {
    closeBtn.addEventListener('click', () => closeProfileDrawer());
  }

  if (nameInput !== null) {
    nameInput.addEventListener('input', () => {
      const raw = nameInput.value;
      const { value, error } = validateDisplayName(raw);
      if (nameError !== null) {
        nameError.textContent = error ?? '';
      }
      nameInput.classList.toggle('profile-input-invalid', error !== null);
      const current = getProfile();
      if (error === null && value !== null && current !== null && value !== current.displayName) {
        const res = setDisplayName(value);
        if (res.error === null) flashSaved(savedNote);
      }
    });
  }

  if (customColor !== null) {
    customColor.addEventListener('input', () => {
      const hex = customColor.value;
      if (!validateAvatarColor(hex)) return;
      const { error } = setAvatarColor(hex);
      if (error === null) flashSaved(savedNote);
    });
  }

  if (saveBtn !== null) {
    saveBtn.addEventListener('click', () => {
      if (nameInput !== null) {
        const { value, error } = validateDisplayName(nameInput.value);
        if (error === null && value !== null) {
          setDisplayName(value);
          flushPendingDisplayName();
        }
      }
      flashSaved(savedNote);
    });
  }

  if (resetBtn !== null) {
    resetBtn.addEventListener('click', () => {
      resetProfile();
      flashSaved(savedNote);
    });
  }

  onProfile((p) => {
    if (nameInput !== null && document.activeElement !== nameInput) {
      nameInput.value = p.displayName;
      nameInput.classList.remove('profile-input-invalid');
      if (nameError !== null) nameError.textContent = '';
    }
    if (customColor !== null && document.activeElement !== customColor) {
      customColor.value = expandHex(p.avatarColor);
    }
    if (previewAvatar !== null) {
      const el = previewAvatar as HTMLElement;
      el.style.backgroundColor = p.avatarColor;
      el.textContent = initialsFromName(p.displayName);
    }
    if (previewName !== null) {
      previewName.textContent = p.displayName === '' ? 'Guest' : p.displayName;
    }
    if (presetsHost !== null) {
      for (const btn of presetsHost.querySelectorAll<HTMLButtonElement>('.profile-avatar-preset')) {
        const matches = btn.getAttribute('data-color')?.toLowerCase() === p.avatarColor.toLowerCase();
        btn.classList.toggle('profile-avatar-preset-selected', matches);
        btn.setAttribute('aria-checked', matches ? 'true' : 'false');
      }
    }
  });
}

export function openProfileDrawer(): void {
  const drawer = document.getElementById('profile-drawer');
  if (drawer === null) return;
  drawer.classList.add('profile-drawer-open');
  drawer.setAttribute('aria-hidden', 'false');
  const nameInput = document.getElementById('profile-display-name-input') as HTMLInputElement | null;
  if (nameInput !== null) {
    window.setTimeout(() => nameInput.focus(), 220);
  }
}

export function closeProfileDrawer(): void {
  const drawer = document.getElementById('profile-drawer');
  if (drawer === null) return;
  drawer.classList.remove('profile-drawer-open');
  drawer.setAttribute('aria-hidden', 'true');
}

/** Wire the lobby's small "Profile" shortcut button (chip + label). */
export function installProfileToggle(): void {
  const btn = document.getElementById('lobby-open-profile') as HTMLButtonElement | null;
  if (btn === null) return;
  btn.addEventListener('click', (e) => {
    e.preventDefault();
    openProfileDrawer();
  });

  const avatar = document.getElementById('lobby-open-profile-avatar');
  const label = document.getElementById('lobby-open-profile-label');
  onProfile((p) => {
    if (avatar !== null) {
      const el = avatar as HTMLElement;
      el.style.backgroundColor = p.avatarColor;
      el.textContent = initialsFromName(p.displayName);
    }
    if (label !== null) {
      label.textContent = p.displayName === '' ? 'Profile' : p.displayName;
    }
  });
}
