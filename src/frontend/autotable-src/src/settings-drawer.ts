// Phase J Wave 7 — App-wide settings drawer.
//
// A *global* settings drawer (separate from the per-game settings drawer
// that Wave 2 added for bot-strength / hand-count / auto-deal).  The
// Wave-7 drawer is the canonical place for app-level preferences:
//   • General  — display name + avatar colour (mirrors the profile
//                module so the lobby chip + onboarding card stay in
//                sync).
//   • Audio    — sound on/off + master volume (0..1 float).
//   • Display  — perspective vs flat table view + table cloth colour.
//   • Network  — server URL override for power users.
//
// Persistence: a single JSON blob in localStorage under
// `mahjong.settings.v1` so every knob round-trips through one key —
// no fragmented keys like the Wave-2 per-game payload.
//
// The drawer is dialog-modal-ish: Escape closes it, focus is trapped
// while it is open, the gear-icon toggle (#settings-button) flips the
// `settings-drawer-v2-open` class on the drawer aside.  We deliberately
// pick a brand-new toggle id (`settings-button`) so the Wave-2
// `settings-toggle` keeps its per-game wiring untouched — the Wave-7
// drawer is opened from the new gear in the lobby header.

import {
  AVATAR_COLOR_PRESETS,
  getProfile,
  onProfile,
  setAvatarColor,
  setDisplayName,
  validateAvatarColor,
  validateDisplayName,
} from './profile';
import { renderEditorPanel as renderRulePresetsEditor } from './rule-presets';
import {
  getMotionPreference,
  getThemePreference,
  setMotionPreference,
  setThemePreference,
  type MotionPreference,
  type ThemePreference,
} from './theme';
import {
  getLanguage,
  setLanguage,
  onLanguageChange,
  t,
  translateElements,
  type LanguagePreference,
} from './i18n';
import { hideEl } from './dom-utils';
import { handSortMode, type HandSortMode } from './hand-sort';
import { COMPACT_VIEW_QUERY, mobileInfoPanel, type MobileInfoPanel } from './mobile-overlay-policy';
import {
  getGameState, getGameStateStatus, loadGameState, refreshGameState, subscribeGameState,
  type GameState,
} from './game-state';

// ── Public types ────────────────────────────────────────────────────

export type SettingsTab = 'general' | 'audio' | 'display' | 'network' | 'rule-presets';

export interface AppSettings {
  /** Audio master volume — 0..1.  0 effectively mutes. */
  masterVolume: number;
  /** Sound on/off — mirrors `mahjong:soundEnabled` LS key. */
  soundEnabled: boolean;
  /** Perspective vs orthographic table view (Phase F #perspective). */
  perspective: boolean;
  cameraMode: 'auto' | 'perspective' | 'flat';
  boardLayout: 'auto' | 'classic' | 'rectangular';
  handLayout: 'auto' | 'in-table' | 'separate';
  handSort: HandSortMode;
  mobileTableStatus: boolean;
  mobileInfoPanel: MobileInfoPanel;
  /** Table cloth colour (CSS hex).  Read by world.ts via a CSS variable. */
  tableColor: string;
  /** Network override.  Empty string = use page origin. */
  serverUrl: string;
}

// ── Constants ───────────────────────────────────────────────────────

export const SETTINGS_LS_KEY = 'mahjong.settings.v1';
const LS_KEY_SOUND_ENABLED_MIRROR = 'mahjong:soundEnabled';

export const SETTINGS_DEFAULT: AppSettings = {
  masterVolume: 0.8,
  soundEnabled: true,
  perspective: true,
  cameraMode: 'auto',
  boardLayout: 'auto',
  handLayout: 'auto',
  handSort: 'suit',
  mobileTableStatus: false,
  mobileInfoPanel: 'none',
  tableColor: '#0a5a3a',
  serverUrl: '',
};

const TABS: ReadonlyArray<{ id: SettingsTab; labelKey: string; fallback: string }> = [
  { id: 'general',       labelKey: 'settings.tab.general',       fallback: 'General' },
  { id: 'audio',         labelKey: 'settings.tab.audio',         fallback: 'Audio' },
  { id: 'display',       labelKey: 'settings.tab.display',       fallback: 'Display' },
  { id: 'network',       labelKey: 'settings.tab.network',       fallback: 'Network' },
  { id: 'rule-presets',  labelKey: 'settings.tab.rule_presets',  fallback: 'Rule presets' },
];

// ── State ───────────────────────────────────────────────────────────

let current: AppSettings = { ...SETTINGS_DEFAULT };
let settingsLoaded = false;
let installed = false;
let perspectiveInstalled = false;
let legacyDisplayInstalled = false;
let rendererLayoutInstalled = false;
let drawerOpened = false;
let applyingCamera = false;
let activeTab: SettingsTab = 'general';
const listeners = new Set<(s: AppSettings) => void>();

// ── LS helpers ──────────────────────────────────────────────────────

function loadFromStorage(): AppSettings {
  try {
    const raw = window.localStorage.getItem(SETTINGS_LS_KEY);
    if (raw === null) return { ...SETTINGS_DEFAULT };
    const j = JSON.parse(raw) as Record<string, unknown>;
    const out: AppSettings = { ...SETTINGS_DEFAULT };
    if (typeof j.masterVolume === 'number' && isFinite(j.masterVolume)) {
      out.masterVolume = Math.max(0, Math.min(1, j.masterVolume));
    }
    if (typeof j.soundEnabled === 'boolean') out.soundEnabled = j.soundEnabled;
    if (typeof j.perspective === 'boolean') out.perspective = j.perspective;
    if (j.cameraMode === 'auto' || j.cameraMode === 'perspective' || j.cameraMode === 'flat') out.cameraMode = j.cameraMode;
    else if (typeof j.perspective === 'boolean') out.cameraMode = j.perspective ? 'perspective' : 'flat';
    if (j.boardLayout === 'classic' || j.boardLayout === 'rectangular') out.boardLayout = j.boardLayout;
    if (j.handLayout === 'in-table' || j.handLayout === 'separate') out.handLayout = j.handLayout;
    out.handSort = handSortMode(j.handSort);
    if (typeof j.mobileTableStatus === 'boolean') out.mobileTableStatus = j.mobileTableStatus;
    out.mobileInfoPanel = mobileInfoPanel(j.mobileInfoPanel);
    if (typeof j.tableColor === 'string' && /^#[0-9a-fA-F]{3,6}$/.test(j.tableColor)) {
      out.tableColor = j.tableColor;
    }
    if (typeof j.serverUrl === 'string') out.serverUrl = j.serverUrl;
    return out;
  } catch {
    return { ...SETTINGS_DEFAULT };
  }
}

function writeToStorage(s: AppSettings): void {
  try {
    let saved: Record<string, unknown> = {};
    try {
      const value: unknown = JSON.parse(window.localStorage.getItem(SETTINGS_LS_KEY) ?? '{}');
      if (value && typeof value === 'object' && !Array.isArray(value)) saved = value as Record<string, unknown>;
    } catch { /* replace an unreadable preference blob */ }
    window.localStorage.setItem(SETTINGS_LS_KEY, JSON.stringify({ ...saved, ...s }));
  } catch {
    /* private mode / quota — skip */
  }
}

function mirrorSoundEnabled(s: AppSettings): void {
  try {
    window.localStorage.setItem(LS_KEY_SOUND_ENABLED_MIRROR, s.soundEnabled ? 'true' : 'false');
  } catch {
    /* skip */
  }
}

// ── Public API ──────────────────────────────────────────────────────

export function getSettings(): AppSettings {
  ensureSettingsLoaded();
  return { ...current };
}

export function cameraPerspective(settings: AppSettings, width: number, height: number): boolean {
  return settings.cameraMode === 'auto' ? !(width < 768 && height > width)
    : settings.cameraMode === 'perspective';
}

export function syncCameraInputs(width: number, height: number): void {
  const settings = getSettings();
  const checked = cameraPerspective(settings, width, height);
  const previous = applyingCamera;
  applyingCamera = true;
  try {
    const mode = document.getElementById('settings-camera-view') as HTMLSelectElement | null;
    if (mode) mode.value = settings.cameraMode;
    for (const id of ['perspective', 'settings-game-perspective']) {
      const input = document.getElementById(id) as HTMLInputElement | null;
      if (input && input.checked !== checked) {
        input.checked = checked;
        input.dispatchEvent(new Event('change', { bubbles: true }));
      }
    }
  } finally { applyingCamera = previous; }
}

/** Bind the existing camera input before the optional drawer is opened. */
export function installPerspectiveSetting(): void {
  if (perspectiveInstalled) return;
  const input = document.getElementById('perspective') as HTMLInputElement | null;
  if (input === null) return;
  perspectiveInstalled = true;
  input.addEventListener('change', () => {
    if (!applyingCamera) setSettings({ perspective: input.checked });
  });
  const settings = getSettings();
  applyDerivedSettings(settings, { perspective: settings.perspective });
}

export function installLegacyDisplaySettings(): void {
  if (legacyDisplayInstalled) return;
  const root = document.getElementById('settings-table-display');
  const perspective = document.getElementById('settings-game-perspective') as HTMLInputElement | null;
  const order = document.getElementById('settings-game-hand-sort') as HTMLSelectElement | null;
  if (root === null || perspective === null || order === null) return;
  legacyDisplayInstalled = true;
  installPerspectiveSetting();
  perspective.addEventListener('change', () => {
    if (!applyingCamera) setSettings({ perspective: perspective.checked });
  });
  order.addEventListener('change', () => {
    const mode = handSortMode(order.value);
    if (getSettings().handSort !== mode) setSettings({ handSort: mode });
  });
  const localize = (): void => {
    translateElements(root);
    order.setAttribute('aria-label', t('hand.order'));
    order.dispatchEvent(new Event('change', { bubbles: true }));
  };
  const settings = getSettings();
  applyDerivedSettings(settings, { perspective: settings.perspective, handSort: settings.handSort });
  localize();
  onLanguageChange(localize);
}

export function installRendererLayoutSettings(): void {
  if (rendererLayoutInstalled) return;
  const board = document.getElementById('renderer-board-layout') as HTMLSelectElement | null;
  const hand = document.getElementById('renderer-hand-layout') as HTMLSelectElement | null;
  if (!board || !hand) return;
  rendererLayoutInstalled = true;
  board.addEventListener('change', () => {
    const boardLayout = board.value === 'classic' || board.value === 'rectangular' ? board.value : 'auto';
    if (getSettings().boardLayout !== boardLayout) setSettings({ boardLayout });
  });
  hand.addEventListener('change', () => {
    const handLayout = hand.value === 'in-table' || hand.value === 'separate' ? hand.value : 'auto';
    if (getSettings().handLayout !== handLayout) setSettings({ handLayout });
  });
  const settings = getSettings();
  applyDerivedSettings(settings, { boardLayout: settings.boardLayout, handLayout: settings.handLayout });
}

function ensureSettingsLoaded(): void {
  if (settingsLoaded) return;
  current = loadFromStorage();
  settingsLoaded = true;
}

export function onSettingsChange(handler: (s: AppSettings) => void): () => void {
  listeners.add(handler);
  return () => listeners.delete(handler);
}

function emit(): void {
  for (const fn of listeners) {
    try { fn(current); } catch { /* swallow */ }
  }
}

/** Replace the current settings (full object).  Persists + emits. */
export function setSettings(next: Partial<AppSettings>): void {
  ensureSettingsLoaded();
  if ('perspective' in next && !('cameraMode' in next)) {
    next = { ...next, cameraMode: next.perspective ? 'perspective' : 'flat' };
  } else if (next.cameraMode === 'perspective' || next.cameraMode === 'flat') {
    next = { ...next, perspective: next.cameraMode === 'perspective' };
  }
  current = { ...current, ...next };
  writeToStorage(current);
  if ('soundEnabled' in next) mirrorSoundEnabled(current);
  applyDerivedSettings(current, next);
  emit();
}

/** Apply the in-memory settings to known surfaces (sound, CSS, etc.). */
function applyDerivedSettings(s: AppSettings, changed: Partial<AppSettings> = s): void {
  for (const [key, ids] of [
    ['boardLayout', ['renderer-board-layout', 'settings-board-layout']],
    ['handLayout', ['renderer-hand-layout', 'settings-hand-layout']],
  ] as const) {
    if (key in changed) for (const id of ids) {
      const input = document.getElementById(id) as HTMLSelectElement | null;
      if (input && input.value !== s[key]) { input.value = s[key]; input.dispatchEvent(new Event('change', { bubbles: true })); }
    }
  }
  if ('mobileTableStatus' in changed) {
    document.body.classList.toggle('mobile-table-status-visible', s.mobileTableStatus);
  }
  if ('handSort' in changed) {
    for (const id of ['settings-hand-sort', 'settings-game-hand-sort']) {
      const select = document.getElementById(id) as HTMLSelectElement | null;
      if (select && select.value !== s.handSort) {
        select.value = s.handSort;
        select.dispatchEvent(new Event('change', { bubbles: true }));
      }
    }
  }
  // Sound on/off mirror — write `mahjong:soundEnabled` so the Wave-3
  // mute toggle in sound.ts (via the existing settings drawer +
  // installSoundEnabledMirror) sees the same state.
  if ('soundEnabled' in changed) {
    try {
      window.localStorage.setItem(LS_KEY_SOUND_ENABLED_MIRROR, s.soundEnabled ? 'true' : 'false');
    } catch { /* skip */ }
  }
  // Notify the legacy #settings-sound checkbox so its change handlers
  // (Sound.setMuted) fire from this single source.
  const legacyCheckbox = document.getElementById('settings-sound') as HTMLInputElement | null;
  if ('soundEnabled' in changed && legacyCheckbox !== null && legacyCheckbox.checked !== s.soundEnabled) {
    legacyCheckbox.checked = s.soundEnabled;
    legacyCheckbox.dispatchEvent(new Event('change', { bubbles: true }));
  }
  // Table colour — expose as a CSS variable so style.css / main.css
  // can apply it without each surface importing this module.
  if ('tableColor' in changed) document.documentElement.style.setProperty('--app-table-color', s.tableColor);
  if ('perspective' in changed || 'cameraMode' in changed)
    syncCameraInputs(window.innerWidth ?? 1024, window.innerHeight ?? 768);
}

/** Reset all settings to defaults. */
export function resetSettings(): void {
  setSettings({ ...SETTINGS_DEFAULT });
}

// ── Drawer install ──────────────────────────────────────────────────

export function installSettingsDrawerV2(): void {
  if (installed) return;
  const drawer = document.getElementById('settings-drawer-v2');
  if (drawer === null) return;
  installed = true;

  installPerspectiveSetting();
  ensureSettingsLoaded();
  applyDerivedSettings(current);
  emit();

  const btn = document.getElementById('settings-button') as HTMLButtonElement | null;
  const localizeButton = (): void => {
    btn?.setAttribute('title', t('settings.open_display'));
    btn?.setAttribute('aria-label', t('settings.open_display'));
  };
  localizeButton();
  const closeBtn = document.getElementById('settings-close-v2') as HTMLButtonElement | null;
  const saveBtn = document.getElementById('settings-save') as HTMLButtonElement | null;
  const resetBtn = document.getElementById('settings-reset') as HTMLButtonElement | null;

  // Tab strip — render dynamically so tab metadata stays in this module.
  const tabsHost = document.getElementById('settings-drawer-v2-tabs');
  const panelHost = document.getElementById('settings-drawer-v2-panels');
  if (tabsHost === null || panelHost === null) return;

  tabsHost.replaceChildren();
  for (const tab_ of TABS) {
    const tab = document.createElement('button');
    tab.type = 'button';
    tab.className = 'settings-v2-tab';
    tab.setAttribute('role', 'tab');
    tab.setAttribute('aria-controls', `settings-panel-${tab_.id}`);
    tab.setAttribute('aria-selected', tab_.id === activeTab ? 'true' : 'false');
    tab.setAttribute('data-tab', tab_.id);
    tab.setAttribute('data-testid', `settings-tab-${tab_.id}`);
    tab.id = `settings-tab-${tab_.id}`;
    tab.textContent = t(tab_.labelKey) || tab_.fallback;
    tab.addEventListener('click', () => activateTab(tab_.id));
    tabsHost.appendChild(tab);
  }

  // Panels.
  panelHost.replaceChildren();
  panelHost.appendChild(buildGeneralPanel());
  panelHost.appendChild(buildAudioPanel());
  panelHost.appendChild(buildDisplayPanel());
  panelHost.appendChild(buildNetworkPanel());
  panelHost.appendChild(buildRulePresetsPanel());
  activateTab(activeTab);

  // Toggle.
  if (btn !== null) {
    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      if (drawer.classList.contains('settings-drawer-v2-open')) {
        closeDrawer();
      } else {
        openDrawer();
      }
    });
  }
  if (closeBtn !== null) {
    closeBtn.addEventListener('click', () => closeDrawer());
  }
  if (saveBtn !== null) {
    saveBtn.addEventListener('click', () => {
      // Read the current panel state and persist.  setSettings already
      // persists on each input change, so Save is mostly a UX
      // affordance — but we also explicitly flash a saved note.
      writeToStorage(current);
      flashSavedNote();
    });
  }
  if (resetBtn !== null) {
    resetBtn.addEventListener('click', () => {
      resetSettings();
      // Re-render the panels with the defaulted values.
      rerenderPanels();
    });
  }

  // Escape closes the drawer when open.
  document.addEventListener('keydown', (e: KeyboardEvent) => {
    if (e.key !== 'Escape') return;
    if (drawer.classList.contains('settings-drawer-v2-open')) {
      closeDrawer();
      btn?.focus();
    }
  });
  // Click outside closes.
  document.addEventListener('mousedown', (e: MouseEvent) => {
    if (!drawer.classList.contains('settings-drawer-v2-open')) return;
    const target = e.target as Node | null;
    if (target !== null && (drawer.contains(target) || (btn?.contains(target) ?? false))) return;
    closeDrawer();
  });

  // Profile mirror — sync the General tab when the profile changes
  // externally (e.g. via the onboarding card).
  onProfile(() => rerenderPanels());

  // Phase J Wave 9 — re-render the drawer's tab strip + panel labels
  // whenever the active locale changes (Language picker change).
  onLanguageChange(() => {
    localizeButton();
    // Re-render tabs (textContent depends on t()).
    for (const tab_ of TABS) {
      const tabEl = document.getElementById(`settings-tab-${tab_.id}`);
      if (tabEl !== null) tabEl.textContent = t(tab_.labelKey) || tab_.fallback;
    }
    rerenderPanels();
  });
}

export function openSettingsDrawerV2(): void {
  installSettingsDrawerV2();
  openDrawer();
}

function openDrawer(): void {
  const drawer = document.getElementById('settings-drawer-v2');
  const btn = document.getElementById('settings-button') as HTMLButtonElement | null;
  if (drawer === null) return;
  if (!drawerOpened && !document.body.classList.contains('lobby-active')
    && window.matchMedia(COMPACT_VIEW_QUERY).matches) {
    activateTab('display');
  }
  drawerOpened = true;
  drawer.classList.add('settings-drawer-v2-open');
  drawer.setAttribute('aria-hidden', 'false');
  if (btn !== null) btn.setAttribute('aria-expanded', 'true');
  // Focus the active tab so keyboard users land in the drawer.
  window.setTimeout(() => {
    const tab = document.getElementById(`settings-tab-${activeTab}`);
    tab?.focus();
  }, 50);
}

function closeDrawer(): void {
  const drawer = document.getElementById('settings-drawer-v2');
  const btn = document.getElementById('settings-button') as HTMLButtonElement | null;
  if (drawer === null) return;
  drawer.classList.remove('settings-drawer-v2-open');
  drawer.setAttribute('aria-hidden', 'true');
  if (btn !== null) btn.setAttribute('aria-expanded', 'false');
}

function activateTab(id: SettingsTab): void {
  activeTab = id;
  for (const tab_ of TABS) {
    const tab = document.getElementById(`settings-tab-${tab_.id}`);
    const panel = document.getElementById(`settings-panel-${tab_.id}`);
    const isActive = tab_.id === id;
    if (tab !== null) {
      tab.classList.toggle('settings-v2-tab-active', isActive);
      tab.setAttribute('aria-selected', isActive ? 'true' : 'false');
      tab.setAttribute('tabindex', isActive ? '0' : '-1');
    }
    if (panel !== null) {
      panel.hidden = !isActive;
    }
  }
  // Ferro WP-E/#131 — buildRulePresetsPanel()'s initial renderEditorPanel()
  // no-ops because the panel isn't in the DOM yet (getElementById → null), so
  // (re)render the editor when its tab is shown. This is what makes the editor
  // body AND the "not yet functional" notice appear reliably when the user
  // opens the tab — not only after an async auth event.
  if (id === 'rule-presets') renderRulePresetsEditor();
}

function flashSavedNote(): void {
  const note = document.getElementById('settings-saved-note-v2');
  if (note === null) return;
  note.textContent = t('settings.saved') || 'Saved ✓';
  (note as HTMLElement).hidden = false;
  (note as HTMLElement).style.display = 'inline';
  window.setTimeout(() => {
    hideEl(note as HTMLElement);
  }, 1400);
}

function rerenderPanels(): void {
  const panelHost = document.getElementById('settings-drawer-v2-panels');
  if (panelHost === null) return;
  panelHost.replaceChildren();
  panelHost.appendChild(buildGeneralPanel());
  panelHost.appendChild(buildAudioPanel());
  panelHost.appendChild(buildDisplayPanel());
  panelHost.appendChild(buildNetworkPanel());
  panelHost.appendChild(buildRulePresetsPanel());
  activateTab(activeTab);
}

// ── Panels ──────────────────────────────────────────────────────────

function buildPanelShell(id: SettingsTab, ariaLabel: string): HTMLDivElement {
  const panel = document.createElement('div');
  panel.className = 'settings-v2-panel';
  panel.id = `settings-panel-${id}`;
  panel.setAttribute('role', 'tabpanel');
  panel.setAttribute('aria-labelledby', `settings-tab-${id}`);
  panel.setAttribute('aria-label', ariaLabel);
  panel.setAttribute('data-testid', `settings-panel-${id}`);
  panel.hidden = id !== activeTab;
  return panel;
}

function buildGeneralPanel(): HTMLDivElement {
  const panel = buildPanelShell('general', t('settings.tab.general') || 'General settings');
  const profile = getProfile();
  const currentName = profile?.displayName ?? '';
  const currentColor = profile?.avatarColor ?? AVATAR_COLOR_PRESETS[5];

  const nameField = document.createElement('label');
  nameField.className = 'settings-v2-field';
  const nameLabel = document.createElement('span');
  nameLabel.className = 'settings-v2-label';
  nameLabel.textContent = t('settings.display_name') || 'Display name';
  const nameInput = document.createElement('input');
  nameInput.type = 'text';
  nameInput.className = 'settings-v2-input';
  nameInput.setAttribute('data-testid', 'settings-display-name-input');
  nameInput.maxLength = 32;
  nameInput.value = currentName;
  const nameError = document.createElement('span');
  nameError.className = 'settings-v2-error';
  nameError.setAttribute('aria-live', 'polite');
  nameInput.addEventListener('input', () => {
    const { value, error } = validateDisplayName(nameInput.value);
    nameError.textContent = error ?? '';
    nameInput.classList.toggle('settings-v2-input-invalid', error !== null);
    if (error === null && value !== null) {
      setDisplayName(value);
    }
  });
  nameField.appendChild(nameLabel);
  nameField.appendChild(nameInput);
  nameField.appendChild(nameError);

  const colorField = document.createElement('div');
  colorField.className = 'settings-v2-field';
  const colorLabel = document.createElement('span');
  colorLabel.className = 'settings-v2-label';
  colorLabel.textContent = t('settings.avatar_color') || 'Avatar colour';
  colorField.appendChild(colorLabel);
  const presets = document.createElement('div');
  presets.className = 'settings-v2-color-presets';
  presets.setAttribute('role', 'radiogroup');
  presets.setAttribute('aria-label', t('settings.avatar_color') || 'Avatar colour presets');
  AVATAR_COLOR_PRESETS.forEach((hex, i) => {
    const swatch = document.createElement('button');
    swatch.type = 'button';
    swatch.className = 'settings-v2-color-swatch';
    swatch.style.backgroundColor = hex;
    swatch.setAttribute('data-color', hex);
    swatch.setAttribute('data-testid', `settings-avatar-color-${i}`);
    swatch.setAttribute('role', 'radio');
    const isSelected = hex.toLowerCase() === currentColor.toLowerCase();
    swatch.setAttribute('aria-checked', isSelected ? 'true' : 'false');
    swatch.setAttribute('aria-label', `Avatar colour ${i + 1}: ${hex}`);
    swatch.title = hex;
    if (isSelected) swatch.classList.add('settings-v2-color-swatch-selected');
    swatch.addEventListener('click', () => {
      setAvatarColor(hex);
      for (const s of presets.querySelectorAll<HTMLButtonElement>('.settings-v2-color-swatch')) {
        const sel = s.getAttribute('data-color')?.toLowerCase() === hex.toLowerCase();
        s.classList.toggle('settings-v2-color-swatch-selected', sel);
        s.setAttribute('aria-checked', sel ? 'true' : 'false');
      }
    });
    presets.appendChild(swatch);
  });
  colorField.appendChild(presets);
  const customRow = document.createElement('label');
  customRow.className = 'settings-v2-color-custom-row';
  const customText = document.createElement('span');
  customText.textContent = 'Custom colour';
  const customInput = document.createElement('input');
  customInput.type = 'color';
  customInput.value = currentColor;
  customInput.setAttribute('data-testid', 'settings-avatar-color-custom');
  customInput.addEventListener('input', () => {
    if (validateAvatarColor(customInput.value)) {
      setAvatarColor(customInput.value);
    }
  });
  customRow.appendChild(customText);
  customRow.appendChild(customInput);
  colorField.appendChild(customRow);

  // Phase J Wave 9 — Language selector.  Persists to
  // `mahjong.settings.v1.lang` via i18n.ts; default Auto follows
  // navigator.language family detection.
  const langRow = document.createElement('label');
  langRow.className = 'settings-v2-field';
  const langLabel = document.createElement('span');
  langLabel.className = 'settings-v2-label';
  langLabel.textContent = t('settings.language') || 'Language';
  const langSelect = document.createElement('select');
  langSelect.className = 'dark-select form-control form-control-sm';
  langSelect.setAttribute('data-testid', 'settings-language-select');
  langSelect.setAttribute('aria-label', t('settings.language') || 'Language preference');
  for (const opt of [
    { v: 'auto',    label: t('settings.language_auto')    || 'Auto (browser default)' },
    { v: 'en',      label: t('settings.language_en')      || 'English' },
    { v: 'zh-Hans', label: t('settings.language_zh_hans') || '简体中文' },
    { v: 'zh-Hant', label: t('settings.language_zh_hant') || '繁體中文' },
  ] as ReadonlyArray<{ v: LanguagePreference; label: string }>) {
    const o = document.createElement('option');
    o.value = opt.v;
    o.textContent = opt.label;
    langSelect.appendChild(o);
  }
  langSelect.value = getLanguage();
  langSelect.addEventListener('change', () => {
    setLanguage(langSelect.value as LanguagePreference);
  });
  langRow.appendChild(langLabel);
  langRow.appendChild(langSelect);

  panel.appendChild(nameField);
  panel.appendChild(colorField);
  panel.appendChild(langRow);
  return panel;
}

function buildAudioPanel(): HTMLDivElement {
  const panel = buildPanelShell('audio', t('settings.tab.audio') || 'Audio settings');

  const soundRow = document.createElement('label');
  soundRow.className = 'settings-v2-field settings-v2-checkbox-row';
  const soundInput = document.createElement('input');
  soundInput.type = 'checkbox';
  soundInput.checked = current.soundEnabled;
  soundInput.setAttribute('data-testid', 'settings-sound');
  const soundText = document.createElement('span');
  soundText.textContent = t('settings.sound_effects') || 'Sound effects';
  soundInput.addEventListener('change', () => {
    setSettings({ soundEnabled: soundInput.checked });
  });
  soundRow.appendChild(soundInput);
  soundRow.appendChild(soundText);

  const volumeRow = document.createElement('label');
  volumeRow.className = 'settings-v2-field';
  const volumeLabel = document.createElement('span');
  volumeLabel.className = 'settings-v2-label';
  volumeLabel.textContent = t('settings.master_volume') || 'Master volume';
  const volumeInput = document.createElement('input');
  volumeInput.type = 'range';
  volumeInput.min = '0';
  volumeInput.max = '100';
  volumeInput.step = '1';
  volumeInput.value = String(Math.round(current.masterVolume * 100));
  volumeInput.setAttribute('data-testid', 'settings-master-volume');
  volumeInput.setAttribute('aria-label', 'Master volume percent');
  const volumeValue = document.createElement('output');
  volumeValue.className = 'settings-v2-output';
  volumeValue.textContent = `${volumeInput.value}%`;
  volumeInput.addEventListener('input', () => {
    const v = parseInt(volumeInput.value, 10);
    volumeValue.textContent = `${v}%`;
    setSettings({ masterVolume: Math.max(0, Math.min(1, v / 100)) });
  });
  volumeRow.appendChild(volumeLabel);
  volumeRow.appendChild(volumeInput);
  volumeRow.appendChild(volumeValue);

  panel.appendChild(soundRow);
  panel.appendChild(volumeRow);
  return panel;
}

function buildDisplayPanel(): HTMLDivElement {
  const panel = buildPanelShell('display', t('settings.tab.display') || 'Display settings');

  const perspRow = document.createElement('label');
  perspRow.className = 'settings-v2-field settings-perspective-row';
  const perspInput = document.createElement('select');
  perspInput.id = 'settings-camera-view';
  perspInput.className = 'form-control';
  for (const value of ['auto', 'perspective', 'flat'] as const) {
    const option = document.createElement('option');
    option.value = value;
    option.textContent = t(`settings.camera_${value}`);
    perspInput.appendChild(option);
  }
  perspInput.value = current.cameraMode;
  perspInput.setAttribute('data-testid', 'settings-camera-view');
  perspInput.setAttribute('aria-describedby', 'settings-perspective-hint');
  const perspText = document.createElement('span');
  perspText.className = 'settings-v2-label';
  perspText.textContent = t('settings.camera_view');
  perspInput.addEventListener('change', () => {
    const cameraMode = perspInput.value === 'perspective' || perspInput.value === 'flat' ? perspInput.value : 'auto';
    setSettings({ cameraMode });
  });
  perspRow.appendChild(perspText);
  perspRow.appendChild(perspInput);
  const perspHint = document.createElement('p');
  perspHint.id = 'settings-perspective-hint';
  perspHint.className = 'settings-v2-help';
  perspHint.textContent = t('settings.perspective_hint');

  const colorRow = document.createElement('label');
  colorRow.className = 'settings-v2-field';
  const colorLabel = document.createElement('span');
  colorLabel.className = 'settings-v2-label';
  colorLabel.textContent = t('settings.table_color') || 'Table cloth colour';
  const colorInput = document.createElement('input');
  colorInput.type = 'color';
  colorInput.value = current.tableColor;
  colorInput.setAttribute('data-testid', 'settings-table-color');
  colorInput.addEventListener('input', () => {
    setSettings({ tableColor: colorInput.value });
  });
  const colorReset = document.createElement('button');
  colorReset.type = 'button';
  colorReset.className = 'btn btn-secondary btn-sm';
  colorReset.textContent = t('common.reset') || 'Reset';
  colorReset.setAttribute('data-testid', 'settings-table-color-reset');
  colorReset.addEventListener('click', () => {
    colorInput.value = SETTINGS_DEFAULT.tableColor;
    setSettings({ tableColor: SETTINGS_DEFAULT.tableColor });
  });
  colorRow.appendChild(colorLabel);
  colorRow.appendChild(colorInput);
  colorRow.appendChild(colorReset);

  // Phase J Wave 8 — Motion preference (Auto/Reduced/Full).  Honours
  // prefers-reduced-motion when Auto; overrides the OS preference on
  // Reduced/Full.  Stored in theme.ts so the choice persists across
  // page reloads.
  const motionRow = document.createElement('label');
  motionRow.className = 'settings-v2-field';
  const motionLabel = document.createElement('span');
  motionLabel.className = 'settings-v2-label';
  motionLabel.textContent = t('settings.motion') || 'Motion';
  const motionSelect = document.createElement('select');
  motionSelect.className = 'dark-select form-control form-control-sm';
  motionSelect.setAttribute('data-testid', 'settings-motion-select');
  motionSelect.setAttribute('aria-label', t('settings.motion') || 'Motion preference');
  for (const opt of [
    { v: 'auto',    label: t('settings.motion_auto')    || 'Auto (follow OS preference)' },
    { v: 'reduced', label: t('settings.motion_reduced') || 'Reduced (disable animations)' },
    { v: 'full',    label: t('settings.motion_full')    || 'Full (always animate)' },
  ] as ReadonlyArray<{ v: MotionPreference; label: string }>) {
    const o = document.createElement('option');
    o.value = opt.v;
    o.textContent = opt.label;
    motionSelect.appendChild(o);
  }
  motionSelect.value = getMotionPreference();
  motionSelect.addEventListener('change', () => {
    setMotionPreference(motionSelect.value as MotionPreference);
  });
  motionRow.appendChild(motionLabel);
  motionRow.appendChild(motionSelect);

  // Phase J Wave 8 — Theme preference (Auto/Light/Dark).  Honours
  // prefers-color-scheme: dark when Auto; the 3D table itself is
  // textured and is NOT recolored.
  const themeRow = document.createElement('label');
  themeRow.className = 'settings-v2-field';
  const themeLabel = document.createElement('span');
  themeLabel.className = 'settings-v2-label';
  themeLabel.textContent = t('settings.theme') || 'Theme';
  const themeSelect = document.createElement('select');
  themeSelect.className = 'dark-select form-control form-control-sm';
  themeSelect.setAttribute('data-testid', 'settings-theme-select');
  themeSelect.setAttribute('aria-label', t('settings.theme') || 'Theme preference');
  for (const opt of [
    { v: 'auto',  label: t('settings.theme_auto')  || 'Auto (follow OS preference)' },
    { v: 'light', label: t('settings.theme_light') || 'Light' },
    { v: 'dark',  label: t('settings.theme_dark')  || 'Dark' },
  ] as ReadonlyArray<{ v: ThemePreference; label: string }>) {
    const o = document.createElement('option');
    o.value = opt.v;
    o.textContent = opt.label;
    themeSelect.appendChild(o);
  }
  themeSelect.value = getThemePreference();
  themeSelect.addEventListener('change', () => {
    setThemePreference(themeSelect.value as ThemePreference);
  });
  themeRow.appendChild(themeLabel);
  themeRow.appendChild(themeSelect);

  panel.append(perspRow, perspHint);
  for (const [key, id, labelKey, choices] of [
    ['boardLayout', 'settings-board-layout', 'settings.board_layout', ['auto', 'classic', 'rectangular']],
    ['handLayout', 'settings-hand-layout', 'settings.hand_layout', ['auto', 'in-table', 'separate']],
  ] as const) {
    const row = document.createElement('label');
    row.className = 'settings-v2-field';
    const label = document.createElement('span');
    label.className = 'settings-v2-label';
    label.textContent = t(labelKey);
    const input = document.createElement('select');
    input.id = id;
    input.className = 'dark-select form-control';
    input.setAttribute('aria-label', t(labelKey));
    input.setAttribute('data-testid', id);
    for (const choice of choices) {
      const option = document.createElement('option');
      option.value = choice;
      option.textContent = t(`settings.layout_${choice}`);
      input.appendChild(option);
    }
    input.value = current[key];
    input.addEventListener('change', () => {
      if (key === 'boardLayout') setSettings({ boardLayout: input.value as AppSettings['boardLayout'] });
      else setSettings({ handLayout: input.value as AppSettings['handLayout'] });
    });
    row.append(label, input);
    panel.appendChild(row);
  }
  const sortRow = document.createElement('label');
  sortRow.className = 'settings-v2-field';
  const sortLabel = document.createElement('span');
  sortLabel.className = 'settings-v2-label';
  sortLabel.textContent = t('hand.order');
  const sortSelect = document.createElement('select');
  sortSelect.id = 'settings-hand-sort';
  sortSelect.className = 'dark-select form-control';
  sortSelect.setAttribute('data-testid', 'settings-hand-sort');
  sortSelect.setAttribute('aria-label', t('hand.order'));
  for (const mode of ['suit', 'groups'] as const) {
    const option = document.createElement('option');
    option.value = mode;
    option.textContent = t(`hand.sort_${mode}`);
    sortSelect.appendChild(option);
  }
  sortSelect.value = current.handSort;
  sortSelect.addEventListener('change', () => {
    const mode = handSortMode(sortSelect.value);
    if (mode !== current.handSort) setSettings({ handSort: mode });
  });
  const sortHint = document.createElement('span');
  sortHint.className = 'settings-v2-hint';
  sortHint.id = 'settings-hand-sort-hint';
  sortHint.textContent = t('hand.sort_hint');
  sortSelect.setAttribute('aria-describedby', sortHint.id);
  sortRow.append(sortLabel, sortSelect, sortHint);
  panel.appendChild(sortRow);
  const statusRow = document.createElement('label');
  statusRow.className = 'settings-v2-field settings-v2-checkbox-row';
  const statusInput = document.createElement('input');
  statusInput.type = 'checkbox';
  statusInput.checked = current.mobileTableStatus;
  statusInput.setAttribute('data-testid', 'settings-mobile-table-status');
  const statusText = document.createElement('span');
  statusText.textContent = t('settings.mobile_table_status');
  statusInput.addEventListener('change', () => setSettings({ mobileTableStatus: statusInput.checked }));
  statusRow.append(statusInput, statusText);
  const overlayHint = document.createElement('p');
  overlayHint.className = 'settings-v2-hint';
  overlayHint.textContent = t('settings.mobile_overlays_hint');
  panel.append(statusRow, overlayHint);
  panel.appendChild(colorRow);
  panel.appendChild(motionRow);
  panel.appendChild(themeRow);
  return panel;
}

function buildNetworkPanel(): HTMLDivElement {
  const panel = buildPanelShell('network', t('settings.tab.network') || 'Network settings');

  const urlField = document.createElement('label');
  urlField.className = 'settings-v2-field';
  const urlLabel = document.createElement('span');
  urlLabel.className = 'settings-v2-label';
  urlLabel.textContent = t('settings.server_url') || 'Server URL override';
  const urlInput = document.createElement('input');
  urlInput.type = 'url';
  urlInput.placeholder = 'Defaults to page origin';
  urlInput.className = 'settings-v2-input';
  urlInput.value = current.serverUrl;
  urlInput.setAttribute('data-testid', 'settings-server-url');
  urlInput.setAttribute('autocomplete', 'off');
  urlInput.addEventListener('change', () => {
    setSettings({ serverUrl: urlInput.value.trim() });
  });
  const urlHint = document.createElement('span');
  urlHint.className = 'settings-v2-hint';
  urlHint.textContent =
    'Power users only — empty = same origin. Takes effect on next page reload.';
  urlField.appendChild(urlLabel);
  urlField.appendChild(urlInput);
  urlField.appendChild(urlHint);

  panel.appendChild(urlField);

  // Phase K Wave 3 — Per-game "Enable voice" toggle.  Only rendered
  // when the viewer is in a live game (URL has `?gameId=…`) and the
  // GET /api/games/{id}/settings call reports `viewerIsOwner: true`.
  // The toggle posts to /api/games/{id}/settings/voice and fires a
  // `mahjong:voice-enabled` event so voice.ts can flip its mic
  // button live without a page reload.
  const voiceSection = buildVoiceEnableToggle();
  if (voiceSection !== null) {
    panel.appendChild(voiceSection);
  }

  return panel;
}

// Phase K Wave 3 — Per-game voice settings (owner-only).
//
// Bishop's Wave-3 backend exposes:
//   GET  /api/games/{id}/settings
//     → 200 { voiceEnabled: bool, viewerIsOwner: bool, … } | 404
//   POST /api/games/{id}/settings/voice
//     body: { enabled: bool }  → 204 | 403 (not owner) | 404
//
// Account-authorized voice administration is distinct from signed-guest room ownership.

// Phase K Wave 4 — Voice settings are now fetched via the unified
// `./game-state` reactive store (`loadGameState` / `subscribeGameState`),
// so the per-module response type that lived here is gone.  The
// settings POST writer below still owns the wire-shape for the
// PUT body (`{ enabled }`).

function currentGameIdFromUrl(): string | null {
  try {
    const id = new URLSearchParams(window.location.search).get('gameId');
    return id !== null && id !== '' ? id : null;
  } catch {
    return null;
  }
}

function buildVoiceEnableToggle(): HTMLElement | null {
  const gameId = currentGameIdFromUrl();
  if (gameId === null) return null;

  const wrap = document.createElement('div');
  wrap.className = 'settings-v2-field settings-v2-voice-enable';

  const heading = document.createElement('span');
  heading.className = 'settings-v2-section-heading';
  heading.textContent = t('settings.voice.section');
  wrap.appendChild(heading);

  const row = document.createElement('label');
  row.className = 'settings-v2-toggle-row';

  const input = document.createElement('input');
  input.type = 'checkbox';
  input.id = 'settings-voice-enable';
  input.setAttribute('data-testid', 'voice-enable-toggle');
  input.disabled = true;
  row.appendChild(input);

  const labelText = document.createElement('span');
  labelText.className = 'settings-v2-toggle-label';
  labelText.textContent = t('settings.voice.toggle');
  row.appendChild(labelText);

  const hint = document.createElement('span');
  hint.className = 'settings-v2-hint';
  hint.setAttribute('data-testid', 'voice-enable-hint');
  hint.textContent = t('common.loading');

  wrap.appendChild(row);
  wrap.appendChild(hint);

  input.addEventListener('change', () => {
    void postVoiceEnable(gameId, input);
  });

  void primeVoiceToggle(gameId, wrap, input);

  return wrap;
}

async function primeVoiceToggle(
  gameId: string,
  wrap: HTMLElement,
  input: HTMLInputElement,
): Promise<void> {
  const apply = (snapshot: GameState | null): void => {
    const load = getGameStateStatus();
    const matches = load.roomId === gameId || snapshot?.gameId === gameId;
    if (snapshot !== null && matches && input.dataset.saving !== 'true') input.checked = snapshot.voiceEnabled;
    input.disabled = !matches || load.status !== 'ready' || snapshot?.viewerCanManageVoice !== true
      || input.dataset.saving === 'true';
    wrap.hidden = false;
    const hint = wrap.querySelector<HTMLElement>('[data-testid="voice-enable-hint"]');
    if (hint !== null) {
      hint.textContent = snapshot === null ? t('settings.voice.unavailable')
        : snapshot.viewerCanManageVoice ? t('settings.voice.hint') : t('settings.voice.permission');
    }
  };
  subscribeGameState(apply);
  await loadGameState(gameId);
}

async function postVoiceEnable(
  gameId: string,
  input: HTMLInputElement,
): Promise<void> {
  const snapshot = getGameState();
  const { showToast } = await import('./toast');
  if (snapshot === null || !snapshot.viewerCanManageVoice
      || (getGameStateStatus().roomId !== gameId && snapshot.gameId !== gameId)) {
    input.checked = snapshot?.voiceEnabled ?? false;
    input.disabled = true;
    showToast(t('settings.voice.permission'), 'error');
    return;
  }
  const desired = input.checked;
  const identityId = getGameStateStatus().playerId;
  input.dataset.saving = 'true';
  input.disabled = true;
  try {
    const r = await fetch(
      `/api/games/${encodeURIComponent(snapshot.gameId)}/settings/voice`,
      {
        method: 'POST',
        credentials: 'same-origin',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({ enabled: desired }),
      },
    );
    if (!r.ok) {
      throw new Error(`HTTP ${r.status}`);
    }
    if (identityId !== getGameStateStatus().playerId || currentGameIdFromUrl() !== gameId) return;
    const fresh = await refreshGameState(snapshot.gameId);
    if (fresh === null) {
      showToast(t('settings.voice.refresh_failed'), 'error');
      return;
    }
    showToast(t(fresh.voiceEnabled ? 'settings.voice.enabled' : 'settings.voice.disabled'), 'success');
  } catch (failure) {
    input.checked = snapshot.voiceEnabled;
    showToast(t('settings.voice.failed', { reason: failure instanceof Error ? failure.message : String(failure) }), 'error');
  } finally {
    delete input.dataset.saving;
    const fresh = getGameState();
    input.checked = fresh?.voiceEnabled ?? snapshot.voiceEnabled;
    input.disabled = fresh?.viewerCanManageVoice !== true || getGameStateStatus().status !== 'ready';
  }
}

// Phase J Wave 8 — Rule presets panel.  The body of this tab is
// rendered by rule-presets.ts:renderEditorPanel() so the editor's
// state (current draft, picker selection) survives a settings
// drawer close+reopen without leaking back into this module.
function buildRulePresetsPanel(): HTMLDivElement {
  const panel = buildPanelShell('rule-presets', t('settings.tab.rule_presets') || 'Rule presets');
  // The host element is the panel itself; rule-presets.ts looks up
  // `#settings-panel-rule-presets` and populates its body in-place.
  renderRulePresetsEditor();
  return panel;
}
