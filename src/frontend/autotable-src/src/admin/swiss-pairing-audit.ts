// Phase K Wave 19 — Hicks (Frontend).
//
// Operator UI for Bishop's W19 Tournament Swiss-pairing audit log:
//
//   GET  /api/admin/tournaments/swiss-pairing-audit
//   GET  /api/admin/tournaments/swiss-pairing-audit/<tournamentId>
//
// Read-only surface — exposes the per-round Swiss pairing decisions
// the matcher made for a given tournament (Bishop W19 added the
// audit-log table backing the algorithm-visibility requirement from
// the W18 hand-off).  Each row carries:
//
//   • tournamentId       — Bishop's TournamentId GUID
//   • round              — 1..N
//   • pairingKey         — stable hash for one (player_a, player_b)
//                          decision in this round
//   • playerA / playerB  — display names (snapshot at pairing time)
//   • scoreA / scoreB    — running tournament score before this round
//   • bucket             — Swiss bucket index (rows with the same
//                          bucket got paired by Bishop's matcher)
//   • rationale          — one of `same-score-bucket`,
//                          `floater-up`, `floater-down`,
//                          `bye-assigned`, `prior-opponent-skip`
//   • createdAt          — when the matcher wrote the row
//
// The operator surface is read-only (no Create / Edit / Delete);
// we re-use the shared admin runtime by emitting empty `fields`
// + `buildBody` (the create form is hidden in CSS by the omitted
// "Create" toolbar override below — see `admin-panel.ts` for the
// shared toolbar mount).  Filtering by tournament happens via a
// URL-bar surrogate (`?tournamentId=...`); the shared list view
// just shows the running audit log in newest-first order.

import {
  type AdminSurfaceSpec,
  escapeHtml,
  fmtIso,
} from './admin-shared';

export type SwissPairingRationale =
  | 'same-score-bucket'
  | 'floater-up'
  | 'floater-down'
  | 'bye-assigned'
  | 'prior-opponent-skip';

interface SwissPairingRow {
  tournamentId: string;
  round: number;
  pairingKey: string;
  playerA: string;
  playerB: string;
  scoreA: number;
  scoreB: number;
  bucket: number;
  rationale: SwissPairingRationale;
  createdAt?: string;
}

interface SwissPairingBody {
  // The surface is read-only — buildBody returns a degenerate body
  // (the shared admin runtime never calls POST/PUT on a row because
  // the operator UI hides the "Create" + "Edit" affordances by the
  // empty `fields` list).  Kept here to satisfy the generic.
  noop: true;
}

const RATIONALES: SwissPairingRationale[] = [
  'same-score-bucket',
  'floater-up',
  'floater-down',
  'bye-assigned',
  'prior-opponent-skip',
];

function parseRow(raw: unknown): SwissPairingRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const tournamentId = typeof o.tournamentId === 'string' ? o.tournamentId : null;
  const pairingKey = typeof o.pairingKey === 'string' ? o.pairingKey : null;
  const round = typeof o.round === 'number' && Number.isFinite(o.round)
    ? Math.floor(o.round) : null;
  if (tournamentId === null || pairingKey === null || round === null) return null;
  const rationale = typeof o.rationale === 'string'
    && (RATIONALES as string[]).includes(o.rationale)
    ? o.rationale as SwissPairingRationale : 'same-score-bucket';
  return {
    tournamentId,
    round,
    pairingKey,
    playerA: typeof o.playerA === 'string' ? o.playerA : '',
    playerB: typeof o.playerB === 'string' ? o.playerB : '',
    scoreA: typeof o.scoreA === 'number' && Number.isFinite(o.scoreA)
      ? o.scoreA : 0,
    scoreB: typeof o.scoreB === 'number' && Number.isFinite(o.scoreB)
      ? o.scoreB : 0,
    bucket: typeof o.bucket === 'number' && Number.isFinite(o.bucket)
      ? Math.floor(o.bucket) : 0,
    rationale,
    createdAt: typeof o.createdAt === 'string' ? o.createdAt : undefined,
  };
}

function rationaleBadgeHtml(r: SwissPairingRationale): string {
  const colour =
    r === 'same-score-bucket' ? '#3a85c8'
    : r === 'bye-assigned' ? '#a36ec8'
    : r === 'prior-opponent-skip' ? '#c8a13a'
    : '#3aa8c8';
  return `<span class="admin-panel-badge" `
    + `style="display:inline-block;padding:2px 6px;border-radius:3px;`
    + `background:${colour};color:#fff;font-size:12px;">`
    + `${escapeHtml(r)}</span>`;
}

export const SWISS_PAIRING_AUDIT_SPEC: AdminSurfaceSpec<SwissPairingRow, SwissPairingBody> = {
  id: 'swiss-pairing-audit',
  title: 'Tournament Swiss pairing audit',
  description: 'Per-round Swiss matcher decisions.  Bishop W19 — read-'
    + 'only audit log for the Tournament Swiss pairing algorithm; '
    + 'shows bucket assignment, prior-opponent skips, floaters, '
    + 'and bye assignments.  Use the URL surrogate '
    + '`?tournamentId=<id>` to filter to one tournament.',
  endpoint: '/api/admin/tournaments/swiss-pairing-audit',
  parseRow,
  rowKey: (r) => `${r.tournamentId}:${r.round}:${r.pairingKey}`,
  // Read-only surface — empty fields hides the Create/Edit affordance.
  rowToFormValues: () => ({}),
  buildBody: () => ({ noop: true }),
  fields: [],
  columns: [
    {
      key: 'tournamentId',
      label: 'Tournament',
      render: (r) => ({ __html: `<code>${escapeHtml(r.tournamentId.slice(0, 8))}…</code>` }),
    },
    {
      key: 'round',
      label: 'Round',
      render: (r) => ({ __html: `<span class="admin-panel-num">${escapeHtml(String(r.round))}</span>` }),
    },
    {
      key: 'pair',
      label: 'Pairing',
      render: (r) => ({ __html:
        `<strong>${escapeHtml(r.playerA)}</strong>`
        + ` <small class="admin-panel-muted">(${escapeHtml(String(r.scoreA))})</small>`
        + ` vs `
        + `<strong>${escapeHtml(r.playerB)}</strong>`
        + ` <small class="admin-panel-muted">(${escapeHtml(String(r.scoreB))})</small>`,
      }),
    },
    {
      key: 'bucket',
      label: 'Bucket',
      render: (r) => String(r.bucket),
    },
    {
      key: 'rationale',
      label: 'Rationale',
      render: (r) => ({ __html: rationaleBadgeHtml(r.rationale) }),
    },
    {
      key: 'createdAt',
      label: 'Paired at',
      render: (r) => fmtIso(r.createdAt),
    },
  ],
};
