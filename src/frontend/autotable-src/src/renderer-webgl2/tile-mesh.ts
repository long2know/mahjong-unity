// Phase K Wave 16 — WebGL2 tile-mesh graph (Hicks).
//
// Phase L W2 spike implementation extension.  W15 (`hello.ts` +
// `index.ts`) proved a single textured quad renders into a
// `renderer-webgl2.<hash>.js` chunk for 6.2 KB.  This wave expands
// the foundation to a real **instanced tile mesh** — the geometry
// shape the production renderer needs for 144 mahjong tiles in a
// single draw call.
//
// What's HERE in W16:
//   • `TILE_GEOMETRY`         — one-tile reference geometry
//     (positions + UVs + normals + indices).  Shared by every
//     instance.
//   • `createTileMesh()`      — VAO + per-instance attribute
//     buffers (model matrix + tile-id) for instanced draw.
//   • `drawTileMesh()`        — single `drawElementsInstanced` call
//     that consumes the per-instance buffers.
//   • `TILE_INSTANCE_VS`      — vertex shader pulling per-instance
//     transform + atlas-tile-id attribute.
//   • `TILE_INSTANCE_FS`      — fragment shader sampling the
//     atlas (see `tile-atlas.ts`) by tile-id.
//   • `MAX_INSTANCES`         — 200, covering 144 wall tiles +
//     14 deal + 4 dora + 38 discard ceiling.
//
// What's NOT here (Phase L W3+):
//   • Tile-pickup animation graph (W3).
//   • Lighting model (W4).
//   • Picking / raycaster (W5).
//   • Outline + bloom post-fx (W6).
//
// Bundle math (W15 → W16 target, ≤ 22 KB total for
// `renderer-webgl2`):
//   • W15 hello-world baseline: 6,237 B.
//   • W16 budget for this module + tile-atlas + camera: ≤ ~15 KB
//     additional.  Each file is tight (no class hierarchies, no
//     three.js types, no Float32Array helpers we don't need).

import {
  type WebglNullable,
  identity4,
  scaleMatrix4,
  translateMatrix4,
} from './math';

// ── Tile aspect ratio ──────────────────────────────────────────────
//
// Mahjong tiles are 12 × 20 × 8 mm in physical units.  The Phase
// L scene uses unit-cube-ish dimensions where 1.0 = "one tile
// width" (X).  These ratios mirror the three-renderer's
// `tile.ts` constants so the visual-regression captures don't
// shift when the renderer-webgl2 path takes over.
export const TILE_WIDTH = 1.0;
export const TILE_HEIGHT = 20 / 12;
export const TILE_DEPTH = 8 / 12;

// ── Reference geometry: one box, instanced for every tile ─────────
//
// Box vertices: 24 unique positions (4 per face × 6 faces) so each
// face can carry its own UV without breaking vertex re-use.  The
// face layout in the atlas is:
//
//   front  (+z)  → tile face (the labelled side)
//   back   (−z)  → tile back (yellow / generic)
//   sides  (±x, ±y, ±z back) → tile side (cream solid colour)
//
// Index buffer: 12 triangles × 3 indices = 36 indices, CCW-wound
// when viewed from the +face direction.
export interface TileGeometry {
  positions: Float32Array;
  uvs: Float32Array;
  normals: Float32Array;
  indices: Uint16Array;
}

export function tileGeometry(): TileGeometry {
  const hw = TILE_WIDTH * 0.5;
  const hh = TILE_HEIGHT * 0.5;
  const hd = TILE_DEPTH * 0.5;
  // 24 positions × (x,y,z) = 72 floats.  Face order:
  // 0..3 front (+z), 4..7 back (−z), 8..11 right (+x),
  // 12..15 left (−x), 16..19 top (+y), 20..23 bottom (−y).
  const positions = new Float32Array([
    // front +z
    -hw, -hh,  hd,   hw, -hh,  hd,   hw,  hh,  hd,  -hw,  hh,  hd,
    // back −z
     hw, -hh, -hd,  -hw, -hh, -hd,  -hw,  hh, -hd,   hw,  hh, -hd,
    // right +x
     hw, -hh,  hd,   hw, -hh, -hd,   hw,  hh, -hd,   hw,  hh,  hd,
    // left −x
    -hw, -hh, -hd,  -hw, -hh,  hd,  -hw,  hh,  hd,  -hw,  hh, -hd,
    // top +y
    -hw,  hh,  hd,   hw,  hh,  hd,   hw,  hh, -hd,  -hw,  hh, -hd,
    // bottom −y
    -hw, -hh, -hd,   hw, -hh, -hd,   hw, -hh,  hd,  -hw, -hh,  hd,
  ]);

  // UV layout — 4 corners per face × (u, v) = 48 floats.  Face
  // index encoded as the integer part of `u`, atlas tile lookup
  // shifts UVs in the fragment shader by the per-instance tile id.
  // The face-id encoding lets one fragment shader handle all six
  // faces without branching:
  //   front  (face 0) → u ∈ [0, 1)
  //   back   (face 1) → u ∈ [1, 2)
  //   side   (faces 2-5, identical) → u ∈ [2, 3)
  const uvs = new Float32Array([
    // front (face 0)
    0.0, 0.0,  1.0, 0.0,  1.0, 1.0,  0.0, 1.0,
    // back  (face 1)
    1.0, 0.0,  2.0, 0.0,  2.0, 1.0,  1.0, 1.0,
    // right (face 2)
    2.0, 0.0,  3.0, 0.0,  3.0, 1.0,  2.0, 1.0,
    // left  (face 3, same atlas tile as right)
    2.0, 0.0,  3.0, 0.0,  3.0, 1.0,  2.0, 1.0,
    // top   (face 4)
    2.0, 0.0,  3.0, 0.0,  3.0, 1.0,  2.0, 1.0,
    // bottom (face 5)
    2.0, 0.0,  3.0, 0.0,  3.0, 1.0,  2.0, 1.0,
  ]);

  // Outward normals — one per face × 4 verts × (x,y,z) = 72 floats.
  const normals = new Float32Array([
    // +z
    0, 0, 1,   0, 0, 1,   0, 0, 1,   0, 0, 1,
    // -z
    0, 0, -1,  0, 0, -1,  0, 0, -1,  0, 0, -1,
    // +x
    1, 0, 0,   1, 0, 0,   1, 0, 0,   1, 0, 0,
    // -x
    -1, 0, 0,  -1, 0, 0,  -1, 0, 0,  -1, 0, 0,
    // +y
    0, 1, 0,   0, 1, 0,   0, 1, 0,   0, 1, 0,
    // -y
    0, -1, 0,  0, -1, 0,  0, -1, 0,  0, -1, 0,
  ]);

  // 12 triangles, CCW order viewed from outside the box.
  const indices = new Uint16Array([
    0,  1,  2,    0,  2,  3,    // front
    4,  5,  6,    4,  6,  7,    // back
    8,  9, 10,    8, 10, 11,    // right
   12, 13, 14,   12, 14, 15,    // left
   16, 17, 18,   16, 18, 19,    // top
   20, 21, 22,   20, 22, 23,    // bottom
  ]);

  return { positions, uvs, normals, indices };
}

// ── Instanced vertex shader ───────────────────────────────────────
//
// Per-vertex attributes:  a_position (vec3), a_uv (vec2), a_normal (vec3)
// Per-instance attribute: a_modelCol0..3 (4 × vec4 = mat4),
//                         a_tileId (float, points into the atlas)
// Uniforms:               u_viewProj (mat4), u_atlasGrid (vec2)
//
// We pass the model matrix as four vec4 attributes because WebGL2
// caps `vertexAttribDivisor`-eligible attributes at vec4; mat4 must
// be split into 4 columns and re-assembled in the shader.  Cost:
// 16 floats per instance vs. 4 floats for a quaternion — but the
// matrix form lets the future shear / non-uniform-scale animation
// graph (W3) drop straight in without a per-instance branch.

export const TILE_INSTANCE_VS = `#version 300 es
precision highp float;

in vec3 a_position;
in vec2 a_uv;
in vec3 a_normal;

in vec4 a_modelCol0;
in vec4 a_modelCol1;
in vec4 a_modelCol2;
in vec4 a_modelCol3;
in float a_tileId;

uniform mat4 u_viewProj;
uniform vec2 u_atlasGrid;

out vec2 v_uv;
out vec3 v_normal;
flat out float v_tileId;

void main() {
  mat4 model = mat4(a_modelCol0, a_modelCol1, a_modelCol2, a_modelCol3);
  gl_Position = u_viewProj * model * vec4(a_position, 1.0);
  v_uv = a_uv;
  v_normal = mat3(model) * a_normal;
  v_tileId = a_tileId;
}
`;

export const TILE_INSTANCE_FS = `#version 300 es
precision highp float;

in vec2 v_uv;
in vec3 v_normal;
flat in float v_tileId;

uniform sampler2D u_atlas;
uniform vec2 u_atlasGrid;
uniform vec3 u_lightDir;

out vec4 fragColor;

void main() {
  // Face id is the integer part of v_uv.x (0/1/2..).  Local UV
  // inside the face is the fractional part.
  float faceId = floor(v_uv.x);
  vec2 localUv = vec2(fract(v_uv.x), v_uv.y);

  // Atlas cell: tile id chooses a row, faceId (0 = front, 1 = back,
  // 2 = side) chooses a column inside the row.  The W16 atlas
  // stub uses a 3 × N grid so a tile sample is:
  //   atlasUv = (col + localUv.x, row + localUv.y) / u_atlasGrid
  float row = floor(v_tileId);
  float col = clamp(faceId, 0.0, 2.0);
  vec2 cell = vec2(col, row);
  vec2 atlasUv = (cell + localUv) / u_atlasGrid;

  vec4 base = texture(u_atlas, atlasUv);

  // Lambert dot — single directional light, mahjong tables don't
  // need anything fancier.  Half-Lambert wrap keeps the back faces
  // visible during edge-on dice rolls.
  vec3 n = normalize(v_normal);
  float wrap = 0.5 + 0.5 * dot(n, normalize(u_lightDir));
  fragColor = vec4(base.rgb * wrap, base.a);
}
`;

// ── Tile mesh runtime ─────────────────────────────────────────────
//
// Per-instance buffers:
//   • Model matrix    (16 floats × MAX_INSTANCES, 4 mat4-columns)
//   • Tile-id          (1 float × MAX_INSTANCES)
//
// Buffers are STREAM_DRAW because the animation graph (W3) will
// rewrite the model matrices every frame; the tile-id buffer is
// effectively static (a tile is dealt into a row + face id and
// rarely changes mid-game).

export const MAX_INSTANCES = 200;

const FLOATS_PER_INSTANCE_MATRIX = 16;
const BYTES_PER_FLOAT = 4;

export interface TileMesh {
  vao: WebGLVertexArrayObject;
  indexBuffer: WebGLBuffer;
  modelBuffer: WebGLBuffer;
  tileIdBuffer: WebGLBuffer;
  indexCount: number;
  capacity: number;
  modelData: Float32Array;
  tileIdData: Float32Array;
  /** Currently-uploaded instance count (≤ capacity). */
  instanceCount: number;
}

export function createTileMesh(
  gl: WebGL2RenderingContext,
  program: WebGLProgram,
): TileMesh {
  const geom = tileGeometry();

  const vao = mustCreate(gl.createVertexArray(), 'tile-mesh:createVertexArray');
  gl.bindVertexArray(vao);

  // Per-vertex buffers (shared across all instances).
  bindAttribBuffer(gl, program, 'a_position', geom.positions, 3, 0);
  bindAttribBuffer(gl, program, 'a_uv', geom.uvs, 2, 0);
  bindAttribBuffer(gl, program, 'a_normal', geom.normals, 3, 0);

  // Index buffer.
  const indexBuffer = mustCreate(gl.createBuffer(), 'tile-mesh:indexBuffer');
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, indexBuffer);
  gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, geom.indices, gl.STATIC_DRAW);

  // Per-instance model-matrix buffer (4 vec4 columns × MAX_INSTANCES).
  const modelData = new Float32Array(MAX_INSTANCES * FLOATS_PER_INSTANCE_MATRIX);
  const modelBuffer = mustCreate(gl.createBuffer(), 'tile-mesh:modelBuffer');
  gl.bindBuffer(gl.ARRAY_BUFFER, modelBuffer);
  gl.bufferData(gl.ARRAY_BUFFER, modelData.byteLength, gl.STREAM_DRAW);

  // Attach 4 vec4 attributes pointing into the same buffer at
  // 16-byte strides for each column.
  for (let col = 0; col < 4; col++) {
    const attrName = `a_modelCol${col}`;
    const loc = gl.getAttribLocation(program, attrName);
    if (loc < 0) {
      throw new Error(`[tile-mesh] attribute "${attrName}" not found in program`);
    }
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(
      loc,
      4,
      gl.FLOAT,
      false,
      FLOATS_PER_INSTANCE_MATRIX * BYTES_PER_FLOAT,
      col * 4 * BYTES_PER_FLOAT,
    );
    gl.vertexAttribDivisor(loc, 1);
  }

  // Per-instance tile-id buffer (1 float × MAX_INSTANCES).
  const tileIdData = new Float32Array(MAX_INSTANCES);
  const tileIdBuffer = mustCreate(gl.createBuffer(), 'tile-mesh:tileIdBuffer');
  gl.bindBuffer(gl.ARRAY_BUFFER, tileIdBuffer);
  gl.bufferData(gl.ARRAY_BUFFER, tileIdData.byteLength, gl.STREAM_DRAW);
  const tileIdLoc = gl.getAttribLocation(program, 'a_tileId');
  if (tileIdLoc < 0) {
    throw new Error('[tile-mesh] attribute "a_tileId" not found in program');
  }
  gl.enableVertexAttribArray(tileIdLoc);
  gl.vertexAttribPointer(tileIdLoc, 1, gl.FLOAT, false, BYTES_PER_FLOAT, 0);
  gl.vertexAttribDivisor(tileIdLoc, 1);

  gl.bindVertexArray(null);
  gl.bindBuffer(gl.ARRAY_BUFFER, null);
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, null);

  return {
    vao,
    indexBuffer,
    modelBuffer,
    tileIdBuffer,
    indexCount: geom.indices.length,
    capacity: MAX_INSTANCES,
    modelData,
    tileIdData,
    instanceCount: 0,
  };
}

/** Write one instance's model matrix + tile id into the local CPU buffers. */
export function setTileInstance(
  mesh: TileMesh,
  index: number,
  modelMatrix: Float32Array,
  tileId: number,
): void {
  if (index < 0 || index >= mesh.capacity) {
    throw new Error(`[tile-mesh] instance index ${index} out of range [0, ${mesh.capacity})`);
  }
  if (modelMatrix.length !== 16) {
    throw new Error(`[tile-mesh] expected 16-float matrix, got ${modelMatrix.length}`);
  }
  mesh.modelData.set(modelMatrix, index * FLOATS_PER_INSTANCE_MATRIX);
  mesh.tileIdData[index] = tileId;
  if (index + 1 > mesh.instanceCount) mesh.instanceCount = index + 1;
}

/** Upload the local CPU buffers to the GPU. Call after `setTileInstance` batch. */
export function uploadTileInstances(gl: WebGL2RenderingContext, mesh: TileMesh): void {
  gl.bindBuffer(gl.ARRAY_BUFFER, mesh.modelBuffer);
  gl.bufferSubData(gl.ARRAY_BUFFER, 0, mesh.modelData);
  gl.bindBuffer(gl.ARRAY_BUFFER, mesh.tileIdBuffer);
  gl.bufferSubData(gl.ARRAY_BUFFER, 0, mesh.tileIdData);
  gl.bindBuffer(gl.ARRAY_BUFFER, null);
}

/** Single `drawElementsInstanced` call for all uploaded instances. */
export function drawTileMesh(gl: WebGL2RenderingContext, mesh: TileMesh): void {
  if (mesh.instanceCount === 0) return;
  gl.bindVertexArray(mesh.vao);
  gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, mesh.indexBuffer);
  gl.drawElementsInstanced(
    gl.TRIANGLES,
    mesh.indexCount,
    gl.UNSIGNED_SHORT,
    0,
    mesh.instanceCount,
  );
  gl.bindVertexArray(null);
}

/** Release every GL handle owned by the mesh. */
export function disposeTileMesh(gl: WebGL2RenderingContext, mesh: TileMesh): void {
  gl.deleteBuffer(mesh.indexBuffer);
  gl.deleteBuffer(mesh.modelBuffer);
  gl.deleteBuffer(mesh.tileIdBuffer);
  gl.deleteVertexArray(mesh.vao);
  mesh.instanceCount = 0;
}

// ── Demo helper: place 144 wall tiles in a 4 × 36 ring ────────────
//
// Mahjong wall layout: 4 sides × 18 stacks × 2 tiles = 144.  We
// flatten to a 4-row × 36-column grid for the W16 smoke render.
// Real-game placement (corner stacks, dora indicator, dead-wall
// split) lands in W3+ with the gameplay state hookup.
export function populateWallDemo(mesh: TileMesh): void {
  let i = 0;
  for (let row = 0; row < 4; row++) {
    for (let col = 0; col < 36; col++) {
      if (i >= mesh.capacity) break;
      const xOffset = (col - 17.5) * (TILE_WIDTH + 0.02);
      const zOffset = (row - 1.5) * (TILE_DEPTH + 0.02) - 6.0;
      const m = identity4();
      scaleMatrix4(m, 1.0, 1.0, 1.0);
      translateMatrix4(m, xOffset, 0, zOffset);
      // Cycle tile ids 0..33 (mahjong has 34 distinct tile faces).
      setTileInstance(mesh, i, m, i % 34);
      i++;
    }
  }
}

// ── Internal helpers ──────────────────────────────────────────────

function bindAttribBuffer(
  gl: WebGL2RenderingContext,
  program: WebGLProgram,
  name: string,
  data: Float32Array,
  components: number,
  divisor: number,
): WebGLBuffer {
  const buf = mustCreate(gl.createBuffer(), `tile-mesh:${name} buffer`);
  gl.bindBuffer(gl.ARRAY_BUFFER, buf);
  gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW);
  const loc = gl.getAttribLocation(program, name);
  if (loc < 0) {
    throw new Error(`[tile-mesh] attribute "${name}" not found in program`);
  }
  gl.enableVertexAttribArray(loc);
  gl.vertexAttribPointer(loc, components, gl.FLOAT, false, 0, 0);
  if (divisor !== 0) gl.vertexAttribDivisor(loc, divisor);
  return buf;
}

function mustCreate<T>(handle: WebglNullable<T>, label: string): T {
  if (handle === null) throw new Error(`[tile-mesh] ${label} returned null`);
  return handle;
}
