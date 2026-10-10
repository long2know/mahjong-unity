// Phase K Wave 21 — WebGL2 meld-display module (Hicks, Frontend).
//
// Phase L W6 sister module to `./tile-claim-animation`.  When a
// claim settles, the tiles become part of a *visible meld group*
// at the player's table-edge row.  This module owns the per-seat
// meld layout: how meld groups stack along the table edge, how
// individual melds are spaced, and which tile of a chi/kong is
// rotated (kong = 4 tiles with one face-down, chi = 3 tiles with
// the claimed tile sideways indicating the source seat).
//
// Design:
//   • A `MeldGroup` is the resolved record of one declared claim —
//     `{ kind, tiles, claimedFromSeat }`.  The seat owns an ordered
//     list of `MeldGroup`s (the order they were declared in).
//   • `layoutMeldRow()` translates that ordered list into per-tile
//     model matrices (the same shape `TileMesh.setTileInstance()`
//     consumes), arranging the groups end-to-end along the seat's
//     table-edge row.
//   • Rotation conventions:
//       - pung: all tiles oriented the same way.
//       - kong: 4 tiles, middle tile rotated 90° to indicate kong.
//       - chi:  3 tiles, the claimed tile (always tiles[0] in our
//         convention) rotated 90° to indicate the claim source.
//   • No three.js dependency — column-major Float32Array(16) all
//     the way through.  Mirrors `tile-mesh.ts` conventions exactly.
//
// What's HERE in W21:
//   • `MeldKind`             — pung | kong | chi  (mirrors
//     `tile-claim-animation.ClaimKind`).
//   • `MeldGroup`            — a single declared meld record.
//   • `MeldDisplayState`     — per-seat ordered list of groups +
//     the cached model matrices.
//   • `createMeldDisplay()`  — build an empty per-seat state.
//   • `appendMeld()`         — push a new MeldGroup, recompute the
//     row layout, return the per-tile model matrices.
//   • `layoutMeldRow()`      — pure layout function; computes the
//     model matrix for every tile in the row in one pass.
//
// What's NOT here (Phase L W7+):
//   • Per-tile face texture selection (lives in `tile-atlas.ts`
//     when the meld is rendered).
//   • Animated drop-into-row (the claim animation drives the
//     drop-in; once settled, the row layout is static until the
//     next claim).
//
// Bundle math: target ≤ 48 KB for `renderer-webgl2` at W21.  W20
// baseline 35,258 B; this module + `./tile-claim-animation`
// together must stay under ~13 KB to clear the target.

import { TILE_DEPTH, TILE_HEIGHT, TILE_WIDTH } from './tile-mesh';

/** Meld kind — mirrors `tile-claim-animation.ClaimKind`. */
export type MeldKind = 'pung' | 'kong' | 'chi';

/** Per-seat seat indices (0 = self, 1/2/3 = right/opposite/left). */
export type SeatIndex = 0 | 1 | 2 | 3;

/** A single declared meld record. */
export interface MeldGroup {
  readonly kind: MeldKind;
  /** Tile-id (atlas index) for every tile in the meld, in declared order. */
  readonly tiles: ReadonlyArray<number>;
  /** Seat the claim was taken from (relative seat index, 0..3). */
  readonly claimedFromSeat: SeatIndex;
}

/** Per-seat display state. */
export interface MeldDisplayState {
  /** Seat this row belongs to. */
  readonly seat: SeatIndex;
  /** Ordered list of declared melds (oldest first). */
  readonly groups: MeldGroup[];
  /** Cached per-tile model matrices, in the same order as `groups`
   *  flattened.  Refreshed on every `appendMeld()` call. */
  matrices: Float32Array[];
  /** Cached per-tile tile-ids, parallel to `matrices`. */
  tileIds: number[];
}

/** Gap between meld groups along the row, in scene units. */
export const MELD_GROUP_GAP = TILE_WIDTH * 0.30;

/** Total tile count if every meld is at its max size (kong = 4). */
export const MELD_ROW_MAX_TILES = 4 * 4; // up to 4 kongs

/** Build an empty per-seat meld display state. */
export function createMeldDisplay(seat: SeatIndex): MeldDisplayState {
  return {
    seat,
    groups: [],
    matrices: [],
    tileIds: [],
  };
}

/**
 * Append a meld group + re-layout the row.  Returns the per-tile
 * matrices (same array referenced by `state.matrices` — caller
 * MUST NOT retain the reference across calls).  The caller writes
 * each matrix through `setTileInstance()` + requests a redraw.
 */
export function appendMeld(
  state: MeldDisplayState,
  group: MeldGroup,
): { matrices: ReadonlyArray<Float32Array>; tileIds: ReadonlyArray<number> } {
  if (group.tiles.length < 3 || group.tiles.length > 4) {
    throw new Error(
      `[meld-display] meld tiles must be 3 or 4, got ${group.tiles.length}`,
    );
  }
  if (group.kind === 'kong' && group.tiles.length !== 4) {
    throw new Error('[meld-display] kong requires 4 tiles');
  }
  if ((group.kind === 'pung' || group.kind === 'chi') && group.tiles.length !== 3) {
    throw new Error(`[meld-display] ${group.kind} requires 3 tiles`);
  }
  state.groups.push(group);
  layoutMeldRow(state);
  return { matrices: state.matrices, tileIds: state.tileIds };
}

/**
 * Pure layout pass — walk every meld in the state and compute the
 * per-tile matrices + tile-ids.  Mutates `state.matrices` and
 * `state.tileIds` in place.  Idempotent (re-running with the same
 * groups yields the same output).
 *
 * Layout coordinates:
 *   • Seat 0 (self) — row runs along +X at z = +EDGE, facing camera.
 *   • Seat 1 (right) — row runs along +Z at x = +EDGE.
 *   • Seat 2 (opposite) — row runs along -X at z = -EDGE.
 *   • Seat 3 (left)  — row runs along -Z at x = -EDGE.
 *
 * Tiles within a group are laid out shoulder-to-shoulder along the
 * row.  Groups are separated by `MELD_GROUP_GAP`.  The "claimed"
 * tile (kong middle, chi tiles[0]) is rotated 90° around Y so its
 * long side runs perpendicular to the row — the visual cue that
 * the meld was claimed (vs. drawn into).
 */
export function layoutMeldRow(state: MeldDisplayState): void {
  const totalTiles = state.groups.reduce((n, g) => n + g.tiles.length, 0);
  if (state.matrices.length !== totalTiles) {
    state.matrices = new Array(totalTiles);
    state.tileIds = new Array(totalTiles);
    for (let i = 0; i < totalTiles; i++) {
      state.matrices[i] = new Float32Array(16);
    }
  }

  // Edge distance from world origin to the seat's table-edge row.
  // Chosen so the meld row sits just past the player's hand row.
  const EDGE = 4.5 * TILE_WIDTH;

  // Per-seat axis directions (forward = direction along the row;
  // perpendicular = direction away from the table center).
  const seatAxes = seatAxesFor(state.seat);

  let cursor = 0; // running offset along the row
  let idx = 0;    // running index into matrices/tileIds
  for (const group of state.groups) {
    for (let t = 0; t < group.tiles.length; t++) {
      const isClaimedTile = isClaimedTileWithinGroup(group, t);
      const tileLength = isClaimedTile ? TILE_HEIGHT : TILE_WIDTH;
      // Position the tile's CENTER at cursor + half its length so
      // neighbouring tiles meet edge-to-edge.
      const offset = cursor + tileLength * 0.5;
      writeTileMatrix(
        state.matrices[idx],
        state.seat,
        seatAxes,
        offset,
        EDGE,
        isClaimedTile,
      );
      state.tileIds[idx] = group.tiles[t];
      cursor += tileLength;
      idx += 1;
    }
    cursor += MELD_GROUP_GAP;
  }
}

/**
 * Identify the "claimed tile" within a group — the tile that should
 * be rotated 90° as a visual claim indicator.
 *
 * Convention:
 *   • pung: no tile rotated (all three look identical).
 *   • kong: middle tile (index 1) rotated.
 *   • chi:  tiles[0] (the claim source) rotated.
 */
function isClaimedTileWithinGroup(group: MeldGroup, tileIdx: number): boolean {
  switch (group.kind) {
    case 'pung': return false;
    case 'kong': return tileIdx === 1;
    case 'chi':  return tileIdx === 0;
  }
}

interface SeatAxes {
  /** Unit vector along the row direction (world-space). */
  readonly forwardX: number;
  readonly forwardZ: number;
  /** Unit vector perpendicular to the row (toward table center). */
  readonly perpX: number;
  readonly perpZ: number;
  /** Rotation around Y (radians) so the tile faces the seat. */
  readonly yawRad: number;
}

function seatAxesFor(seat: SeatIndex): SeatAxes {
  switch (seat) {
    case 0:
      return { forwardX:  1, forwardZ:  0, perpX:  0, perpZ:  1, yawRad: 0 };
    case 1:
      return { forwardX:  0, forwardZ:  1, perpX: -1, perpZ:  0, yawRad: -Math.PI / 2 };
    case 2:
      return { forwardX: -1, forwardZ:  0, perpX:  0, perpZ: -1, yawRad: Math.PI };
    case 3:
      return { forwardX:  0, forwardZ: -1, perpX:  1, perpZ:  0, yawRad: Math.PI / 2 };
  }
}

/**
 * Write the per-tile model matrix into `out` (16 floats).  The
 * matrix is column-major.  Builds a Y-yaw rotation, an optional
 * +π/2 Y rotation for the "claimed" tile, and a translation to
 * the row position.  Allocation-free — the caller owns `out`.
 */
function writeTileMatrix(
  out: Float32Array,
  seat: SeatIndex,
  axes: SeatAxes,
  alongRowOffset: number,
  edgeDistance: number,
  rotateForClaim: boolean,
): void {
  // Effective yaw: base seat yaw + π/2 if claimed.
  const yaw = axes.yawRad + (rotateForClaim ? Math.PI / 2 : 0);
  const c = Math.cos(yaw);
  const s = Math.sin(yaw);

  // Identity scaffold.
  out[0] = c;  out[1] = 0;  out[2] = -s; out[3] = 0;
  out[4] = 0;  out[5] = 1;  out[6] = 0;  out[7] = 0;
  out[8] = s;  out[9] = 0;  out[10] = c; out[11] = 0;

  // World-space row position: the row starts at one end and runs
  // along the forward axis.  We subtract half the row's max span so
  // the row is centered along the seat edge.
  const rowAnchorX = axes.perpX * edgeDistance + axes.forwardX * (alongRowOffset - rowSpanCenter());
  const rowAnchorZ = axes.perpZ * edgeDistance + axes.forwardZ * (alongRowOffset - rowSpanCenter());

  out[12] = rowAnchorX;
  // Tiles rest flat on the table — half-depth above y=0.
  out[13] = TILE_DEPTH * 0.5;
  out[14] = rowAnchorZ;
  out[15] = 1;
  // Suppress unused-arg lint warning — `seat` is part of the
  // public API surface for future per-seat edge tweaks (e.g.
  // mirror seat 2 onto the same row plane as seat 0 for camera
  // sweeps in spectator mode).
  void seat;
}

/**
 * Half the row's max span — used to center the row on the seat
 * edge.  Computed against `MELD_ROW_MAX_TILES * TILE_WIDTH`
 * (worst case: every meld is a kong); the row visibly centers as
 * meld groups accumulate.
 */
function rowSpanCenter(): number {
  return MELD_ROW_MAX_TILES * TILE_WIDTH * 0.5;
}

/**
 * Get the meld-slot origin for `tile-claim-animation.meldSlotMatrix()`.
 * Returns the world-space anchor point (x, z) at which the next
 * claim's tiles should snap to when the claim animation finishes.
 *
 * This is the start-of-next-group position in the seat's row —
 * append a meld via `appendMeld()` first, then this returns the
 * NEW group's anchor.  Used by the scene runtime to drive the
 * `target` matrices in `startClaimAnimation()`.
 */
export function nextMeldOriginXZ(state: MeldDisplayState): [number, number] {
  const axes = seatAxesFor(state.seat);
  const EDGE = 4.5 * TILE_WIDTH;
  // Walk the existing groups to compute the current cursor end.
  let cursor = 0;
  for (const group of state.groups) {
    for (let t = 0; t < group.tiles.length; t++) {
      const tileLength = isClaimedTileWithinGroup(group, t)
        ? TILE_HEIGHT
        : TILE_WIDTH;
      cursor += tileLength;
    }
    cursor += MELD_GROUP_GAP;
  }
  // Translate the cursor into world-space.
  const x = axes.perpX * EDGE + axes.forwardX * (cursor - rowSpanCenter());
  const z = axes.perpZ * EDGE + axes.forwardZ * (cursor - rowSpanCenter());
  return [x, z];
}
