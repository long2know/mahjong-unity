// Phase K Wave 22 — Hicks (Frontend).
//
// Operator UI for Bishop's W22 JWT-keys emergency-revoke endpoint:
//
//   POST /api/admin/jwt-keys/emergency-revoke
//   body: { keyId, tenantId?, reason }
//
// This surface is the *emergency* revocation path for JWT signing
// keys — distinct from the W20 routine `JWT_ROTATION_DRILL_SPEC`
// which exercises the rotation procedure as a non-disruptive
// drill.  Emergency revoke is a one-way trapdoor:
//
//   • The named key is removed from the active signing set
//     IMMEDIATELY.
//   • All JWTs signed by the revoked key fail validation on the
//     next request (the JWKS cache is invalidated server-side).
//   • Cannot be undone — operators must issue a fresh key via the
//     normal rotation flow if the revoke was in error.
//
// Wire contract:
//   • Auth ladder: 401/403/503 → 200 OK with the revoke manifest.
//   • `X-Admin-Reason` header MANDATORY
//     (governance.jwt-keys.emergency-revoke.fired).
//   • Multi-confirm UI guard — operator must type the keyId
//     verbatim into a second confirm field before the form
//     submits.  The shared admin runtime can't enforce this
//     declaratively from a spec; we layer the guard on top.

import {
  type AdminSurfaceSpec,
  escapeHtml,
  fmtIso,
} from './admin-shared';

interface JwtEmergencyRevokeRow {
  keyId: string;
  tenantId: string;
  state: 'active' | 'revoked' | 'rotated-out';
  algorithm: string;
  notBefore?: string;
  revokedAt?: string;
  revokedBy?: string;
  revocationReason?: string;
}

interface JwtEmergencyRevokeBody {
  keyId: string;
  keyIdConfirm: string;
  tenantId: string;
  reason: string;
}

function parseRow(raw: unknown): JwtEmergencyRevokeRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const keyId = typeof o.keyId === 'string' ? o.keyId : null;
  if (keyId === null) return null;
  const stateRaw = o.state;
  const state: JwtEmergencyRevokeRow['state'] =
    stateRaw === 'revoked' || stateRaw === 'rotated-out' ? stateRaw : 'active';
  return {
    keyId,
    tenantId: typeof o.tenantId === 'string' ? o.tenantId : '',
    state,
    algorithm: typeof o.algorithm === 'string' ? o.algorithm : 'unknown',
    notBefore: typeof o.notBefore === 'string' ? o.notBefore : undefined,
    revokedAt: typeof o.revokedAt === 'string' ? o.revokedAt : undefined,
    revokedBy: typeof o.revokedBy === 'string' ? o.revokedBy : undefined,
    revocationReason: typeof o.revocationReason === 'string'
      ? o.revocationReason : undefined,
  };
}

function stateLabel(s: JwtEmergencyRevokeRow['state']): string {
  switch (s) {
    case 'active':       return 'Active';
    case 'revoked':      return 'Revoked';
    case 'rotated-out':  return 'Rotated out';
  }
}

function stateClass(s: JwtEmergencyRevokeRow['state']): string {
  switch (s) {
    case 'active':       return 'admin-panel-outcome-ok';
    case 'rotated-out':  return 'admin-panel-outcome-warn';
    case 'revoked':      return 'admin-panel-outcome-err';
  }
}

export const JWT_EMERGENCY_REVOKE_SPEC: AdminSurfaceSpec<JwtEmergencyRevokeRow, JwtEmergencyRevokeBody> = {
  id: 'jwt-emergency-revoke',
  title: 'JWT keys · Emergency revoke',
  description: 'One-way trapdoor: revoke a JWT signing key '
    + 'IMMEDIATELY.  All JWTs signed by the revoked key fail '
    + 'validation on the next request.  Use the W20 JWT rotation '
    + 'drill for routine key rotation; this surface is for '
    + 'compromise scenarios only.  Type the keyId verbatim into '
    + 'the confirm field before submit.  Audit kind: '
    + 'governance.jwt-keys.emergency-revoke.fired.',
  endpoint: '/api/admin/jwt-keys/emergency-revoke',
  parseRow,
  rowKey: (r) => r.keyId,
  rowToFormValues: (r) => ({
    keyId: r.keyId,
    keyIdConfirm: '',
    tenantId: r.tenantId,
    reason: '',
  }),
  buildBody: (v) => {
    const keyId = (v.keyId ?? '').trim();
    const confirm = (v.keyIdConfirm ?? '').trim();
    if (keyId === '' || confirm !== keyId) {
      throw new Error('keyId confirm mismatch — abort revoke');
    }
    const reason = (v.reason ?? '').trim();
    if (reason === '') {
      throw new Error('revocation reason is required');
    }
    return {
      keyId,
      keyIdConfirm: confirm,
      tenantId: (v.tenantId ?? '').trim(),
      reason,
    };
  },
  fields: [
    {
      name: 'keyId',
      label: 'Key ID',
      type: 'text',
      required: true,
      primaryKey: true,
      placeholder: 'jwt-key-2026-summer-01',
      help: 'The key ID currently being revoked.',
    },
    {
      name: 'keyIdConfirm',
      label: 'Confirm Key ID',
      type: 'text',
      required: true,
      placeholder: 'retype keyId verbatim',
      help: 'Must match Key ID exactly.  Guards against typo-driven '
        + 'misfires; the revoke is one-way.',
    },
    {
      name: 'tenantId',
      label: 'Tenant ID (optional)',
      type: 'text',
      required: false,
      placeholder: '(global if blank)',
      help: 'Empty → global revoke across all tenants.  Specifying '
        + 'a tenant scopes the revoke to that tenant only.',
    },
    {
      name: 'reason',
      label: 'Revocation reason',
      type: 'text',
      required: true,
      placeholder: 'e.g. compromise, vendor disclosure, drill',
      help: 'Stamped onto the audit log.  Required.',
    },
  ],
  columns: [
    {
      key: 'keyId',
      label: 'Key ID',
      render: (r) => ({ __html: `<code>${escapeHtml(r.keyId)}</code>` }),
    },
    {
      key: 'tenantId',
      label: 'Tenant',
      render: (r) => r.tenantId === ''
        ? ({ __html: '<em class="admin-panel-muted">(global)</em>' })
        : ({ __html: `<code>${escapeHtml(r.tenantId)}</code>` }),
    },
    {
      key: 'state',
      label: 'State',
      render: (r) => ({
        __html: `<span class="${stateClass(r.state)}">${escapeHtml(stateLabel(r.state))}</span>`,
      }),
    },
    {
      key: 'algorithm',
      label: 'Alg',
      render: (r) => r.algorithm,
    },
    {
      key: 'notBefore',
      label: 'Not before',
      render: (r) => fmtIso(r.notBefore),
    },
    {
      key: 'revokedAt',
      label: 'Revoked',
      render: (r) => fmtIso(r.revokedAt),
    },
    {
      key: 'revokedBy',
      label: 'By',
      render: (r) => r.revokedBy === undefined
        ? ({ __html: '<span class="admin-panel-muted">—</span>' })
        : escapeHtml(r.revokedBy),
    },
  ],
};
