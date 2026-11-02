// Phase K Wave 22 — Hicks (Frontend).
//
// Operator UI for Bishop's W22 SignalR diagnostics endpoint:
//
//   GET /api/admin/signalr/diagnostics
//
// Read-only operational telemetry surface — exposes the live
// SignalR hub diagnostics that Bishop's W22 controller surfaces:
// connection counts (by transport), per-group fan-out latency
// histograms, hub-method invocation counts, and the current
// circuit-breaker state for the WS retention pipeline.
//
// Companion to the W17 SignalR retention surface (CRUD policies)
// and the W21 SignalR purge surface (operational trigger);
// diagnostics is the *passive* observation surface that drives
// the decision to invoke either of the writes.
//
// Wire contract:
//   • Auth ladder: 401/403/503 → 200 OK with the diagnostics rows.
//   • Query params: `tenantId` (optional — empty = global view),
//     `windowMinutes` (optional — observation window, default 15).
//   • No X-Admin-Reason required (read-only).

import {
  type AdminSurfaceSpec,
  escapeHtml,
  fmtIso,
} from './admin-shared';

interface SignalRDiagnosticsRow {
  metricKey: string;
  tenantId: string;
  transport: 'websocket' | 'longpolling' | 'serversentevents' | 'mixed';
  connections: number;
  invocations: number;
  /** Per-method p99 fan-out latency (ms). */
  fanoutP99Ms: number;
  /** Current circuit-breaker state for the WS retention pipeline. */
  circuitState: 'closed' | 'half-open' | 'open';
  sampledAt: string;
}

function parseRow(raw: unknown): SignalRDiagnosticsRow | null {
  if (raw === null || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const metricKey = typeof o.metricKey === 'string' ? o.metricKey : null;
  if (metricKey === null) return null;
  const trans = o.transport;
  const transport: SignalRDiagnosticsRow['transport'] =
    trans === 'websocket' || trans === 'longpolling'
      || trans === 'serversentevents' || trans === 'mixed'
      ? trans
      : 'mixed';
  const circ = o.circuitState;
  const circuitState: SignalRDiagnosticsRow['circuitState'] =
    circ === 'open' || circ === 'half-open' ? circ : 'closed';
  return {
    metricKey,
    tenantId: typeof o.tenantId === 'string' ? o.tenantId : '',
    transport,
    connections: typeof o.connections === 'number'
      && Number.isFinite(o.connections)
      ? Math.max(0, Math.floor(o.connections)) : 0,
    invocations: typeof o.invocations === 'number'
      && Number.isFinite(o.invocations)
      ? Math.max(0, Math.floor(o.invocations)) : 0,
    fanoutP99Ms: typeof o.fanoutP99Ms === 'number'
      && Number.isFinite(o.fanoutP99Ms)
      ? Math.max(0, o.fanoutP99Ms) : 0,
    circuitState,
    sampledAt: typeof o.sampledAt === 'string' ? o.sampledAt : '',
  };
}

function circuitClass(c: SignalRDiagnosticsRow['circuitState']): string {
  switch (c) {
    case 'closed':    return 'admin-panel-outcome-ok';
    case 'half-open': return 'admin-panel-outcome-warn';
    case 'open':      return 'admin-panel-outcome-err';
  }
}

function circuitLabel(c: SignalRDiagnosticsRow['circuitState']): string {
  switch (c) {
    case 'closed':    return 'Closed';
    case 'half-open': return 'Half-open';
    case 'open':      return 'Open';
  }
}

export const SIGNALR_DIAGNOSTICS_SPEC: AdminSurfaceSpec<SignalRDiagnosticsRow, never> = {
  id: 'signalr-diagnostics',
  title: 'SignalR · Diagnostics',
  description: 'Read-only operational telemetry from Bishop\'s '
    + 'SignalR hub: connection counts by transport, per-method '
    + 'p99 fan-out latency, invocation counts, and the WS '
    + 'retention pipeline\'s circuit-breaker state.  Drives the '
    + 'decision to invoke the W21 SignalR purge surface (when the '
    + 'pipeline trips half-open or open) or to tighten the W17 '
    + 'retention policy.',
  endpoint: '/api/admin/signalr/diagnostics',
  parseRow,
  rowKey: (r) => `${r.metricKey}@${r.tenantId}`,
  fields: [],
  buildBody: () => { throw new Error('signalr-diagnostics is read-only'); },
  columns: [
    {
      key: 'metricKey',
      label: 'Metric',
      render: (r) => ({ __html: `<code>${escapeHtml(r.metricKey)}</code>` }),
    },
    {
      key: 'tenantId',
      label: 'Tenant',
      render: (r) => r.tenantId === ''
        ? ({ __html: '<em class="admin-panel-muted">(global)</em>' })
        : ({ __html: `<code>${escapeHtml(r.tenantId)}</code>` }),
    },
    {
      key: 'transport',
      label: 'Transport',
      render: (r) => r.transport,
    },
    {
      key: 'connections',
      label: 'Connections',
      render: (r) => String(r.connections),
    },
    {
      key: 'invocations',
      label: 'Invocations',
      render: (r) => String(r.invocations),
    },
    {
      key: 'fanoutP99Ms',
      label: 'p99 (ms)',
      render: (r) => `${r.fanoutP99Ms.toFixed(1)}`,
    },
    {
      key: 'circuitState',
      label: 'Circuit',
      render: (r) => ({
        __html: `<span class="${circuitClass(r.circuitState)}">${escapeHtml(circuitLabel(r.circuitState))}</span>`,
      }),
    },
    {
      key: 'sampledAt',
      label: 'Sampled',
      render: (r) => fmtIso(r.sampledAt),
    },
  ],
};
