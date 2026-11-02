// Phase K Wave 14 — Hicks (Frontend).
//
// Renders the `?action=bracket&tournamentId=<id>` overlay surface
// against Bishop's W14 `GET /api/tournaments/{id}/brackets` listing
// endpoint.  The endpoint lists the bracket records for the given
// tournament — the renderer groups them by `roundNumber` and emits
// a simple HTML grid of rounds, match cards, winner highlight, and
// per-match status badges.
//
// The W14 ask is deliberately simple ("simple HTML grid; no fancy
// UI — that's Phase L"), so this module stays small enough to ship
// in its own lazy chunk without dragging the W6/W8 bracket-renderer
// strategy graph (`bracket-renderer.ts` → `tournaments.ts` →
// `replay-launcher.ts`) into the lobby cold path.  The W6 renderer
// stays the source-of-truth for the in-lobby live bracket; the
// W14 overlay is the lightweight share-link surface (deep-link to
// a bracket snapshot for a tournament).
//
// Wire shape (Bishop W14, defensively parsed):
//
//   GET /api/tournaments/{id}/brackets
//     → 200 { brackets: BracketRecord[] }   - canonical
//     → 200 [ BracketRecord, ... ]          - tolerated bare array
//     → 404                                 - "No brackets" placeholder
//     → 5xx / network                       - "Brackets unavailable" toast
//
//   BracketRecord {
//     id?           string
//     roundNumber   number
//     matchIndex?   number
//     seedA?        number
//     seedB?        number
//     playerA?      string | { name?: string; displayName?: string }
//     playerB?      string | { name?: string; displayName?: string }
//     winnerSeed?   number | null
//     status?       'pending' | 'in-progress' | 'completed' | string
//     bracketSide?  'winners' | 'losers' | string
//   }
//
// Defensive contract — any missing field is tolerated, the row just
// renders blank for that slot.  Bishop's W14 wire shape can drift
// without crashing the overlay.

interface RawBracketRecord {
  id?: unknown;
  roundNumber?: unknown;
  matchIndex?: unknown;
  seedA?: unknown;
  seedB?: unknown;
  playerA?: unknown;
  playerB?: unknown;
  winnerSeed?: unknown;
  status?: unknown;
  bracketSide?: unknown;
}

interface BracketRecord {
  id: string;
  roundNumber: number;
  matchIndex: number;
  seedA: number | null;
  seedB: number | null;
  playerA: string;
  playerB: string;
  winnerSeed: number | null;
  status: string;
  bracketSide: string;
}

const OVERLAY_ID = 'bracket-listing-overlay';

function parsePlayer(raw: unknown): string {
  if (typeof raw === 'string') return raw;
  if (raw !== null && typeof raw === 'object') {
    const obj = raw as { name?: unknown; displayName?: unknown };
    if (typeof obj.displayName === 'string') return obj.displayName;
    if (typeof obj.name === 'string') return obj.name;
  }
  return '';
}

function parseRecord(raw: RawBracketRecord, idx: number): BracketRecord {
  return {
    id: typeof raw.id === 'string' && raw.id !== '' ? raw.id : `match-${idx}`,
    roundNumber: typeof raw.roundNumber === 'number' && Number.isFinite(raw.roundNumber)
      ? Math.max(1, Math.floor(raw.roundNumber))
      : 1,
    matchIndex: typeof raw.matchIndex === 'number' && Number.isFinite(raw.matchIndex)
      ? Math.max(0, Math.floor(raw.matchIndex))
      : idx,
    seedA: typeof raw.seedA === 'number' && Number.isFinite(raw.seedA) ? raw.seedA : null,
    seedB: typeof raw.seedB === 'number' && Number.isFinite(raw.seedB) ? raw.seedB : null,
    playerA: parsePlayer(raw.playerA),
    playerB: parsePlayer(raw.playerB),
    winnerSeed: typeof raw.winnerSeed === 'number' && Number.isFinite(raw.winnerSeed)
      ? raw.winnerSeed
      : null,
    status: typeof raw.status === 'string' ? raw.status : 'pending',
    bracketSide: typeof raw.bracketSide === 'string' ? raw.bracketSide : 'winners',
  };
}

function extractBracketArray(body: unknown): BracketRecord[] {
  let arr: unknown[] | null = null;
  if (Array.isArray(body)) {
    arr = body;
  } else if (body !== null && typeof body === 'object') {
    const obj = body as { brackets?: unknown; records?: unknown };
    if (Array.isArray(obj.brackets)) arr = obj.brackets;
    else if (Array.isArray(obj.records)) arr = obj.records;
  }
  if (arr === null) return [];
  return arr
    .filter((r): r is RawBracketRecord => r !== null && typeof r === 'object')
    .map((r, i) => parseRecord(r, i));
}

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function statusBadgeClass(status: string): string {
  const k = status.toLowerCase();
  if (k === 'completed' || k === 'complete') return 'bracket-listing-status-completed';
  if (k === 'in-progress' || k === 'in_progress' || k === 'inprogress') return 'bracket-listing-status-active';
  return 'bracket-listing-status-pending';
}

function ensureOverlay(): HTMLElement {
  let overlay = document.getElementById(OVERLAY_ID) as HTMLDivElement | null;
  if (overlay !== null) {
    overlay.innerHTML = '';
    return overlay;
  }
  overlay = document.createElement('div');
  overlay.id = OVERLAY_ID;
  overlay.className = 'bracket-listing-overlay';
  overlay.setAttribute('role', 'dialog');
  overlay.setAttribute('aria-modal', 'false');
  overlay.setAttribute('data-testid', 'bracket-listing-overlay');
  document.body.appendChild(overlay);
  return overlay;
}

function closeOverlay(): void {
  const overlay = document.getElementById(OVERLAY_ID);
  if (overlay !== null && overlay.parentNode !== null) {
    overlay.parentNode.removeChild(overlay);
  }
}

function renderEmptyState(tournamentId: string, message: string): string {
  return `
    <div class="bracket-listing-card" data-testid="bracket-listing-empty">
      <header class="bracket-listing-header">
        <h2 class="bracket-listing-title">Tournament brackets</h2>
        <button type="button" class="bracket-listing-close"
                data-testid="bracket-listing-close"
                aria-label="Close brackets overlay">×</button>
      </header>
      <p class="bracket-listing-tournament-id">Tournament <code>${escapeHtml(tournamentId)}</code></p>
      <p class="bracket-listing-empty-message">${escapeHtml(message)}</p>
    </div>`;
}

function renderMatchCard(rec: BracketRecord): string {
  const aWins = rec.winnerSeed !== null && rec.winnerSeed === rec.seedA;
  const bWins = rec.winnerSeed !== null && rec.winnerSeed === rec.seedB;
  const aClass = aWins ? 'bracket-listing-player bracket-listing-player-winner' : 'bracket-listing-player';
  const bClass = bWins ? 'bracket-listing-player bracket-listing-player-winner' : 'bracket-listing-player';
  const aLabel = rec.playerA !== '' ? rec.playerA : (rec.seedA !== null ? `Seed ${rec.seedA}` : 'TBD');
  const bLabel = rec.playerB !== '' ? rec.playerB : (rec.seedB !== null ? `Seed ${rec.seedB}` : 'TBD');
  const statusCls = statusBadgeClass(rec.status);
  const sideAttr = rec.bracketSide !== '' ? ` data-bracket-side="${escapeHtml(rec.bracketSide)}"` : '';
  return `
    <article class="bracket-listing-match" data-testid="bracket-listing-match"
             data-match-id="${escapeHtml(rec.id)}"${sideAttr}>
      <div class="${aClass}" data-testid="bracket-listing-player-a">
        <span class="bracket-listing-seed">${rec.seedA !== null ? escapeHtml(String(rec.seedA)) : '—'}</span>
        <span class="bracket-listing-player-name">${escapeHtml(aLabel)}</span>
        ${aWins ? '<span class="bracket-listing-winner-badge" aria-label="Winner">★</span>' : ''}
      </div>
      <div class="${bClass}" data-testid="bracket-listing-player-b">
        <span class="bracket-listing-seed">${rec.seedB !== null ? escapeHtml(String(rec.seedB)) : '—'}</span>
        <span class="bracket-listing-player-name">${escapeHtml(bLabel)}</span>
        ${bWins ? '<span class="bracket-listing-winner-badge" aria-label="Winner">★</span>' : ''}
      </div>
      <span class="bracket-listing-status ${statusCls}"
            data-testid="bracket-listing-status">${escapeHtml(rec.status)}</span>
    </article>`;
}

function renderBracketGrid(tournamentId: string, records: BracketRecord[]): string {
  if (records.length === 0) {
    return renderEmptyState(tournamentId, 'No brackets have been generated for this tournament yet.');
  }
  // Group by roundNumber; within each round sort by matchIndex.
  const rounds = new Map<number, BracketRecord[]>();
  for (const rec of records) {
    const bucket = rounds.get(rec.roundNumber) ?? [];
    bucket.push(rec);
    rounds.set(rec.roundNumber, bucket);
  }
  const ordered = Array.from(rounds.keys()).sort((a, b) => a - b);
  const columns = ordered.map((rn) => {
    const matches = (rounds.get(rn) ?? []).slice().sort((a, b) => a.matchIndex - b.matchIndex);
    return `
      <section class="bracket-listing-round"
               data-testid="bracket-listing-round-${rn}"
               data-round-number="${rn}">
        <h3 class="bracket-listing-round-title">Round ${rn}</h3>
        <div class="bracket-listing-matches">
          ${matches.map(renderMatchCard).join('')}
        </div>
      </section>`;
  }).join('');
  return `
    <div class="bracket-listing-card" data-testid="bracket-listing-card">
      <header class="bracket-listing-header">
        <h2 class="bracket-listing-title">Tournament brackets</h2>
        <button type="button" class="bracket-listing-close"
                data-testid="bracket-listing-close"
                aria-label="Close brackets overlay">×</button>
      </header>
      <p class="bracket-listing-tournament-id">Tournament <code>${escapeHtml(tournamentId)}</code></p>
      <div class="bracket-listing-grid" data-testid="bracket-listing-grid">
        ${columns}
      </div>
    </div>`;
}

function wireCloseHandler(overlay: HTMLElement): void {
  const closeBtn = overlay.querySelector<HTMLButtonElement>('.bracket-listing-close');
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
 * Public entry point — fetches the Bishop W14 brackets listing and
 * mounts the overlay.  Idempotent: re-invocation tears the previous
 * overlay down and re-renders.
 */
export async function openBracketListing(tournamentId: string): Promise<void> {
  const overlay = ensureOverlay();
  overlay.innerHTML = `
    <div class="bracket-listing-card" data-testid="bracket-listing-loading">
      <header class="bracket-listing-header">
        <h2 class="bracket-listing-title">Tournament brackets</h2>
        <button type="button" class="bracket-listing-close"
                data-testid="bracket-listing-close"
                aria-label="Close brackets overlay">×</button>
      </header>
      <p class="bracket-listing-loading-message">Loading brackets…</p>
    </div>`;
  wireCloseHandler(overlay);

  let resp: Response;
  try {
    resp = await fetch(
      `/api/tournaments/${encodeURIComponent(tournamentId)}/brackets`,
      {
        credentials: 'same-origin',
        headers: { 'Accept': 'application/json' },
      },
    );
  } catch {
    overlay.innerHTML = renderEmptyState(tournamentId, 'Could not reach the brackets service.');
    wireCloseHandler(overlay);
    return;
  }

  if (resp.status === 404) {
    overlay.innerHTML = renderEmptyState(tournamentId, 'No brackets found for this tournament.');
    wireCloseHandler(overlay);
    return;
  }
  if (!resp.ok) {
    overlay.innerHTML = renderEmptyState(tournamentId, 'Brackets unavailable.');
    wireCloseHandler(overlay);
    return;
  }

  let body: unknown;
  try {
    body = await resp.json();
  } catch {
    overlay.innerHTML = renderEmptyState(tournamentId, 'Brackets response malformed.');
    wireCloseHandler(overlay);
    return;
  }

  const records = extractBracketArray(body);
  overlay.innerHTML = renderBracketGrid(tournamentId, records);
  wireCloseHandler(overlay);
}
