// Phase K Wave 16 — WebGL2 tile-atlas loader (Hicks).
//
// Phase L W2 spike scaffolding (W16) + W17 production-grade loader +
// W18 face-catalogue wiring.  The production renderer-webgl2 path
// needs a single atlas image that packs every distinct tile face
// (34 mahjong faces × 3 face categories — front / back / side) into
// a 3 × 34 grid so the instanced tile mesh can sample by (faceId,
// tileId) in `TILE_INSTANCE_FS` without branching.
//
// What's HERE (W16 + W17 + W18):
//   • `loadTileAtlas()` — fetch + decode an HTMLImageElement from
//     a URL (default: the production tile atlas asset path).
//   • `createTileAtlasTexture()` — upload the loaded image into a
//     WebGL2 texture with the canonical filter / wrap params.
//   • `buildFallbackAtlas()` — synthesize a 3 × 34 cell test pattern
//     into an offscreen canvas so the smoke render produces a visible
//     image even when the canonical asset is missing on disk (e.g.
//     during dev when a wave skips the generator step).
//   • `acquireTileAtlas()` — end-to-end: try the canonical URL,
//     fall back to the synth pattern, return a `TileAtlas` carrying
//     both the GL texture and the grid metadata.
//
// W18 status:
//   • The atlas image is now loaded via `Image` element + WebGL
//     `texImage2D` at startup — the W17 PNG generator commits
//     a 192 × 2176 px canonical asset that the W18 face-catalogue
//     (`./tile-faces.ts`) maps to suit/value tuples.  Together
//     these complete the Phase L "render 144 tiles with correct
//     face textures" deliverable.
//   • The shader (`tile-mesh.ts:TILE_INSTANCE_FS`) samples the
//     atlas at `(faceCol + localUv) / (gridCols, gridRows)`,
//     mirrored by the host-side `atlasUvForTile()` helper for
//     callers that need raw UVs outside the shader path.
//
// What's NOT here (Phase L W5+):
//   • Blender-rendered tile-face source (W5 — currently the front
//     column shows a hue-shifted cell + bitmap-font tile-id label).
//   • Mipmap generation tuned for the atlas (W5 — currently we let
//     `generateMipmap` do the default thing).
//   • KTX2 / Basis compression (W6).

export const TILE_ATLAS_GRID_COLS = 3;   // front / back / side
export const TILE_ATLAS_GRID_ROWS = 34;  // 34 distinct mahjong faces
export const TILE_ATLAS_CELL_PX = 64;
export const TILE_ATLAS_URL_DEFAULT = '/img/tiles-atlas-webgl2.auto.png';

export interface TileAtlas {
  texture: WebGLTexture;
  gridCols: number;
  gridRows: number;
  /** Source pixel dimensions of the uploaded atlas image. */
  width: number;
  height: number;
  /** True when the canonical asset failed to load and the fallback synth was used. */
  fallback: boolean;
}

/**
 * Fetch the canonical tile atlas from `/img/tiles-atlas-webgl2.auto.png`
 * (default URL; override via `url`).  Returns null on any decode or
 * network failure so the caller can synthesize a fallback.
 */
export async function loadTileAtlas(url: string = TILE_ATLAS_URL_DEFAULT): Promise<HTMLImageElement | null> {
  return new Promise((resolve) => {
    const img = new Image();
    img.crossOrigin = 'anonymous';
    img.onload = (): void => resolve(img);
    img.onerror = (): void => resolve(null);
    img.src = url;
  });
}

/**
 * Synthesize a 3 × 34 cell fallback atlas into an offscreen canvas.
 * Each cell carries the tile-id number painted on a hue-shifted
 * background so the smoke render produces visibly distinct tiles
 * even when the real asset isn't on disk yet.
 */
export function buildFallbackAtlas(): HTMLCanvasElement {
  const cell = TILE_ATLAS_CELL_PX;
  const w = TILE_ATLAS_GRID_COLS * cell;
  const h = TILE_ATLAS_GRID_ROWS * cell;
  const canvas = document.createElement('canvas');
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext('2d');
  if (ctx === null) return canvas;
  for (let row = 0; row < TILE_ATLAS_GRID_ROWS; row++) {
    for (let col = 0; col < TILE_ATLAS_GRID_COLS; col++) {
      const x = col * cell;
      const y = row * cell;
      const hue = (row * 11) % 360;
      const sat = col === 0 ? 70 : col === 1 ? 30 : 18;
      const light = col === 2 ? 78 : 60;
      ctx.fillStyle = `hsl(${hue}, ${sat}%, ${light}%)`;
      ctx.fillRect(x, y, cell, cell);
      // Cell borders so the atlas grid is visible in the smoke
      // render.
      ctx.strokeStyle = 'rgba(0,0,0,0.45)';
      ctx.lineWidth = 1;
      ctx.strokeRect(x + 0.5, y + 0.5, cell - 1, cell - 1);
      // Per-cell label: row index (= tile id) only on the front
      // column so the back / side columns stay readable.
      if (col === 0) {
        ctx.fillStyle = '#101010';
        ctx.font = `bold ${Math.floor(cell * 0.45)}px system-ui, sans-serif`;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(String(row), x + cell / 2, y + cell / 2);
      }
    }
  }
  return canvas;
}

/**
 * Upload an `HTMLImageElement` (or `HTMLCanvasElement`) as the tile
 * atlas texture.  Configured for the W16 instanced tile-mesh:
 *   • mag filter: LINEAR (smooth edge between cells)
 *   • min filter: LINEAR_MIPMAP_LINEAR (mipmaps generated below)
 *   • wrap: CLAMP_TO_EDGE on both axes — the atlas is a sealed
 *     grid; wrapping would sample foreign cells at the boundary.
 */
export function createTileAtlasTexture(
  gl: WebGL2RenderingContext,
  source: TexImageSource,
): WebGLTexture {
  const tex = gl.createTexture();
  if (tex === null) throw new Error('[tile-atlas] gl.createTexture() returned null');
  gl.bindTexture(gl.TEXTURE_2D, tex);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
  gl.generateMipmap(gl.TEXTURE_2D);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  gl.bindTexture(gl.TEXTURE_2D, null);
  return tex;
}

/**
 * End-to-end atlas acquisition: try the canonical URL first, fall
 * back to the synthesized cell-grid pattern on any failure.  Returns
 * a `TileAtlas` carrying both the GL texture and the grid metadata
 * the tile-mesh shader needs.
 */
export async function acquireTileAtlas(
  gl: WebGL2RenderingContext,
  url: string = TILE_ATLAS_URL_DEFAULT,
): Promise<TileAtlas> {
  const img = await loadTileAtlas(url);
  if (img !== null) {
    return {
      texture: createTileAtlasTexture(gl, img),
      gridCols: TILE_ATLAS_GRID_COLS,
      gridRows: TILE_ATLAS_GRID_ROWS,
      width: img.naturalWidth,
      height: img.naturalHeight,
      fallback: false,
    };
  }
  const synth = buildFallbackAtlas();
  return {
    texture: createTileAtlasTexture(gl, synth),
    gridCols: TILE_ATLAS_GRID_COLS,
    gridRows: TILE_ATLAS_GRID_ROWS,
    width: synth.width,
    height: synth.height,
    fallback: true,
  };
}

/** Release the GL texture handle owned by the atlas. */
export function disposeTileAtlas(gl: WebGL2RenderingContext, atlas: TileAtlas): void {
  gl.deleteTexture(atlas.texture);
}
