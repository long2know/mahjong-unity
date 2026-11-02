// Phase K Wave 18 — Hicks (Frontend).
//
// Operator admin panel — single entry point that tabs across the
// Bishop CRUD/ops surfaces.  Lazy-loaded behind `?action=admin-
// panel` (see `action-router.ts:dispatchAdminPanel()`).
//
// Wire contract: see `admin-shared.ts` for the auth ladder, the
// `X-Admin-Reason` header convention, and the `AdminSurfaceSpec`
// descriptor shape.  Each surface module ships a const
// `*_SPEC` that this entry consumes uniformly.
//
// Bundle math: vite.config.ts:manualChunks splits everything under
// `src/admin/` into two chunks at W22 (was one at W18-W21):
//
//   • `admin-panel-core` — admin-panel.ts entry + admin-shared.ts
//     scaffolding + W18 baseline-CRUD surfaces (replay retention,
//     JWKS rotation, SignalR retention, rotation-policy family,
//     JWT rotation drill).  Always loaded with the entry.
//   • `admin-panel-tournaments` — all swiss/tournament surfaces
//     plus W19+ audit logs, SignalR ops, replay-chunked download,
//     cross-cutting audit-log browser, JWT emergency-revoke.
//     Lazy-loaded via `loadTournamentSpecs()` below the first
//     time the user activates a tab whose owner lives in this
//     chunk (or eagerly during render shell if the tournament
//     tab is the initial activation target).
//
// Each chunk targets ≤ 30 KB at W22 close.  See
// `docs/frontend-bundle-audit.md §3.7` for the audit reasoning.

import {
  type AdminSurfaceSpec,
  ADMIN_REASON_HEADER,
  closeAdminOverlay,
  collectFormValues,
  escapeHtml,
  gateAdminFetch,
  mountAdminOverlay,
  promptAdminReason,
  renderAdminFormHtml,
  renderAdminListHtml,
} from './admin-shared';
import { REPLAY_RETENTION_SPEC } from './replay-retention';
import { JWKS_ROTATION_SPEC } from './jwks-rotation';
import { SIGNALR_RETENTION_SPEC } from './signalr-retention';
import { ROTATION_POLICY_BULK_SPEC } from './rotation-policy-bulk';
import { ROTATION_POLICY_BULK_ACTIONS_SPEC } from './rotation-policy-bulk-actions';
import { ROTATION_SCHEDULE_SPEC } from './rotation-schedule';
import { JWT_ROTATION_DRILL_SPEC } from './jwt-rotation-drill';

interface AnySpec extends AdminSurfaceSpec<unknown, unknown> {}

// Type cast: the concrete specs are heterogeneous over their row /
// body types but uniform over the API used by this entry.  We
// erase the generics at the registry level and re-cast at the call
// sites that matter (row parsing returns `unknown`, which the
// per-surface `parseRow` narrows internally — the shared runtime
// never has to know the concrete row type).
//
// Phase K Wave 22 — chunk-split.  CORE_SURFACES is everything that
// statically lands in the `admin-panel-core` chunk; the tournament
// + audit + ops surfaces are lazy-loaded via `loadTournamentSpecs`
// into the `admin-panel-tournaments` chunk on the first activation.
const CORE_SURFACES: ReadonlyArray<AnySpec> = [
  REPLAY_RETENTION_SPEC as unknown as AnySpec,
  JWKS_ROTATION_SPEC as unknown as AnySpec,
  SIGNALR_RETENTION_SPEC as unknown as AnySpec,
  ROTATION_POLICY_BULK_SPEC as unknown as AnySpec,
  ROTATION_POLICY_BULK_ACTIONS_SPEC as unknown as AnySpec,
  ROTATION_SCHEDULE_SPEC as unknown as AnySpec,
  JWT_ROTATION_DRILL_SPEC as unknown as AnySpec,
];

let SURFACES: ReadonlyArray<AnySpec> = CORE_SURFACES;
let tournamentSpecsLoaded = false;
let tournamentSpecsPromise: Promise<ReadonlyArray<AnySpec>> | null = null;

/**
 * Lazy-load the `admin-panel-tournaments` chunk and merge its
 * specs into the `SURFACES` registry.  Idempotent — concurrent
 * callers share the same in-flight promise; subsequent calls
 * resolve to the cached `SURFACES` array.
 */
async function loadTournamentSpecs(): Promise<ReadonlyArray<AnySpec>> {
  if (tournamentSpecsLoaded) return SURFACES;
  if (tournamentSpecsPromise !== null) return tournamentSpecsPromise;
  tournamentSpecsPromise = (async (): Promise<ReadonlyArray<AnySpec>> => {
    try {
      const mod = await import('./admin-tournaments');
      SURFACES = [...CORE_SURFACES, ...mod.TOURNAMENT_SURFACES];
      tournamentSpecsLoaded = true;
      return SURFACES;
    } catch {
      // Chunk-load failure: degrade to CORE_SURFACES only so the
      // entry surfaces still render.  The dispatcher swallows the
      // exception so the operator at least sees the core tabs.
      tournamentSpecsLoaded = true;
      return SURFACES;
    }
  })();
  return tournamentSpecsPromise;
}

let activeSurfaceIndex = 0;

/**
 * Public mount: invoked by `action-router.ts:dispatchAdminPanel`
 * after the pre-flight admin probe passes.  Idempotent — calling
 * twice rebuilds the overlay rather than stacking it.
 *
 * W22 — kicks off the lazy `admin-panel-tournaments` chunk fetch
 * in the background so the tournament tabs become available
 * shortly after the overlay paints; the core tabs are clickable
 * immediately.
 */
export async function openAdminPanel(): Promise<void> {
  const overlay = mountAdminOverlay();
  overlay.innerHTML = renderShellHtml();
  attachShellHandlers(overlay);
  await loadActiveSurface(overlay);
  // Fire-and-forget the tournament chunk load so the tab strip
  // grows once the chunk lands; re-render the shell to surface
  // the new tabs.
  void loadTournamentSpecs().then(() => {
    if (!tournamentSpecsLoaded) return;
    const nav = overlay.querySelector('.admin-panel-tabs') as HTMLElement | null;
    if (nav !== null) nav.outerHTML = renderTabStripHtml();
  });
}

function renderShellHtml(): string {
  return `
    <div class="admin-panel-shell" data-testid="admin-panel-shell">
      <header class="admin-panel-header">
        <h1 class="admin-panel-title">Admin · Tenant policies</h1>
        <button type="button"
                class="admin-panel-close"
                data-testid="admin-panel-close"
                aria-label="Close admin panel">×</button>
      </header>
      ${renderTabStripHtml()}
      <section class="admin-panel-body" data-testid="admin-panel-body">
        <p>Loading…</p>
      </section>
    </div>`;
}

function renderTabStripHtml(): string {
  const tabs = SURFACES.map((s, i) => `
    <button type="button"
            class="admin-panel-tab${i === activeSurfaceIndex ? ' admin-panel-tab-active' : ''}"
            data-testid="admin-panel-tab-${s.id}"
            data-surface-index="${i}">
      ${escapeHtml(s.title)}
    </button>`).join('');
  return `<nav class="admin-panel-tabs" role="tablist">
        ${tabs}
      </nav>`;
}

function attachShellHandlers(overlay: HTMLElement): void {
  overlay.addEventListener('click', (ev: MouseEvent) => {
    const target = ev.target;
    if (!(target instanceof HTMLElement)) return;
    if (target.classList.contains('admin-panel-close')) {
      closeAdminOverlay();
      return;
    }
    if (target.classList.contains('admin-panel-tab')) {
      const idx = Number(target.getAttribute('data-surface-index'));
      if (Number.isInteger(idx) && idx >= 0 && idx < SURFACES.length) {
        activeSurfaceIndex = idx;
        // Re-render tab strip to highlight the active tab.
        const nav = overlay.querySelector('.admin-panel-tabs');
        if (nav !== null) {
          const tabs = nav.querySelectorAll('.admin-panel-tab');
          tabs.forEach((t, i) => {
            t.classList.toggle('admin-panel-tab-active', i === idx);
          });
        }
        void loadActiveSurface(overlay);
      }
    }
  });
}

async function loadActiveSurface(overlay: HTMLElement): Promise<void> {
  const spec = SURFACES[activeSurfaceIndex];
  const body = overlay.querySelector('.admin-panel-body') as HTMLElement | null;
  if (body === null) return;
  body.innerHTML = renderSurfaceLoading(spec);
  const res = await gateAdminFetch(spec.endpoint);
  if (!res.ok) {
    if (res.status === 401) {
      // The gate already initiated the redirect.  Leave the
      // overlay in place — the page will navigate away momentarily.
      return;
    }
    body.innerHTML = renderSurfaceFrame(spec, res.placeholderHtml ?? '');
    return;
  }
  const rows = parseListBody(spec, res.body);
  body.innerHTML = renderSurfaceFrame(spec, renderAdminListHtml(spec, rows));
  attachListHandlers(body, spec, rows);
}

function renderSurfaceLoading(spec: AnySpec): string {
  return renderSurfaceFrame(spec, `
    <p class="admin-panel-loading"
       data-testid="admin-panel-${spec.id}-loading">
      Loading ${escapeHtml(spec.title.toLowerCase())}…
    </p>`);
}

function renderSurfaceFrame(spec: AnySpec, innerHtml: string): string {
  // Phase K Wave 19 — read-only surfaces (e.g. Swiss pairing audit
  // log) omit the Create toolbar button when `spec.fields` is empty.
  // The Edit/Delete row buttons in `renderAdminListHtml` are not
  // emitted for read-only specs either (gated below).
  const isReadOnly = spec.fields.length === 0;
  const createBtn = isReadOnly ? '' : `
        <button type="button"
                class="admin-panel-btn admin-panel-btn-primary"
                data-testid="admin-panel-${spec.id}-create"
                data-action="create">+ Create</button>`;
  return `
    <article class="admin-panel-surface"
             data-testid="admin-panel-surface-${spec.id}">
      <header>
        <h2>${escapeHtml(spec.title)}</h2>
        <p class="admin-panel-description">${escapeHtml(spec.description)}</p>
      </header>
      <div class="admin-panel-toolbar">${createBtn}
        <button type="button"
                class="admin-panel-btn"
                data-testid="admin-panel-${spec.id}-refresh"
                data-action="refresh">Refresh</button>
      </div>
      <div class="admin-panel-surface-body"
           data-testid="admin-panel-${spec.id}-content">
        ${innerHtml}
      </div>
    </article>`;
}

function parseListBody(spec: AnySpec, body: unknown): unknown[] {
  if (body === null || typeof body !== 'object') return [];
  const o = body as { items?: unknown };
  const raw = Array.isArray(o.items) ? o.items : [];
  const out: unknown[] = [];
  for (const r of raw) {
    const parsed = spec.parseRow(r);
    if (parsed !== null) out.push(parsed);
  }
  return out;
}

function attachListHandlers(body: HTMLElement, spec: AnySpec, rows: unknown[]): void {
  const toolbar = body.querySelector(`[data-testid="admin-panel-${spec.id}-create"]`);
  toolbar?.addEventListener('click', () => {
    showForm(body, spec, 'create', {});
  });
  const refresh = body.querySelector(`[data-testid="admin-panel-${spec.id}-refresh"]`);
  refresh?.addEventListener('click', () => {
    const overlay = document.getElementById('admin-panel-overlay');
    if (overlay !== null) void loadActiveSurface(overlay);
  });
  body.querySelectorAll(`[data-testid="admin-panel-${spec.id}-edit"]`).forEach((btn) => {
    btn.addEventListener('click', () => {
      const tenantId = btn.getAttribute('data-tenant-id') ?? '';
      const row = rows.find((r) => spec.rowKey(r) === tenantId);
      if (row === undefined) return;
      const initial = spec.rowToFormValues !== undefined
        ? spec.rowToFormValues(row) : { tenantId };
      showForm(body, spec, 'edit', initial);
    });
  });
  body.querySelectorAll(`[data-testid="admin-panel-${spec.id}-delete"]`).forEach((btn) => {
    btn.addEventListener('click', () => {
      const tenantId = btn.getAttribute('data-tenant-id') ?? '';
      if (tenantId === '') return;
      void deleteRow(spec, tenantId);
    });
  });
}

function showForm(
  body: HTMLElement,
  spec: AnySpec,
  mode: 'create' | 'edit',
  values: Record<string, string>,
): void {
  const content = body.querySelector(`[data-testid="admin-panel-${spec.id}-content"]`) as HTMLElement | null;
  if (content === null) return;
  content.innerHTML = renderAdminFormHtml(spec, mode, values);
  const form = content.querySelector('form') as HTMLFormElement | null;
  if (form === null) return;
  form.addEventListener('submit', (ev: SubmitEvent) => {
    ev.preventDefault();
    const submitted = collectFormValues(form);
    void saveRow(spec, mode, submitted);
  });
  const cancel = content.querySelector(`[data-testid="admin-panel-${spec.id}-cancel"]`);
  cancel?.addEventListener('click', () => {
    const overlay = document.getElementById('admin-panel-overlay');
    if (overlay !== null) void loadActiveSurface(overlay);
  });
}

async function saveRow(
  spec: AnySpec,
  mode: 'create' | 'edit',
  values: Record<string, string>,
): Promise<void> {
  const tenantId = (values.tenantId ?? '').trim();
  if (tenantId === '') {
    // eslint-disable-next-line no-alert
    window.alert('tenantId is required.');
    return;
  }
  const reason = promptAdminReason(mode);
  if (reason === null) {
    // Operator dismissed the prompt — abort the write quietly.  The
    // backend's 400 would surface the same outcome but skipping the
    // RTT keeps the audit log clean.
    return;
  }
  const payload = spec.buildBody(values);
  const url = mode === 'create'
    ? spec.endpoint
    : `${spec.endpoint}/${encodeURIComponent(tenantId)}`;
  const method = mode === 'create' ? 'POST' : 'PUT';
  const res = await gateAdminFetch(url, {
    method,
    headers: {
      'Content-Type': 'application/json',
      [ADMIN_REASON_HEADER]: reason,
    },
    body: JSON.stringify(payload),
  });
  if (!res.ok) {
    // eslint-disable-next-line no-alert
    window.alert(`Save failed (HTTP ${res.status ?? '?'}).  See the panel for detail.`);
    const overlay = document.getElementById('admin-panel-overlay');
    if (overlay !== null) void loadActiveSurface(overlay);
    return;
  }
  // Re-load the list to show the new row.
  const overlay = document.getElementById('admin-panel-overlay');
  if (overlay !== null) void loadActiveSurface(overlay);
}

async function deleteRow(spec: AnySpec, tenantId: string): Promise<void> {
  // eslint-disable-next-line no-alert
  const confirmed = window.confirm(
    `Delete the ${spec.id} policy for "${tenantId}"?  This cannot be undone.`,
  );
  if (!confirmed) return;
  const reason = promptAdminReason('delete');
  if (reason === null) return;
  const res = await gateAdminFetch(
    `${spec.endpoint}/${encodeURIComponent(tenantId)}`,
    {
      method: 'DELETE',
      headers: { [ADMIN_REASON_HEADER]: reason },
    },
  );
  if (!res.ok && res.status !== 204) {
    // eslint-disable-next-line no-alert
    window.alert(`Delete failed (HTTP ${res.status ?? '?'}).`);
  }
  const overlay = document.getElementById('admin-panel-overlay');
  if (overlay !== null) void loadActiveSurface(overlay);
}
