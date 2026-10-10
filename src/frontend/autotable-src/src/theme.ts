// Phase J Wave 8 — Motion + theme preference module.
//
// Honours `prefers-reduced-motion: reduce` and `prefers-color-scheme:
// dark` by default, but lets the user override either preference via
// the Wave-7 settings drawer Display tab.
//
// Implementation:
//   • Motion → toggle `body.reduced-motion` class.  style.css has a
//     blanket `body.reduced-motion *` override that sets transition /
//     animation durations to 0.01ms.  CSS animations on the 3D scene
//     are unaffected (those live in three.js).
//   • Theme  → toggle `body.theme-dark` (the chrome dark palette) and
//     `body.theme-light`.  When neither class is set the page uses
//     its baseline (dark-leaning) styling.
//
// Both preferences persist in a single LS blob (`mahjong.display.v1`).

const LS_KEY = 'mahjong.display.v1';

export type MotionPreference = 'auto' | 'reduced' | 'full';
export type ThemePreference = 'auto' | 'light' | 'dark';

interface DisplayPrefs {
  motion: MotionPreference;
  theme: ThemePreference;
}

const DEFAULT: DisplayPrefs = { motion: 'auto', theme: 'auto' };

let cached: DisplayPrefs = { ...DEFAULT };
let installed = false;

// ── LS helpers ─────────────────────────────────────────────────────

function load(): DisplayPrefs {
  try {
    const raw = window.localStorage.getItem(LS_KEY);
    if (raw === null) return { ...DEFAULT };
    const j = JSON.parse(raw) as Partial<DisplayPrefs>;
    return {
      motion: j.motion === 'reduced' || j.motion === 'full' ? j.motion : 'auto',
      theme: j.theme === 'light' || j.theme === 'dark' ? j.theme : 'auto',
    };
  } catch {
    return { ...DEFAULT };
  }
}

function persist(): void {
  try {
    window.localStorage.setItem(LS_KEY, JSON.stringify(cached));
  } catch { /* skip */ }
}

// ── Media-query helpers ────────────────────────────────────────────

function osPrefersReducedMotion(): boolean {
  try {
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  } catch {
    return false;
  }
}

function osPrefersDark(): boolean {
  try {
    return window.matchMedia('(prefers-color-scheme: dark)').matches;
  } catch {
    return false;
  }
}

// ── Apply derived classes ─────────────────────────────────────────

function apply(): void {
  const body = document.body;
  if (body === null) return;

  // Motion.
  const reduced = cached.motion === 'reduced'
    || (cached.motion === 'auto' && osPrefersReducedMotion());
  body.classList.toggle('reduced-motion', reduced);
  body.classList.toggle('full-motion', cached.motion === 'full');

  // Theme.
  const dark = cached.theme === 'dark'
    || (cached.theme === 'auto' && osPrefersDark());
  const light = cached.theme === 'light'
    || (cached.theme === 'auto' && !osPrefersDark());
  body.classList.toggle('theme-dark', dark);
  body.classList.toggle('theme-light', light);
}

// ── Public API ─────────────────────────────────────────────────────

export function getMotionPreference(): MotionPreference {
  return cached.motion;
}

export function getThemePreference(): ThemePreference {
  return cached.theme;
}

export function setMotionPreference(value: MotionPreference): void {
  cached = { ...cached, motion: value };
  persist();
  apply();
}

export function setThemePreference(value: ThemePreference): void {
  cached = { ...cached, theme: value };
  persist();
  apply();
}

/** Whether reduced-motion is currently active (computed). */
export function isReducedMotionActive(): boolean {
  if (cached.motion === 'reduced') return true;
  if (cached.motion === 'full') return false;
  return osPrefersReducedMotion();
}

/** Whether dark theme is currently active (computed). */
export function isDarkThemeActive(): boolean {
  if (cached.theme === 'dark') return true;
  if (cached.theme === 'light') return false;
  return osPrefersDark();
}

/**
 * Install the OS media-query listeners and apply the initial body
 * classes.  Idempotent.  Safe to call before/after DOMContentLoaded —
 * `document.body` is guaranteed by callers (lobby.ts:initLobby boots
 * after the body exists).
 */
export function installDisplayPreferences(): void {
  if (installed) return;
  installed = true;
  cached = load();
  apply();
  // Re-apply when the OS preference changes (the user flips macOS
  // appearance in System Preferences, for example).  Only matters
  // for the 'auto' branches.
  try {
    const motionMql = window.matchMedia('(prefers-reduced-motion: reduce)');
    const themeMql = window.matchMedia('(prefers-color-scheme: dark)');
    const onChange = (): void => apply();
    if (typeof motionMql.addEventListener === 'function') {
      motionMql.addEventListener('change', onChange);
      themeMql.addEventListener('change', onChange);
    } else if (typeof (motionMql as MediaQueryList).addListener === 'function') {
      // Safari < 14 fallback.
      (motionMql as MediaQueryList).addListener(onChange);
      (themeMql as MediaQueryList).addListener(onChange);
    }
  } catch { /* swallow — no matchMedia */ }
}
