// Phase K Wave 21 — Hicks (Frontend).
//
// Operator UI for Bishop's W21 tournament-withdraw-player endpoint:
//
//   POST /api/admin/tournaments/<tournamentId>/withdraw-player
//   body: { playerId: string, reason: string, refund?: boolean }
//
// Used to formally withdraw a player from an in-flight tournament
// (illness, code-of-conduct violation, accidental dupe entry,
// etc.).  The withdrawal is recorded in the audit log + cascades
// into the next pairing: the withdrawn player is paired against a
// bye in future rounds.  If `refund: true` and the entry-fee
// pipeline applies, Bishop's W21 refund worker is triggered.
//
// Wire contract:
//   • Auth ladder: 401/403/503 → 200 OK with the post-withdraw
//     manifest in the body (the withdrawn-player row + updated
//     standings).
//   • `X-Admin-Reason` header MANDATORY (governance.tournaments.
//     withdraw-player.fired).
//   • `reason` form field is REQUIRED and is logged separately
//     from `X-Admin-Reason` (operator audit vs. competitive log).

import {
  type AdminSurfaceSpec,
  ADMIN_REASON_HEADER,
  escapeHtml,
  fmtIso,
  gateAdminFetch,
  promptAdminReason,
} from './admin-shared';

interface TournamentWithdrawRow {
  tournamentId: string;
  playerId: string;
  withdrawnAt?: string;
  withdrawnBy?: string;
  withdrawalReason?: string;
  refundIssued?: boolean;
}

interface TournamentWithdrawBody {
  tournamentId: string;
  playerId: string;
  reason: string;
  refund: boolean;
}

function parseRow(raw: unknown): TournamentWithdrawRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const tournamentId = typeof o.tournamentId === 'string' ? o.tournamentId : null;
  const playerId = typeof o.playerId === 'string' ? o.playerId : null;
  if (tournamentId === null || playerId === null) return null;
  return {
    tournamentId,
    playerId,
    withdrawnAt: typeof o.withdrawnAt === 'string' ? o.withdrawnAt : undefined,
    withdrawnBy: typeof o.withdrawnBy === 'string' ? o.withdrawnBy : undefined,
    withdrawalReason: typeof o.withdrawalReason === 'string'
      ? o.withdrawalReason : undefined,
    refundIssued: typeof o.refundIssued === 'boolean'
      ? o.refundIssued : undefined,
  };
}

/**
 * Withdraw a player from an in-flight tournament.  Wraps
 * `gateAdminFetch` so the 401/403/503 auth ladder is consistent
 * with the rest of the admin panel surfaces.  Throws on cancel /
 * non-2xx.
 */
export async function fireTournamentWithdraw(
  tournamentId: string,
  playerId: string,
  reason: string,
  refund: boolean,
): Promise<unknown> {
  const adminReason = promptAdminReason(
    `Withdraw player ${playerId} from ${tournamentId}`,
  );
  if (adminReason === null) throw new Error('cancelled');
  const body: TournamentWithdrawBody = {
    tournamentId,
    playerId,
    reason,
    refund,
  };
  const res = await gateAdminFetch(
    `/api/admin/tournaments/${encodeURIComponent(tournamentId)}/withdraw-player`,
    {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        [ADMIN_REASON_HEADER]: adminReason,
      },
      body: JSON.stringify(body),
    },
  );
  if (!res.ok) {
    throw new Error(`withdraw-player POST failed: ${res.status ?? 'network'}`);
  }
  return res.body ?? null;
}

export const TOURNAMENT_WITHDRAW_SPEC: AdminSurfaceSpec<TournamentWithdrawRow, TournamentWithdrawBody> = {
  id: 'tournament-withdraw',
  title: 'Tournaments · Withdraw player',
  description: 'Formally withdraw a player from an in-flight tournament.  '
    + 'The withdrawal cascades into the next pairing (withdrawn player '
    + 'gets a bye in future rounds).  When the entry-fee pipeline '
    + 'applies, set `refund: true` to trigger Bishop\'s W21 refund '
    + 'worker.  Audit kind: governance.tournaments.withdraw-player.fired.',
  endpoint: '/api/admin/tournaments/withdraw-player',
  parseRow,
  rowKey: (r) => `${r.tournamentId}::${r.playerId}`,
  rowToFormValues: (r) => ({
    tournamentId: r.tournamentId,
    playerId: r.playerId,
    reason: r.withdrawalReason ?? '',
    refund: r.refundIssued === true ? 'true' : 'false',
  }),
  buildBody: (v) => ({
    tournamentId: (v.tournamentId ?? '').trim(),
    playerId: (v.playerId ?? '').trim(),
    reason: (v.reason ?? '').trim(),
    refund: (v.refund ?? 'false').toLowerCase() === 'true',
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
      name: 'playerId',
      label: 'Player ID',
      type: 'text',
      required: true,
      primaryKey: true,
      placeholder: 'player-12345',
    },
    {
      name: 'reason',
      label: 'Withdrawal reason',
      type: 'text',
      required: true,
      placeholder: 'Illness / CoC violation / dupe entry',
      help: 'Competitive-log entry — distinct from the X-Admin-Reason '
        + 'operator audit reason.',
    },
    {
      name: 'refund',
      label: 'Refund entry fee',
      type: 'select',
      required: true,
      options: [
        { value: 'false', label: 'No — withdraw without refund' },
        { value: 'true',  label: 'Yes — trigger refund worker' },
      ],
      help: 'When the entry-fee pipeline applies, this triggers '
        + 'Bishop\'s W21 refund worker.',
    },
  ],
  columns: [
    {
      key: 'tournamentId',
      label: 'Tournament',
      render: (r) => ({ __html: `<code>${escapeHtml(r.tournamentId)}</code>` }),
    },
    {
      key: 'playerId',
      label: 'Player',
      render: (r) => ({ __html: `<code>${escapeHtml(r.playerId)}</code>` }),
    },
    {
      key: 'withdrawnAt',
      label: 'Withdrawn',
      render: (r) => fmtIso(r.withdrawnAt),
    },
    {
      key: 'withdrawalReason',
      label: 'Reason',
      render: (r) => r.withdrawalReason === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : escapeHtml(r.withdrawalReason),
    },
    {
      key: 'refundIssued',
      label: 'Refund',
      render: (r) => r.refundIssued === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : (r.refundIssued ? 'Yes' : 'No'),
    },
    {
      key: 'withdrawnBy',
      label: 'By',
      render: (r) => r.withdrawnBy === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : escapeHtml(r.withdrawnBy),
    },
  ],
};
