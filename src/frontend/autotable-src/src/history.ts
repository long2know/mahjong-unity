// Phase K Wave 1 — Match-history export modal.
//
// Surfaces a "Match history" link on the profile page that opens a
// modal with:
//   • Date range filter — last 7 / 30 / 90 / 365 days plus a custom
//     range with two date inputs.
//   • Format toggle (JSON / CSV).
//   • Download button — triggers a browser download via Bishop's
//     `GET /api/games?playerId=&format=json|csv&from=<ISO>&to=<ISO>`
//     endpoint.  Falls back to "endpoint unavailable" when 404.
//   • Recent matches preview — last 20 hits, sortable on date /
//     result / score.
//
// All endpoints are feature-detected.  When the backend is missing the
// modal renders a quiet "Match-history export is not yet available"
// message instead of failing.
//
// data-testids (declared in tests/selectors.md):
//   profile-history-link, history-modal, history-date-range,
//   history-format-toggle, history-download, history-recent-table,
//   history-recent-row-{N}.

import { getProfile } from './profile';
import { setElHidden, showEl, hideEl } from './dom-utils';

// ── Wire shape ──────────────────────────────────────────────────────

export interface HistoryRow {
  gameId: string;
  finishedAt: string | null;
  result: 'win' | 'loss' | 'draw' | 'unknown';
  finalScore: number;
  opponentSummary: string;
}

const RECENT_PREVIEW_LIMIT = 20;

type RangeKey = '7' | '30' | '90' | '365' | 'custom';
type Format = 'json' | 'csv';
type SortKey = 'date' | 'result' | 'score';

interface State {
  installed: boolean;
  open: boolean;
  range: RangeKey;
  customFrom: string | null; // YYYY-MM-DD
  customTo: string | null;
  format: Format;
  rows: HistoryRow[];
  loading: boolean;
  error: string | null;
  endpointAvailable: boolean | null;
  sort: SortKey;
  sortAsc: boolean;
}

const state: State = {
  installed: false,
  open: false,
  range: '30',
  customFrom: null,
  customTo: null,
  format: 'json',
  rows: [],
  loading: false,
  error: null,
  endpointAvailable: null,
  sort: 'date',
  sortAsc: false,
};

// ── DOM helpers ─────────────────────────────────────────────────────

function $(id: string): HTMLElement | null {
  return document.getElementById(id);
}

// ── Public API ──────────────────────────────────────────────────────

export function installHistoryModal(): void {
  if (state.installed) return;
  // The "Match history" link lives on the profile page section.
  // Inject it on first render so we don't have to teach index.html
  // about the new control surface — keeps the HTML diff minimal.
  installHistoryLink();
  installModalShell();
  state.installed = true;
}

export function openHistoryModal(): void {
  if (!state.installed) installHistoryModal();
  const modal = $('history-modal');
  if (modal === null) return;
  state.open = true;
  modal.classList.add('history-modal-open');
  modal.setAttribute('aria-hidden', 'false');
  showEl(modal);
  // Initial fetch (best effort).
  void refresh();
  // Focus the close button.
  window.setTimeout(() => {
    const closeBtn = $('history-modal-close');
    closeBtn?.focus();
  }, 50);
}

export function closeHistoryModal(): void {
  const modal = $('history-modal');
  if (modal === null) return;
  state.open = false;
  modal.classList.remove('history-modal-open');
  modal.setAttribute('aria-hidden', 'true');
  hideEl(modal);
}

// ── Profile-page integration ───────────────────────────────────────

function installHistoryLink(): void {
  const recentSection = document.querySelector<HTMLElement>(
    '#profile-recent-games')?.closest('.profile-page-section') as HTMLElement | null;
  if (recentSection === null) return;
  if (recentSection.querySelector('#profile-history-link') !== null) return;
  const title = recentSection.querySelector<HTMLElement>('.profile-page-section-title');
  const link = document.createElement('button');
  link.id = 'profile-history-link';
  link.type = 'button';
  link.className = 'btn btn-info btn-sm profile-history-link';
  link.setAttribute('data-testid', 'profile-history-link');
  link.textContent = '📥 Match history';
  link.setAttribute('aria-label', 'Open match history export modal');
  link.addEventListener('click', () => openHistoryModal());
  if (title !== null && title.parentElement !== null) {
    // Insert next to the heading so the affordance is visible at the
    // top of the section.
    const wrap = document.createElement('div');
    wrap.className = 'profile-page-section-title-row';
    title.parentElement.insertBefore(wrap, title);
    wrap.appendChild(title);
    wrap.appendChild(link);
  } else {
    recentSection.insertBefore(link, recentSection.firstChild);
  }
}

// ── Modal scaffold ──────────────────────────────────────────────────

function installModalShell(): void {
  if ($('history-modal') !== null) return;
  const modal = document.createElement('div');
  modal.id = 'history-modal';
  modal.className = 'history-modal';
  modal.setAttribute('role', 'dialog');
  modal.setAttribute('aria-modal', 'true');
  modal.setAttribute('aria-label', 'Match history export');
  modal.setAttribute('aria-hidden', 'true');
  modal.setAttribute('data-testid', 'history-modal');
  modal.hidden = true;

  modal.innerHTML = `
    <div class="history-modal-backdrop" data-history-dismiss></div>
    <div class="history-modal-shell" role="document">
      <header class="history-modal-header">
        <h2 class="history-modal-title">📥 Match history</h2>
        <button id="history-modal-close" type="button"
                class="history-modal-close"
                data-testid="history-modal-close"
                aria-label="Close match history">×</button>
      </header>
      <section class="history-modal-controls">
        <label class="history-modal-field">
          <span class="history-modal-field-label">Date range</span>
          <select id="history-date-range"
                  class="form-control form-control-sm"
                  data-testid="history-date-range">
            <option value="7">Last 7 days</option>
            <option value="30" selected>Last 30 days</option>
            <option value="90">Last 90 days</option>
            <option value="365">Last 365 days</option>
            <option value="custom">Custom range…</option>
          </select>
        </label>
        <div id="history-custom-range" class="history-modal-custom-range" hidden>
          <label class="history-modal-field">
            <span class="history-modal-field-label">From</span>
            <input id="history-date-from" type="date"
                   class="form-control form-control-sm"
                   data-testid="history-date-from">
          </label>
          <label class="history-modal-field">
            <span class="history-modal-field-label">To</span>
            <input id="history-date-to" type="date"
                   class="form-control form-control-sm"
                   data-testid="history-date-to">
          </label>
        </div>
        <fieldset class="history-modal-format" data-testid="history-format-toggle">
          <legend class="history-modal-field-label">Format</legend>
          <label class="history-modal-format-option">
            <input type="radio" name="history-format" value="json"
                   data-testid="history-format-json" checked>
            <span>JSON</span>
          </label>
          <label class="history-modal-format-option">
            <input type="radio" name="history-format" value="csv"
                   data-testid="history-format-csv">
            <span>CSV</span>
          </label>
        </fieldset>
        <button id="history-download" type="button"
                class="btn btn-success btn-sm history-modal-download"
                data-testid="history-download">
          ⬇ Download
        </button>
      </section>
      <section class="history-modal-status">
        <div id="history-modal-error"
             class="history-modal-error" hidden aria-live="polite"></div>
        <div id="history-modal-loading"
             class="history-modal-loading" hidden aria-live="polite">
          Loading recent matches…
        </div>
        <div id="history-modal-unavailable"
             class="history-modal-unavailable" hidden>
          Match-history export is not yet available on this server.
        </div>
      </section>
      <section class="history-modal-recent">
        <h3 class="history-modal-section-title">Recent matches</h3>
        <div id="history-recent-table-host"
             class="history-modal-recent-host"></div>
      </section>
    </div>
  `;
  document.body.appendChild(modal);

  // Wire controls.
  modal.querySelectorAll<HTMLElement>('[data-history-dismiss]').forEach(el => {
    el.addEventListener('click', () => closeHistoryModal());
  });
  const closeBtn = modal.querySelector<HTMLButtonElement>('#history-modal-close');
  closeBtn?.addEventListener('click', () => closeHistoryModal());

  const range = modal.querySelector<HTMLSelectElement>('#history-date-range');
  range?.addEventListener('change', () => {
    state.range = (range.value as RangeKey);
    const custom = modal.querySelector<HTMLElement>('#history-custom-range');
    if (custom !== null) setElHidden(custom, state.range !== 'custom');
    void refresh();
  });
  const fromInput = modal.querySelector<HTMLInputElement>('#history-date-from');
  fromInput?.addEventListener('change', () => {
    state.customFrom = fromInput.value === '' ? null : fromInput.value;
    if (state.range === 'custom') void refresh();
  });
  const toInput = modal.querySelector<HTMLInputElement>('#history-date-to');
  toInput?.addEventListener('change', () => {
    state.customTo = toInput.value === '' ? null : toInput.value;
    if (state.range === 'custom') void refresh();
  });

  modal.querySelectorAll<HTMLInputElement>('input[name="history-format"]').forEach(input => {
    input.addEventListener('change', () => {
      if (input.checked && (input.value === 'json' || input.value === 'csv')) {
        state.format = input.value;
      }
    });
  });

  const dl = modal.querySelector<HTMLButtonElement>('#history-download');
  dl?.addEventListener('click', () => void triggerDownload());

  // ESC closes when open.
  document.addEventListener('keydown', (ev) => {
    if (!state.open) return;
    if (ev.key === 'Escape') {
      closeHistoryModal();
    }
  });
}

// ── Range resolution ────────────────────────────────────────────────

function resolveRange(): { from: string | null; to: string | null } {
  if (state.range === 'custom') {
    return { from: state.customFrom, to: state.customTo };
  }
  const days = parseInt(state.range, 10);
  if (isNaN(days) || days <= 0) return { from: null, to: null };
  const to = new Date();
  const from = new Date(to.getTime() - days * 24 * 60 * 60 * 1000);
  return {
    from: from.toISOString(),
    to: to.toISOString(),
  };
}

function buildUrl(format: Format): string | null {
  const profile = getProfile();
  const playerId = profile?.playerId ?? '';
  if (playerId === '' || playerId === 'offline') return null;
  const params = new URLSearchParams();
  params.set('playerId', playerId);
  params.set('format', format);
  const { from, to } = resolveRange();
  if (from !== null && from !== '') params.set('from', from);
  if (to !== null && to !== '') params.set('to', to);
  return `/api/games?${params.toString()}`;
}

// ── Recent preview ─────────────────────────────────────────────────

async function refresh(): Promise<void> {
  if (!state.open) return;
  const url = buildUrl('json');
  if (url === null) {
    state.rows = [];
    state.error = 'Sign in to view your match history.';
    renderRecent();
    return;
  }
  state.loading = true;
  state.error = null;
  renderRecent();
  try {
    const resp = await fetch(url, {
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
    });
    if (resp.status === 404) {
      state.endpointAvailable = false;
      state.rows = [];
      state.loading = false;
      renderRecent();
      return;
    }
    if (!resp.ok) {
      throw new Error(`HTTP ${resp.status}`);
    }
    state.endpointAvailable = true;
    const body = await resp.json() as unknown;
    state.rows = normalizeRows(body).slice(0, RECENT_PREVIEW_LIMIT);
  } catch (e) {
    state.error = (e as Error).message;
    state.rows = [];
  } finally {
    state.loading = false;
    renderRecent();
  }
}

function normalizeRows(raw: unknown): HistoryRow[] {
  const rows: HistoryRow[] = [];
  const list = Array.isArray(raw)
    ? raw
    : (raw !== null && typeof raw === 'object'
       && Array.isArray((raw as { games?: unknown[] }).games))
      ? (raw as { games: unknown[] }).games
      : [];
  for (const r of list) {
    if (r === null || typeof r !== 'object') continue;
    const o = r as Record<string, unknown>;
    const gameId = typeof o.gameId === 'string' && o.gameId !== ''
      ? o.gameId
      : (typeof o.id === 'string' ? o.id : '');
    if (gameId === '') continue;
    const finishedAt = typeof o.finishedAt === 'string'
      ? o.finishedAt
      : (typeof o.completedAt === 'string' ? o.completedAt : null);
    const result = (() => {
      const r2 = typeof o.result === 'string' ? o.result.toLowerCase() : '';
      if (r2 === 'win' || r2 === 'won') return 'win' as const;
      if (r2 === 'loss' || r2 === 'lost') return 'loss' as const;
      if (r2 === 'draw' || r2 === 'washout') return 'draw' as const;
      return 'unknown' as const;
    })();
    const finalScore = typeof o.finalScore === 'number'
      ? o.finalScore
      : (typeof o.score === 'number' ? o.score : 0);
    const opponentSummary = typeof o.opponentSummary === 'string'
      ? o.opponentSummary
      : (typeof o.summary === 'string' ? o.summary
        : (typeof o.opponents === 'string' ? o.opponents : ''));
    rows.push({ gameId, finishedAt, result, finalScore, opponentSummary });
  }
  return rows;
}

function renderRecent(): void {
  const host = $('history-recent-table-host');
  const errorEl = $('history-modal-error');
  const loadingEl = $('history-modal-loading');
  const unavailableEl = $('history-modal-unavailable');
  if (host === null) return;

  if (errorEl !== null) {
    if (state.error !== null) {
      showEl(errorEl);
      errorEl.textContent = state.error;
    } else {
      hideEl(errorEl);
      errorEl.textContent = '';
    }
  }
  if (loadingEl !== null) {
    setElHidden(loadingEl, !state.loading);
  }
  if (unavailableEl !== null) {
    setElHidden(unavailableEl, state.endpointAvailable !== false);
  }

  host.replaceChildren();
  if (state.endpointAvailable === false) return;
  if (state.rows.length === 0 && !state.loading) {
    const empty = document.createElement('div');
    empty.className = 'history-modal-empty';
    empty.textContent = 'No matches found in this range.';
    host.appendChild(empty);
    return;
  }
  host.appendChild(buildRecentTable());
}

function buildRecentTable(): HTMLTableElement {
  const table = document.createElement('table');
  table.className = 'history-modal-table history-recent-table';
  table.setAttribute('data-testid', 'history-recent-table');
  table.setAttribute('role', 'table');
  const head = document.createElement('thead');
  const headRow = document.createElement('tr');
  const cols: Array<{ key: SortKey | null; label: string }> = [
    { key: 'date',   label: 'Date' },
    { key: null,     label: 'Opponents' },
    { key: 'result', label: 'Result' },
    { key: 'score',  label: 'Score' },
    { key: null,     label: '' },
  ];
  for (const col of cols) {
    const th = document.createElement('th');
    th.className = 'history-modal-th';
    th.scope = 'col';
    th.textContent = col.label;
    if (col.key !== null) {
      th.setAttribute('tabindex', '0');
      th.setAttribute('role', 'columnheader');
      if (col.key === state.sort) {
        th.classList.add('history-modal-th-active');
        th.setAttribute('aria-sort', state.sortAsc ? 'ascending' : 'descending');
      }
      const cycle = (): void => {
        if (state.sort === col.key) {
          state.sortAsc = !state.sortAsc;
        } else {
          state.sort = col.key as SortKey;
          state.sortAsc = col.key === 'date' ? false : true;
        }
        renderRecent();
      };
      th.addEventListener('click', cycle);
      th.addEventListener('keydown', (ev) => {
        if (ev.key === 'Enter' || ev.key === ' ') {
          ev.preventDefault();
          cycle();
        }
      });
    }
    headRow.appendChild(th);
  }
  head.appendChild(headRow);
  table.appendChild(head);

  const body = document.createElement('tbody');
  const sorted = sortRows(state.rows);
  sorted.forEach((row, idx) => {
    const tr = document.createElement('tr');
    tr.className = `history-modal-row history-modal-row-${row.result}`;
    tr.setAttribute('data-testid', `history-recent-row-${idx}`);
    tr.setAttribute('data-game-id', row.gameId);
    appendCell(tr, formatDate(row.finishedAt), 'history-modal-cell-date');
    appendCell(tr, row.opponentSummary || '—', 'history-modal-cell-opponents');
    appendCell(tr, capitalize(row.result), `history-modal-cell-result history-modal-cell-result-${row.result}`);
    appendCell(tr, formatSigned(row.finalScore), 'history-modal-cell-score');

    // Replay link cell (best effort — replay launcher does its own
    // feature-detection).
    const replayCell = document.createElement('td');
    replayCell.className = 'history-modal-cell-actions';
    const replayBtn = document.createElement('button');
    replayBtn.type = 'button';
    replayBtn.className = 'btn btn-sm btn-info history-modal-replay';
    replayBtn.textContent = '🎞';
    replayBtn.title = 'Watch replay';
    replayBtn.setAttribute('aria-label', `Watch replay for game ${row.gameId.slice(0, 8)}`);
    replayBtn.addEventListener('click', () => {
      void openReplayInBackground(row.gameId);
    });
    replayCell.appendChild(replayBtn);
    tr.appendChild(replayCell);
    body.appendChild(tr);
  });
  table.appendChild(body);
  return table;
}

async function openReplayInBackground(gameId: string): Promise<void> {
  if (gameId === '') return;
  try {
    const mod = await import('./replay-launcher');
    closeHistoryModal();
    void mod.openReplayForGame(gameId);
  } catch {
    /* graceful no-op */
  }
}

function sortRows(rows: ReadonlyArray<HistoryRow>): HistoryRow[] {
  const copy = rows.slice();
  const sign = state.sortAsc ? 1 : -1;
  copy.sort((a, b) => {
    switch (state.sort) {
      case 'date': {
        const ta = a.finishedAt === null ? 0 : Date.parse(a.finishedAt);
        const tb = b.finishedAt === null ? 0 : Date.parse(b.finishedAt);
        return sign * (ta - tb);
      }
      case 'result': return sign * a.result.localeCompare(b.result);
      case 'score':  return sign * (a.finalScore - b.finalScore);
      default:       return 0;
    }
  });
  return copy;
}

function appendCell(row: HTMLElement, text: string, cls: string): void {
  const td = document.createElement('td');
  td.className = `history-modal-cell ${cls}`;
  td.textContent = text;
  row.appendChild(td);
}

function formatDate(iso: string | null): string {
  if (iso === null || iso === '') return '—';
  const t = Date.parse(iso);
  if (isNaN(t)) return iso;
  return new Date(t).toLocaleDateString();
}

function formatSigned(n: number): string {
  if (!isFinite(n)) return '—';
  const r = Math.round(n);
  return r > 0 ? `+${r}` : String(r);
}

function capitalize(s: string): string {
  if (s.length === 0) return s;
  return s.charAt(0).toUpperCase() + s.slice(1);
}

// ── Download ────────────────────────────────────────────────────────

async function triggerDownload(): Promise<void> {
  const url = buildUrl(state.format);
  if (url === null) {
    state.error = 'Sign in to download your match history.';
    renderRecent();
    return;
  }
  state.error = null;
  renderRecent();
  try {
    const resp = await fetch(url, {
      credentials: 'same-origin',
      headers: {
        Accept: state.format === 'csv' ? 'text/csv' : 'application/json',
      },
    });
    if (resp.status === 404) {
      state.endpointAvailable = false;
      renderRecent();
      return;
    }
    if (!resp.ok) {
      throw new Error(`HTTP ${resp.status}`);
    }
    state.endpointAvailable = true;
    const blob = await resp.blob();
    const objectUrl = window.URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = objectUrl;
    a.download = filenameFor(state.format);
    document.body.appendChild(a);
    a.click();
    a.remove();
    // Revoke after a short tick so Safari can pick up the blob.
    window.setTimeout(() => window.URL.revokeObjectURL(objectUrl), 5000);
  } catch (e) {
    state.error = (e as Error).message;
    renderRecent();
  }
}

function filenameFor(format: Format): string {
  const stamp = new Date().toISOString().slice(0, 10);
  return `mahjong-history-${stamp}.${format}`;
}
