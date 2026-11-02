// Phase K Wave 22 — WebGL2 discard-pile module (Hicks, Frontend).
//
// Phase L W7 — visible discarded-tile pile per seat.  W21 landed
// the claim animation + meld-display row (declared meld groups
// shown at the table edge); W22 layers the *discard pile* on top:
// when a player discards a tile that is NOT claimed by anyone,
// the tile lands in the player's discard pile, which grows in
// a 6-column grid in front of the seat.
//
// Design constraints (mirror the W20/W21 modules):
//   • Zero three.js dependency — `renderer-webgl2` chunk stays
//     free-standing (W15 directive).
//   • Allocation-free per-frame: every discarded tile reuses an
//     existing Float32Array(16) model matrix; no per-redraw
//     allocations beyond the constant-size buffers the state owns.
//   • Each seat's pile uses a 6-column-wide grid that wraps to the
//     next row at 6 tiles.  Standard mahjong discard layout is
//     6 tiles per row × N rows; tiles are arranged left-to-right,
//     row-by-row, with the active row growing toward the table
//     centre.  Worst-case ceiling per seat: 28+ tiles (after a
//     long hand with no claims taken).
//   • Riichi-declaration sideways rotation: when a tile is the
//     riichi-declaring discard (the tile that triggers a riichi
//     bet), it is rotated 90° to indicate the riichi tile.  Stored
//     as a per-tile flag in the pile entry.
//
// What's HERE in W22:
//   • `DiscardEntry`           — per-tile record (tileId, riichi flag).
//   • `DiscardPileState`       — per-seat pile state + cached
//     model matrices.
//   • `createDiscardPile()`    — build an empty per-seat state.
//   • `pushDiscard()`          — append a tile to the pile; recompute
//     the layout grid; return the new entry's model matrix.
//   • `layoutDiscardPile()`    — pure layout function; computes the
//     model matrix for every tile in the pile in one pass.
//   • `popDiscard()`           — remove the most-recent discard
//     (used when a player retracts their throw, or when a claim
//     re-routes the tile into a meld group).
//   • `discardTileCount()`     — public accessor for the current
//     pile size, used by the scene runtime to gate the redraw stop.
//
// What's NOT here (Phase L W8+):
//   • Per-tile face texture selection (lives in `tile-atlas.ts`
//     when the pile is rendered).
//   • Animated drop-into-pile (the discard animation would live
//     in a sibling `tile-discard-animation.ts` module — TBD).
//
// Bundle math: target ≤ 52 KB for `renderer-webgl2` at W22.  W21
// baseline 40,292 B; this module + `./score-display` together
// must stay under ~12 KB to clear the target with headroom.

import { TILE_DEPTH, TILE_HEIGHT, TILE_WIDTH } from './tile-mesh';
import type { SeatIndex } from './meld-display';

/** Per-tile entry within a discard pile. */
export interface DiscardEntry {
  /** Tile-id (atlas index) for the discarded tile. */
  readonly tileId: number;
  /** True when the discard is the riichi-declaring tile (rotated
   *  90° in the pile to indicate the riichi bet).  Standard
   *  mahjong convention. */
  readonly isRiichi: boolean;
}

/** Per-seat discard pile state. */
export interface DiscardPileState {
  /** Seat this pile belongs to. */
  readonly seat: SeatIndex;
  /** Ordered list of discarded tiles (oldest first). */
  readonly entries: DiscardEntry[];
  /** Cached per-tile model matrices, parallel to `entries`.
   *  Refreshed on every `pushDiscard()` / `popDiscard()` call. */
  matrices: Float32Array[];
}

/** Grid columns per discard pile row (standard mahjong layout). */
export const DISCARD_COLS_PER_ROW = 6;

/** Worst-case ceiling per seat (28+ tile design budget). */
export const DISCARD_MAX_TILES = 30;

/** Gap between adjacent tiles in the grid, as a fraction of TILE_WIDTH. */
export const DISCARD_GAP_RATIO = 0.06;

/** Distance from the centre line where the pile's first row sits. */
const DISCARD_ROW_EDGE = 2.6 * TILE_WIDTH;

/**
 * Build an empty per-seat discard pile state.  Allocation-free
 * after this call — `pushDiscard()` reuses the matrix buffers.
 */
export function createDiscardPile(seat: SeatIndex): DiscardPileState {
  return {
    seat,
    entries: [],
    matrices: [],
  };
}

/**
 * Append a tile to the discard pile, refresh the grid layout, and
 * return the new entry's model matrix.  The returned matrix is
 * owned by `state.matrices` — do NOT mutate it after the call
 * returns; the next `pushDiscard()` / `popDiscard()` will rewrite
 * the buffer in place.
 */
export function pushDiscard(
  state: DiscardPileState,
  tileId: number,
  isRiichi: boolean = false,
): Float32Array {
  if (state.entries.length >= DISCARD_MAX_TILES) {
    throw new Error(
      `discard pile overflow: max ${DISCARD_MAX_TILES} tiles per seat`,
    );
  }
  (state.entries as DiscardEntry[]).push({ tileId, isRiichi });
  // Allocate a new matrix slot only on the first push that exceeds
  // the cache; otherwise reuse the existing buffer in place.
  while (state.matrices.length < state.entries.length) {
    state.matrices.push(new Float32Array(16));
  }
  layoutDiscardPile(state);
  return state.matrices[state.entries.length - 1];
}

/**
 * Remove the most-recent discard (used when a claim re-routes the
 * tile into a meld group, or when a player retracts a throw before
 * the next move resolves).  No-op on an empty pile.
 */
export function popDiscard(state: DiscardPileState): DiscardEntry | null {
  if (state.entries.length === 0) return null;
  const popped = (state.entries as DiscardEntry[]).pop() ?? null;
  // Don't shrink `state.matrices` — keep the cache for the next push.
  if (state.entries.length > 0) layoutDiscardPile(state);
  return popped;
}

/**
 * Compute the per-tile model matrix for every entry in the pile.
 * Pure layout function — allocation-free; the caller owns
 * `state.matrices`.  Layout is column-major in scene units:
 *   • 6-column grid, growing toward the table centre as rows fill.
 *   • Row 0 sits at `DISCARD_ROW_EDGE` (closest to the seat); row N
 *     sits at `DISCARD_ROW_EDGE - N * (TILE_HEIGHT + gap)`.
 *   • Column 0 is left-most from the seat's perspective; column 5
 *     is right-most.  Tiles within a row sit at
 *     `(c - 2.5) * (TILE_WIDTH + gap)` along the perpendicular axis.
 *   • Riichi tiles are rotated 90° around the Y axis.
 */
export function layoutDiscardPile(state: DiscardPileState): void {
  const axes = seatAxesFor(state.seat);
  const tileGap = TILE_WIDTH * DISCARD_GAP_RATIO;
  const colStride = TILE_WIDTH + tileGap;
  const rowStride = TILE_HEIGHT + tileGap;
  for (let i = 0; i < state.entries.length; i++) {
    const entry = state.entries[i];
    const col = i % DISCARD_COLS_PER_ROW;
    const row = Math.floor(i / DISCARD_COLS_PER_ROW);
    // Centre the 6-column grid on the seat's forward axis.
    const colOffset = (col - (DISCARD_COLS_PER_ROW - 1) * 0.5) * colStride;
    // First row sits at the seat-edge distance; subsequent rows
    // grow toward the table centre.
    const rowOffset = DISCARD_ROW_EDGE - row * rowStride;
    writeDiscardMatrix(
      state.matrices[i],
      axes,
      colOffset,
      rowOffset,
      entry.isRiichi,
    );
  }
}

/** Number of discards currently in the pile. */
export function discardTileCount(state: DiscardPileState): number {
  return state.entries.length;
}

/** Internal seat-axis helper — mirrors `meld-display.ts`. */
interface SeatAxes {
  /** Forward unit-vector (toward table centre). */
  readonly forwardX: number;
  readonly forwardZ: number;
  /** Perpendicular (right-hand-side from seat's perspective). */
  readonly perpX: number;
  readonly perpZ: number;
  /** Rotation around Y (radians) so the tile faces the seat. */
  readonly yawRad: number;
}

function seatAxesFor(seat: SeatIndex): SeatAxes {
  switch (seat) {
    case 0:
      return { forwardX:  0, forwardZ:  1, perpX:  1, perpZ:  0, yawRad: 0 };
    case 1:
      return { forwardX: -1, forwardZ:  0, perpX:  0, perpZ:  1, yawRad: -Math.PI / 2 };
    case 2:
      return { forwardX:  0, forwardZ: -1, perpX: -1, perpZ:  0, yawRad: Math.PI };
    case 3:
      return { forwardX:  1, forwardZ:  0, perpX:  0, perpZ: -1, yawRad: Math.PI / 2 };
  }
}

/**
 * Write the per-tile model matrix into `out` (16 floats,
 * column-major).  Builds a Y-yaw rotation, an optional +π/2 Y
 * rotation for riichi tiles, and a translation to the grid cell.
 * Allocation-free — the caller owns `out`.
 */
function writeDiscardMatrix(
  out: Float32Array,
  axes: SeatAxes,
  colOffset: number,
  rowOffset: number,
  isRiichi: boolean,
): void {
  const yaw = axes.yawRad + (isRiichi ? Math.PI / 2 : 0);
  const c = Math.cos(yaw);
  const s = Math.sin(yaw);

  out[0] = c;  out[1] = 0;  out[2] = -s; out[3] = 0;
  out[4] = 0;  out[5] = 1;  out[6] = 0;  out[7] = 0;
  out[8] = s;  out[9] = 0;  out[10] = c; out[11] = 0;

  // World-space position: grid cell projected onto the seat's
  // forward + perpendicular axes.  `rowOffset` controls distance
  // from the table centre, `colOffset` controls lateral position.
  out[12] = axes.forwardX * rowOffset + axes.perpX * colOffset;
  out[13] = TILE_DEPTH * 0.5;  // tiles rest flat on the table
  out[14] = axes.forwardZ * rowOffset + axes.perpZ * colOffset;
  out[15] = 1;
}
