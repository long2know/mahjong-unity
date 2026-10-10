// Phase K Wave 18 — WebGL2 tile-face catalogue (Hicks, Frontend).
//
// Phase L W4 atlas-wiring deliverable.  W16/W17 stood up the
// `tile-atlas.ts` loader + the `tile-mesh.ts` shader pipeline; W18
// formalises the **suit / value catalogue** that maps the 34
// canonical mahjong tile faces onto the canonical atlas row index
// (0..33).  Together with the W17 PNG loader this completes the
// "render 144 tiles with correct face textures" Phase L milestone:
//
//   • Row 0..8   man  (萬) 1..9
//   • Row 9..17  pin  (筒) 1..9
//   • Row 18..26 sou  (索) 1..9
//   • Row 27..30 winds:  east / south / west / north (東南西北)
//   • Row 31..33 dragons: white / green / red (白發中)
//
// The atlas image (`/img/tiles-atlas-webgl2.auto.png`) stores each
// face at column 0 (FRONT) / column 1 (BACK) / column 2 (SIDE) of
// the 3 × 34 grid.  The W17 `acquireTileAtlas()` loader already
// uploads the texture; this module documents the row mapping so
// downstream renderer consumers (Phase L production renderer, the
// W18 mountScene demo, future picking code) can render real hands
// without re-deriving the contract.
//
// UV math (used by `atlasUvForTile`, mirroring the
// `TILE_INSTANCE_FS` fragment shader at `tile-mesh.ts`):
//
//   atlasUv = (col + localUv.x, row + localUv.y) / (gridCols, gridRows)
//   where row = tile-id (0..33), col = faceId (0=front, 1=back, 2=side).

import {
  TILE_ATLAS_GRID_COLS,
  TILE_ATLAS_GRID_ROWS,
} from './tile-atlas';

export type TileSuit = 'man' | 'pin' | 'sou' | 'wind' | 'dragon';

export interface TileFace {
  /** Canonical atlas row index, also the tile-id used by the
   *  instanced shader's `a_tileId` attribute. */
  id: number;
  suit: TileSuit;
  /** Numeric value: 1..9 for man/pin/sou; 1..4 for winds (E/S/W/N);
   *  1..3 for dragons (white/green/red). */
  value: number;
  /** Short ASCII label suitable for UI status text + log lines. */
  label: string;
  /** Unicode glyph from the U+1F000 mahjong block, when one exists. */
  glyph: string;
}

/**
 * The W18 face catalogue.  Indexed 0..33; the array index IS the
 * canonical tile-id consumed by `setTileInstance(...tileId)`.
 */
export const TILE_FACES: ReadonlyArray<TileFace> = (() => {
  const out: TileFace[] = [];
  // Man (1..9).
  for (let v = 1; v <= 9; v++) {
    out.push({
      id: out.length, suit: 'man', value: v,
      label: `m${v}`,
      // U+1F007 + (v-1) covers the man (萬) range.
      glyph: String.fromCodePoint(0x1F007 + (v - 1)),
    });
  }
  // Pin (1..9).
  for (let v = 1; v <= 9; v++) {
    out.push({
      id: out.length, suit: 'pin', value: v,
      label: `p${v}`,
      // U+1F019 + (v-1) covers the pin (筒/dots) range.
      glyph: String.fromCodePoint(0x1F019 + (v - 1)),
    });
  }
  // Sou (1..9).
  for (let v = 1; v <= 9; v++) {
    out.push({
      id: out.length, suit: 'sou', value: v,
      label: `s${v}`,
      // U+1F010 + (v-1) covers the sou (索/bams) range.
      glyph: String.fromCodePoint(0x1F010 + (v - 1)),
    });
  }
  // Winds: East / South / West / North.
  const windLabels = ['Ew', 'Sw', 'Ww', 'Nw'];
  const windGlyphs = ['\u{1F000}', '\u{1F001}', '\u{1F002}', '\u{1F003}'];
  for (let v = 1; v <= 4; v++) {
    out.push({
      id: out.length, suit: 'wind', value: v,
      label: windLabels[v - 1],
      glyph: windGlyphs[v - 1],
    });
  }
  // Dragons: White / Green / Red.
  const dragonLabels = ['Wd', 'Gd', 'Rd'];
  const dragonGlyphs = ['\u{1F006}', '\u{1F005}', '\u{1F004}'];
  for (let v = 1; v <= 3; v++) {
    out.push({
      id: out.length, suit: 'dragon', value: v,
      label: dragonLabels[v - 1],
      glyph: dragonGlyphs[v - 1],
    });
  }
  return Object.freeze(out);
})();

/** Total distinct tile faces (34 — the canonical mahjong set). */
export const TILE_FACE_COUNT = TILE_FACES.length;

/** Resolve a tile-id (0..33) to its face descriptor.  Returns
 *  null for out-of-range ids so callers can gracefully fall back. */
export function tileFace(id: number): TileFace | null {
  if (!Number.isInteger(id) || id < 0 || id >= TILE_FACES.length) return null;
  return TILE_FACES[id];
}

/**
 * Compute the atlas UV for a given tile id + face column (0=front,
 * 1=back, 2=side) at a normalised local coordinate `(u, v)` in
 * `[0, 1]²`.  Used by Phase L picking / debug overlays that need
 * raw UVs outside the shader path (the shader's
 * `TILE_INSTANCE_FS` re-derives this inline).
 *
 * Returns `[atlasU, atlasV]` in `[0, 1]²`, sampled with the
 * canonical 3 × 34 grid dimensions from `tile-atlas.ts`.
 */
export function atlasUvForTile(
  tileId: number,
  faceCol: 0 | 1 | 2,
  localU: number,
  localV: number,
): [number, number] {
  const row = Math.max(0, Math.min(TILE_ATLAS_GRID_ROWS - 1, tileId | 0));
  const col = Math.max(0, Math.min(TILE_ATLAS_GRID_COLS - 1, faceCol | 0));
  const u = (col + Math.max(0, Math.min(1, localU))) / TILE_ATLAS_GRID_COLS;
  const v = (row + Math.max(0, Math.min(1, localV))) / TILE_ATLAS_GRID_ROWS;
  return [u, v];
}

/**
 * The canonical 144-tile mahjong wall (4 copies of each face × 34
 * faces = 136 + 8 flower / season placeholders → 136 in this
 * stripped-down build; flowers / seasons land in Phase L W5).
 * Returns a `Uint8Array` of tile-ids suitable for `setTileInstance`
 * batching.
 */
export function canonicalWallTileIds(): Uint8Array {
  const out = new Uint8Array(TILE_FACE_COUNT * 4);
  let i = 0;
  for (let copy = 0; copy < 4; copy++) {
    for (let id = 0; id < TILE_FACE_COUNT; id++) {
      out[i++] = id;
    }
  }
  return out;
}
