// Phase K Wave 21 — Hicks (Frontend).
//
// Operator UI for Bishop's W21 SignalR retention-purge endpoint:
//
//   POST /api/admin/signalr/retention-purge
//   body: { tenantId?: string, olderThanIso: string, dryRun: boolean }
//
// The W17 surface (`./signalr-retention.ts`) was the CRUD config
// for the SignalR connection-retention policy.  W21 layers on the
// *purge* surface — an on-demand sweep that drops connections /
// messages older than `olderThanIso` ahead of the next scheduled
// purge tick.  Wire contract:
//
//   • Auth ladder: 401/403/503 → 200 OK with the purge manifest.
//   • `X-Admin-Reason` header MANDATORY (governance.signalr.
//     retention-purge.fired).
//   • `dryRun: true` → server returns the would-be-purged count
//     WITHOUT actually purging.
//   • `tenantId` empty → global purge across every tenant; non-
//     empty → per-tenant purge.
//   • `olderThanIso` must be ISO-8601 UTC; server rejects locally-
//     biased timestamps.

import {
  type AdminSurfaceSpec,
  ADMIN_REASON_HEADER,
  escapeHtml,
  fmtIso,
  gateAdminFetch,
  promptAdminReason,
} from './admin-shared';

interface SignalrPurgeRow {
  tenantId: string;
  lastPurgedAt?: string;
  lastPurgedBy?: string;
  lastPurgedCount?: number;
  lastPurgeDryRun?: boolean;
  lastOlderThanIso?: string;
}

interface SignalrPurgeBody {
  tenantId: string;
  olderThanIso: string;
  dryRun: boolean;
}

function parseRow(raw: unknown): SignalrPurgeRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const tenantId = typeof o.tenantId === 'string' ? o.tenantId : '';
  return {
    tenantId,
    lastPurgedAt: typeof o.lastPurgedAt === 'string' ? o.lastPurgedAt : undefined,
    lastPurgedBy: typeof o.lastPurgedBy === 'string' ? o.lastPurgedBy : undefined,
    lastPurgedCount: typeof o.lastPurgedCount === 'number'
      && Number.isFinite(o.lastPurgedCount)
      ? Math.max(0, Math.floor(o.lastPurgedCount)) : undefined,
    lastPurgeDryRun: typeof o.lastPurgeDryRun === 'boolean'
      ? o.lastPurgeDryRun : undefined,
    lastOlderThanIso: typeof o.lastOlderThanIso === 'string'
      ? o.lastOlderThanIso : undefined,
  };
}

/**
 * Fire a SignalR retention-purge sweep.  Wraps `gateAdminFetch`
 * so the 401/403/503 auth ladder is consistent with the rest of
 * the admin panel surfaces.  Throws on cancel / non-2xx.
 */
export async function fireSignalrPurge(
  tenantId: string,
  olderThanIso: string,
  dryRun: boolean,
): Promise<unknown> {
  const reason = promptAdminReason(
    tenantId === ''
      ? `global SignalR retention-purge (older than ${olderThanIso})`
      : `SignalR retention-purge for ${tenantId} (older than ${olderThanIso})`,
  );
  if (reason === null) throw new Error('cancelled');
  const body: SignalrPurgeBody = { tenantId, olderThanIso, dryRun };
  const res = await gateAdminFetch(
    '/api/admin/signalr/retention-purge',
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
    throw new Error(`signalr retention-purge failed: ${res.status ?? 'network'}`);
  }
  return res.body ?? null;
}

export const SIGNALR_PURGE_SPEC: AdminSurfaceSpec<SignalrPurgeRow, SignalrPurgeBody> = {
  id: 'signalr-purge',
  title: 'SignalR · Retention purge',
  description: 'On-demand sweep that drops SignalR connections / '
    + 'messages older than `olderThan` ahead of the next scheduled '
    + 'purge tick.  Use dry-run to preview the count without actually '
    + 'purging.  Leave Tenant blank for a global sweep.  Audit kind: '
    + 'governance.signalr.retention-purge.fired.',
  endpoint: '/api/admin/signalr/retention-purge',
  parseRow,
  rowKey: (r) => r.tenantId === '' ? '(global)' : r.tenantId,
  rowToFormValues: (r) => ({
    tenantId: r.tenantId,
    olderThanIso: r.lastOlderThanIso ?? '',
    dryRun: 'true',
  }),
  buildBody: (v) => ({
    tenantId: (v.tenantId ?? '').trim(),
    olderThanIso: (v.olderThanIso ?? '').trim(),
    dryRun: (v.dryRun ?? 'true').toLowerCase() === 'true',
  }),
  fields: [
    {
      name: 'tenantId',
      label: 'Tenant ID',
      type: 'text',
      required: false,
      primaryKey: true,
      placeholder: 'tenant-acme (leave blank for global)',
      help: 'Empty → purge across every tenant.',
    },
    {
      name: 'olderThanIso',
      label: 'Older than (ISO-8601 UTC)',
      type: 'datetime-local',
      required: true,
      placeholder: '2026-04-01T00:00:00Z',
      help: 'Drop records older than this timestamp.  Server rejects '
        + 'non-UTC timestamps.',
    },
    {
      name: 'dryRun',
      label: 'Dry run',
      type: 'select',
      required: true,
      options: [
        { value: 'true',  label: 'Yes — preview count, do not purge' },
        { value: 'false', label: 'No — perform the purge' },
      ],
      help: 'Default Yes — preview first, confirm via re-submit '
        + 'with No.',
    },
  ],
  columns: [
    {
      key: 'tenantId',
      label: 'Tenant',
      render: (r) => r.tenantId === ''
        ? ({ __html: '<em class="admin-panel-muted">(global)</em>' })
        : ({ __html: `<code>${escapeHtml(r.tenantId)}</code>` }),
    },
    {
      key: 'lastPurgedAt',
      label: 'Last purge',
      render: (r) => fmtIso(r.lastPurgedAt),
    },
    {
      key: 'lastOlderThanIso',
      label: 'Last cutoff',
      render: (r) => fmtIso(r.lastOlderThanIso),
    },
    {
      key: 'lastPurgedCount',
      label: 'Last count',
      render: (r) => r.lastPurgedCount === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : String(r.lastPurgedCount),
    },
    {
      key: 'lastPurgeDryRun',
      label: 'Dry run',
      render: (r) => r.lastPurgeDryRun === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : (r.lastPurgeDryRun ? 'Yes' : 'No'),
    },
    {
      key: 'lastPurgedBy',
      label: 'By',
      render: (r) => r.lastPurgedBy === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : escapeHtml(r.lastPurgedBy),
    },
  ],
};
