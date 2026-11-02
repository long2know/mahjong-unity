// Phase K Wave 22 — Hicks (Frontend).
//
// Operator UI for Bishop's W22 tournament-finalize endpoint:
//
//   POST /api/admin/tournaments/<tournamentId>/finalize
//   body: { tournamentId, finalStandings?, dryRun?: boolean }
//
// The W20/W21 swiss-pair-next-round + swiss-apply-round surfaces
// drive the round-by-round Swiss lifecycle; W22 layers on the
// terminal *finalize* surface so operators can close out a
// completed tournament once all rounds are applied.  Wire
// contract:
//
//   • Auth ladder: 401/403/503 → 200 OK with the finalize manifest.
//   • `X-Admin-Reason` header MANDATORY
//     (governance.tournaments.finalize.fired).
//   • `dryRun: true` → returns the would-be standings WITHOUT
//     persisting the closure (used to preview the final podium
//     and verify trophy/payout assignments before sealing).
//   • Once finalized, the tournament becomes READ-ONLY — no
//     subsequent swiss-apply-round / swiss-pair-next-round calls
//     succeed against it.
//
// Routed into the `admin-panel-tournaments` chunk via
// `vite.config.ts:manualChunks`; lazy-loaded by the entry
// `./admin-panel.ts` when the operator activates a tournament
// tab.

import {
  type AdminSurfaceSpec,
  escapeHtml,
  fmtIso,
} from './admin-shared';

export type FinalizeOutcome =
  | 'pending'
  | 'finalized'
  | 'rounds-incomplete'
  | 'already-finalized';

interface TournamentFinalizeRow {
  tournamentId: string;
  outcome: FinalizeOutcome;
  roundsCompleted: number;
  roundsTotal: number;
  finalizedAt?: string;
  finalizedBy?: string;
  championPlayerId?: string;
}

interface TournamentFinalizeBody {
  tournamentId: string;
  dryRun: boolean;
}

const OUTCOMES: FinalizeOutcome[] = [
  'pending',
  'finalized',
  'rounds-incomplete',
  'already-finalized',
];

function parseRow(raw: unknown): TournamentFinalizeRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const tournamentId = typeof o.tournamentId === 'string' ? o.tournamentId : null;
  if (tournamentId === null) return null;
  const outcome = typeof o.outcome === 'string'
    && (OUTCOMES as string[]).includes(o.outcome)
    ? o.outcome as FinalizeOutcome
    : 'pending';
  return {
    tournamentId,
    outcome,
    roundsCompleted: typeof o.roundsCompleted === 'number'
      && Number.isFinite(o.roundsCompleted)
      ? Math.max(0, Math.floor(o.roundsCompleted)) : 0,
    roundsTotal: typeof o.roundsTotal === 'number'
      && Number.isFinite(o.roundsTotal)
      ? Math.max(0, Math.floor(o.roundsTotal)) : 0,
    finalizedAt: typeof o.finalizedAt === 'string' ? o.finalizedAt : undefined,
    finalizedBy: typeof o.finalizedBy === 'string' ? o.finalizedBy : undefined,
    championPlayerId: typeof o.championPlayerId === 'string' ? o.championPlayerId : undefined,
  };
}

function outcomeLabel(o: FinalizeOutcome): string {
  switch (o) {
    case 'pending':            return 'Pending';
    case 'finalized':          return 'Finalized';
    case 'rounds-incomplete':  return 'Rounds incomplete';
    case 'already-finalized':  return 'Already finalized';
  }
}

export const TOURNAMENT_FINALIZE_SPEC: AdminSurfaceSpec<TournamentFinalizeRow, TournamentFinalizeBody> = {
  id: 'tournament-finalize',
  title: 'Tournaments · Finalize',
  description: 'Close out a completed Swiss tournament once all '
    + 'rounds are applied.  Use dry-run to preview the final '
    + 'podium + payout/trophy assignments before sealing.  Once '
    + 'finalized, no further swiss-apply-round or swiss-pair-'
    + 'next-round calls succeed against this tournament.  Audit '
    + 'kind: governance.tournaments.finalize.fired.',
  endpoint: '/api/admin/tournaments/finalize',
  parseRow,
  rowKey: (r) => r.tournamentId,
  rowToFormValues: (r) => ({
    tournamentId: r.tournamentId,
    dryRun: 'true',
  }),
  buildBody: (v) => ({
    tournamentId: (v.tournamentId ?? '').trim(),
    dryRun: (v.dryRun ?? 'true').toLowerCase() !== 'false',
  }),
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
      name: 'dryRun',
      label: 'Dry run',
      type: 'select',
      required: true,
      options: [
        { value: 'true',  label: 'Yes — preview the final podium' },
        { value: 'false', label: 'No — seal the tournament' },
      ],
      help: 'Dry-run returns the would-be podium WITHOUT '
        + 'persisting — strongly recommended before sealing.',
    },
  ],
  columns: [
    {
      key: 'tournamentId',
      label: 'Tournament',
      render: (r) => ({ __html: `<code>${escapeHtml(r.tournamentId)}</code>` }),
    },
    {
      key: 'outcome',
      label: 'State',
      render: (r) => outcomeLabel(r.outcome),
    },
    {
      key: 'rounds',
      label: 'Rounds',
      render: (r) => `${r.roundsCompleted}/${r.roundsTotal}`,
    },
    {
      key: 'finalizedAt',
      label: 'Finalized',
      render: (r) => fmtIso(r.finalizedAt),
    },
    {
      key: 'finalizedBy',
      label: 'By',
      render: (r) => r.finalizedBy === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : escapeHtml(r.finalizedBy),
    },
    {
      key: 'championPlayerId',
      label: 'Champion',
      render: (r) => r.championPlayerId === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : ({ __html: `<code>${escapeHtml(r.championPlayerId)}</code>` }),
    },
  ],
};
