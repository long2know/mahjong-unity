// Phase K Wave 18 — Hicks (Frontend).
//
// Operator UI for Bishop's W16/W17 per-tenant JWKS rotation
// policy CRUD:
//   POST/GET/PUT/DELETE /api/admin/jwks-rotation/per-tenant[/<tenantId>]
//
// Body shape:
//   { tenantId, activeKid, previousKid?, rotationStartUtc,
//     rotationCompleteUtc, overlapWindowDays (≥0) }
//
// Audit kinds: auth.jwks.per-tenant.{rotation-staged|deleted}.
// Bishop W17 added DeleteAsync — the controller now hard-deletes
// instead of stamping the W16 sentinel marker.  This UI does
// NOT require X-Admin-Reason on writes (the rotation surface
// post-dates the W17 reason-header unification — the operator's
// intent is recorded in the staged rotation row itself).

import {
  type AdminSurfaceSpec,
  escapeHtml,
  fmtIso,
} from './admin-shared';

interface JwksRotationRow {
  tenantId: string;
  activeKid: string;
  previousKid: string;
  rotationStartUtc: string;
  rotationCompleteUtc: string;
  overlapWindowDays: number;
  createdAt?: string;
  updatedAt?: string;
}

interface JwksRotationBody {
  tenantId: string;
  activeKid: string;
  previousKid: string;
  rotationStartUtc: string;
  rotationCompleteUtc: string;
  overlapWindowDays: number;
}

function parseRow(raw: unknown): JwksRotationRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const tenantId = typeof o.tenantId === 'string' ? o.tenantId : null;
  const activeKid = typeof o.activeKid === 'string' ? o.activeKid : null;
  if (tenantId === null || activeKid === null) return null;
  return {
    tenantId,
    activeKid,
    previousKid: typeof o.previousKid === 'string' ? o.previousKid : '',
    rotationStartUtc: typeof o.rotationStartUtc === 'string' ? o.rotationStartUtc : '',
    rotationCompleteUtc: typeof o.rotationCompleteUtc === 'string' ? o.rotationCompleteUtc : '',
    overlapWindowDays: typeof o.overlapWindowDays === 'number'
      && Number.isFinite(o.overlapWindowDays)
      ? Math.max(0, Math.floor(o.overlapWindowDays)) : 0,
    createdAt: typeof o.createdAt === 'string' ? o.createdAt : undefined,
    updatedAt: typeof o.updatedAt === 'string' ? o.updatedAt : undefined,
  };
}

function toLocalInput(iso: string | undefined): string {
  if (iso === undefined || iso === '') return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const pad = (n: number): string => n.toString().padStart(2, '0');
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`
    + `T${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}`;
}

function fromLocalInput(v: string): string {
  if (v === '') return new Date().toISOString();
  // datetime-local strings are wallclock; append `Z` so the server
  // sees UTC unambiguously.  Operators are warned in the help text.
  const candidate = v.endsWith('Z') ? v : `${v}:00Z`;
  const d = new Date(candidate);
  return Number.isNaN(d.getTime()) ? new Date().toISOString() : d.toISOString();
}

export const JWKS_ROTATION_SPEC: AdminSurfaceSpec<JwksRotationRow, JwksRotationBody> = {
  id: 'jwks-rotation',
  title: 'Per-tenant JWKS rotation policies',
  description: 'Stages a per-tenant active/previous KID overlap window.  '
    + 'Bishop W16/W17 — the validator gates JWT issue against this row '
    + 'and blocks (`stale_per_tenant_policy`) when the rotation has '
    + 'gone stale.',
  endpoint: '/api/admin/jwks-rotation/per-tenant',
  parseRow,
  rowKey: (r) => r.tenantId,
  rowToFormValues: (r) => ({
    tenantId: r.tenantId,
    activeKid: r.activeKid,
    previousKid: r.previousKid,
    rotationStartUtc: toLocalInput(r.rotationStartUtc),
    rotationCompleteUtc: toLocalInput(r.rotationCompleteUtc),
    overlapWindowDays: String(r.overlapWindowDays),
  }),
  buildBody: (v) => ({
    tenantId: (v.tenantId ?? '').trim(),
    activeKid: (v.activeKid ?? '').trim(),
    previousKid: (v.previousKid ?? '').trim(),
    rotationStartUtc: fromLocalInput(v.rotationStartUtc ?? ''),
    rotationCompleteUtc: fromLocalInput(v.rotationCompleteUtc ?? ''),
    overlapWindowDays: Math.max(0, Math.floor(Number(v.overlapWindowDays ?? '0'))),
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
      name: 'activeKid',
      label: 'Active KID',
      type: 'text',
      required: true,
      placeholder: 'kid-2026-05',
      help: 'Current signing key ID.',
    },
    {
      name: 'previousKid',
      label: 'Previous KID',
      type: 'text',
      placeholder: 'kid-2026-04',
      help: 'Optional — the KID that was active before the rotation began.',
    },
    {
      name: 'rotationStartUtc',
      label: 'Rotation start (UTC)',
      type: 'datetime-local',
      required: true,
      help: 'Inputs are treated as UTC — local time-zone NOT applied.',
    },
    {
      name: 'rotationCompleteUtc',
      label: 'Rotation complete (UTC)',
      type: 'datetime-local',
      required: true,
      help: 'Must strictly follow rotation start.',
    },
    {
      name: 'overlapWindowDays',
      label: 'Overlap window (days)',
      type: 'number',
      required: true,
      min: 0,
      max: 90,
      integer: true,
      placeholder: '7',
      help: 'Grace period during which previous KID is still accepted.',
    },
  ],
  columns: [
    {
      key: 'tenantId',
      label: 'Tenant',
      render: (r) => r.tenantId,
    },
    {
      key: 'activeKid',
      label: 'Active KID',
      render: (r) => ({ __html: `<code>${escapeHtml(r.activeKid)}</code>` }),
    },
    {
      key: 'previousKid',
      label: 'Previous KID',
      render: (r) => r.previousKid === ''
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : ({ __html: `<code>${escapeHtml(r.previousKid)}</code>` }),
    },
    {
      key: 'rotationCompleteUtc',
      label: 'Completes',
      render: (r) => fmtIso(r.rotationCompleteUtc),
    },
    {
      key: 'overlapWindowDays',
      label: 'Overlap',
      render: (r) => `${r.overlapWindowDays}d`,
    },
  ],
};
