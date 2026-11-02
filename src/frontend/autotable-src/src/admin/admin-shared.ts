// Phase K Wave 18 — Hicks (Frontend).
//
// Shared admin-surface helpers backing the W18 admin operator UI
// for Bishop's three W17 CRUD controllers:
//
//   • POST/GET/PUT/DELETE /api/admin/replays/retention
//   • POST/GET/PUT/DELETE /api/admin/jwks-rotation/per-tenant
//   • POST/GET/PUT/DELETE /api/admin/signalr/retention
//
// All three follow Bishop's W17 unified wire contract:
//
//   • Auth ladder: 401 (no session) → 403 (non-admin) →
//     503 (per-tenant-disabled) → 200/201/204.
//   • `X-Admin-Reason` header MANDATORY on every write; empty /
//     whitespace fails closed (400).
//   • Wire-stable error payload `{ error: "...", ... }`.
//
// The W18 surface is a thin list+form pair (one per surface)
// driven by a tiny `AdminSurfaceSpec` descriptor.  Concrete
// surface modules (replay-retention.ts / jwks-rotation.ts /
// signalr-retention.ts) supply the descriptor; the shared
// runtime here owns:
//
//   • The overlay scaffold (mountAdminOverlay / closeAdminOverlay).
//   • The auth-gate ladder (gateAdminFetch — 401 → window.location='/',
//     403 → admins-only placeholder, 503 → disabled placeholder).
//   • The reason-prompt modal (mandatory X-Admin-Reason).
//   • The list table + create / edit / delete row actions.
//   • Defensive parsing helpers (parseDate, escapeHtml, fmtIso).
//
// Bundle math: target the entire admin/ directory bundles into
// a single `admin-panel.<hash>.js` chunk ≤ 40 KB.  vite.config.ts
// :manualChunks routes anything under `src/admin/` into that
// chunk so we ship one operator surface per chunk regardless of
// how many sub-surfaces accumulate.

export interface AdminFieldSpec {
  name: string;
  label: string;
  /** Input type: `text`, `number`, `datetime-local`, or `select`. */
  type: 'text' | 'number' | 'datetime-local' | 'select';
  required?: boolean;
  /** When `type === 'number'`: lower bound (inclusive). */
  min?: number;
  /** When `type === 'number'`: upper bound (inclusive). */
  max?: number;
  /** When `type === 'number'`: integer-only flag. */
  integer?: boolean;
  /** Placeholder hint, rendered in the input element. */
  placeholder?: string;
  /** Help text, rendered below the input. */
  help?: string;
  /** When `type === 'select'`: option list. */
  options?: Array<{ value: string; label: string }>;
  /** When true, the field is rendered read-only on EDIT but
   *  still required on CREATE.  (Used for the tenantId key.) */
  primaryKey?: boolean;
}

export interface AdminColumnSpec<TRow> {
  key: string;
  label: string;
  /** Render a cell value from a row.  Returns plain text or HTML;
   *  the renderer escapes plain text automatically and trusts HTML
   *  marked `__html: true`. */
  render: (row: TRow) => string | { __html: string };
}

export interface AdminSurfaceSpec<TRow, TBody> {
  /** Surface identifier — used for testids + the surface picker. */
  id: string;
  /** Human-readable title in the overlay header. */
  title: string;
  /** Short description rendered below the header. */
  description: string;
  /** Backend route base, e.g. `/api/admin/replays/retention`. */
  endpoint: string;
  /** Form fields for create + edit. */
  fields: AdminFieldSpec[];
  /** List view columns. */
  columns: AdminColumnSpec<TRow>[];
  /** Defensive parse: take an unknown row body, return null on
   *  malformed shape. */
  parseRow: (raw: unknown) => TRow | null;
  /** Extract the primary key (tenantId) from a row. */
  rowKey: (row: TRow) => string;
  /** Build a POST/PUT body from raw form values. */
  buildBody: (values: Record<string, string>) => TBody;
  /** Optional: pre-fill form values from an existing row (EDIT). */
  rowToFormValues?: (row: TRow) => Record<string, string>;
}

export const ADMIN_REASON_HEADER = 'X-Admin-Reason';
export const ADMIN_PANEL_OVERLAY_ID = 'admin-panel-overlay';

// ── DOM scaffolding ──────────────────────────────────────────────

export function escapeHtml(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

export function fmtIso(v: unknown): string {
  if (typeof v !== 'string' || v === '') return '—';
  // Normalise to YYYY-MM-DD HH:mm UTC for compact table rendering.
  const d = new Date(v);
  if (Number.isNaN(d.getTime())) return escapeHtml(v);
  const pad = (n: number): string => n.toString().padStart(2, '0');
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())} `
    + `${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}Z`;
}

export function mountAdminOverlay(): HTMLElement {
  let overlay = document.getElementById(ADMIN_PANEL_OVERLAY_ID);
  if (overlay !== null) {
    overlay.innerHTML = '';
    return overlay;
  }
  overlay = document.createElement('div');
  overlay.id = ADMIN_PANEL_OVERLAY_ID;
  overlay.className = 'admin-panel-overlay';
  overlay.setAttribute('role', 'dialog');
  overlay.setAttribute('aria-modal', 'false');
  overlay.setAttribute('data-testid', 'admin-panel-overlay');
  overlay.style.cssText =
    'position:fixed;inset:0;background:rgba(8,12,18,0.86);'
    + 'display:flex;flex-direction:column;z-index:9990;'
    + 'overflow:auto;color:#eaeaea;'
    + 'font-family:system-ui,Segoe UI,Helvetica,Arial,sans-serif;';
  document.body.appendChild(overlay);
  return overlay;
}

export function closeAdminOverlay(): void {
  const overlay = document.getElementById(ADMIN_PANEL_OVERLAY_ID);
  if (overlay !== null && overlay.parentNode !== null) {
    overlay.parentNode.removeChild(overlay);
  }
}

// ── Auth ladder ──────────────────────────────────────────────────

export interface GateResult {
  ok: boolean;
  /** When ok=false: a self-rendered placeholder describing the gate
   *  failure (admins-only / disabled / network) the surface can
   *  inject straight into the overlay body. */
  placeholderHtml?: string;
  status?: number;
}

/**
 * Fetch a JSON resource against an admin endpoint with the canonical
 * 401 → redirect / 403 → placeholder / 503 → placeholder ladder.
 * On 200 → returns `{ ok: true, body }`.  On any non-2xx the caller
 * receives a self-rendered placeholder it can splash into the body.
 */
export async function gateAdminFetch(
  url: string,
  init: RequestInit = {},
): Promise<GateResult & { body?: unknown }> {
  let resp: Response;
  try {
    resp = await fetch(url, {
      method: init.method ?? 'GET',
      credentials: 'same-origin',
      headers: {
        'Accept': 'application/json',
        ...(init.headers ?? {}),
      },
      body: init.body,
    });
  } catch {
    return { ok: false, placeholderHtml: renderPlaceholder(
      'Network error',
      'Admin endpoint unreachable.  Retry from the toolbar.',
    ) };
  }
  if (resp.status === 401) {
    redirectForSignIn();
    return { ok: false, status: 401, placeholderHtml: '' };
  }
  if (resp.status === 403) {
    return { ok: false, status: 403, placeholderHtml: renderPlaceholder(
      'Admins only',
      'This panel is reserved for users with the admin role.',
    ) };
  }
  if (resp.status === 503) {
    return { ok: false, status: 503, placeholderHtml: renderPlaceholder(
      'Surface disabled',
      'The per-tenant store for this surface is not registered '
      + 'on this deployment.  See Bishop\'s W17 controller docs.',
    ) };
  }
  if (resp.status === 204) {
    return { ok: true, status: 204 };
  }
  if (!resp.ok) {
    let detail = '';
    try {
      const j = await resp.json() as { error?: string; detail?: string };
      detail = `${j.error ?? ''}${j.detail !== undefined ? `: ${j.detail}` : ''}`;
    } catch {
      detail = `HTTP ${resp.status}`;
    }
    return { ok: false, status: resp.status, placeholderHtml: renderPlaceholder(
      `Request failed (${resp.status})`,
      detail || 'No detail returned.',
    ) };
  }
  let body: unknown;
  try {
    body = await resp.json();
  } catch {
    body = null;
  }
  return { ok: true, status: resp.status, body };
}

function redirectForSignIn(): void {
  try {
    window.location.assign('/');
  } catch { /* ignore */ }
}

function renderPlaceholder(title: string, message: string): string {
  return `
    <div class="admin-panel-placeholder" data-testid="admin-panel-placeholder">
      <h3>${escapeHtml(title)}</h3>
      <p>${escapeHtml(message)}</p>
    </div>`;
}

// ── X-Admin-Reason prompt ────────────────────────────────────────

/**
 * Block on a tiny modal that prompts the operator for an
 * `X-Admin-Reason` value.  Resolves to the trimmed string or
 * `null` when the operator dismisses without entering one.
 *
 * Implemented with `window.prompt` to keep the chunk lean — the
 * native prompt is keyboard-accessible by default and Playwright
 * automates it via `page.on('dialog')`.  A future wave can swap
 * to a richer inline modal if the operator UX demands one.
 */
export function promptAdminReason(action: string): string | null {
  // eslint-disable-next-line no-alert
  const raw = window.prompt(
    `Enter a short X-Admin-Reason for this ${action}.\n`
    + 'Required by Bishop\'s W17 audit contract.',
    '',
  );
  if (raw === null) return null;
  const trimmed = raw.trim();
  if (trimmed === '') return null;
  return trimmed;
}

// ── List + form rendering ────────────────────────────────────────

export function renderAdminListHtml<TRow, TBody>(
  spec: AdminSurfaceSpec<TRow, TBody>,
  rows: TRow[],
): string {
  // Phase K Wave 19 — read-only surfaces (spec.fields.length === 0)
  // suppress the per-row Edit / Delete affordances; the table emits
  // only data columns + the column header set (no Actions column).
  const isReadOnly = spec.fields.length === 0;
  if (rows.length === 0) {
    return `
      <div class="admin-panel-list" data-testid="admin-panel-${spec.id}-list">
        <p class="admin-panel-empty">${isReadOnly
          ? 'No audit rows recorded yet.'
          : 'No policies recorded.  Use <strong>Create</strong> to add one.'}</p>
      </div>`;
  }
  const head = spec.columns.map(
    (c) => `<th scope="col">${escapeHtml(c.label)}</th>`,
  ).join('');
  const actionsHead = isReadOnly ? '' : '<th scope="col">Actions</th>';
  const body = rows.map((row) => {
    const key = spec.rowKey(row);
    const cells = spec.columns.map((c) => {
      const v = c.render(row);
      const inner = typeof v === 'string' ? escapeHtml(v) : v.__html;
      return `<td>${inner}</td>`;
    }).join('');
    const actionCells = isReadOnly ? '' : `
        <td>
          <button type="button"
                  class="admin-panel-btn"
                  data-testid="admin-panel-${spec.id}-edit"
                  data-tenant-id="${escapeHtml(key)}"
                  data-action="edit">Edit</button>
          <button type="button"
                  class="admin-panel-btn admin-panel-btn-danger"
                  data-testid="admin-panel-${spec.id}-delete"
                  data-tenant-id="${escapeHtml(key)}"
                  data-action="delete">Delete</button>
        </td>`;
    return `
      <tr data-testid="admin-panel-${spec.id}-row"
          data-tenant-id="${escapeHtml(key)}">
        ${cells}${actionCells}
      </tr>`;
  }).join('');
  return `
    <div class="admin-panel-list" data-testid="admin-panel-${spec.id}-list">
      <table class="admin-panel-table">
        <thead><tr>${head}${actionsHead}</tr></thead>
        <tbody>${body}</tbody>
      </table>
    </div>`;
}

export function renderAdminFormHtml<TRow, TBody>(
  spec: AdminSurfaceSpec<TRow, TBody>,
  mode: 'create' | 'edit',
  values: Record<string, string>,
): string {
  const fieldsHtml = spec.fields.map((f) => {
    const v = values[f.name] ?? '';
    const readOnly = mode === 'edit' && f.primaryKey === true;
    const id = `admin-panel-${spec.id}-${f.name}`;
    const common =
      ` id="${id}" name="${escapeHtml(f.name)}"`
      + ` data-testid="${id}"`
      + (readOnly ? ' readonly' : '')
      + (f.required === true ? ' required' : '')
      + (f.placeholder !== undefined ? ` placeholder="${escapeHtml(f.placeholder)}"` : '');
    let input: string;
    if (f.type === 'select') {
      const opts = (f.options ?? []).map(
        (o) => `<option value="${escapeHtml(o.value)}"`
          + (o.value === v ? ' selected' : '') + `>${escapeHtml(o.label)}</option>`,
      ).join('');
      input = `<select${common}>${opts}</select>`;
    } else if (f.type === 'number') {
      input = `<input type="number"${common}`
        + (f.min !== undefined ? ` min="${f.min}"` : '')
        + (f.max !== undefined ? ` max="${f.max}"` : '')
        + (f.integer === true ? ' step="1"' : '')
        + ` value="${escapeHtml(v)}" />`;
    } else if (f.type === 'datetime-local') {
      input = `<input type="datetime-local"${common} value="${escapeHtml(v)}" />`;
    } else {
      input = `<input type="text"${common} value="${escapeHtml(v)}" />`;
    }
    return `
      <div class="admin-panel-field">
        <label for="${id}">${escapeHtml(f.label)}${f.required === true ? ' *' : ''}</label>
        ${input}
        ${f.help !== undefined ? `<small class="admin-panel-help">${escapeHtml(f.help)}</small>` : ''}
      </div>`;
  }).join('');
  return `
    <form class="admin-panel-form"
          data-testid="admin-panel-${spec.id}-form"
          data-mode="${mode}">
      <h3>${escapeHtml(mode === 'create' ? 'Create policy' : 'Edit policy')}</h3>
      ${fieldsHtml}
      <div class="admin-panel-form-actions">
        <button type="submit"
                class="admin-panel-btn admin-panel-btn-primary"
                data-testid="admin-panel-${spec.id}-save">
          ${escapeHtml(mode === 'create' ? 'Create' : 'Save')}
        </button>
        <button type="button"
                class="admin-panel-btn"
                data-testid="admin-panel-${spec.id}-cancel"
                data-action="cancel">Cancel</button>
      </div>
    </form>`;
}

export function collectFormValues(form: HTMLFormElement): Record<string, string> {
  const out: Record<string, string> = {};
  for (const el of Array.from(form.elements)) {
    if (el instanceof HTMLInputElement
      || el instanceof HTMLSelectElement
      || el instanceof HTMLTextAreaElement) {
      if (el.name !== '') out[el.name] = el.value;
    }
  }
  return out;
}
