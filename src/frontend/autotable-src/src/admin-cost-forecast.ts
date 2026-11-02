// Phase K Wave 15 — Hicks (Frontend).
//
// Renders the `?action=cost-forecast&days=<n>` overlay surface
// against Bishop's W15 `GET /api/commentary/cost/forecast?days=<n>`
// endpoint.  Admin-only client: on 401 the dispatch in
// `action-router.ts` redirects to `/` (sign-in modal); on 403 this
// module renders an "Admins only" placeholder.
//
// W15 is the foundation: a small summary card with three signals
// — projected month-end cost, confidence, and days-of-data — so the
// frontend has a measurable hook to attach Phase L cost-dashboard
// charts to later.  Kept deliberately minimal so the lazy chunk
// stays sub-3 kB (no charting dependency).
//
// Wire shape (Bishop W15, defensively parsed — same posture as
// `admin-cost.ts`):
//
//   GET /api/commentary/cost/forecast?days=<n>
//     → 200 {
//         projectedMonthEndCostUsd:  number   // USD
//         confidence:                number   // 0-1 (or 0-100)
//         daysOfData:                number   // integer
//         windowDays:                number   // requested window
//         currency?:                 string   // optional, default "USD"
//       }
//     → 400 → "Invalid forecast window" placeholder
//     → 401 → caller redirects to `/`
//     → 403 → "Admins only" placeholder
//     → 404 → "Cost forecast not available" placeholder
//     → 5xx → "Cost forecast unavailable" placeholder
//
// Defensive aliases tolerated (Bishop W14 conventions):
//   • `projectedCostUsd` / `projectedCost` instead of
//     `projectedMonthEndCostUsd`.
//   • `daysWithData` instead of `daysOfData`.
//   • Confidence as percentage (0-100) OR fraction (0-1) — auto-
//     detected the same way `admin-cost.ts` handles `percentUsed`.

interface RawForecast {
  projectedMonthEndCostUsd?: unknown;
  projectedCostUsd?: unknown;
  projectedCost?: unknown;
  confidence?: unknown;
  daysOfData?: unknown;
  daysWithData?: unknown;
  windowDays?: unknown;
  currency?: unknown;
}

interface Forecast {
  projectedCostUsd: number;
  confidence: number;      // canonical: 0-100
  daysOfData: number;
  windowDays: number;
  currency: string;
}

const OVERLAY_ID = 'admin-cost-forecast-overlay';

function parseForecast(body: unknown): Forecast | null {
  if (body === null || typeof body !== 'object') return null;
  const obj = body as RawForecast;
  const projectedRaw =
    typeof obj.projectedMonthEndCostUsd === 'number' && Number.isFinite(obj.projectedMonthEndCostUsd)
      ? obj.projectedMonthEndCostUsd
      : (typeof obj.projectedCostUsd === 'number' && Number.isFinite(obj.projectedCostUsd)
        ? obj.projectedCostUsd
        : (typeof obj.projectedCost === 'number' && Number.isFinite(obj.projectedCost)
          ? obj.projectedCost
          : null));
  if (projectedRaw === null) return null;

  let confidence = typeof obj.confidence === 'number' && Number.isFinite(obj.confidence)
    ? obj.confidence
    : 0;
  // Normalise 0-1 fractions to 0-100 (Bishop W15 has both
  // conventions in flight depending on the per-tenant config).
  if (confidence > 0 && confidence <= 1) confidence = confidence * 100;
  confidence = Math.max(0, Math.min(100, confidence));

  const daysRaw = typeof obj.daysOfData === 'number' && Number.isFinite(obj.daysOfData)
    ? obj.daysOfData
    : (typeof obj.daysWithData === 'number' && Number.isFinite(obj.daysWithData)
      ? obj.daysWithData
      : 0);
  const days = Math.max(0, Math.floor(daysRaw));

  const windowRaw = typeof obj.windowDays === 'number' && Number.isFinite(obj.windowDays)
    ? obj.windowDays
    : days;
  const windowDays = Math.max(1, Math.floor(windowRaw));

  const currency = typeof obj.currency === 'string' && obj.currency !== ''
    ? obj.currency
    : 'USD';

  return {
    projectedCostUsd: Math.max(0, projectedRaw),
    confidence,
    daysOfData: days,
    windowDays,
    currency,
  };
}

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function fmtCurrency(n: number, currency: string): string {
  // Hard-coded `$` for USD to keep parity with admin-cost.ts; other
  // currencies render with their code prefix.  W16 can graduate to
  // Intl.NumberFormat once we have a real i18n locale plumb.
  if (currency === 'USD') return `$${n.toFixed(2)}`;
  return `${currency} ${n.toFixed(2)}`;
}

function fmtPct(n: number): string {
  return `${n.toFixed(1)}%`;
}

function confidenceClass(pct: number): string {
  // Mirror admin-cost.ts threshold language for visual consistency.
  if (pct >= 80) return 'admin-cost-forecast-conf admin-cost-forecast-conf-strong';
  if (pct >= 50) return 'admin-cost-forecast-conf admin-cost-forecast-conf-moderate';
  return 'admin-cost-forecast-conf admin-cost-forecast-conf-weak';
}

function ensureOverlay(): HTMLElement {
  let overlay = document.getElementById(OVERLAY_ID) as HTMLDivElement | null;
  if (overlay !== null) {
    overlay.innerHTML = '';
    return overlay;
  }
  overlay = document.createElement('div');
  overlay.id = OVERLAY_ID;
  overlay.className = 'admin-cost-forecast-overlay';
  overlay.setAttribute('role', 'dialog');
  overlay.setAttribute('aria-modal', 'false');
  overlay.setAttribute('data-testid', 'admin-cost-forecast-overlay');
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
    <div class="admin-cost-forecast-card" data-testid="admin-cost-forecast-empty">
      <header class="admin-cost-forecast-header">
        <h2 class="admin-cost-forecast-title">${escapeHtml(title)}</h2>
        <button type="button" class="admin-cost-forecast-close"
                data-testid="admin-cost-forecast-close"
                aria-label="Close forecast panel">×</button>
      </header>
      <p class="admin-cost-forecast-empty-message">${escapeHtml(message)}</p>
    </div>`;
}

function renderSummary(f: Forecast): string {
  const confC = confidenceClass(f.confidence);
  return `
    <div class="admin-cost-forecast-card" data-testid="admin-cost-forecast-card">
      <header class="admin-cost-forecast-header">
        <h2 class="admin-cost-forecast-title">Commentary cost forecast</h2>
        <button type="button" class="admin-cost-forecast-close"
                data-testid="admin-cost-forecast-close"
                aria-label="Close forecast panel">×</button>
      </header>
      <section class="admin-cost-forecast-summary"
               data-testid="admin-cost-forecast-summary">
        <div class="admin-cost-forecast-summary-line">
          <span class="admin-cost-forecast-summary-label">Projected month-end</span>
          <strong class="admin-cost-forecast-summary-value"
                  data-testid="admin-cost-forecast-projected">${escapeHtml(fmtCurrency(f.projectedCostUsd, f.currency))}</strong>
        </div>
        <div class="admin-cost-forecast-summary-line">
          <span class="admin-cost-forecast-summary-label">Confidence</span>
          <span class="${confC}"
                data-testid="admin-cost-forecast-confidence">${escapeHtml(fmtPct(f.confidence))}</span>
        </div>
        <div class="admin-cost-forecast-summary-line">
          <span class="admin-cost-forecast-summary-label">Days of data</span>
          <span class="admin-cost-forecast-summary-value"
                data-testid="admin-cost-forecast-days">${escapeHtml(String(f.daysOfData))} / ${escapeHtml(String(f.windowDays))}</span>
        </div>
      </section>
    </div>`;
}

function wireCloseHandler(overlay: HTMLElement): void {
  const closeBtn = overlay.querySelector<HTMLButtonElement>('.admin-cost-forecast-close');
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
 * Normalise the requested forecast window.  Returns a number
 * within `[1, 90]` to keep us inside Bishop's documented forecast
 * envelope (W15 endpoint rejects windows beyond 90 days as 400).
 * Default = 30 days when the caller omits or fat-fingers the
 * value.
 */
export function normaliseDays(raw: unknown): number {
  const n = typeof raw === 'number' ? raw : Number(raw);
  if (!Number.isFinite(n)) return 30;
  const floored = Math.floor(n);
  if (floored < 1) return 1;
  if (floored > 90) return 90;
  return floored;
}

/**
 * Public entry point — fetches Bishop W15 forecast and mounts
 * the overlay.  Idempotent.
 */
export async function openCommentaryCostForecastPanel(days: number): Promise<void> {
  const windowDays = normaliseDays(days);
  const overlay = ensureOverlay();
  overlay.innerHTML = `
    <div class="admin-cost-forecast-card" data-testid="admin-cost-forecast-loading">
      <header class="admin-cost-forecast-header">
        <h2 class="admin-cost-forecast-title">Commentary cost forecast</h2>
        <button type="button" class="admin-cost-forecast-close"
                data-testid="admin-cost-forecast-close"
                aria-label="Close forecast panel">×</button>
      </header>
      <p class="admin-cost-forecast-loading-message">Forecasting against ${windowDays}-day window…</p>
    </div>`;
  wireCloseHandler(overlay);

  let resp: Response;
  try {
    resp = await fetch(`/api/commentary/cost/forecast?days=${windowDays}`, {
      credentials: 'same-origin',
      headers: { 'Accept': 'application/json' },
    });
  } catch {
    overlay.innerHTML = renderEmpty('Commentary cost forecast',
      'Could not reach the cost forecast service.');
    wireCloseHandler(overlay);
    return;
  }

  if (resp.status === 401) {
    closeOverlay();
    redirectToLobbyForSignIn();
    return;
  }
  if (resp.status === 400) {
    overlay.innerHTML = renderEmpty('Commentary cost forecast',
      'Invalid forecast window — pick a value between 1 and 90 days.');
    wireCloseHandler(overlay);
    return;
  }
  if (resp.status === 403) {
    overlay.innerHTML = renderEmpty('Commentary cost forecast',
      'Admins only — this surface is gated to admin accounts.');
    wireCloseHandler(overlay);
    return;
  }
  if (resp.status === 404) {
    overlay.innerHTML = renderEmpty('Commentary cost forecast',
      'Cost forecast not available.');
    wireCloseHandler(overlay);
    return;
  }
  if (!resp.ok) {
    overlay.innerHTML = renderEmpty('Commentary cost forecast',
      'Cost forecast unavailable.');
    wireCloseHandler(overlay);
    return;
  }

  let body: unknown;
  try {
    body = await resp.json();
  } catch {
    overlay.innerHTML = renderEmpty('Commentary cost forecast',
      'Cost forecast response malformed.');
    wireCloseHandler(overlay);
    return;
  }

  const forecast = parseForecast(body);
  if (forecast === null) {
    overlay.innerHTML = renderEmpty('Commentary cost forecast',
      'Cost forecast response malformed.');
    wireCloseHandler(overlay);
    return;
  }
  overlay.innerHTML = renderSummary(forecast);
  wireCloseHandler(overlay);
}
