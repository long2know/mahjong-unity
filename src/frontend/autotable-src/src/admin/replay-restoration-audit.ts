// Phase K Wave 21 — Hicks (Frontend).
//
// Operator UI for Bishop's W21 replay restoration-audit endpoint:
//
//   GET /api/admin/replays/restoration-audit?tenantId=<id>&sinceIso=<ts>
//
// READ-ONLY surface — there's no POST/PUT/DELETE for this surface;
// it exposes Bishop's replay-store *restoration audit log* so
// operators can see when a replay was restored from cold storage,
// who triggered the restore, and how long the round-trip took.
// Companions the W19 replay-integrity audit surface (`./replay-
// integrity-audit.ts`) — both are READ-ONLY listings of audit
// kinds emitted by Bishop's replay pipeline.
//
// Wire contract:
//   • Auth ladder: 401/403/503 → 200 OK with the audit listing.
//   • Query params: `tenantId` (optional — empty = global),
//     `sinceIso` (optional — defaults to last 7 days).
//   • No X-Admin-Reason required (read-only).

import {
  type AdminSurfaceSpec,
  escapeHtml,
  fmtIso,
} from './admin-shared';

export type RestorationOutcome =
  | 'restored'
  | 'already-warm'
  | 'cold-miss'
  | 'failed'
  | 'cancelled';

interface ReplayRestorationAuditRow {
  auditId: string;
  tenantId: string;
  replayId: string;
  outcome: RestorationOutcome;
  requestedAt: string;
  completedAt?: string;
  durationMs?: number;
  requestedBy?: string;
  errorDetail?: string;
}

const OUTCOMES: RestorationOutcome[] = [
  'restored',
  'already-warm',
  'cold-miss',
  'failed',
  'cancelled',
];

function parseRow(raw: unknown): ReplayRestorationAuditRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const auditId = typeof o.auditId === 'string' ? o.auditId : null;
  const replayId = typeof o.replayId === 'string' ? o.replayId : null;
  const requestedAt = typeof o.requestedAt === 'string' ? o.requestedAt : null;
  if (auditId === null || replayId === null || requestedAt === null) return null;
  const outcome = typeof o.outcome === 'string'
    && (OUTCOMES as string[]).includes(o.outcome)
    ? o.outcome as RestorationOutcome
    : 'failed';
  return {
    auditId,
    tenantId: typeof o.tenantId === 'string' ? o.tenantId : '',
    replayId,
    outcome,
    requestedAt,
    completedAt: typeof o.completedAt === 'string' ? o.completedAt : undefined,
    durationMs: typeof o.durationMs === 'number' && Number.isFinite(o.durationMs)
      ? Math.max(0, Math.floor(o.durationMs)) : undefined,
    requestedBy: typeof o.requestedBy === 'string' ? o.requestedBy : undefined,
    errorDetail: typeof o.errorDetail === 'string' ? o.errorDetail : undefined,
  };
}

function outcomeLabel(o: RestorationOutcome): string {
  switch (o) {
    case 'restored':     return 'Restored';
    case 'already-warm': return 'Already warm';
    case 'cold-miss':    return 'Cold miss';
    case 'failed':       return 'Failed';
    case 'cancelled':    return 'Cancelled';
  }
}

function outcomeClass(o: RestorationOutcome): string {
  switch (o) {
    case 'restored':
    case 'already-warm':
      return 'admin-panel-outcome-ok';
    case 'cold-miss':
    case 'cancelled':
      return 'admin-panel-outcome-warn';
    case 'failed':
      return 'admin-panel-outcome-err';
  }
}

export const REPLAY_RESTORATION_AUDIT_SPEC: AdminSurfaceSpec<ReplayRestorationAuditRow, never> = {
  id: 'replay-restoration-audit',
  title: 'Replays · Restoration audit',
  description: 'Read-only audit log of replay restorations from cold '
    + 'storage.  Shows when a replay was restored, who triggered the '
    + 'restore, the round-trip duration, and the outcome (restored, '
    + 'already-warm, cold-miss, failed, cancelled).  Audit kinds: '
    + 'replay.restoration.requested / .completed / .failed.',
  endpoint: '/api/admin/replays/restoration-audit',
  parseRow,
  rowKey: (r) => r.auditId,
  // READ-ONLY: empty fields list → renderSurfaceFrame() suppresses
  // the Create button + the per-row Edit/Delete buttons.
  fields: [],
  buildBody: () => { throw new Error('replay-restoration-audit is read-only'); },
  columns: [
    {
      key: 'requestedAt',
      label: 'Requested',
      render: (r) => fmtIso(r.requestedAt),
    },
    {
      key: 'tenantId',
      label: 'Tenant',
      render: (r) => r.tenantId === ''
        ? ({ __html: '<em class="admin-panel-muted">(global)</em>' })
        : ({ __html: `<code>${escapeHtml(r.tenantId)}</code>` }),
    },
    {
      key: 'replayId',
      label: 'Replay',
      render: (r) => ({ __html: `<code>${escapeHtml(r.replayId)}</code>` }),
    },
    {
      key: 'outcome',
      label: 'Outcome',
      render: (r) => ({
        __html: `<span class="${outcomeClass(r.outcome)}">${escapeHtml(outcomeLabel(r.outcome))}</span>`,
      }),
    },
    {
      key: 'durationMs',
      label: 'Duration',
      render: (r) => r.durationMs === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : `${r.durationMs} ms`,
    },
    {
      key: 'completedAt',
      label: 'Completed',
      render: (r) => fmtIso(r.completedAt),
    },
    {
      key: 'requestedBy',
      label: 'By',
      render: (r) => r.requestedBy === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : escapeHtml(r.requestedBy),
    },
    {
      key: 'errorDetail',
      label: 'Error',
      render: (r) => r.errorDetail === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : escapeHtml(r.errorDetail),
    },
  ],
};
