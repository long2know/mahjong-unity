// Phase J Wave 8 — Rule presets module.
//
// Surfaces:
//   • Lobby host selects a `RulePreset` from a dropdown when creating
//     a new game.  Default "Classic Changsha" is always present.
//   • Authenticated users see a "Create custom preset" link that opens
//     the Wave-7 settings drawer's new "Rule presets" tab — an editable
//     form for the same fields Bishop exposes server-side.
//
// ── Wire contract with Bishop ─────────────────────────────────────
//
//   GET  /api/rule-presets
//     → 200  { presets: [
//                { id: "classic-changsha", name: "Classic Changsha",
//                  isBuiltin: true, ownerId: null,
//                  handLimit: 16, maxScorePerHand: 200,
//                  allowWashout: true, allowKongRobbing: true,
//                  allowConcealedKongPromotion: true } ] }
//     → 404 → degrade to a single hardcoded "Classic Changsha" entry.
//
//   POST /api/rule-presets
//     body: <RulePreset minus id/ownerId/isBuiltin>
//     → 201  { id, ownerId, isBuiltin: false, … }
//     → 401  unauthenticated
//
//   PUT  /api/rule-presets/{id}
//   DELETE /api/rule-presets/{id}
//
// The lobby URL gains a `&rulePreset=<id>` param so the backend can
// route the chosen ruleset to the runtime at game creation time.

import { EventEmitter } from 'events';
import { getAuthState, onAuth } from './auth';
import { t } from './i18n';

// ── Public types ────────────────────────────────────────────────────

export interface RulePreset {
  id: string;
  name: string;
  isBuiltin: boolean;
  ownerId: string | null;
  handLimit: number;
  maxScorePerHand: number;
  allowWashout: boolean;
  allowKongRobbing: boolean;
  allowConcealedKongPromotion: boolean;
}

// ── Constants ───────────────────────────────────────────────────────

const ENDPOINT_LIST = '/api/rule-presets';
const presetEndpoint = (id: string): string => `/api/rule-presets/${encodeURIComponent(id)}`;
const LS_KEY_SELECTED = 'mahjong.rule-preset.selected.v1';

export const CLASSIC_CHANGSHA: RulePreset = Object.freeze({
  id: 'classic-changsha',
  name: 'Classic Changsha',
  isBuiltin: true,
  ownerId: null,
  handLimit: 16,
  maxScorePerHand: 200,
  allowWashout: true,
  allowKongRobbing: true,
  allowConcealedKongPromotion: true,
});

// ── State ───────────────────────────────────────────────────────────

const events = new EventEmitter();
let cached: RulePreset[] = [CLASSIC_CHANGSHA];
let installed = false;

// ── LS helpers ──────────────────────────────────────────────────────

function readSelectedId(): string {
  try {
    return window.localStorage.getItem(LS_KEY_SELECTED) ?? CLASSIC_CHANGSHA.id;
  } catch {
    return CLASSIC_CHANGSHA.id;
  }
}

function writeSelectedId(id: string): void {
  try {
    window.localStorage.setItem(LS_KEY_SELECTED, id);
  } catch { /* skip */ }
}

// ── Public API ──────────────────────────────────────────────────────

export function getRulePresets(): RulePreset[] {
  return cached.slice();
}

export function getSelectedPresetId(): string {
  return readSelectedId();
}

export function getSelectedPreset(): RulePreset {
  const id = readSelectedId();
  return cached.find((p) => p.id === id) ?? CLASSIC_CHANGSHA;
}

export function setSelectedPresetId(id: string): void {
  writeSelectedId(id);
  events.emit('selected', id);
}

export function onRulePresetsChange(handler: () => void): () => void {
  events.on('change', handler);
  return () => events.off('change', handler);
}

function emitChange(): void {
  events.emit('change');
  renderLobbySelect();
  renderEditorPanel();
}

// ── Normalisation ──────────────────────────────────────────────────

function normalisePreset(raw: unknown, fallback: RulePreset = CLASSIC_CHANGSHA): RulePreset {
  if (raw === null || typeof raw !== 'object') return fallback;
  const o = raw as Record<string, unknown>;
  const numField = (key: string, def: number): number => {
    const v = o[key] ?? o[capitalize(key)];
    if (typeof v === 'number' && isFinite(v)) return v;
    return def;
  };
  const boolField = (key: string, def: boolean): boolean => {
    const v = o[key] ?? o[capitalize(key)];
    if (typeof v === 'boolean') return v;
    return def;
  };
  const strField = (key: string, def: string): string => {
    const v = o[key] ?? o[capitalize(key)];
    if (typeof v === 'string' && v !== '') return v;
    return def;
  };
  return {
    id: strField('id', fallback.id),
    name: strField('name', fallback.name),
    isBuiltin: boolField('isBuiltin', fallback.isBuiltin),
    ownerId: typeof o.ownerId === 'string' ? o.ownerId
      : (typeof o.OwnerId === 'string' ? o.OwnerId : null),
    handLimit: numField('handLimit', fallback.handLimit),
    maxScorePerHand: numField('maxScorePerHand', fallback.maxScorePerHand),
    allowWashout: boolField('allowWashout', fallback.allowWashout),
    allowKongRobbing: boolField('allowKongRobbing', fallback.allowKongRobbing),
    allowConcealedKongPromotion: boolField('allowConcealedKongPromotion', fallback.allowConcealedKongPromotion),
  };
}

function capitalize(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1);
}

// ── Fetch presets ──────────────────────────────────────────────────

export async function refreshRulePresets(): Promise<RulePreset[]> {
  try {
    const resp = await fetch(ENDPOINT_LIST, {
      method: 'GET',
      credentials: 'include',
      headers: { Accept: 'application/json' },
    });
    if (resp.status === 404) {
      cached = [CLASSIC_CHANGSHA];
      emitChange();
      return cached;
    }
    if (!resp.ok) {
      emitChange();
      return cached;
    }
    const body = await resp.json() as Record<string, unknown>;
    const arr = (body.presets ?? body.Presets) as unknown;
    if (Array.isArray(arr)) {
      const presets = arr.map((p) => normalisePreset(p));
      // Always guarantee Classic Changsha is in the list.
      if (presets.findIndex((p) => p.id === CLASSIC_CHANGSHA.id) === -1) {
        presets.unshift(CLASSIC_CHANGSHA);
      }
      cached = presets;
    }
  } catch {
    /* keep existing cached */
  }
  emitChange();
  return cached;
}

// ── Create / update / delete ───────────────────────────────────────

interface PresetMutation {
  ok: boolean;
  preset: RulePreset | null;
  error: string | null;
}

export async function createRulePreset(
  draft: Omit<RulePreset, 'id' | 'isBuiltin' | 'ownerId'>,
): Promise<PresetMutation> {
  try {
    const resp = await fetch(ENDPOINT_LIST, {
      method: 'POST',
      credentials: 'include',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(draft),
    });
    if (resp.status === 401 || resp.status === 403) {
      return { ok: false, preset: null, error: 'Sign in to save custom presets.' };
    }
    if (resp.status === 404) {
      return { ok: false, preset: null, error: 'Server does not support custom presets yet.' };
    }
    if (!resp.ok) {
      return { ok: false, preset: null, error: `Server rejected (${resp.status}).` };
    }
    const body = await resp.json() as unknown;
    const preset = normalisePreset(body, { ...CLASSIC_CHANGSHA, ...draft, isBuiltin: false });
    await refreshRulePresets();
    return { ok: true, preset, error: null };
  } catch (e) {
    return { ok: false, preset: null, error: e instanceof Error ? e.message : String(e) };
  }
}

export async function updateRulePreset(preset: RulePreset): Promise<PresetMutation> {
  if (preset.isBuiltin) {
    return { ok: false, preset: null, error: 'Built-in presets cannot be edited.' };
  }
  try {
    const resp = await fetch(presetEndpoint(preset.id), {
      method: 'PUT',
      credentials: 'include',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(preset),
    });
    if (!resp.ok) {
      return { ok: false, preset: null, error: `Server rejected (${resp.status}).` };
    }
    const body = await resp.json() as unknown;
    const updated = normalisePreset(body, preset);
    await refreshRulePresets();
    return { ok: true, preset: updated, error: null };
  } catch (e) {
    return { ok: false, preset: null, error: e instanceof Error ? e.message : String(e) };
  }
}

export async function deleteRulePreset(id: string): Promise<{ ok: boolean; error: string | null }> {
  if (id === CLASSIC_CHANGSHA.id) {
    return { ok: false, error: 'Built-in presets cannot be deleted.' };
  }
  try {
    const resp = await fetch(presetEndpoint(id), {
      method: 'DELETE',
      credentials: 'include',
    });
    if (!resp.ok) {
      return { ok: false, error: `Server rejected (${resp.status}).` };
    }
    if (readSelectedId() === id) {
      setSelectedPresetId(CLASSIC_CHANGSHA.id);
    }
    await refreshRulePresets();
    return { ok: true, error: null };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : String(e) };
  }
}

// ── DOM: lobby select + create-preset link ─────────────────────────

export function installRulePresetsUi(): void {
  if (installed) return;
  installed = true;
  wireLobbySelect();
  wireCreatePresetLink();
  renderLobbySelect();
  // Re-render the editor section whenever auth changes (the editor is
  // only available for authenticated users).
  onAuth(() => renderEditorPanel());
  void refreshRulePresets();
}

function wireLobbySelect(): void {
  const sel = document.getElementById('lobby-rule-preset-select') as HTMLSelectElement | null;
  if (sel === null) return;
  sel.addEventListener('change', () => {
    setSelectedPresetId(sel.value);
  });
}

function wireCreatePresetLink(): void {
  const link = document.getElementById('lobby-create-preset-link');
  if (link === null) return;
  link.addEventListener('click', (e) => {
    e.preventDefault();
    const auth = getAuthState();
    if (!auth.authenticated) {
      const signinBtn = document.getElementById('signin-button') as HTMLButtonElement | null;
      signinBtn?.click();
      return;
    }
    // Click the Wave-7 settings button to open the drawer …
    const settingsBtn = document.getElementById('settings-button') as HTMLButtonElement | null;
    settingsBtn?.click();
    // … then ask it to switch to the Rule presets tab.
    window.setTimeout(() => {
      const tab = document.getElementById('settings-tab-rule-presets');
      tab?.click();
    }, 60);
  });
}

function renderLobbySelect(): void {
  const sel = document.getElementById('lobby-rule-preset-select') as HTMLSelectElement | null;
  if (sel === null) return;
  const current = readSelectedId();
  sel.replaceChildren();
  for (const p of cached) {
    const opt = document.createElement('option');
    opt.value = p.id;
    opt.textContent = p.isBuiltin ? p.name : `${p.name} (custom)`;
    opt.setAttribute('data-testid', `lobby-rule-preset-option-${p.id}`);
    sel.appendChild(opt);
  }
  // Re-select the persisted id if it's still in the list.
  if (cached.some((p) => p.id === current)) {
    sel.value = current;
  } else {
    sel.value = CLASSIC_CHANGSHA.id;
  }
  // Ferro WP-E/#120 (Ripley C-2 ruling) — rule presets are NOT yet applied to
  // Changsha gameplay (the WS handshake never reads `?rulePreset=` and the
  // lobby no longer emits it). Keep the lobby picker disabled so it can't
  // imply an effect; the settings-panel editor below remains the real CRUD
  // surface for managing presets. Remove this once Bishop (WP-A) wires a
  // create-time rule-preset read.
  sel.disabled = true;
}

// ── DOM: settings-drawer "Rule presets" tab editor ─────────────────

let editorDraft: RulePreset | null = null;

export function renderEditorPanel(): void {
  const host = document.getElementById('settings-panel-rule-presets');
  if (host === null) return;
  host.replaceChildren();

  // Ferro WP-E/#131 — the rules engine does not yet read ANY preset knob, so
  // this editor authors inert gameplay settings. Surface an unmistakable,
  // i18n'd "preview / not yet functional" banner (shown for every auth state)
  // so a user can't believe a preset changes the game. Remove when #130 wires
  // the rules-engine semantics. (Backend / API CRUD is intentional + shipped,
  // so we only gate the "this affects your game" affordance, not the CRUD.)
  const notice = document.createElement('div');
  notice.className = 'rule-preset-preview-notice';
  notice.setAttribute('role', 'note');
  notice.setAttribute('data-testid', 'rule-preset-preview-notice');
  notice.textContent = t('settings.rule_presets.preview_notice');
  host.appendChild(notice);

  const auth = getAuthState();
  if (!auth.authenticated) {
    const p = document.createElement('p');
    p.className = 'settings-v2-hint';
    p.textContent = 'Sign in to create and edit custom rule presets.';
    host.appendChild(p);
    const signin = document.createElement('button');
    signin.type = 'button';
    signin.className = 'btn btn-sm btn-primary';
    signin.textContent = 'Sign in';
    signin.addEventListener('click', () => {
      const signinBtn = document.getElementById('signin-button') as HTMLButtonElement | null;
      signinBtn?.click();
    });
    host.appendChild(signin);
    return;
  }

  // Picker — choose which preset to edit (or "+ New preset").
  const pickerRow = document.createElement('div');
  pickerRow.className = 'settings-v2-field';
  const pickerLabel = document.createElement('span');
  pickerLabel.className = 'settings-v2-label';
  pickerLabel.textContent = 'Edit preset';
  pickerRow.appendChild(pickerLabel);
  const picker = document.createElement('select');
  picker.className = 'dark-select form-control form-control-sm';
  for (const p of cached) {
    const opt = document.createElement('option');
    opt.value = p.id;
    opt.textContent = p.isBuiltin ? `${p.name} (built-in — read-only)` : p.name;
    picker.appendChild(opt);
  }
  const newOpt = document.createElement('option');
  newOpt.value = '__new__';
  newOpt.textContent = '+ New preset';
  picker.appendChild(newOpt);
  picker.value = editorDraft !== null ? editorDraft.id : CLASSIC_CHANGSHA.id;
  pickerRow.appendChild(picker);
  host.appendChild(pickerRow);

  const draft = pickDraft(picker.value);
  editorDraft = draft;

  const form = buildEditorForm(draft, picker.value === '__new__' || !draft.isBuiltin);
  host.appendChild(form);

  picker.addEventListener('change', () => {
    editorDraft = pickDraft(picker.value);
    renderEditorPanel();
  });
}

function pickDraft(value: string): RulePreset {
  if (value === '__new__') {
    return {
      id: '',
      name: 'My preset',
      isBuiltin: false,
      ownerId: null,
      handLimit: 16,
      maxScorePerHand: 200,
      allowWashout: true,
      allowKongRobbing: true,
      allowConcealedKongPromotion: true,
    };
  }
  const found = cached.find((p) => p.id === value);
  if (found === undefined) return { ...CLASSIC_CHANGSHA };
  // Clone so the editor can mutate freely.
  return { ...found };
}

function buildEditorForm(draft: RulePreset, editable: boolean): HTMLDivElement {
  const form = document.createElement('div');
  form.className = 'settings-v2-rule-preset-form';

  const fields: ReadonlyArray<{
    key: keyof RulePreset;
    label: string;
    type: 'text' | 'number' | 'checkbox';
    min?: number;
    max?: number;
  }> = [
    { key: 'name', label: 'Name', type: 'text' },
    { key: 'handLimit', label: 'Hand limit', type: 'number', min: 1, max: 200 },
    { key: 'maxScorePerHand', label: 'Max score per hand', type: 'number', min: 1, max: 9999 },
    { key: 'allowWashout', label: 'Allow washout', type: 'checkbox' },
    { key: 'allowKongRobbing', label: 'Allow kong robbing', type: 'checkbox' },
    { key: 'allowConcealedKongPromotion', label: 'Allow concealed kong promotion', type: 'checkbox' },
  ];

  for (const f of fields) {
    const row = document.createElement('label');
    row.className = f.type === 'checkbox'
      ? 'settings-v2-field settings-v2-checkbox-row'
      : 'settings-v2-field';
    if (f.type !== 'checkbox') {
      const lbl = document.createElement('span');
      lbl.className = 'settings-v2-label';
      lbl.textContent = f.label;
      row.appendChild(lbl);
    }
    const input = document.createElement('input');
    input.type = f.type;
    input.className = f.type === 'checkbox' ? '' : 'settings-v2-input';
    input.setAttribute('data-testid', `rule-preset-edit-${f.key}`);
    if (f.min !== undefined) input.min = String(f.min);
    if (f.max !== undefined) input.max = String(f.max);
    input.disabled = !editable;
    if (f.type === 'checkbox') {
      input.checked = Boolean(draft[f.key]);
      input.addEventListener('change', () => {
        (draft as unknown as Record<string, unknown>)[f.key as string] = input.checked;
      });
    } else if (f.type === 'number') {
      input.value = String(draft[f.key]);
      input.addEventListener('input', () => {
        const n = parseInt(input.value, 10);
        if (!isNaN(n)) {
          (draft as unknown as Record<string, unknown>)[f.key as string] = n;
        }
      });
    } else {
      input.value = String(draft[f.key]);
      input.maxLength = 64;
      input.addEventListener('input', () => {
        (draft as unknown as Record<string, unknown>)[f.key as string] = input.value;
      });
    }
    row.appendChild(input);
    if (f.type === 'checkbox') {
      const lbl = document.createElement('span');
      lbl.textContent = f.label;
      row.appendChild(lbl);
    }
    form.appendChild(row);
  }

  const status = document.createElement('div');
  status.className = 'settings-v2-hint';
  status.setAttribute('aria-live', 'polite');
  form.appendChild(status);

  const actions = document.createElement('div');
  actions.className = 'settings-v2-rule-preset-actions';

  const save = document.createElement('button');
  save.type = 'button';
  save.className = 'btn btn-warning btn-sm';
  save.textContent = draft.id === '' ? 'Create preset' : 'Save preset';
  save.setAttribute('data-testid', 'rule-preset-save');
  save.disabled = !editable;
  save.addEventListener('click', async () => {
    save.disabled = true;
    status.textContent = draft.id === '' ? 'Creating…' : 'Saving…';
    let result: PresetMutation;
    if (draft.id === '') {
      result = await createRulePreset({
        name: draft.name,
        handLimit: draft.handLimit,
        maxScorePerHand: draft.maxScorePerHand,
        allowWashout: draft.allowWashout,
        allowKongRobbing: draft.allowKongRobbing,
        allowConcealedKongPromotion: draft.allowConcealedKongPromotion,
      });
    } else {
      result = await updateRulePreset(draft);
    }
    save.disabled = false;
    status.textContent = result.ok
      ? 'Saved ✓'
      : (result.error ?? 'Failed to save.');
    if (result.ok && result.preset !== null) {
      setSelectedPresetId(result.preset.id);
    }
  });
  actions.appendChild(save);

  if (editable && draft.id !== '' && !draft.isBuiltin) {
    const del = document.createElement('button');
    del.type = 'button';
    del.className = 'btn btn-secondary btn-sm';
    del.textContent = 'Delete preset';
    del.setAttribute('data-testid', 'rule-preset-delete');
    del.addEventListener('click', async () => {
      const ok = window.confirm(`Delete preset "${draft.name}"?`);
      if (!ok) return;
      del.disabled = true;
      status.textContent = 'Deleting…';
      const result = await deleteRulePreset(draft.id);
      del.disabled = false;
      status.textContent = result.ok ? 'Deleted ✓' : (result.error ?? 'Failed to delete.');
      if (result.ok) {
        editorDraft = null;
        renderEditorPanel();
      }
    });
    actions.appendChild(del);
  }

  form.appendChild(actions);
  return form;
}
