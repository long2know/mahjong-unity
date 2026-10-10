// Phase K Wave 14 — Hicks (Frontend).
//
// Renders the `?action=replays` listing overlay.  Bishop's W14
// `GET /api/replays` endpoint returns a metadata-only listing
// (no event arrays) so the lobby can show recent completed games
// without pulling the per-replay payload until the user picks one.
//
// Each row links to `?action=replay&replayId=<id>`, which is the
// W12 deep-link the action-router already handles (fetches the
// full replay via `GET /api/replays/{replayId}` and opens the
// in-page viewer).
//
// Wire shape (Bishop W14, defensively parsed):
//
//   GET /api/replays
//     → 200 { replays: ReplayMeta[] }   - canonical
//     → 200 [ ReplayMeta, ... ]         - tolerated bare array
//     → 404                             - "No replays found" placeholder
//     → 5xx / network                   - "Replays unavailable" placeholder
//
//   ReplayMeta {
//     replayId      string
//     gameId?       string             - some emitters dual-emit
//     completedAt?  string             - ISO-8601 UTC
//     variant?      string             - e.g. "changsha" / "japanese"
//     turnCount?    number
//   }
//
// Defensive contract — any missing field renders the cell as "—".

interface RawReplayMeta {
  replayId?: unknown;
  id?: unknown;
  gameId?: unknown;
  completedAt?: unknown;
  completedAtUtc?: unknown;
  variant?: unknown;
  turnCount?: unknown;
}

interface ReplayMeta {
  replayId: string;
  completedAt: string;
  variant: string;
  turnCount: number | null;
}

const OVERLAY_ID = 'replays-listing-overlay';

function parseReplayMeta(raw: RawReplayMeta): ReplayMeta | null {
  const rid = typeof raw.replayId === 'string' && raw.replayId !== ''
    ? raw.replayId
    : (typeof raw.id === 'string' && raw.id !== '' ? raw.id : null);
  if (rid === null) return null;
  const completed = typeof raw.completedAt === 'string' && raw.completedAt !== ''
    ? raw.completedAt
    : (typeof raw.completedAtUtc === 'string' ? raw.completedAtUtc : '');
  const variant = typeof raw.variant === 'string' ? raw.variant : '';
  const turnCount = typeof raw.turnCount === 'number' && Number.isFinite(raw.turnCount)
    ? Math.max(0, Math.floor(raw.turnCount))
    : null;
  return { replayId: rid, completedAt: completed, variant, turnCount };
}

function extractReplayArray(body: unknown): ReplayMeta[] {
  let arr: unknown[] | null = null;
  if (Array.isArray(body)) {
    arr = body;
  } else if (body !== null && typeof body === 'object') {
    const obj = body as { replays?: unknown; items?: unknown };
    if (Array.isArray(obj.replays)) arr = obj.replays;
    else if (Array.isArray(obj.items)) arr = obj.items;
  }
  if (arr === null) return [];
  const out: ReplayMeta[] = [];
  for (const r of arr) {
    if (r === null || typeof r !== 'object') continue;
    const parsed = parseReplayMeta(r as RawReplayMeta);
    if (parsed !== null) out.push(parsed);
  }
  return out;
}

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function formatCompletedAt(iso: string): string {
  if (iso === '') return '—';
  const dt = new Date(iso);
  if (Number.isNaN(dt.getTime())) return iso;
  // Locale-friendly default: yyyy-mm-dd HH:MM UTC.  We use UTC so
  // share-link snapshots match across timezones (no "Yesterday" /
  // "2 hours ago" relative renders here — keep it operator-facing).
  const y = dt.getUTCFullYear();
  const m = String(dt.getUTCMonth() + 1).padStart(2, '0');
  const d = String(dt.getUTCDate()).padStart(2, '0');
  const hh = String(dt.getUTCHours()).padStart(2, '0');
  const mm = String(dt.getUTCMinutes()).padStart(2, '0');
  return `${y}-${m}-${d} ${hh}:${mm} UTC`;
}

function ensureOverlay(): HTMLElement {
  let overlay = document.getElementById(OVERLAY_ID) as HTMLDivElement | null;
  if (overlay !== null) {
    overlay.innerHTML = '';
    return overlay;
  }
  overlay = document.createElement('div');
  overlay.id = OVERLAY_ID;
  overlay.className = 'replays-listing-overlay';
  overlay.setAttribute('role', 'dialog');
  overlay.setAttribute('aria-modal', 'false');
  overlay.setAttribute('data-testid', 'replays-listing-overlay');
  document.body.appendChild(overlay);
  return overlay;
}

function closeOverlay(): void {
  const overlay = document.getElementById(OVERLAY_ID);
  if (overlay !== null && overlay.parentNode !== null) {
    overlay.parentNode.removeChild(overlay);
  }
}

function renderEmpty(message: string): string {
  return `
    <div class="replays-listing-card" data-testid="replays-listing-empty">
      <header class="replays-listing-header">
        <h2 class="replays-listing-title">Recent replays</h2>
        <button type="button" class="replays-listing-close"
                data-testid="replays-listing-close"
                aria-label="Close replays overlay">×</button>
      </header>
      <p class="replays-listing-empty-message">${escapeHtml(message)}</p>
    </div>`;
}

function renderTable(rows: ReplayMeta[]): string {
  const tbody = rows.map((row) => {
    const url = `/?action=replay&replayId=${encodeURIComponent(row.replayId)}`;
    return `
      <tr data-testid="replays-listing-row" data-replay-id="${escapeHtml(row.replayId)}">
        <td>${escapeHtml(formatCompletedAt(row.completedAt))}</td>
        <td>${escapeHtml(row.variant !== '' ? row.variant : '—')}</td>
        <td>${row.turnCount !== null ? escapeHtml(String(row.turnCount)) : '—'}</td>
        <td>
          <a class="replays-listing-link" href="${escapeHtml(url)}"
             data-testid="replays-listing-open">Open replay</a>
        </td>
      </tr>`;
  }).join('');
  return `
    <div class="replays-listing-card" data-testid="replays-listing-card">
      <header class="replays-listing-header">
        <h2 class="replays-listing-title">Recent replays</h2>
        <button type="button" class="replays-listing-close"
                data-testid="replays-listing-close"
                aria-label="Close replays overlay">×</button>
      </header>
      <table class="replays-listing-table" data-testid="replays-listing-table">
        <thead>
          <tr>
            <th scope="col">Completed</th>
            <th scope="col">Variant</th>
            <th scope="col">Turns</th>
            <th scope="col">Action</th>
          </tr>
        </thead>
        <tbody>${tbody}</tbody>
      </table>
    </div>`;
}

function wireCloseHandler(overlay: HTMLElement): void {
  const closeBtn = overlay.querySelector<HTMLButtonElement>('.replays-listing-close');
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

/**
 * Public entry point — fetches Bishop W14 replays listing and
 * mounts the overlay.  Idempotent.
 */
export async function openReplaysListing(): Promise<void> {
  const overlay = ensureOverlay();
  overlay.innerHTML = `
    <div class="replays-listing-card" data-testid="replays-listing-loading">
      <header class="replays-listing-header">
        <h2 class="replays-listing-title">Recent replays</h2>
        <button type="button" class="replays-listing-close"
                data-testid="replays-listing-close"
                aria-label="Close replays overlay">×</button>
      </header>
      <p class="replays-listing-loading-message">Loading replays…</p>
    </div>`;
  wireCloseHandler(overlay);

  let resp: Response;
  try {
    resp = await fetch('/api/replays', {
      credentials: 'same-origin',
      headers: { 'Accept': 'application/json' },
    });
  } catch {
    overlay.innerHTML = renderEmpty('Could not reach the replays service.');
    wireCloseHandler(overlay);
    return;
  }

  if (resp.status === 404) {
    overlay.innerHTML = renderEmpty('No replays found.');
    wireCloseHandler(overlay);
    return;
  }
  if (!resp.ok) {
    overlay.innerHTML = renderEmpty('Replays unavailable.');
    wireCloseHandler(overlay);
    return;
  }

  let body: unknown;
  try {
    body = await resp.json();
  } catch {
    overlay.innerHTML = renderEmpty('Replays response malformed.');
    wireCloseHandler(overlay);
    return;
  }

  const rows = extractReplayArray(body);
  if (rows.length === 0) {
    overlay.innerHTML = renderEmpty('No replays yet.');
    wireCloseHandler(overlay);
    return;
  }

  overlay.innerHTML = renderTable(rows);
  wireCloseHandler(overlay);
}
