// Changsha mode/input boundary policy (Hicks — UAT §9 FE-1/FE-2).
//
// Pure, dependency-free predicates so the server-authoritative boundary is
// covered by browser-free contract tests (mirrors turn-cue.ts / hand-accounting.ts).
//
// The single architectural fault the UAT found: the client runs the upstream
// *relay, client-authoritative* Setup/Deal/free-drag physics while the
// connection also spins up a *server-authoritative* ChangshaRuntime. In
// Changsha the client must render authoritative state and emit ONLY sanctioned
// player actions — never author the scene locally.

import { BreakPointWire, DealMode, DealType, GameType } from './types';

/**
 * Hudson rev2 — format the authoritative pickup break ADDRESS for the move-log /
 * break-marker readout. `pickup.breakPoint` is a typed
 * { wallIndex, stackIndex, tileIndex } OBJECT (AutotableProtocol.BreakPointWire);
 * interpolating it directly ("@ col ${p.breakPoint}") rendered the user-visible
 * "break-point marked @ col [object Object]" bug. Returns a stable, human-readable
 * "wall {wallIndex} col {stackIndex}" suffix, or '' when the break is absent/untyped.
 * Pure so the format is contract-locked browser-free.
 */
export function formatBreakPointLabel(bp: BreakPointWire | null | undefined): string {
  if (!bp || typeof bp !== 'object') return '';
  if (typeof bp.wallIndex !== 'number' || typeof bp.stackIndex !== 'number') return '';
  return `wall ${bp.wallIndex} col ${bp.stackIndex}`;
}

/** True when the runtime — not the client — authors the scene (Changsha). */
export function isServerAuthoritative(gameType: GameType): boolean {
  return gameType === GameType.CHANGSHA;
}

/**
 * SC-1 / RC-13 (Ripley integration sub-contract) — first-paint deal bootstrap.
 * The World ctor seeds `conditions` from `Conditions.initial()` (which hardcodes
 * `dealMode:'manual'`); this maps the URL's `dealMode` for server-authoritative
 * Changsha BEFORE any authoritative field arrives. The pre-WS placeholder is
 * ALWAYS the canonical all-in-walls square (INITIAL) — for BOTH manual and auto:
 *   • manual ⇒ INITIAL  (canonical 108 face-down in the four walls)
 *   • auto   ⇒ INITIAL  (NOT HANDS — the HANDS local pre-deal scattered
 *                        world.things vs the server arc = the "four half-walls")
 *   • absent ⇒ keep the base (variant default; non-Changsha safety)
 * Only `dealType` is pinned to INITIAL; `dealMode` is tracked verbatim so the
 * auto/manual handshake + ceremony are unchanged. The authoritative server deal
 * drives the REAL layout on JOIN (auto ⇒ dealt hands + arc atomically; manual ⇒
 * the ceremony). Pure so it is covered by browser-free contract tests.
 */
export function bootstrapDealFromUrl(
  urlDealMode: DealMode | null,
  base: { dealMode: DealMode; dealType: DealType },
): { dealMode: DealMode; dealType: DealType } {
  // The pre-WS local placeholder is ALWAYS the canonical all-in-walls square
  // (INITIAL) for server-authoritative Changsha — NEVER the HANDS local deal.
  // HANDS pre-guesses a deal client-side (13/seat into hands + an asymmetric
  // 14/15/13/13 wall remnant, from a RANDOM local shuffle) — the "four
  // half-walls" world.things scatter Vasquez/Frost flagged: it renders a WRONG,
  // scattered layout that does NOT match the server's contiguous client.things
  // arc, then has to be reconciled away. INITIAL shows only face-down backs in
  // the four walls, so nothing wrong is ever revealed; the authoritative server
  // snapshot then drives the REAL deal (auto ⇒ dealt hands + arc atomically;
  // manual ⇒ the ceremony) on JOIN. `dealMode` is still tracked verbatim so the
  // auto/manual handshake + ceremony logic is unchanged — only the placeholder
  // layout differs.
  if (urlDealMode === 'manual') return { dealMode: 'manual', dealType: DealType.INITIAL };
  if (urlDealMode === 'auto') return { dealMode: 'auto', dealType: DealType.INITIAL };
  return { dealMode: base.dealMode, dealType: base.dealType };
}

/**
 * FE-1 — the legacy relay local deal (`world.deal` → `setup.deal` scatter +
 * `match`/`dice`/`things` broadcast) must be INERT in Changsha. Auto/manual
 * start comes from backend snapshots. Returns true when a local deal must be
 * suppressed.
 */
export function blocksLocalDeal(gameType: GameType): boolean {
  return isServerAuthoritative(gameType);
}

export interface TileFaceResolution {
  /** render the tile as a face-down back (no revealed identity). */
  faceDown: boolean;
  /** the tile-type index to show face-up, or null when hidden. */
  faceIndex: number | null;
}

/**
 * FE-7 / SC-2 / G19 — parse a `things` key to its REAL entitled tile id, or null
 * if the key is an OPAQUE hidden handle. The wire (Ripley 2026-08-07T10:44):
 * VISIBLE tiles keep numeric keys `"0".."107"` (number OR numeric-string);
 * HIDDEN tiles use `h_<22 base64url>` (or any non-numeric). So a key is REAL ⟺
 * it int-parses to 0..107 — NEVER a numeric-threshold and NEVER `typeof`
 * (a numeric-string `"0"` is a real tile, not an opaque back).
 */
export function parseRealTileId(key: string | number): number | null {
  if (typeof key === 'number') {
    return Number.isInteger(key) && key >= 0 && key <= 107 ? key : null;
  }
  // Only a canonical non-negative integer string 0..107 is a real tile id; the
  // opaque `h_<base64url>` form (or any non-numeric string) ⇒ null.
  if (!/^\d{1,3}$/.test(key)) return null;
  const n = Number(key);
  return n >= 0 && n <= 107 ? n : null;
}

/**
 * FE-7 / SC-2 / G19 — is a `things` key an OPAQUE per-viewer hidden-tile handle?
 * Opaque ⟺ it does NOT int-parse to a real tile id (0..107). Handles are
 * server-secret'd strings (`h_<base64url>`); visible keys are numeric `"0".."107"`.
 * Detected by VALUE (real-id-parse), never a numeric threshold or bare `typeof`.
 */
export function isOpaqueHandle(key: string | number): boolean {
  return parseRealTileId(key) === null;
}

/**
 * FE-7 / SC-2 / G19 (frontend hidden-information contract) — decide how to
 * render a `things` entry from its key + entitled face, WITHOUT ever leaking a
 * hidden tile's identity. Per the G19 clarification: treat keys as OPAQUE
 * (string-safe) and derive the face ONLY from an entitled explicit identity.
 *
 *   • OPAQUE key (string handle — a per-viewer, reconnect-stable but
 *     cross-viewer-unlinkable id for a tile you are NOT entitled to see) ⇒
 *     face-down back; NEVER derive a face from the key.
 *   • Entitled explicit strip (`face === null`, Phase-D per-viewer mask) ⇒
 *     face-down back.
 *   • Entitled real tile (numeric key 0..107, itself the entitled identity) ⇒
 *     face-up from the explicit `face` when the server sent one, else the real
 *     tileId's type (`⌊key/4⌋`).
 *
 * Pure ⇒ browser-free contract-tested; the single rule the `things` render path
 * must consume once Bishop ships opaque keys (the collection retype +
 * tombstone→real-id reveal wiring is the coordinated seam).
 */
export function resolveTileFace(
  key: string | number,
  face: number | null | undefined,
): TileFaceResolution {
  const realId = parseRealTileId(key);
  if (realId === null) return { faceDown: true, faceIndex: null };  // opaque handle
  if (face === null) return { faceDown: true, faceIndex: null };    // entitled per-viewer strip
  // Entitled real tile: face from the explicit `face`, else the tile's type
  // (⌊id/4⌋) — id parsed from the numeric key (number OR numeric-string).
  return { faceDown: false, faceIndex: face ?? Math.floor(realId / 4) };
}

export interface PartitionedThingEntries<V> {
  /** entitled real tiles — numeric key 0..107, drive the identity render path. */
  real: Array<[number, V]>;
  /** opaque per-viewer hidden-tile handles — string key, render as anonymous backs. */
  hidden: Array<[string, V]>;
}

/**
 * FE-7 / SC-2 / G19 — partition a `things` update batch (mixed entitled numeric
 * keys + opaque STRING handles) by REAL-ID PARSE (value-based via
 * `parseRealTileId`, NOT bare `typeof` — a numeric-string `"0"` is a real tile).
 * The `real` entries are NORMALIZED to a numeric key and flow through the
 * unchanged identity render path; the `hidden` (opaque-handle) entries route to
 * the anonymous-back path and NEVER touch tile-index arithmetic. Pure
 * ⇒ browser-free contract-tested; the single choke point that keeps opaque
 * handles off the numeric path (used by world.onThings). Reconnect-safe: a full
 * snapshot re-partitions deterministically by type, so a stable per-player
 * handle lands in `hidden` on every reconnect.
 */
export function partitionThingEntries<V>(
  entries: ReadonlyArray<[string | number, V]>,
): PartitionedThingEntries<V> {
  const real: Array<[number, V]> = [];
  const hidden: Array<[string, V]> = [];
  for (const [key, value] of entries) {
    // Opaque hidden handles are STRING keys ONLY (server HMAC `h_<base64url>`,
    // per the wire contract in sc2-hidden-pool.ts). A NUMERIC key is
    // authoritatively a local/real thing INDEX and must NEVER be treated as an
    // anonymous back: entitled real tiles are 0..107, while HIGHER numeric
    // indices are local non-tile things echoed back through world.sendUpdate →
    // onThings (marker=2000, the SC-2 hidden-back pool=108..215, sticks=1000+).
    // Routing those to `hidden` let reconcileHiddenBackPool hijack the local
    // slot they name — most visibly `marker@0`, which orphaned the real
    // marker's slot pointer and then threw `slot not empty: 108 marker@0` on the
    // next setup.replace (two things mapped to marker@0). Numeric keys stay on
    // the numeric path, where they self-heal via prepareMove and are ignored by
    // reconcileRealVisibility (which bounds ids to [0, poolSize)).
    if (typeof key === 'number') {
      real.push([key, value]);
      continue;
    }
    const realId = parseRealTileId(key);
    if (realId === null) {
      // Opaque hidden handle (`h_<base64url>` / non-numeric) — render as a back.
      hidden.push([key, value]);
    } else {
      // Entitled real tile — NORMALIZE the key to a number so the numeric
      // render path works whether the wire sent `0` or `"0"`.
      real.push([realId, value]);
    }
  }
  return { real, hidden };
}

export interface PointerSlotView {
  group: string;         // 'hand' | 'wall' | 'discard' | 'meld' | ...
  seat: number | null;   // owning seat of the slot, or null
}

/**
 * FE-2 — input allowlist for NON-wall pointer interactions in Changsha:
 *
 *   • the local seat's OWN hand tiles — the discard intercept turns a click on
 *     these into an authoritative discard on your turn.
 *
 * Wall tiles are governed separately by the R-1 §D10 predicate
 * ({@link wallTileInteractive}) because they require the manual-pickup phase +
 * the server-designated target set. Everything else — other seats' hands,
 * discards, exposed melds, runtime-owned things — is NON-interactive.
 */
export function changshaAllowsPointer(
  view: PointerSlotView,
  mySeat: number | null,
): boolean {
  if (mySeat === null || mySeat < 0) return false;
  return view.group === 'hand' && view.seat === mySeat;
}

/**
 * R-1 §B — the manual-deal ceremony pickup phases (authoritative ChangshaPhase
 * names). Accepts the wire's Pascal spelling; wall interaction is legal ONLY
 * while the AUTHORITATIVE `turn.phase` is one of these (never after
 * →AwaitingDiscard, even if the sticky `pickup` entry lingers — R-1 §E3).
 */
export const PICKUP_PHASES: ReadonlySet<string> = new Set([
  'BreakPointMarked', 'PickupRound1', 'PickupRound2', 'PickupRound3',
  'SingleTilePickup', 'DealerExtra',
]);

export interface WallInteractionInput {
  variantIsChangsha: boolean;
  /** dealMode is Manual (the ceremony only exists in manual). */
  dealModeIsManual: boolean;
  /** a pickup affordance is present and targets THIS seat (`isMyPickupTurn`). */
  pickupIsMine: boolean;
  /** the AUTHORITATIVE `turn.phase` (Bishop's turn signal), NOT the sticky pickup entry. */
  authoritativePhase: string | null;
  /**
   * the touched wall tile is the single server-designated trigger (SC-4). The
   * caller computes this via {@link tileInDesignatedTrigger}: `pickup.targetSlots`
   * exactly length 1 (the co-derived Wall[0] slot) matching the hovered slot
   * name. Fails closed on missing/empty/multiple — never raw tile ids
   * (SC-2/G19), never a batch-set / any-wall fallback.
   */
  inDesignatedSet: boolean;
}

/**
 * R-1 §D10 — SINGLE SOURCE OF TRUTH for wall-tile interactivity in Changsha.
 * INTERACTIVE ⟺ changsha ∧ manual ∧ pickup-is-mine ∧ authoritative phase ∈
 * pickup phases ∧ touched tile ∈ the server-designated batch. Otherwise INERT.
 * AUTO (no pickup) ⇒ always inert; post-deal (phase==AwaitingDiscard) ⇒ always
 * inert even if the pickup entry is stale.
 */
export function wallTileInteractive(i: WallInteractionInput): boolean {
  return i.variantIsChangsha
      && i.dealModeIsManual
      && i.pickupIsMine
      && i.authoritativePhase !== null
      && PICKUP_PHASES.has(i.authoritativePhase)
      && i.inDesignatedSet;
}

/**
 * SC-4 (Ripley/Frost/Vasquez, latest) — is the hovered wall tile the single
 * server-designated pickup TRIGGER? `pickup.targetSlots` MUST be **exactly
 * length 1** = the co-derived `Wall[0]` trigger slot (the `count` field carries
 * the batch size; the server takes the whole batch `Wall[0..count-1]`). This
 * FAILS CLOSED on missing / empty / multiple (>1) targetSlots — the old
 * over-broad "any wall tile during pickup" / batch-set (`length>0 ⇒ includes`)
 * fallback is FORBIDDEN (fails G17). We match on the render slot name (NOT raw
 * tile ids — SC-2/G19 wall keys are opaque per-viewer).
 */
export function tileInDesignatedTrigger(
  hoveredSlotName: string,
  targetSlots: ReadonlyArray<string> | null | undefined,
): boolean {
  return Array.isArray(targetSlots)
      && targetSlots.length === 1
      && targetSlots[0] === hoveredSlotName;
}

/**
 * F2 binding (Ralph/Vasquez) — the exact pickup trigger must be the REACHABLE
 * TOP-layer projection of `state.Wall[0]`, never a covered/bottom tile. A wall
 * stack is 2-high; the bottom (layer 0) tile is occluded by the top (layer 1)
 * and is non-selectable (its `up` slot is occupied). So the trigger is
 * actionable ⟺ it is the exact designated slot ({@link tileInDesignatedTrigger})
 * AND it is not covered. If Bishop ever designates a covered/bottom slot
 * (F2 inconsistency) this FAILS CLOSED — no pickup action — complementing the
 * ordinary `canSelect` coverage check (defense-in-depth).
 */
export function pickupTriggerActionable(
  hoveredSlotName: string,
  targetSlots: ReadonlyArray<string> | null | undefined,
  hoveredCovered: boolean,
): boolean {
  return tileInDesignatedTrigger(hoveredSlotName, targetSlots) && !hoveredCovered;
}

export interface PickupTakeCommand {
  seatIndex: number;
  count: number;
}

/**
 * P0 (Hudson/Vasquez) — the outbound manual-pickup take. It is COUNT-BASED and
 * carries ZERO client tile authority: ONLY `{seatIndex, count}` — never a tile
 * id, opaque handle, or slot. The server validates phase/seat and removes
 * `Wall[0..count-1]` itself; the tile moves solely via the server `things`
 * snapshot (no optimistic client move). One real pointer click on the exact
 * `targetSlots[0]` emits exactly one of these. Pure so the shape is
 * contract-locked browser-free.
 */
export function pickupTakeCommand(seatIndex: number, count: number): PickupTakeCommand {
  return { seatIndex, count };
}
