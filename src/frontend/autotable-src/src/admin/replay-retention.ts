// Phase K Wave 18 — Hicks (Frontend).
//
// Operator UI for Bishop's W17 replay-retention CRUD:
//   POST/GET/PUT/DELETE /api/admin/replays/retention[/<tenantId>]
//
// Body: { tenantId: string, retentionDays: int (1..1825) }
// Audit kinds: replays.retention.{created|updated|deleted}.
// X-Admin-Reason header MANDATORY on every write.

import {
  type AdminSurfaceSpec,
  escapeHtml,
  fmtIso,
} from './admin-shared';

interface ReplayRetentionRow {
  tenantId: string;
  retentionDays: number;
  createdAt?: string;
  updatedAt?: string;
}

interface ReplayRetentionBody {
  tenantId: string;
  retentionDays: number;
}

function parseRow(raw: unknown): ReplayRetentionRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const tenantId = typeof o.tenantId === 'string' ? o.tenantId : null;
  const retentionDays = typeof o.retentionDays === 'number' && Number.isFinite(o.retentionDays)
    ? Math.floor(o.retentionDays) : null;
  if (tenantId === null || retentionDays === null) return null;
  return {
    tenantId,
    retentionDays,
    createdAt: typeof o.createdAt === 'string' ? o.createdAt
      : (typeof o.createdAtOffset === 'string' ? o.createdAtOffset : undefined),
    updatedAt: typeof o.updatedAt === 'string' ? o.updatedAt
      : (typeof o.updatedAtOffset === 'string' ? o.updatedAtOffset : undefined),
  };
}

export const REPLAY_RETENTION_SPEC: AdminSurfaceSpec<ReplayRetentionRow, ReplayRetentionBody> = {
  id: 'replay-retention',
  title: 'Replay retention policies',
  description: 'Per-tenant TTL for completed replays.  Bishop W17 — '
    + 'rows older than the configured day-count are swept by the '
    + 'replay-retention background job.',
  endpoint: '/api/admin/replays/retention',
  parseRow,
  rowKey: (r) => r.tenantId,
  rowToFormValues: (r) => ({
    tenantId: r.tenantId,
    retentionDays: String(r.retentionDays),
  }),
  buildBody: (v) => ({
    tenantId: (v.tenantId ?? '').trim(),
    retentionDays: Math.max(1, Math.floor(Number(v.retentionDays))),
  }),
  fields: [
    {
      name: 'tenantId',
      label: 'Tenant ID',
      type: 'text',
      required: true,
      primaryKey: true,
      placeholder: 'tenant-acme',
      help: 'Matches Replays.TenantId — case-sensitive.',
    },
    {
      name: 'retentionDays',
      label: 'Retention (days)',
      type: 'number',
      required: true,
      min: 1,
      max: 365 * 5,
      integer: true,
      placeholder: '90',
      help: 'Upper bound 1825 (5 years) enforced server-side.',
    },
  ],
  columns: [
    {
      key: 'tenantId',
      label: 'Tenant',
      render: (r) => r.tenantId,
    },
    {
      key: 'retentionDays',
      label: 'Days',
      render: (r) => ({ __html: `<span class="admin-panel-num">${escapeHtml(String(r.retentionDays))}</span>` }),
    },
    {
      key: 'updatedAt',
      label: 'Updated',
      render: (r) => fmtIso(r.updatedAt),
    },
    {
      key: 'createdAt',
      label: 'Created',
      render: (r) => fmtIso(r.createdAt),
    },
  ],
};
