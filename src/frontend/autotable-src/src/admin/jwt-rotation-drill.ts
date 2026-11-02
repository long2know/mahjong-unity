// Phase K Wave 20 — Hicks (Frontend).
//
// Operator UI for Bishop's W20 JWT-keys rotation-drill endpoint:
//
//   POST /api/admin/jwt-keys/rotation-drill
//   body: { tenantId?: string, dryRun: boolean, simulateFailureAt?: string }
//
// The drill validates the per-tenant rotation pipeline END-TO-END
// without performing a real KID rotation: it stages a synthetic
// rotation row, walks every step (stage / overlap-window / commit /
// rollback), and confirms each step would have succeeded against
// the live JWKS + the staged policy.  Audit kind:
// `auth.jwt-keys.rotation-drill.ran`.
//
// The drill is the W20 follow-up to Bishop's W16 / W17 rotation
// pipeline.  Operators run the drill before a planned rotation
// window to catch staging / overlap mis-configurations before they
// hit production.  Output is a structured report carrying the
// per-step outcome + any anomalies the drill detected.
//
// Wire contract:
//   • Auth ladder: 401/403/503 → 200 OK with the drill report.
//   • `X-Admin-Reason` header MANDATORY.
//   • `simulateFailureAt`: one of the rotation step names
//     ('stage' | 'overlap' | 'commit' | 'rollback') to force-fail
//     that step so the operator can verify the failure path
//     surfaces cleanly.
//   • `dryRun: true` ALWAYS — the drill never persists a real KID
//     rotation, only the audit log row.

import {
  type AdminSurfaceSpec,
  ADMIN_REASON_HEADER,
  escapeHtml,
  fmtIso,
  gateAdminFetch,
  promptAdminReason,
} from './admin-shared';

export type JwtRotationStep = 'stage' | 'overlap' | 'commit' | 'rollback';

export type JwtRotationStepOutcome =
  | 'ok'
  | 'skipped'
  | 'failed-stage'
  | 'failed-overlap'
  | 'failed-commit'
  | 'failed-rollback';

interface JwtRotationDrillRow {
  tenantId: string;
  lastDrillAt?: string;
  lastDrillBy?: string;
  lastDrillOutcome?: JwtRotationStepOutcome;
  lastDrillStep?: JwtRotationStep;
}

interface JwtRotationDrillBody {
  tenantId: string;
  dryRun: boolean;
  simulateFailureAt: JwtRotationStep | '';
}

const STEPS: JwtRotationStep[] = ['stage', 'overlap', 'commit', 'rollback'];

const OUTCOMES: JwtRotationStepOutcome[] = [
  'ok',
  'skipped',
  'failed-stage',
  'failed-overlap',
  'failed-commit',
  'failed-rollback',
];

function parseRow(raw: unknown): JwtRotationDrillRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const tenantId = typeof o.tenantId === 'string' ? o.tenantId : null;
  if (tenantId === null) return null;
  const lastDrillOutcome = typeof o.lastDrillOutcome === 'string'
    && (OUTCOMES as string[]).includes(o.lastDrillOutcome)
    ? o.lastDrillOutcome as JwtRotationStepOutcome
    : undefined;
  const lastDrillStep = typeof o.lastDrillStep === 'string'
    && (STEPS as string[]).includes(o.lastDrillStep)
    ? o.lastDrillStep as JwtRotationStep
    : undefined;
  return {
    tenantId,
    lastDrillAt: typeof o.lastDrillAt === 'string' ? o.lastDrillAt : undefined,
    lastDrillBy: typeof o.lastDrillBy === 'string' ? o.lastDrillBy : undefined,
    lastDrillOutcome,
    lastDrillStep,
  };
}

function outcomeLabel(o: JwtRotationStepOutcome): string {
  switch (o) {
    case 'ok':                return 'OK';
    case 'skipped':           return 'Skipped';
    case 'failed-stage':      return 'Failed @ stage';
    case 'failed-overlap':    return 'Failed @ overlap';
    case 'failed-commit':     return 'Failed @ commit';
    case 'failed-rollback':   return 'Failed @ rollback';
  }
}

/**
 * Run a rotation drill against the supplied tenant (or globally if
 * tenantId is empty).  Wraps `gateAdminFetch` so the 401/403/503
 * auth ladder is consistent with the rest of the admin panel
 * surfaces.  Throws on cancel / non-2xx.
 */
export async function fireJwtRotationDrill(
  tenantId: string,
  simulateFailureAt: JwtRotationStep | '',
): Promise<unknown> {
  const reason = promptAdminReason(
    tenantId === ''
      ? 'global rotation drill'
      : `rotation drill for ${tenantId}`,
  );
  if (reason === null) throw new Error('cancelled');
  const body: JwtRotationDrillBody = {
    tenantId,
    dryRun: true,
    simulateFailureAt,
  };
  const res = await gateAdminFetch(
    '/api/admin/jwt-keys/rotation-drill',
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
    throw new Error(`rotation-drill failed: ${res.status ?? 'network'}`);
  }
  return res.body ?? null;
}

export const JWT_ROTATION_DRILL_SPEC: AdminSurfaceSpec<JwtRotationDrillRow, JwtRotationDrillBody> = {
  id: 'jwt-rotation-drill',
  title: 'JWT keys · Rotation drill',
  description: 'Validates the per-tenant JWKS rotation pipeline '
    + 'end-to-end WITHOUT performing a real KID rotation.  Stages a '
    + 'synthetic rotation row, walks every pipeline step (stage / '
    + 'overlap-window / commit / rollback) and confirms each would '
    + 'have succeeded.  Use `simulateFailureAt` to force-fail one '
    + 'step and verify the failure path surfaces cleanly.  Audit '
    + 'kind: auth.jwt-keys.rotation-drill.ran.',
  endpoint: '/api/admin/jwt-keys/rotation-drill',
  parseRow,
  rowKey: (r) => r.tenantId,
  rowToFormValues: (r) => ({
    tenantId: r.tenantId,
    simulateFailureAt: '',
  }),
  buildBody: (v) => ({
    tenantId: (v.tenantId ?? '').trim(),
    dryRun: true,
    simulateFailureAt: ((STEPS as string[]).includes(v.simulateFailureAt ?? '')
      ? v.simulateFailureAt as JwtRotationStep
      : ''),
  }),
  fields: [
    {
      name: 'tenantId',
      label: 'Tenant ID',
      type: 'text',
      required: false,
      primaryKey: true,
      placeholder: 'tenant-acme (leave blank for global drill)',
      help: 'Empty → drill against the global JWKS rotation policy.',
    },
    {
      name: 'simulateFailureAt',
      label: 'Simulate failure at',
      type: 'select',
      required: true,
      options: [
        { value: '',         label: '(none — all steps should succeed)' },
        { value: 'stage',    label: 'stage — block staging the synthetic row' },
        { value: 'overlap',  label: 'overlap — block during the overlap window' },
        { value: 'commit',   label: 'commit — block at the commit step' },
        { value: 'rollback', label: 'rollback — block during teardown' },
      ],
      help: 'Use to verify the failure-path surfaces cleanly without '
        + 'staging a real rotation.',
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
      key: 'lastDrillAt',
      label: 'Last drill',
      render: (r) => fmtIso(r.lastDrillAt),
    },
    {
      key: 'lastDrillStep',
      label: 'Last step',
      render: (r) => r.lastDrillStep === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : escapeHtml(r.lastDrillStep),
    },
    {
      key: 'lastDrillOutcome',
      label: 'Outcome',
      render: (r) => r.lastDrillOutcome === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : outcomeLabel(r.lastDrillOutcome),
    },
    {
      key: 'lastDrillBy',
      label: 'By',
      render: (r) => r.lastDrillBy === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : escapeHtml(r.lastDrillBy),
    },
  ],
};
