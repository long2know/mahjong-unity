// Phase K Wave 18 — Hicks (Frontend).
//
// Operator UI for Bishop's W17 SignalR per-tenant retention CRUD:
//   POST/GET/PUT/DELETE /api/admin/signalr/retention[/<tenantId>]
//
// Body: { tenantId: string, retentionMinutes: int (1....) }
// Audit kinds: signalr.retention.{created|updated|deleted}.
// X-Admin-Reason header MANDATORY on every write.
//
// Operator context: the W14 global SignalRSequenceStoreOptions
// SequenceRetention knob was too coarse — free-tier wants short
// reconnect windows, enterprise wants 24h+.  This per-tenant
// surface lets ops tune both ends without redeploying.

import {
  type AdminSurfaceSpec,
  escapeHtml,
  fmtIso,
} from './admin-shared';

interface SignalRRetentionRow {
  tenantId: string;
  retentionMinutes: number;
  createdAt?: string;
  updatedAt?: string;
}

interface SignalRRetentionBody {
  tenantId: string;
  retentionMinutes: number;
}

function parseRow(raw: unknown): SignalRRetentionRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const tenantId = typeof o.tenantId === 'string' ? o.tenantId : null;
  const retentionMinutes = typeof o.retentionMinutes === 'number'
    && Number.isFinite(o.retentionMinutes)
    ? Math.floor(o.retentionMinutes) : null;
  if (tenantId === null || retentionMinutes === null) return null;
  return {
    tenantId,
    retentionMinutes,
    createdAt: typeof o.createdAt === 'string' ? o.createdAt
      : (typeof o.createdAtOffset === 'string' ? o.createdAtOffset : undefined),
    updatedAt: typeof o.updatedAt === 'string' ? o.updatedAt
      : (typeof o.updatedAtOffset === 'string' ? o.updatedAtOffset : undefined),
  };
}

function fmtMinutes(n: number): string {
  if (n < 60) return `${n}m`;
  const h = Math.floor(n / 60);
  const m = n % 60;
  if (m === 0) return `${h}h`;
  return `${h}h${m.toString().padStart(2, '0')}m`;
}

export const SIGNALR_RETENTION_SPEC: AdminSurfaceSpec<SignalRRetentionRow, SignalRRetentionBody> = {
  id: 'signalr-retention',
  title: 'SignalR sequence retention policies',
  description: 'Per-tenant SignalR sequence-entry TTL (reconnect window).  '
    + 'Bishop W17 — overrides the global SequenceRetention knob for '
    + 'enterprise tenants that need longer-lived reconnect tokens.',
  endpoint: '/api/admin/signalr/retention',
  parseRow,
  rowKey: (r) => r.tenantId,
  rowToFormValues: (r) => ({
    tenantId: r.tenantId,
    retentionMinutes: String(r.retentionMinutes),
  }),
  buildBody: (v) => ({
    tenantId: (v.tenantId ?? '').trim(),
    retentionMinutes: Math.max(1, Math.floor(Number(v.retentionMinutes))),
  }),
  fields: [
    {
      name: 'tenantId',
      label: 'Tenant ID',
      type: 'text',
      required: true,
      primaryKey: true,
      placeholder: 'tenant-acme',
      help: 'Empty string falls through to global default (back-compat).',
    },
    {
      name: 'retentionMinutes',
      label: 'Retention (minutes)',
      type: 'number',
      required: true,
      min: 1,
      max: 60 * 24 * 30,
      integer: true,
      placeholder: '1440',
      help: 'Common values: 60 (free-tier), 1440 (24h), 10080 (1 week).',
    },
  ],
  columns: [
    {
      key: 'tenantId',
      label: 'Tenant',
      render: (r) => r.tenantId,
    },
    {
      key: 'retentionMinutes',
      label: 'Retention',
      render: (r) => ({
        __html: `<span class="admin-panel-num">${escapeHtml(fmtMinutes(r.retentionMinutes))}</span>`
          + ` <small class="admin-panel-muted">(${r.retentionMinutes}m)</small>`,
      }),
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
