// Phase K Wave 20 — Hicks (Frontend).
//
// Operator UI for Bishop's W20 rotation-policy bulk-actions surfaces.
// Combines THREE related W19/W20 endpoints behind one panel so
// operators can sweep across tenant rotation policies in one place:
//
//   • W19 bulk-update — POST /api/admin/rotation-policy/bulk-update
//     (already surfaced via `./rotation-policy-bulk.ts` — left as-is
//     so the W19 panel still works in isolation; this W20 panel
//     adds the *non-update* bulk actions).
//   • W20 bulk-delete — POST /api/admin/rotation-policy/bulk-delete
//     (body: `{ tenantIds: string[] }`)
//     Audit kind: governance.rotation-policy.bulk-deleted.
//   • W20 bulk-enable — POST /api/admin/rotation-policy/bulk-enable
//     (body: `{ tenantIds: string[], enabled: boolean }`)
//     Audit kind: governance.rotation-policy.bulk-enabled.
//
// All three actions require X-Admin-Reason.  This surface is action-
// oriented (no list / no per-row CRUD), so the wire-shape is a thin
// "tenant ids + dry-run" form per action; the shared admin runtime
// renders the form scaffolding.

import {
  type AdminSurfaceSpec,
  ADMIN_REASON_HEADER,
  escapeHtml,
  gateAdminFetch,
  promptAdminReason,
} from './admin-shared';

export type BulkAction = 'delete' | 'enable' | 'disable';

interface RotationPolicyBulkActionRow {
  /** Synthetic id — the action kind (one row per supported action). */
  action: BulkAction;
  endpoint: string;
  lastFiredAt?: string;
  lastFiredBy?: string;
  lastAffectedTenantCount?: number;
}

interface RotationPolicyBulkActionBody {
  action: BulkAction;
  tenantIds: string[];
  dryRun: boolean;
}

const ACTIONS: BulkAction[] = ['delete', 'enable', 'disable'];

function parseRow(raw: unknown): RotationPolicyBulkActionRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const action = typeof o.action === 'string'
    && (ACTIONS as string[]).includes(o.action)
    ? o.action as BulkAction : null;
  if (action === null) return null;
  return {
    action,
    endpoint: typeof o.endpoint === 'string'
      ? o.endpoint
      : endpointFor(action),
    lastFiredAt: typeof o.lastFiredAt === 'string' ? o.lastFiredAt : undefined,
    lastFiredBy: typeof o.lastFiredBy === 'string' ? o.lastFiredBy : undefined,
    lastAffectedTenantCount: typeof o.lastAffectedTenantCount === 'number'
      && Number.isFinite(o.lastAffectedTenantCount)
      ? Math.max(0, Math.floor(o.lastAffectedTenantCount)) : undefined,
  };
}

function endpointFor(action: BulkAction): string {
  switch (action) {
    case 'delete':  return '/api/admin/rotation-policy/bulk-delete';
    case 'enable':
    case 'disable': return '/api/admin/rotation-policy/bulk-enable';
  }
}

function actionLabel(action: BulkAction): string {
  switch (action) {
    case 'delete':  return 'Bulk delete';
    case 'enable':  return 'Bulk enable';
    case 'disable': return 'Bulk disable';
  }
}

/**
 * Parse a comma- or whitespace-separated tenant id list into an
 * array of trimmed, non-empty ids.  Duplicates are de-duped in
 * declaration order.
 */
export function parseTenantIdList(raw: string): string[] {
  const out: string[] = [];
  const seen = new Set<string>();
  for (const part of raw.split(/[\s,;]+/)) {
    const t = part.trim();
    if (t === '' || seen.has(t)) continue;
    seen.add(t);
    out.push(t);
  }
  return out;
}

/**
 * Fire one of the three bulk actions.  Wraps `gateAdminFetch` so
 * the 401/403/503 auth ladder is consistent with the rest of the
 * admin panel surfaces.  Throws on cancel or on a non-2xx
 * response.
 */
export async function fireRotationPolicyBulkAction(
  action: BulkAction,
  tenantIds: string[],
  dryRun: boolean,
): Promise<unknown> {
  if (tenantIds.length === 0) {
    throw new Error('at-least-one-tenant-required');
  }
  const reason = promptAdminReason(
    `${actionLabel(action)} across ${tenantIds.length} tenant(s)`,
  );
  if (reason === null) throw new Error('cancelled');
  const endpoint = endpointFor(action);
  const body =
    action === 'delete'
      ? { tenantIds, dryRun }
      : { tenantIds, enabled: action === 'enable', dryRun };
  const res = await gateAdminFetch(endpoint, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      [ADMIN_REASON_HEADER]: reason,
    },
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    throw new Error(`rotation-policy ${action} failed: ${res.status ?? 'network'}`);
  }
  return res.body ?? null;
}

export const ROTATION_POLICY_BULK_ACTIONS_SPEC: AdminSurfaceSpec<RotationPolicyBulkActionRow, RotationPolicyBulkActionBody> = {
  id: 'rotation-policy-bulk-actions',
  title: 'Rotation policy · Bulk actions',
  description: 'W20 bulk-delete + bulk-enable surfaces for the per-'
    + 'tenant rotation policy.  Combines the W19 bulk-update editor '
    + '(`/api/admin/rotation-policy/bulk-update`) with W20\'s two '
    + 'new sweepers: bulk-delete + bulk-enable/disable.  Enter a '
    + 'comma- or whitespace-separated tenant id list and pick the '
    + 'action; dry-run mode previews without persisting.',
  endpoint: '/api/admin/rotation-policy/bulk-actions',
  parseRow,
  rowKey: (r) => r.action,
  rowToFormValues: (r) => ({
    action: r.action,
    tenantIds: '',
    dryRun: 'false',
  }),
  buildBody: (v) => {
    const action = ((ACTIONS as string[]).includes(v.action ?? '')
      ? v.action as BulkAction
      : 'enable');
    return {
      action,
      tenantIds: parseTenantIdList(v.tenantIds ?? ''),
      dryRun: (v.dryRun ?? 'false').toLowerCase() === 'true',
    };
  },
  fields: [
    {
      name: 'action',
      label: 'Action',
      type: 'select',
      required: true,
      primaryKey: true,
      options: ACTIONS.map((a) => ({ value: a, label: actionLabel(a) })),
      help: 'bulk-delete hard-deletes; bulk-enable / bulk-disable '
        + 'flip the per-tenant enabled bit.',
    },
    {
      name: 'tenantIds',
      label: 'Tenant IDs',
      type: 'text',
      required: true,
      placeholder: 'tenant-acme, tenant-beta, tenant-gamma',
      help: 'Comma- or whitespace-separated list.  Duplicates de-duped.',
    },
    {
      name: 'dryRun',
      label: 'Dry run',
      type: 'select',
      required: true,
      options: [
        { value: 'false', label: 'No — apply the action' },
        { value: 'true',  label: 'Yes — preview without persisting' },
      ],
      help: 'Dry-run returns the affected-rows manifest without '
        + 'modifying state.  Use to review before firing.',
    },
  ],
  columns: [
    {
      key: 'action',
      label: 'Action',
      render: (r) => actionLabel(r.action),
    },
    {
      key: 'endpoint',
      label: 'Endpoint',
      render: (r) => ({ __html: `<code>${escapeHtml(r.endpoint)}</code>` }),
    },
    {
      key: 'lastFiredAt',
      label: 'Last fired',
      render: (r) => r.lastFiredAt === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : escapeHtml(r.lastFiredAt),
    },
    {
      key: 'lastAffectedTenantCount',
      label: 'Last affected',
      render: (r) => r.lastAffectedTenantCount === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : `${r.lastAffectedTenantCount}`,
    },
  ],
};
