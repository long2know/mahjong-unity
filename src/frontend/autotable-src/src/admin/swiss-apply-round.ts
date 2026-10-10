// Phase K Wave 21 — Hicks (Frontend).
//
// Operator UI for Bishop's W21 Swiss-tournament apply-round endpoint:
//
//   POST /api/admin/tournaments/<tournamentId>/swiss-apply-round
//   body: { roundNumber: number, results: SwissResult[], dryRun?: boolean }
//
// The W20 surface (`./swiss-pair-next-round.ts`) was a pairing
// trigger; W21 layers on the *results-apply* surface so operators
// can finalize a round's outcomes (W/L/D + score deltas) before
// the next pairing computes.  Wire contract:
//
//   • Auth ladder: 401/403/503 → 200 OK with the round-applied
//     manifest in the body.
//   • `X-Admin-Reason` header MANDATORY (governance.tournaments.
//     swiss-apply-round.fired).
//   • `dryRun: true` → server returns the would-be manifest WITHOUT
//     persisting (used for "preview the standings before applying").
//
// This surface is action-oriented (no list / no CRUD), so it
// re-uses the shared admin runtime only for the auth ladder + the
// X-Admin-Reason prompt; the body of the surface is a hand-rolled
// form with per-round results entered as JSON.

import {
  type AdminSurfaceSpec,
  ADMIN_REASON_HEADER,
  escapeHtml,
  fmtIso,
  gateAdminFetch,
  promptAdminReason,
} from './admin-shared';

export type SwissApplyRoundOutcome =
  | 'applied'
  | 'already-applied'
  | 'results-incomplete'
  | 'tournament-complete';

interface SwissApplyRoundRow {
  tournamentId: string;
  state: SwissApplyRoundOutcome;
  roundNumber: number;
  /** ISO timestamp of the most recent apply. */
  lastAppliedAt?: string;
  lastAppliedBy?: string;
  /** Number of player results encoded into the last apply call. */
  resultsCount?: number;
}

interface SwissApplyRoundBody {
  tournamentId: string;
  roundNumber: number;
  resultsJson: string;
  dryRun: boolean;
}

const OUTCOMES: SwissApplyRoundOutcome[] = [
  'applied',
  'already-applied',
  'results-incomplete',
  'tournament-complete',
];

function parseRow(raw: unknown): SwissApplyRoundRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const tournamentId = typeof o.tournamentId === 'string' ? o.tournamentId : null;
  if (tournamentId === null) return null;
  const state = typeof o.state === 'string'
    && (OUTCOMES as string[]).includes(o.state)
    ? o.state as SwissApplyRoundOutcome
    : 'results-incomplete';
  return {
    tournamentId,
    state,
    roundNumber: typeof o.roundNumber === 'number' && Number.isFinite(o.roundNumber)
      ? Math.max(1, Math.floor(o.roundNumber)) : 1,
    lastAppliedAt: typeof o.lastAppliedAt === 'string' ? o.lastAppliedAt : undefined,
    lastAppliedBy: typeof o.lastAppliedBy === 'string' ? o.lastAppliedBy : undefined,
    resultsCount: typeof o.resultsCount === 'number'
      && Number.isFinite(o.resultsCount)
      ? Math.max(0, Math.floor(o.resultsCount)) : undefined,
  };
}

function outcomeLabel(o: SwissApplyRoundOutcome): string {
  switch (o) {
    case 'applied':            return 'Applied';
    case 'already-applied':    return 'Already applied';
    case 'results-incomplete': return 'Results incomplete';
    case 'tournament-complete':return 'Tournament complete';
  }
}

/**
 * Fire an apply-round POST.  Wraps `gateAdminFetch` so the 401/
 * 403/503 auth ladder is consistent with the rest of the admin
 * panel surfaces.  Returns the apply-round response body (the
 * post-apply standings manifest) or throws on a non-2xx response.
 */
export async function fireSwissApplyRound(
  tournamentId: string,
  roundNumber: number,
  results: unknown[],
  dryRun: boolean,
): Promise<unknown> {
  const reason = promptAdminReason(
    `Apply round ${roundNumber} results for ${tournamentId}`,
  );
  if (reason === null) throw new Error('cancelled');
  const body = {
    tournamentId,
    roundNumber,
    results,
    dryRun,
  };
  const res = await gateAdminFetch(
    `/api/admin/tournaments/${encodeURIComponent(tournamentId)}/swiss-apply-round`,
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        [ADMIN_REASON_HEADER]: reason,
      },
      body: JSON.stringify(body),
    },
  );
  if (!res.ok) {
    throw new Error(`swiss-apply-round POST failed: ${res.status ?? 'network'}`);
  }
  return res.body ?? null;
}

export const SWISS_APPLY_ROUND_SPEC: AdminSurfaceSpec<SwissApplyRoundRow, SwissApplyRoundBody> = {
  id: 'swiss-apply-round',
  title: 'Swiss · Apply round results',
  description: 'Finalize a Swiss-tournament round\'s results (W/L/D + '
    + 'score deltas) before the next pairing computes.  Paste the '
    + 'per-player results as a JSON array (one object per player, '
    + 'shape: `{ playerId, score, opponent? }`).  Use dry-run to '
    + 'preview the resulting standings before persisting.  Audit '
    + 'kind: governance.tournaments.swiss-apply-round.fired.',
  endpoint: '/api/admin/tournaments/swiss-apply-round',
  parseRow,
  rowKey: (r) => r.tournamentId,
  rowToFormValues: (r) => ({
    tournamentId: r.tournamentId,
    roundNumber: String(r.roundNumber),
    resultsJson: '[]',
    dryRun: 'false',
  }),
  buildBody: (v) => {
    const resultsJson = (v.resultsJson ?? '[]').trim() || '[]';
    return {
      tournamentId: (v.tournamentId ?? '').trim(),
      roundNumber: Math.max(1, Math.floor(Number(v.roundNumber ?? '1'))),
      resultsJson,
      dryRun: (v.dryRun ?? 'false').toLowerCase() === 'true',
    };
  },
  fields: [
    {
      name: 'tournamentId',
      label: 'Tournament ID',
      type: 'text',
      required: true,
      primaryKey: true,
      placeholder: 'tournament-2026-summer-open',
    },
    {
      name: 'roundNumber',
      label: 'Round number',
      type: 'number',
      required: true,
      min: 1,
      max: 32,
      integer: true,
      placeholder: '3',
      help: 'The round whose results are being applied.',
    },
    {
      name: 'resultsJson',
      label: 'Results (JSON array)',
      type: 'text',
      required: true,
      placeholder: '[{"playerId":"p1","score":50},...]',
      help: 'One object per player.  Required fields: playerId, '
        + 'score.  Optional: opponent (for tie-break).',
    },
    {
      name: 'dryRun',
      label: 'Dry run',
      type: 'select',
      required: true,
      options: [
        { value: 'false', label: 'No — persist the round results' },
        { value: 'true',  label: 'Yes — preview standings only' },
      ],
      help: 'Dry-run returns the would-be standings WITHOUT '
        + 'persisting — use to review before applying.',
    },
  ],
  columns: [
    {
      key: 'tournamentId',
      label: 'Tournament',
      render: (r) => ({ __html: `<code>${escapeHtml(r.tournamentId)}</code>` }),
    },
    {
      key: 'state',
      label: 'State',
      render: (r) => outcomeLabel(r.state),
    },
    {
      key: 'roundNumber',
      label: 'Round',
      render: (r) => `R${r.roundNumber}`,
    },
    {
      key: 'resultsCount',
      label: 'Results',
      render: (r) => r.resultsCount === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : String(r.resultsCount),
    },
    {
      key: 'lastAppliedAt',
      label: 'Last applied',
      render: (r) => fmtIso(r.lastAppliedAt),
    },
    {
      key: 'lastAppliedBy',
      label: 'By',
      render: (r) => r.lastAppliedBy === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : escapeHtml(r.lastAppliedBy),
    },
  ],
};
