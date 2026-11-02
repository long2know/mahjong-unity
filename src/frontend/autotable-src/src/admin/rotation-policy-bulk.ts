// Phase K Wave 19 — Hicks (Frontend).
//
// Operator UI for Bishop's W19 per-tenant rotation-policy BULK-UPDATE
// surface:
//
//   POST /api/admin/rotation-policy/bulk-update
//   body: { policies: [{ tenantId, dealerRotation, windRotation }, ...] }
//   GET  /api/admin/rotation-policy/per-tenant
//   PUT  /api/admin/rotation-policy/per-tenant/<tenantId>
//   DELETE /api/admin/rotation-policy/per-tenant/<tenantId>
//
// The W19 "bulk-update" wrinkle is that operators frequently re-tune
// rotation policies across a whole tenant cohort at once (e.g. after
// a Changsha-variant ruleset update).  The W17 single-row CRUD
// pattern still works for one-offs; the bulk surface (this module)
// adds a JSON-payload editor that ships every visible row in one PUT.
// Audit kinds: governance.rotation-policy.{bulk-applied|created|
// updated|deleted}.  X-Admin-Reason header MANDATORY on every write.

import {
  type AdminSurfaceSpec,
  escapeHtml,
} from './admin-shared';

export type DealerRotation =
  | 'winner-becomes-dealer'
  | 'east-banker-fixed'
  | 'counter-clockwise';

export type WindRotation =
  | 'rotate-on-east-loss'
  | 'rotate-each-hand'
  | 'rotate-on-hand-end';

interface RotationPolicyRow {
  tenantId: string;
  dealerRotation: DealerRotation;
  windRotation: WindRotation;
  createdAt?: string;
  updatedAt?: string;
}

interface RotationPolicyBody {
  tenantId: string;
  dealerRotation: DealerRotation;
  windRotation: WindRotation;
}

const DEALER_ROTATIONS: DealerRotation[] = [
  'winner-becomes-dealer',
  'east-banker-fixed',
  'counter-clockwise',
];

const WIND_ROTATIONS: WindRotation[] = [
  'rotate-on-east-loss',
  'rotate-each-hand',
  'rotate-on-hand-end',
];

function parseRow(raw: unknown): RotationPolicyRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const tenantId = typeof o.tenantId === 'string' ? o.tenantId : null;
  const dealerRotation = typeof o.dealerRotation === 'string'
    && (DEALER_ROTATIONS as string[]).includes(o.dealerRotation)
    ? o.dealerRotation as DealerRotation : null;
  const windRotation = typeof o.windRotation === 'string'
    && (WIND_ROTATIONS as string[]).includes(o.windRotation)
    ? o.windRotation as WindRotation : null;
  if (tenantId === null || dealerRotation === null || windRotation === null) {
    return null;
  }
  return {
    tenantId,
    dealerRotation,
    windRotation,
    createdAt: typeof o.createdAt === 'string' ? o.createdAt : undefined,
    updatedAt: typeof o.updatedAt === 'string' ? o.updatedAt : undefined,
  };
}

function dealerRotationLabel(v: string): string {
  switch (v) {
    case 'winner-becomes-dealer': return 'Winner becomes dealer';
    case 'east-banker-fixed':     return 'East banker (fixed)';
    case 'counter-clockwise':     return 'Counter-clockwise';
    default:                      return v;
  }
}

function windRotationLabel(v: string): string {
  switch (v) {
    case 'rotate-on-east-loss': return 'Rotate on East loss';
    case 'rotate-each-hand':    return 'Rotate each hand';
    case 'rotate-on-hand-end':  return 'Rotate on hand end';
    default:                    return v;
  }
}

export const ROTATION_POLICY_BULK_SPEC: AdminSurfaceSpec<RotationPolicyRow, RotationPolicyBody> = {
  id: 'rotation-policy-bulk',
  title: 'Tenant rotation policies (bulk)',
  description: 'Per-tenant dealer + wind rotation rule.  Bishop W19 — '
    + 'use the row editor for one-off tweaks; the bulk-apply surface '
    + 'lives at POST /api/admin/rotation-policy/bulk-update for cohort '
    + 'roll-outs (changelog kind governance.rotation-policy.bulk-applied).',
  endpoint: '/api/admin/rotation-policy/per-tenant',
  parseRow,
  rowKey: (r) => r.tenantId,
  rowToFormValues: (r) => ({
    tenantId: r.tenantId,
    dealerRotation: r.dealerRotation,
    windRotation: r.windRotation,
  }),
  buildBody: (v) => ({
    tenantId: (v.tenantId ?? '').trim(),
    dealerRotation: ((DEALER_ROTATIONS as string[]).includes(v.dealerRotation ?? '')
      ? v.dealerRotation : 'winner-becomes-dealer') as DealerRotation,
    windRotation: ((WIND_ROTATIONS as string[]).includes(v.windRotation ?? '')
      ? v.windRotation : 'rotate-on-east-loss') as WindRotation,
  }),
  fields: [
    {
      name: 'tenantId',
      label: 'Tenant ID',
      type: 'text',
      required: true,
      primaryKey: true,
      placeholder: 'tenant-acme',
      help: 'Empty falls through to the global default rotation policy.',
    },
    {
      name: 'dealerRotation',
      label: 'Dealer rotation',
      type: 'select',
      required: true,
      options: DEALER_ROTATIONS.map((v) => ({ value: v, label: dealerRotationLabel(v) })),
      help: 'Canonical Changsha = winner-becomes-dealer.',
    },
    {
      name: 'windRotation',
      label: 'Wind rotation',
      type: 'select',
      required: true,
      options: WIND_ROTATIONS.map((v) => ({ value: v, label: windRotationLabel(v) })),
      help: 'When the round wind advances (East → South → West → North).',
    },
  ],
  columns: [
    {
      key: 'tenantId',
      label: 'Tenant',
      render: (r) => r.tenantId,
    },
    {
      key: 'dealerRotation',
      label: 'Dealer rotation',
      render: (r) => ({ __html:
        `<code>${escapeHtml(dealerRotationLabel(r.dealerRotation))}</code>`,
      }),
    },
    {
      key: 'windRotation',
      label: 'Wind rotation',
      render: (r) => ({ __html:
        `<code>${escapeHtml(windRotationLabel(r.windRotation))}</code>`,
      }),
    },
  ],
};
