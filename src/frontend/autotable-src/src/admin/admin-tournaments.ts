// Phase K Wave 22 — Hicks (Frontend).
//
// Admin-panel tournaments barrel module.  This file is the single
// dynamic-import entry point for the `admin-panel-tournaments`
// chunk: it re-exports the descriptor of every "tournament"
// admin surface so `admin-panel.ts` can pull them all with one
// `await import('./admin-tournaments')` call.
//
// Why a barrel: vite/rollup chunks by reachable-module-graph; if
// we statically import the tournament specs from `admin-panel.ts`,
// rollup pulls them into the admin-panel-core chunk.  Routing
// these files into a separate `admin-panel-tournaments` chunk
// (via `vite.config.ts:manualChunks`) only takes effect when the
// import is DYNAMIC — hence this barrel + the dynamic-import
// call site in `./admin-panel.ts:loadTournamentSpecs()`.
//
// W22 split rationale (the "core/tournaments" naming carries
// historical W22 directive baggage; the actual contents below
// extend beyond just the 5 swiss/tournament surfaces to include
// the operational-trigger + audit surfaces that don't belong in
// the W18 baseline-CRUD core).  See
// `docs/frontend-bundle-audit.md §3.7` for the audit reasoning.

import type { AdminSurfaceSpec } from './admin-shared';

import { SWISS_PAIRING_AUDIT_SPEC } from './swiss-pairing-audit';
import { SWISS_PAIR_NEXT_ROUND_SPEC } from './swiss-pair-next-round';
import { SWISS_APPLY_ROUND_SPEC } from './swiss-apply-round';
import { TOURNAMENT_WITHDRAW_SPEC } from './tournament-withdraw';
import { TOURNAMENT_FINALIZE_SPEC } from './tournament-finalize';
import { SIGNALR_PURGE_SPEC } from './signalr-purge';
import { SIGNALR_DIAGNOSTICS_SPEC } from './signalr-diagnostics';
import { REPLAY_INTEGRITY_AUDIT_SPEC } from './replay-integrity-audit';
import { REPLAY_RESTORATION_AUDIT_SPEC } from './replay-restoration-audit';
import { REPLAY_DOWNLOAD_CHUNKED_SPEC } from './replay-download-chunked';
import { AUDIT_LOG_SEARCH_SPEC } from './audit-log-search';
import { JWT_EMERGENCY_REVOKE_SPEC } from './jwt-emergency-revoke';

interface AnySpec extends AdminSurfaceSpec<unknown, unknown> {}

/**
 * Tournament + ops + audit surfaces.  Loaded lazily by
 * `admin-panel.ts` when the operator activates a tab whose owner
 * lives in this chunk.  The order matches the on-screen tab
 * order; the entry inserts these after the core specs so the
 * core surfaces remain the leftmost tabs in the panel header.
 */
export const TOURNAMENT_SURFACES: ReadonlyArray<AnySpec> = [
  // Tournaments (Swiss lifecycle).
  SWISS_PAIRING_AUDIT_SPEC as unknown as AnySpec,
  SWISS_PAIR_NEXT_ROUND_SPEC as unknown as AnySpec,
  SWISS_APPLY_ROUND_SPEC as unknown as AnySpec,
  TOURNAMENT_WITHDRAW_SPEC as unknown as AnySpec,
  TOURNAMENT_FINALIZE_SPEC as unknown as AnySpec,
  // SignalR operational surfaces.
  SIGNALR_PURGE_SPEC as unknown as AnySpec,
  SIGNALR_DIAGNOSTICS_SPEC as unknown as AnySpec,
  // Replay-audit + chunked-download surfaces.
  REPLAY_INTEGRITY_AUDIT_SPEC as unknown as AnySpec,
  REPLAY_RESTORATION_AUDIT_SPEC as unknown as AnySpec,
  REPLAY_DOWNLOAD_CHUNKED_SPEC as unknown as AnySpec,
  // Cross-cutting audit-log browser + JWT emergency-revoke.
  AUDIT_LOG_SEARCH_SPEC as unknown as AnySpec,
  JWT_EMERGENCY_REVOKE_SPEC as unknown as AnySpec,
];
