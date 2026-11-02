// Phase K Wave 19 — WebGL2 wall geometry (Hicks, Frontend).
//
// Phase L W4 spike — canonical Changsha wall layout consumed by the
// `TileMesh` instance buffers (see `./tile-mesh.ts`).  W18 atlas-wired
// the 144 distinct tile faces; W19 builds the geometric layout the
// production renderer needs to place those 144 tiles into the four
// walls + dora indicator that bracket the table at game-start.
//
// Canonical Changsha wall layout:
//
//   • 4 walls, one in front of each seat.
//   • Each wall is 18 stacks × 2 tiles tall (= 36 tiles per wall).
//   • Stacks sit edge-to-edge so the wall reads as one continuous
//     ribbon; the stack pitch is `TILE_WIDTH + WALL_GAP` (~ 1.02 in
//     scene units).
//   • The four walls form a square ~ 18.4 × 18.4 scene units, centred
//     at the origin (the table's centre).  Each wall is rotated 0° /
//     90° / 180° / 270° around the +Y axis so the labelled face
//     always points OUTWARD from the table centre.
//   • Top-of-stack tile rests on top of the bottom-of-stack tile —
//     a Y-translation of TILE_DEPTH (the tile's "thickness" axis) per
//     stack level.
//
// Numbers come from the three-renderer reference path
// (`src/three-renderer.ts` legacy + `src/world.ts` wall placement
// constants), preserved here so the renderer-webgl2 visual-regression
// captures don't shift under the W4 migration.

import {
  type TileMesh,
  TILE_DEPTH,
  TILE_HEIGHT,
  TILE_WIDTH,
  setTileInstance,
} from './tile-mesh';
import { identity4, rotateYMatrix4, translateMatrix4 } from './math';

// ── Wall layout constants ─────────────────────────────────────────

/** Stacks per wall — canonical Changsha builds use 18. */
export const STACKS_PER_WALL = 18;

/** Tiles per stack — Changsha uses 2 (deep wall). */
export const TILES_PER_STACK = 2;

/** Walls — one per seat. */
export const WALL_COUNT = 4;

/** Total tiles in the wall (4 × 18 × 2 = 144). */
export const WALL_TILE_COUNT = WALL_COUNT * STACKS_PER_WALL * TILES_PER_STACK;

/** Small visual gap between stacks (scene units). */
export const WALL_GAP = 0.02;

/** Per-stack horizontal pitch (scene units). */
export const STACK_PITCH = TILE_WIDTH + WALL_GAP;

/**
 * Distance from the table centre to the wall's inner edge.  Computed
 * so the four walls form a closed square (a stack at one wall's far
 * end butts up against the perpendicular wall's near end).
 *
 *   half_wall_length = STACKS_PER_WALL * STACK_PITCH / 2
 *                    ≈ 9.18 scene units
 *
 * The wall sits at radius `half_wall_length + TILE_DEPTH/2` so the
 * outer face of each wall is at `half_wall_length + TILE_DEPTH`
 * (the rear of the tile cuboid).  This matches the three-renderer
 * camera-orbit radius defaults so the Phase L wall renders inside
 * the camera frustum at the W16 orbital defaults.
 */
export const WALL_HALF_LENGTH = (STACKS_PER_WALL * STACK_PITCH) / 2;
export const WALL_OFFSET_FROM_CENTRE = WALL_HALF_LENGTH + TILE_DEPTH * 0.5;

/**
 * Floor Y for the bottom of the first-level stack.  The tile's local
 * origin is the cuboid centre, so the bottom of the first stack sits
 * at `floorY - TILE_DEPTH/2` and the labelled face is at `floorY`.
 *
 * The canonical Changsha table surface is the renderer's y = 0 plane;
 * the wall sits ON the table (no inset).
 */
export const FLOOR_Y = TILE_DEPTH * 0.5;

// ── Tile-id assignment ────────────────────────────────────────────
//
// A canonical Changsha set has 144 tiles drawn from 34 distinct
// faces:  9 wan + 9 tong + 9 sou + 4 winds + 3 dragons + 8 flowers
// (one of each flower/season).  In atlas index space the lower 27
// rows are the suited tiles and rows 27–33 are the honour tiles;
// rows 34–41 are flowers (the W17 atlas generator pads the cell
// grid to a power of 2 for GPU friendliness).
//
// W19 uses a deterministic shuffle (linear-congruential generator
// seeded by `seed`) so the W19 visual-parity smoke test produces a
// reproducible pixel-diff against the baseline.  Real-game wall
// placement (post-shuffle Bishop deal) lands in Phase L W5.

const SUITED_FACE_COUNT = 27;          // wan + tong + sou
const HONOUR_FACE_COUNT = 7;            // 4 winds + 3 dragons
const FLOWER_FACE_COUNT = 8;            // 4 flowers + 4 seasons
const SUITED_COPIES = 4;                // 4 of each suited
const HONOUR_COPIES = 4;                // 4 of each honour
const FLOWER_COPIES = 1;                // 1 of each flower / season
const CANONICAL_TILE_COUNT =
  SUITED_FACE_COUNT * SUITED_COPIES
  + HONOUR_FACE_COUNT * HONOUR_COPIES
  + FLOWER_FACE_COUNT * FLOWER_COPIES;  // = 27*4 + 7*4 + 8*1 = 144

/** Build the canonical 144-tile face-id list (pre-shuffle). */
export function canonicalTileIds(): number[] {
  const ids: number[] = [];
  for (let face = 0; face < SUITED_FACE_COUNT; face++) {
    for (let copy = 0; copy < SUITED_COPIES; copy++) ids.push(face);
  }
  for (let face = 0; face < HONOUR_FACE_COUNT; face++) {
    const atlasRow = SUITED_FACE_COUNT + face;
    for (let copy = 0; copy < HONOUR_COPIES; copy++) ids.push(atlasRow);
  }
  for (let face = 0; face < FLOWER_FACE_COUNT; face++) {
    const atlasRow = SUITED_FACE_COUNT + HONOUR_FACE_COUNT + face;
    for (let copy = 0; copy < FLOWER_COPIES; copy++) ids.push(atlasRow);
  }
  return ids;
}

/** Deterministic linear-congruential shuffle (Fisher-Yates + LCG). */
export function shuffleTileIds(ids: number[], seed: number): number[] {
  const out = ids.slice();
  let s = (seed | 0) >>> 0;
  if (s === 0) s = 0x9e3779b9;
  for (let i = out.length - 1; i > 0; i--) {
    // glibc LCG constants — visible-state shuffle, not crypto.
    s = ((Math.imul(s, 1103515245) + 12345) >>> 0) & 0x7fffffff;
    const j = s % (i + 1);
    const tmp = out[i];
    out[i] = out[j];
    out[j] = tmp;
  }
  return out;
}

// ── Per-tile placement ────────────────────────────────────────────

export interface WallTileSlot {
  /** Wall index 0..3 (= seat index, +x / +z / -x / -z direction). */
  wall: number;
  /** Stack index 0..STACKS_PER_WALL-1 (left → right along the wall). */
  stack: number;
  /** Level 0 = bottom of stack, 1 = top. */
  level: number;
  /** Linear index 0..WALL_TILE_COUNT-1 — useful for atlas lookups. */
  linearIndex: number;
}

/**
 * Iterate every (wall, stack, level) triple in canonical order.
 * Iteration order: wall-major, then stack, then level — matches the
 * three-renderer reference walk so visual-regression captures align.
 */
export function* iterateWallSlots(): Generator<WallTileSlot, void, void> {
  let i = 0;
  for (let wall = 0; wall < WALL_COUNT; wall++) {
    for (let stack = 0; stack < STACKS_PER_WALL; stack++) {
      for (let level = 0; level < TILES_PER_STACK; level++) {
        yield { wall, stack, level, linearIndex: i };
        i++;
      }
    }
  }
}

/**
 * Build the column-major 4×4 model matrix for a single wall slot.
 * The tile is oriented face-up (labelled face on +y) for the wall
 * build phase; Phase L W5 rotates tiles face-down once a hand is in
 * progress.
 *
 * Algorithm:
 *   1. Identity.
 *   2. Translate to slot's local position within the wall (x =
 *      stack offset from wall centre, y = floor + level * depth).
 *      Wall-0 is the "south" wall (closer to the camera in the
 *      canonical orbital view); its tiles run along the local +x
 *      axis and the wall sits at z = -WALL_OFFSET_FROM_CENTRE.
 *   3. Rotate around +Y by `wall * 90°` so wall-N points outward
 *      from the table centre toward seat N.
 *
 * The output matrix multiplies an a-position vec3 from tile-space
 * (centred on origin, face on +z) into world space.
 */
export function wallTileMatrix(slot: WallTileSlot): Float32Array {
  const m = identity4();

  // Tile-x within the wall:  stacks 0..N-1 → -half … +half along +x.
  const localX = (slot.stack - (STACKS_PER_WALL - 1) / 2) * STACK_PITCH;
  // Tile-y: floor + level lift (each level lifts by TILE_DEPTH so
  // the stacks rest on each other).
  const localY = FLOOR_Y + slot.level * TILE_DEPTH;
  // Tile-z: walls run along ±x in their local frame, then rotate to
  // their world orientation.  Place the wall at +z = WALL_OFFSET so
  // after the wall-rotation the "south" wall (wall=0) lands at
  // -z.  Then rotate by `wall * 90°` to place subsequent walls.
  const localZ = -WALL_OFFSET_FROM_CENTRE;

  translateMatrix4(m, localX, localY, localZ);
  rotateYMatrix4(m, slot.wall * (Math.PI / 2));

  // Rotate the tile flat — face-up = labelled side on +y.  The tile
  // mesh's reference geometry has the labelled face on +z, so a +90°
  // rotation around +x flips it to +y (face-up).  We bake that into
  // the model matrix here so the tile-mesh shader doesn't need a
  // per-instance face-up flag.
  //
  // Implementation note: instead of allocating a second matrix and
  // multiplying, we directly compose the rotation by adjusting the
  // matrix entries — `rotateX(π/2)` swaps the y and z columns and
  // negates the new z column.  We do it manually because the math
  // module doesn't ship a generic `rotateX` (the cost is one helper
  // here instead of a public surface there).
  rotateXLocalQuarter(m);

  return m;
}

/** In-place rotate around +X by π/2 (face-up flip). */
function rotateXLocalQuarter(m: Float32Array): void {
  // R_x(π/2):
  //   |1  0  0|
  //   |0  0 -1|
  //   |0  1  0|
  // Right-multiply m × R: new columns are
  //   col0 = col0 (unchanged)
  //   col1 = -col2   (NB: column 1 picks up -col2)
  //   col2 =  col1
  const c10 = m[4], c11 = m[5], c12 = m[6], c13 = m[7];
  const c20 = m[8], c21 = m[9], c22 = m[10], c23 = m[11];
  m[4] = -c20; m[5] = -c21; m[6] = -c22; m[7] = -c23;
  m[8] = c10; m[9] = c11; m[10] = c12; m[11] = c13;
}

// ── High-level populate helpers ───────────────────────────────────

/**
 * Replace the wall-demo placeholder in `tile-mesh.ts` with a
 * canonical 4 × 18 × 2 wall layout.  Tile ids draw from the
 * canonical 144-set with a deterministic shuffle so tests can
 * pixel-diff against a stable baseline.
 *
 * Calls `setTileInstance()` 144 times; the caller is expected to
 * call `uploadTileInstances()` afterward (or rely on a `TileScene`
 * to schedule the upload on the next frame).
 */
export function populateCanonicalWall(mesh: TileMesh, seed: number = 0x9e3779b9): void {
  const ids = shuffleTileIds(canonicalTileIds(), seed);
  let i = 0;
  for (const slot of iterateWallSlots()) {
    if (i >= mesh.capacity) break;
    const m = wallTileMatrix(slot);
    const tileId = ids[i % ids.length];
    setTileInstance(mesh, i, m, tileId);
    i++;
  }
}

/**
 * Convenience: place the canonical wall AND the dora indicator
 * (one face-up tile at the front of wall-0, level 1).  Used by the
 * `?renderer=webgl2-wall` smoke mode to show the full board state at
 * a glance.
 */
export function populateWallWithDora(mesh: TileMesh, seed: number = 0x9e3779b9): void {
  populateCanonicalWall(mesh, seed);
  // Dora indicator sits at wall 0, stack 3, level 1 (the canonical
  // Changsha position 6 tiles in from the right end of the dealer's
  // wall).  We flip its model matrix's face-up rotation back so the
  // labelled face points to the dealer (-z direction in world space).
  const doraSlot: WallTileSlot = { wall: 0, stack: 3, level: 1, linearIndex: 7 };
  const doraIndex = doraSlotLinearIndex(doraSlot);
  if (doraIndex < mesh.capacity) {
    // Re-use the canonical matrix but offset slightly above the wall
    // so the dora sits clearly on top of the second-level stack.
    const m = wallTileMatrix(doraSlot);
    translateMatrix4(m, 0, TILE_HEIGHT * 0.05, 0);
    // Tile id 0 (1-wan) as a placeholder dora face for the smoke.
    setTileInstance(mesh, doraIndex, m, 0);
  }
}

function doraSlotLinearIndex(slot: WallTileSlot): number {
  return (slot.wall * STACKS_PER_WALL * TILES_PER_STACK)
    + (slot.stack * TILES_PER_STACK)
    + slot.level;
}

// ── Diagnostic helper ─────────────────────────────────────────────

/**
 * Returns the world-space (x, y, z) of the slot's tile centre.
 * Used by the visual-parity test to compare the renderer-webgl2
 * placement against the three-renderer reference (legacy
 * `World.placeWallTile()` results, captured in a stable snapshot).
 */
export function wallSlotCentre(slot: WallTileSlot): [number, number, number] {
  const m = wallTileMatrix(slot);
  // The centre is the matrix's translation column (column 3).
  return [m[12], m[13], m[14]];
}

/** Total tile count this module assumes (= 144). */
export const CANONICAL_WALL_TILE_COUNT = CANONICAL_TILE_COUNT;
