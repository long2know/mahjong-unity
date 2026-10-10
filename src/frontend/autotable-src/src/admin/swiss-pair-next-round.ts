// Phase K Wave 20 — Hicks (Frontend).
//
// Operator UI for Bishop's W20 Swiss-pairing next-round trigger:
//
//   POST /api/admin/tournaments/<tournamentId>/swiss-pair-next-round
//   body: { roundNumber?: number, dryRun?: boolean }
//
// The W19 surface (`./swiss-pairing-audit.ts`) was read-only — it
// exposed the per-round pairing decisions Bishop's matcher had
// already made.  W20 layers on the *trigger* surface: operators can
// kick off the next-round pairing themselves (e.g. after manually
// resolving a tie or a withdrawal).  The wire contract:
//
//   • Auth ladder: 401/403/503 → 200 OK with the new pairing
//     manifest in the body.
//   • `X-Admin-Reason` header MANDATORY (governance.tournaments.
//     swiss-pair-next-round.fired).
//   • `dryRun: true` → server returns the pairing manifest WITHOUT
//     persisting (used for "preview the next round before firing").
//
// This surface is action-oriented (no list / no CRUD), so it
// re-uses the shared admin runtime only for the auth ladder + the
// X-Admin-Reason prompt; the body of the surface is a hand-rolled
// form with the trigger button + the manifest preview panel.

import {
  type AdminSurfaceSpec,
  ADMIN_REASON_HEADER,
  escapeHtml,
  gateAdminFetch,
  promptAdminReason,
} from './admin-shared';

export type SwissPairNextRoundOutcome =
  | 'paired'
  | 'all-paired-already'
  | 'tournament-complete'
  | 'awaiting-results';

interface SwissPairNextRoundRow {
  tournamentId: string;
  state: SwissPairNextRoundOutcome;
  nextRoundNumber: number;
  lastFiredAt?: string;
  lastFiredBy?: string;
}

interface SwissPairNextRoundBody {
  tournamentId: string;
  roundNumber: number;
  dryRun: boolean;
}

const OUTCOMES: SwissPairNextRoundOutcome[] = [
  'paired',
  'all-paired-already',
  'tournament-complete',
  'awaiting-results',
];

function parseRow(raw: unknown): SwissPairNextRoundRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const tournamentId = typeof o.tournamentId === 'string' ? o.tournamentId : null;
  if (tournamentId === null) return null;
  const state = typeof o.state === 'string'
    && (OUTCOMES as string[]).includes(o.state)
    ? o.state as SwissPairNextRoundOutcome : 'awaiting-results';
  return {
    tournamentId,
    state,
    nextRoundNumber: typeof o.nextRoundNumber === 'number'
      && Number.isFinite(o.nextRoundNumber)
      ? Math.max(1, Math.floor(o.nextRoundNumber)) : 1,
    lastFiredAt: typeof o.lastFiredAt === 'string' ? o.lastFiredAt : undefined,
    lastFiredBy: typeof o.lastFiredBy === 'string' ? o.lastFiredBy : undefined,
  };
}

function outcomeLabel(o: SwissPairNextRoundOutcome): string {
  switch (o) {
    case 'paired':              return 'Paired';
    case 'all-paired-already':  return 'Already paired';
    case 'tournament-complete': return 'Tournament complete';
    case 'awaiting-results':    return 'Awaiting results';
  }
}

/**
 * Fire a pair-next-round POST.  Wraps `gateAdminFetch` so the 401/
 * 403/503 auth ladder is consistent with the rest of the admin
 * panel surfaces.  Returns the trigger response body (a freshly
 * paired manifest) or throws on a non-2xx response.
 */
export async function fireSwissPairNextRound(
  tournamentId: string,
  roundNumber: number,
  dryRun: boolean,
): Promise<unknown> {
  const reason = promptAdminReason(
    `Pair next round (round ${roundNumber}) for ${tournamentId}`,
  );
  if (reason === null) throw new Error('cancelled');
  const body: SwissPairNextRoundBody = { tournamentId, roundNumber, dryRun };
  const res = await gateAdminFetch(
    `/api/admin/tournaments/${encodeURIComponent(tournamentId)}/swiss-pair-next-round`,
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
    throw new Error(`swiss-pair-next-round POST failed: ${res.status ?? 'network'}`);
  }
  return res.body ?? null;
}

export const SWISS_PAIR_NEXT_ROUND_SPEC: AdminSurfaceSpec<SwissPairNextRoundRow, SwissPairNextRoundBody> = {
  id: 'swiss-pair-next-round',
  title: 'Swiss · Pair next round',
  description: 'Trigger Bishop\'s W20 Swiss pairing matcher for the '
    + 'next round of an in-flight tournament.  Use the dry-run toggle '
    + 'to preview the pairing manifest before persisting.  Audit kind: '
    + 'governance.tournaments.swiss-pair-next-round.fired.',
  endpoint: '/api/admin/tournaments/swiss-pair-next-round',
  parseRow,
  rowKey: (r) => r.tournamentId,
  rowToFormValues: (r) => ({
    tournamentId: r.tournamentId,
    roundNumber: String(r.nextRoundNumber),
    dryRun: 'false',
  }),
  buildBody: (v) => ({
    tournamentId: (v.tournamentId ?? '').trim(),
    roundNumber: Math.max(1, Math.floor(Number(v.roundNumber ?? '1'))),
    dryRun: (v.dryRun ?? 'false').toLowerCase() === 'true',
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
      name: 'roundNumber',
      label: 'Round number',
      type: 'number',
      required: true,
      min: 1,
      max: 32,
      integer: true,
      placeholder: '2',
      help: 'The round to pair (typically last-completed + 1).',
    },
    {
      name: 'dryRun',
      label: 'Dry run',
      type: 'select',
      required: true,
      options: [
        { value: 'false', label: 'No — persist the pairing manifest' },
        { value: 'true',  label: 'Yes — preview without persisting' },
      ],
      help: 'Dry-run mode returns the pairing manifest WITHOUT '
        + 'persisting it — use to review before firing.',
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
      key: 'nextRoundNumber',
      label: 'Next round',
      render: (r) => `R${r.nextRoundNumber}`,
    },
    {
      key: 'lastFiredBy',
      label: 'Last fired by',
      render: (r) => r.lastFiredBy === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : escapeHtml(r.lastFiredBy),
    },
  ],
};
