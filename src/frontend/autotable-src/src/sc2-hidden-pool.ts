// SC-2 (G19) anonymous hidden-back Thing pool — PURE reconciliation core.
//
// Binding shape (Ripley 2026-08-07T10:08): a `things` full snapshot is an
// AUTHORITATIVE REPLACEMENT array with mixed keys —
//   • numeric key (0..107)  ⇒ an ENTITLED real tile (own hand / public
//     discard / exposed meld). Rendered by its pre-baked real Thing, face-up.
//   • string key (opaque HMAC handle) ⇒ a HIDDEN tile (wall / foreign concealed
//     hand / concealed kong). Rendered by an ANONYMOUS back — slot + rotation
//     only, NO identity/face, never type-inferred.
//
// There is NO separate per-tile tombstone: a reveal is "old handle absent + a
// numeric real entry present in the SAME snapshot" — reconciled atomically so a
// physical tile is drawn exactly once per frame (never duplicated, never
// invisible). This module computes WHAT should happen; world.ts applies it to
// the real Thing pool + the anonymous back pool + the InstancedMesh render.

/** Minimal view of a `things` entry payload the pool cares about. */
export interface HiddenSlotInfo {
  slotName: string;
  rotationIndex?: number;
}

export interface HiddenBackPlacement {
  handle: string;
  slotName: string;
  /** the server-authored rotation (already face-down for a hidden tile). */
  rotationIndex?: number;
}

export interface HiddenPoolPlan {
  /** backs to (re)place this snapshot — stable-reuse by handle upstream. */
  place: Array<HiddenBackPlacement>;
  /** handles whose back must be released (absent / tombstoned this snapshot). */
  release: Array<string>;
}

/**
 * Reconcile the anonymous back pool against a snapshot's hidden (string-keyed)
 * entries, given the handles currently assigned a back. Handles present with a
 * non-null payload are (re)placed (stable reuse upstream keeps the same back
 * object → render/animation continuity + reconnect stability). Handles that are
 * explicitly null OR absent from an authoritative full snapshot are released.
 *
 * @param full when true the snapshot is authoritative/complete, so any
 *   previously-assigned handle NOT in this batch is released (the
 *   no-explicit-tombstone reveal path). When false (incremental) only
 *   explicitly-null handles are released.
 */
export function reconcileHiddenBacks(
  hiddenEntries: ReadonlyArray<[string, HiddenSlotInfo | null]>,
  prevHandles: Iterable<string>,
  full: boolean,
): HiddenPoolPlan {
  const place: Array<HiddenBackPlacement> = [];
  const release: Array<string> = [];
  const seen = new Set<string>();

  for (const [handle, info] of hiddenEntries) {
    seen.add(handle);
    if (info === null) {
      release.push(handle);
    } else {
      place.push({ handle, slotName: info.slotName, rotationIndex: info.rotationIndex });
    }
  }

  if (full) {
    // Authoritative replacement: a previously-placed handle not in this
    // snapshot has been revealed/removed ⇒ release its back.
    for (const handle of prevHandles) {
      if (!seen.has(handle)) {
        release.push(handle);
      }
    }
  }

  return { place, release };
}

export interface RealVisibilityPlan {
  /** entitled real ids present in the snapshot — render their real Thing. */
  show: Array<number>;
  /** pre-baked real ids ABSENT from an authoritative snapshot — hide so a
   *  non-entitled real identity can never render/leak. */
  hide: Array<number>;
}

/**
 * Reconcile the 108 pre-baked REAL Things against a snapshot's entitled
 * (numeric-keyed) entries. On an authoritative full snapshot, every pre-baked
 * id NOT present numerically is HIDDEN (its real face/identity must never render
 * while the tile is hidden from this viewer). Present ids are shown.
 */
export function reconcileRealVisibility(
  numericKeys: ReadonlyArray<number>,
  poolSize: number,
  full: boolean,
): RealVisibilityPlan {
  const present = new Set<number>(numericKeys);
  const show = [...present].filter(id => id >= 0 && id < poolSize);
  if (!full) {
    return { show, hide: [] };
  }
  const hide: Array<number> = [];
  for (let id = 0; id < poolSize; id++) {
    if (!present.has(id)) hide.push(id);
  }
  return { show, hide };
}

/**
 * G19 defense/invariant — a physical tile is EITHER entitled (numeric) OR hidden
 * (string) for a given viewer, never both. Returns true when the two key sets
 * are disjoint by construction (numeric vs string can't collide by type, but an
 * upstream projection bug that reused an id would be caught by the count test).
 */
export function physicalTileCount(numericCount: number, hiddenCount: number): number {
  return numericCount + hiddenCount;
}

/**
 * SC-2 (G19) — the distinct occupied SLOT names in a snapshot (non-null
 * entries). Identity/placement is keyed on (key, slotName), NEVER tuple
 * position: the backend sorts slot-canonical for determinism, but the client
 * must NOT infer draw order or identity from incoming order. Used to assert the
 * 108-unique-physical-slots invariant (each of the 108 physical tiles occupies
 * exactly one distinct slot; entitled real OR anonymous back, never both).
 */
export function occupiedSlots(
  entries: ReadonlyArray<[string | number, HiddenSlotInfo | null]>,
): Set<string> {
  const slots = new Set<string>();
  for (const [, info] of entries) {
    if (info !== null) slots.add(info.slotName);
  }
  return slots;
}

/**
 * SC-2 (G19) — true when a full snapshot occupies exactly `expected` DISTINCT
 * physical slots with no duplicate (no two tiles claiming one slot). Order- and
 * key-type-agnostic (uses the slot set only).
 */
export function hasUniquePhysicalSlots(
  entries: ReadonlyArray<[string | number, HiddenSlotInfo | null]>,
  expected: number,
): boolean {
  const nonNull = entries.filter(([, info]) => info !== null).length;
  return occupiedSlots(entries).size === nonNull && nonNull === expected;
}
