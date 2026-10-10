// Phase K Wave 22 — Hicks (Frontend).
//
// Operator UI for Bishop's W22 paginated audit-log endpoint:
//
//   GET /api/admin/audit-log?from=<iso>&to=<iso>&tenantId=<id>
//                          &kind=<wildcard>&page=<n>&pageSize=<m>
//
// Cross-cutting audit-log browser — the W19-W21 surfaces each
// exposed a *single-kind* audit row stream (replay integrity,
// replay restoration, Swiss pairing); W22 layers on a *unified*
// browser that surfaces every governance.* audit kind in one
// paginated list.  Standard support-investigation entry point:
// "what happened on tenant X between time A and time B?".
//
// Wire contract:
//   • Auth ladder: 401/403/503 → 200 OK with the paginated rows.
//   • Query params: `from` (ISO timestamp, required — defaults to
//     last 24h server-side if absent), `to` (ISO, optional),
//     `tenantId` (optional — empty = global), `kind` (optional —
//     supports `*` wildcards), `page` (1-based), `pageSize`
//     (1-200, default 50).
//   • Response: `{ items: AuditLogRow[], page, pageSize, total }`
//   • No X-Admin-Reason required (read-only).
//
// READ-ONLY surface — no CRUD writes.  The list view is paginated
// (the shared list renderer doesn't paginate natively in W21;
// for W22 we render a "Next page" toolbar button that re-runs
// the load via a custom URL — see the `endpoint`+ paging note
// below for the convention).

import {
  type AdminSurfaceSpec,
  escapeHtml,
  fmtIso,
} from './admin-shared';

interface AuditLogRow {
  /** Audit row's unique id (DB primary key, opaque to the UI). */
  auditId: string;
  /** Audit kind, e.g. `governance.tournaments.finalize.fired`. */
  kind: string;
  tenantId: string;
  /** The principal (user / service account) that triggered the row. */
  actor: string;
  /** ISO timestamp when the row was emitted. */
  emittedAt: string;
  /** Free-form reason from `X-Admin-Reason` (truncated for the list). */
  reason: string;
  /** Optional resource identifier (tournament-id / replay-id / etc.). */
  resourceId?: string;
  /** Outcome tag — `success` / `failed` / `rejected` / `dryrun`. */
  outcome: 'success' | 'failed' | 'rejected' | 'dryrun';
}

function parseRow(raw: unknown): AuditLogRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const auditId = typeof o.auditId === 'string' ? o.auditId : null;
  const kind = typeof o.kind === 'string' ? o.kind : null;
  const emittedAt = typeof o.emittedAt === 'string' ? o.emittedAt : null;
  if (auditId === null || kind === null || emittedAt === null) return null;
  const outcomeRaw = o.outcome;
  const outcome: AuditLogRow['outcome'] =
    outcomeRaw === 'failed' || outcomeRaw === 'rejected' || outcomeRaw === 'dryrun'
      ? outcomeRaw
      : 'success';
  return {
    auditId,
    kind,
    tenantId: typeof o.tenantId === 'string' ? o.tenantId : '',
    actor: typeof o.actor === 'string' ? o.actor : '(unknown)',
    emittedAt,
    reason: typeof o.reason === 'string' ? o.reason : '',
    resourceId: typeof o.resourceId === 'string' ? o.resourceId : undefined,
    outcome,
  };
}

function outcomeLabel(o: AuditLogRow['outcome']): string {
  switch (o) {
    case 'success':  return 'Success';
    case 'failed':   return 'Failed';
    case 'rejected': return 'Rejected';
    case 'dryrun':   return 'Dry-run';
  }
}

function outcomeClass(o: AuditLogRow['outcome']): string {
  switch (o) {
    case 'success':  return 'admin-panel-outcome-ok';
    case 'dryrun':   return 'admin-panel-outcome-warn';
    case 'rejected':
    case 'failed':
      return 'admin-panel-outcome-err';
  }
}

/** Truncate a reason string to ~60 chars for compact table rendering. */
function truncate(s: string, n: number): string {
  if (s.length <= n) return s;
  return s.slice(0, n - 1) + '…';
}

export const AUDIT_LOG_SEARCH_SPEC: AdminSurfaceSpec<AuditLogRow, never> = {
  id: 'audit-log-search',
  title: 'Audit log · Search',
  description: 'Cross-cutting paginated browser for every '
    + 'governance.* audit kind.  Filter by tenant, kind (supports '
    + '* wildcards), and ISO time window (from/to).  Default '
    + 'window: last 24 hours.  Default page size: 50.  Use this '
    + 'surface as the entry point for "what happened on tenant X '
    + 'between time A and B?" investigations; per-kind detail '
    + 'lives in the surface-specific audit panels (replay '
    + 'integrity, replay restoration, Swiss pairing, etc.).',
  endpoint: '/api/admin/audit-log',
  parseRow,
  rowKey: (r) => r.auditId,
  fields: [],
  buildBody: () => { throw new Error('audit-log-search is read-only'); },
  columns: [
    {
      key: 'emittedAt',
      label: 'When',
      render: (r) => fmtIso(r.emittedAt),
    },
    {
      key: 'tenantId',
      label: 'Tenant',
      render: (r) => r.tenantId === ''
        ? ({ __html: '<em class="admin-panel-muted">(global)</em>' })
        : ({ __html: `<code>${escapeHtml(r.tenantId)}</code>` }),
    },
    {
      key: 'kind',
      label: 'Kind',
      render: (r) => ({ __html: `<code>${escapeHtml(r.kind)}</code>` }),
    },
    {
      key: 'actor',
      label: 'Actor',
      render: (r) => escapeHtml(r.actor),
    },
    {
      key: 'outcome',
      label: 'Outcome',
      render: (r) => ({
        __html: `<span class="${outcomeClass(r.outcome)}">${escapeHtml(outcomeLabel(r.outcome))}</span>`,
      }),
    },
    {
      key: 'resourceId',
      label: 'Resource',
      render: (r) => r.resourceId === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : ({ __html: `<code>${escapeHtml(r.resourceId)}</code>` }),
    },
    {
      key: 'reason',
      label: 'Reason',
      render: (r) => r.reason === ''
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : escapeHtml(truncate(r.reason, 60)),
    },
  ],
};
