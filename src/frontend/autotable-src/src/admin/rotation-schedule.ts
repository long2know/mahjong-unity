// Phase K Wave 21 — Hicks (Frontend).
//
// Operator UI for Bishop's W21 per-tenant rotation-schedule endpoint:
//
//   POST   /api/admin/rotation-policy/<tenantId>/schedule
//   body: { cron: string, enabled: boolean, nextRunAt?: string }
//
// The W19/W20 surfaces handled rotation-policy bulk update + bulk
// actions; W21 layers a *schedule* surface so operators can set the
// cron expression that drives Bishop's per-tenant rotation pipeline
// (the JWKS rotation drill, the SignalR retention purge, etc.).
//
// Wire contract:
//   • Auth ladder: 401/403/503 → 200 OK with the schedule manifest.
//   • `X-Admin-Reason` header MANDATORY (governance.rotation-policy.
//     schedule.set).
//   • `cron` is a six-field cron expression (sec min hr dom mon dow);
//     server validates + rejects on parse failure.
//   • `enabled: false` keeps the cron string but skips the scheduler.

import {
  type AdminSurfaceSpec,
  ADMIN_REASON_HEADER,
  escapeHtml,
  fmtIso,
  gateAdminFetch,
  promptAdminReason,
} from './admin-shared';

interface RotationScheduleRow {
  tenantId: string;
  cron: string;
  enabled: boolean;
  nextRunAt?: string;
  lastRunAt?: string;
  lastRunOutcome?: 'ok' | 'failed' | 'skipped';
  lastChangedAt?: string;
  lastChangedBy?: string;
}

interface RotationScheduleBody {
  tenantId: string;
  cron: string;
  enabled: boolean;
}

const OUTCOMES = ['ok', 'failed', 'skipped'] as const;
type Outcome = typeof OUTCOMES[number];

function parseRow(raw: unknown): RotationScheduleRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const tenantId = typeof o.tenantId === 'string' ? o.tenantId : null;
  if (tenantId === null) return null;
  const lastRunOutcome = typeof o.lastRunOutcome === 'string'
    && (OUTCOMES as readonly string[]).includes(o.lastRunOutcome)
    ? o.lastRunOutcome as Outcome
    : undefined;
  return {
    tenantId,
    cron: typeof o.cron === 'string' ? o.cron : '',
    enabled: o.enabled === true,
    nextRunAt: typeof o.nextRunAt === 'string' ? o.nextRunAt : undefined,
    lastRunAt: typeof o.lastRunAt === 'string' ? o.lastRunAt : undefined,
    lastRunOutcome,
    lastChangedAt: typeof o.lastChangedAt === 'string' ? o.lastChangedAt : undefined,
    lastChangedBy: typeof o.lastChangedBy === 'string' ? o.lastChangedBy : undefined,
  };
}

/**
 * Set / replace the rotation schedule for `tenantId`.  Wraps
 * `gateAdminFetch` so the 401/403/503 auth ladder is consistent
 * with the rest of the admin panel surfaces.  Throws on cancel /
 * non-2xx.
 */
export async function setRotationSchedule(
  tenantId: string,
  cron: string,
  enabled: boolean,
): Promise<unknown> {
  const reason = promptAdminReason(`Set rotation schedule for ${tenantId}`);
  if (reason === null) throw new Error('cancelled');
  const body: RotationScheduleBody = { tenantId, cron, enabled };
  const res = await gateAdminFetch(
    `/api/admin/rotation-policy/${encodeURIComponent(tenantId)}/schedule`,
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
    throw new Error(`rotation-schedule POST failed: ${res.status ?? 'network'}`);
  }
  return res.body ?? null;
}

export const ROTATION_SCHEDULE_SPEC: AdminSurfaceSpec<RotationScheduleRow, RotationScheduleBody> = {
  id: 'rotation-schedule',
  title: 'Rotation · Per-tenant schedule',
  description: 'Set the cron expression that drives Bishop\'s per-tenant '
    + 'rotation pipeline (JWKS rotation, SignalR retention purge, etc.).  '
    + 'Six-field cron format: `sec min hr dom mon dow`.  Disabling keeps '
    + 'the cron string on file but skips scheduling.  Audit kind: '
    + 'governance.rotation-policy.schedule.set.',
  endpoint: '/api/admin/rotation-policy/schedule',
  parseRow,
  rowKey: (r) => r.tenantId,
  rowToFormValues: (r) => ({
    tenantId: r.tenantId,
    cron: r.cron,
    enabled: r.enabled ? 'true' : 'false',
  }),
  buildBody: (v) => ({
    tenantId: (v.tenantId ?? '').trim(),
    cron: (v.cron ?? '').trim(),
    enabled: (v.enabled ?? 'false').toLowerCase() === 'true',
  }),
  fields: [
    {
      name: 'tenantId',
      label: 'Tenant ID',
      type: 'text',
      required: true,
      primaryKey: true,
      placeholder: 'tenant-acme',
    },
    {
      name: 'cron',
      label: 'Cron expression',
      type: 'text',
      required: true,
      placeholder: '0 30 2 * * *',
      help: 'Six fields: sec min hr dom mon dow.  Example: '
        + '`0 30 2 * * *` runs at 02:30:00 daily.',
    },
    {
      name: 'enabled',
      label: 'Enabled',
      type: 'select',
      required: true,
      options: [
        { value: 'true',  label: 'Yes — run on schedule' },
        { value: 'false', label: 'No — keep cron on file but skip' },
      ],
    },
  ],
  columns: [
    {
      key: 'tenantId',
      label: 'Tenant',
      render: (r) => ({ __html: `<code>${escapeHtml(r.tenantId)}</code>` }),
    },
    {
      key: 'cron',
      label: 'Cron',
      render: (r) => ({ __html: `<code>${escapeHtml(r.cron)}</code>` }),
    },
    {
      key: 'enabled',
      label: 'Enabled',
      render: (r) => r.enabled ? 'Yes' : 'No',
    },
    {
      key: 'nextRunAt',
      label: 'Next run',
      render: (r) => fmtIso(r.nextRunAt),
    },
    {
      key: 'lastRunOutcome',
      label: 'Last outcome',
      render: (r) => r.lastRunOutcome === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : escapeHtml(r.lastRunOutcome),
    },
    {
      key: 'lastChangedBy',
      label: 'Changed by',
      render: (r) => r.lastChangedBy === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : escapeHtml(r.lastChangedBy),
    },
  ],
};
