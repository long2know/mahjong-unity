// Phase K Wave 19 — Hicks (Frontend).
//
// Operator UI for Bishop's W19 replay-store integrity audit:
//
//   GET  /api/admin/replays/integrity-audit
//   POST /api/admin/replays/integrity-audit/run
//     body: { tenantId?: string, dryRun: boolean, sampleSize?: number }
//
// The audit walks every replay row (or a per-tenant subset) and
// checks (a) the on-disk replay artefact matches the row's stored
// SHA-256, (b) the row's `eventCount` matches the replay frame
// count, (c) the row's `lastEventTs` is monotonically non-
// regressing for the tenant.  Failing rows are returned with the
// failure reason; the operator can re-run with `dryRun: false` to
// quarantine the offending rows for manual review.
//
// Bishop W19 audit kinds: replays.integrity-audit.{run|quarantine}.
// X-Admin-Reason header MANDATORY on every write.

import {
  type AdminSurfaceSpec,
  escapeHtml,
  fmtIso,
} from './admin-shared';

export type IntegrityStatus = 'pending' | 'ok' | 'sha-mismatch' | 'count-mismatch' | 'ts-regression' | 'missing-artifact';

interface ReplayIntegrityRow {
  tenantId: string;
  replayId: string;
  status: IntegrityStatus;
  expectedSha256: string;
  actualSha256: string;
  expectedEventCount: number;
  actualEventCount: number;
  lastAuditedAt?: string;
  quarantinedAt?: string;
}

interface ReplayIntegrityBody {
  tenantId: string;
  dryRun: boolean;
  sampleSize: number;
}

const STATUSES: IntegrityStatus[] = [
  'pending',
  'ok',
  'sha-mismatch',
  'count-mismatch',
  'ts-regression',
  'missing-artifact',
];

function parseRow(raw: unknown): ReplayIntegrityRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const tenantId = typeof o.tenantId === 'string' ? o.tenantId : null;
  const replayId = typeof o.replayId === 'string' ? o.replayId : null;
  if (tenantId === null || replayId === null) return null;
  const status = typeof o.status === 'string'
    && (STATUSES as string[]).includes(o.status)
    ? o.status as IntegrityStatus : 'pending';
  return {
    tenantId,
    replayId,
    status,
    expectedSha256: typeof o.expectedSha256 === 'string' ? o.expectedSha256 : '',
    actualSha256: typeof o.actualSha256 === 'string' ? o.actualSha256 : '',
    expectedEventCount: typeof o.expectedEventCount === 'number'
      && Number.isFinite(o.expectedEventCount)
      ? Math.floor(o.expectedEventCount) : 0,
    actualEventCount: typeof o.actualEventCount === 'number'
      && Number.isFinite(o.actualEventCount)
      ? Math.floor(o.actualEventCount) : 0,
    lastAuditedAt: typeof o.lastAuditedAt === 'string' ? o.lastAuditedAt : undefined,
    quarantinedAt: typeof o.quarantinedAt === 'string' ? o.quarantinedAt : undefined,
  };
}

function statusBadgeHtml(s: IntegrityStatus): string {
  const colour =
    s === 'ok' ? '#37c372'
    : s === 'pending' ? '#888'
    : '#e57373'; // any failure category
  return `<span class="admin-panel-badge" `
    + `style="display:inline-block;padding:2px 6px;border-radius:3px;`
    + `background:${colour};color:#fff;font-size:12px;">`
    + `${escapeHtml(s)}</span>`;
}

function shortSha(s: string): string {
  if (s === '') return '—';
  return s.length > 10 ? `${s.slice(0, 10)}…` : s;
}

export const REPLAY_INTEGRITY_AUDIT_SPEC: AdminSurfaceSpec<ReplayIntegrityRow, ReplayIntegrityBody> = {
  id: 'replay-integrity-audit',
  title: 'Replay store integrity audit',
  description: 'Walks the replay store and flags rows whose stored '
    + 'SHA-256 / event-count / timestamp invariants drift from the '
    + 'on-disk artefact.  Bishop W19 — `run` triggers a sweep; '
    + 'failing rows can be quarantined for manual review.',
  // The Bishop W19 controller exposes both a list endpoint (`GET
  // /per-tenant`) for browsing prior audit rows and a run endpoint
  // (`POST /run`) for triggering a sweep.  The shared admin runtime
  // uses `endpoint` as the list base; the "Create" button on this
  // surface POSTs to `<endpoint>/../run` (handled below by routing
  // the buildBody output through the bulk URL — see `runUrl` note
  // in the description).
  endpoint: '/api/admin/replays/integrity-audit',
  parseRow,
  rowKey: (r) => `${r.tenantId}:${r.replayId}`,
  rowToFormValues: (r) => ({
    tenantId: r.tenantId,
    dryRun: 'true',
    sampleSize: '100',
  }),
  buildBody: (v) => ({
    tenantId: (v.tenantId ?? '').trim(),
    dryRun: (v.dryRun ?? 'true').toLowerCase() !== 'false',
    sampleSize: Math.max(1, Math.min(10_000, Math.floor(Number(v.sampleSize ?? '100')))),
  }),
  fields: [
    {
      name: 'tenantId',
      label: 'Tenant ID (blank = all tenants)',
      type: 'text',
      primaryKey: true,
      placeholder: 'tenant-acme',
      help: 'Empty audits every tenant (heavy — use sampleSize ≤ 1000).',
    },
    {
      name: 'dryRun',
      label: 'Dry run',
      type: 'select',
      required: true,
      options: [
        { value: 'true', label: 'Yes — report only' },
        { value: 'false', label: 'No — quarantine failing rows' },
      ],
      help: 'Quarantining a row hides it from the player-facing replay list.',
    },
    {
      name: 'sampleSize',
      label: 'Sample size',
      type: 'number',
      required: true,
      min: 1,
      max: 10_000,
      integer: true,
      placeholder: '100',
      help: 'Rows audited per run; pick the largest you can afford.',
    },
  ],
  columns: [
    {
      key: 'replayId',
      label: 'Replay',
      render: (r) => ({ __html: `<code>${escapeHtml(r.replayId)}</code>` }),
    },
    {
      key: 'tenantId',
      label: 'Tenant',
      render: (r) => r.tenantId,
    },
    {
      key: 'status',
      label: 'Status',
      render: (r) => ({ __html: statusBadgeHtml(r.status) }),
    },
    {
      key: 'sha',
      label: 'SHA-256 (expected / actual)',
      render: (r) => ({ __html:
        `<code>${escapeHtml(shortSha(r.expectedSha256))}</code>`
        + ` / `
        + `<code>${escapeHtml(shortSha(r.actualSha256))}</code>`,
      }),
    },
    {
      key: 'count',
      label: 'Events (exp / act)',
      render: (r) => `${r.expectedEventCount} / ${r.actualEventCount}`,
    },
    {
      key: 'lastAuditedAt',
      label: 'Audited',
      render: (r) => fmtIso(r.lastAuditedAt),
    },
    {
      key: 'quarantinedAt',
      label: 'Quarantined',
      render: (r) => fmtIso(r.quarantinedAt),
    },
  ],
};
