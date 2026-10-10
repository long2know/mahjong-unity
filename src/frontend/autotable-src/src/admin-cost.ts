// Phase K Wave 14 — Hicks (Frontend).
//
// Renders the `?action=admin-cost` overlay surface against Bishop's
// W14 `GET /api/commentary/cost/summary` endpoint.  Admin-only
// client: on 401 the dispatch redirects to `/` so the sign-in modal
// can mount (the redirect contract matches W13
// `dispatchSpectateWithGameId()`'s 401 path); on 403 the panel
// renders a "Admins only" placeholder.
//
// The W14 ask is "summary card + byModel table" — kept deliberately
// minimal so the cost overlay ships as a thin lazy chunk shared
// with no other surface.  A future wave can graduate this into a
// full cost-dashboard with charts (Phase L candidate).
//
// Wire shape (Bishop W14, defensively parsed):
//
//   GET /api/commentary/cost/summary
//     → 200 {
//         currentMonthCost:  number      // USD
//         budgetCapUsd:      number      // USD
//         percentUsed:       number      // 0-100 (or 0-1; defensive)
//         byModel:           ModelRow[]
//       }
//     → 401 → caller redirects to `/`
//     → 403 → "Admins only" placeholder
//     → 404 → "Cost summary not available" placeholder
//     → 5xx → "Cost summary unavailable" placeholder
//
//   ModelRow {
//     model        string
//     costUsd?     number
//     cost?        number          // tolerated alias
//     callCount?   number
//     calls?       number          // tolerated alias
//   }
//
// The `redirectToLobbyForSignIn` path is owned by `action-router.ts`
// (it pre-flights an admin probe via `/api/auth/me` before invoking
// this module's `openCommentaryCostPanel()` so the 401 surface is
// caught before the panel mount).  This module still treats 401 as
// "send the user home" as a defensive fall-back if the probe-based
// gating ever drifts.

interface RawModelRow {
  model?: unknown;
  modelName?: unknown;
  costUsd?: unknown;
  cost?: unknown;
  callCount?: unknown;
  calls?: unknown;
}

interface ModelRow {
  model: string;
  costUsd: number;
  callCount: number | null;
}

interface CostSummary {
  currentMonthCost: number;
  budgetCapUsd: number;
  percentUsed: number;
  byModel: ModelRow[];
}

const OVERLAY_ID = 'admin-cost-overlay';

function parseModelRow(raw: RawModelRow): ModelRow | null {
  const name = typeof raw.model === 'string' && raw.model !== ''
    ? raw.model
    : (typeof raw.modelName === 'string' && raw.modelName !== '' ? raw.modelName : null);
  if (name === null) return null;
  const cost = typeof raw.costUsd === 'number' && Number.isFinite(raw.costUsd)
    ? raw.costUsd
    : (typeof raw.cost === 'number' && Number.isFinite(raw.cost) ? raw.cost : 0);
  const callsRaw = typeof raw.callCount === 'number' && Number.isFinite(raw.callCount)
    ? raw.callCount
    : (typeof raw.calls === 'number' && Number.isFinite(raw.calls) ? raw.calls : NaN);
  return {
    model: name,
    costUsd: Math.max(0, cost),
    callCount: Number.isFinite(callsRaw) ? Math.max(0, Math.floor(callsRaw)) : null,
  };
}

function parseSummary(body: unknown): CostSummary | null {
  if (body === null || typeof body !== 'object') return null;
  const obj = body as {
    currentMonthCost?: unknown;
    budgetCapUsd?: unknown;
    percentUsed?: unknown;
    byModel?: unknown;
  };
  const cur = typeof obj.currentMonthCost === 'number' && Number.isFinite(obj.currentMonthCost)
    ? Math.max(0, obj.currentMonthCost)
    : 0;
  const cap = typeof obj.budgetCapUsd === 'number' && Number.isFinite(obj.budgetCapUsd)
    ? Math.max(0, obj.budgetCapUsd)
    : 0;
  let pct = typeof obj.percentUsed === 'number' && Number.isFinite(obj.percentUsed)
    ? obj.percentUsed
    : (cap > 0 ? (cur / cap) * 100 : 0);
  // Tolerate emitters that ship 0-1 fractions.
  if (pct > 0 && pct <= 1 && cur > 0 && cap > 0 && Math.abs(pct - (cur / cap)) < 0.05) {
    pct = pct * 100;
  }
  pct = Math.max(0, pct);
  const rowsRaw: unknown[] = Array.isArray(obj.byModel) ? obj.byModel : [];
  const rows: ModelRow[] = [];
  for (const r of rowsRaw) {
    if (r === null || typeof r !== 'object') continue;
    const parsed = parseModelRow(r as RawModelRow);
    if (parsed !== null) rows.push(parsed);
  }
  rows.sort((a, b) => b.costUsd - a.costUsd);
  return { currentMonthCost: cur, budgetCapUsd: cap, percentUsed: pct, byModel: rows };
}

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function fmtUsd(n: number): string {
  return `$${n.toFixed(2)}`;
}

function fmtPct(n: number): string {
  return `${n.toFixed(1)}%`;
}

function pctClass(pct: number): string {
  if (pct >= 95) return 'admin-cost-pct admin-cost-pct-critical';
  if (pct >= 80) return 'admin-cost-pct admin-cost-pct-warn';
  return 'admin-cost-pct admin-cost-pct-ok';
}

function ensureOverlay(): HTMLElement {
  let overlay = document.getElementById(OVERLAY_ID) as HTMLDivElement | null;
  if (overlay !== null) {
    overlay.innerHTML = '';
    return overlay;
  }
  overlay = document.createElement('div');
  overlay.id = OVERLAY_ID;
  overlay.className = 'admin-cost-overlay';
  overlay.setAttribute('role', 'dialog');
  overlay.setAttribute('aria-modal', 'false');
  overlay.setAttribute('data-testid', 'admin-cost-overlay');
  document.body.appendChild(overlay);
  return overlay;
}

function closeOverlay(): void {
  const overlay = document.getElementById(OVERLAY_ID);
  if (overlay !== null && overlay.parentNode !== null) {
    overlay.parentNode.removeChild(overlay);
  }
}

function renderEmpty(title: string, message: string): string {
  return `
    <div class="admin-cost-card" data-testid="admin-cost-empty">
      <header class="admin-cost-header">
        <h2 class="admin-cost-title">${escapeHtml(title)}</h2>
        <button type="button" class="admin-cost-close"
                data-testid="admin-cost-close"
                aria-label="Close cost panel">×</button>
      </header>
      <p class="admin-cost-empty-message">${escapeHtml(message)}</p>
    </div>`;
}

function renderSummary(s: CostSummary): string {
  const pctC = pctClass(s.percentUsed);
  const rows = s.byModel.map((r) => `
    <tr data-testid="admin-cost-model-row" data-model="${escapeHtml(r.model)}">
      <td>${escapeHtml(r.model)}</td>
      <td class="admin-cost-num">${escapeHtml(fmtUsd(r.costUsd))}</td>
      <td class="admin-cost-num">${r.callCount !== null ? escapeHtml(String(r.callCount)) : '—'}</td>
    </tr>`).join('');
  const tableBody = rows !== ''
    ? rows
    : '<tr><td colspan="3" class="admin-cost-table-empty">No per-model cost data yet.</td></tr>';
  return `
    <div class="admin-cost-card" data-testid="admin-cost-card">
      <header class="admin-cost-header">
        <h2 class="admin-cost-title">Commentary cost summary</h2>
        <button type="button" class="admin-cost-close"
                data-testid="admin-cost-close"
                aria-label="Close cost panel">×</button>
      </header>
      <section class="admin-cost-summary" data-testid="admin-cost-summary">
        <div class="admin-cost-summary-line">
          <span class="admin-cost-summary-label">Current month</span>
          <strong class="admin-cost-summary-value"
                  data-testid="admin-cost-current">${escapeHtml(fmtUsd(s.currentMonthCost))}</strong>
          <span class="admin-cost-summary-separator">/</span>
          <span class="admin-cost-summary-cap"
                data-testid="admin-cost-cap">${escapeHtml(fmtUsd(s.budgetCapUsd))}</span>
          <span class="${pctC}"
                data-testid="admin-cost-percent">${escapeHtml(fmtPct(s.percentUsed))}</span>
        </div>
      </section>
      <table class="admin-cost-table" data-testid="admin-cost-table">
        <thead>
          <tr>
            <th scope="col">Model</th>
            <th scope="col">Cost</th>
            <th scope="col">Calls</th>
          </tr>
        </thead>
        <tbody>${tableBody}</tbody>
      </table>
    </div>`;
}

function wireCloseHandler(overlay: HTMLElement): void {
  const closeBtn = overlay.querySelector<HTMLButtonElement>('.admin-cost-close');
  closeBtn?.addEventListener('click', () => {
    closeOverlay();
    try {
      const url = new URL(window.location.href);
      url.pathname = '/';
      url.search = '';
      window.history.replaceState(window.history.state, '', url.pathname + url.search + url.hash);
    } catch { /* ignore */ }
  });
}

function redirectToLobbyForSignIn(): void {
  try {
    const url = new URL(window.location.href);
    url.pathname = '/';
    url.search = '';
    url.hash = '';
    window.location.replace(url.toString());
  } catch {
    window.location.href = '/';
  }
}

/**
 * Public entry point — fetches Bishop W14 cost summary and mounts
 * the overlay.  Idempotent.
 *
 * 401 redirects to `/` so `installAuthUi()` mounts the sign-in
 * modal (matches the W13 `dispatchSpectateWithGameId()` contract).
 * 403 renders an "Admins only" placeholder (the surface is still
 * visible so a non-admin who lands on the URL gets a clear no-go
 * message instead of bouncing back to the lobby with no context).
 */
export async function openCommentaryCostPanel(): Promise<void> {
  const overlay = ensureOverlay();
  overlay.innerHTML = `
    <div class="admin-cost-card" data-testid="admin-cost-loading">
      <header class="admin-cost-header">
        <h2 class="admin-cost-title">Commentary cost summary</h2>
        <button type="button" class="admin-cost-close"
                data-testid="admin-cost-close"
                aria-label="Close cost panel">×</button>
      </header>
      <p class="admin-cost-loading-message">Loading cost summary…</p>
    </div>`;
  wireCloseHandler(overlay);

  let resp: Response;
  try {
    resp = await fetch('/api/commentary/cost/summary', {
      credentials: 'same-origin',
      headers: { 'Accept': 'application/json' },
    });
  } catch {
    overlay.innerHTML = renderEmpty('Commentary cost summary',
      'Could not reach the cost summary service.');
    wireCloseHandler(overlay);
    return;
  }

  if (resp.status === 401) {
    closeOverlay();
    redirectToLobbyForSignIn();
    return;
  }
  if (resp.status === 403) {
    overlay.innerHTML = renderEmpty('Commentary cost summary',
      'Admins only — this surface is gated to admin accounts.');
    wireCloseHandler(overlay);
    return;
  }
  if (resp.status === 404) {
    overlay.innerHTML = renderEmpty('Commentary cost summary',
      'Cost summary not available.');
    wireCloseHandler(overlay);
    return;
  }
  if (!resp.ok) {
    overlay.innerHTML = renderEmpty('Commentary cost summary',
      'Cost summary unavailable.');
    wireCloseHandler(overlay);
    return;
  }

  let body: unknown;
  try {
    body = await resp.json();
  } catch {
    overlay.innerHTML = renderEmpty('Commentary cost summary',
      'Cost summary response malformed.');
    wireCloseHandler(overlay);
    return;
  }

  const summary = parseSummary(body);
  if (summary === null) {
    overlay.innerHTML = renderEmpty('Commentary cost summary',
      'Cost summary response malformed.');
    wireCloseHandler(overlay);
    return;
  }
  overlay.innerHTML = renderSummary(summary);
  wireCloseHandler(overlay);
}
