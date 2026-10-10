// Phase K Wave 17 — WebGL2 tile picking (Hicks, Frontend).
//
// Phase L W3 spike — ray-cast a screen-space mouse pointer against
// every tile instance in a `TileMesh` and return the hit instance
// index.  The three-renderer path uses `three.Raycaster` against
// the InstancedMesh + tile bounding boxes; this module ports the
// minimum we need so the renderer-webgl2 path doesn't drag three.js
// in just for pick-and-place.
//
// Pick algorithm:
//   1. Build a world-space ray from the mouse pointer via the inverse
//      view-projection matrix (well-known "unproject" trick).
//   2. For each active instance, transform the ray into the
//      instance's local space via the inverse model matrix.
//   3. Slab-test against the canonical tile AABB
//      ([-w/2..w/2] × [-h/2..h/2] × [-d/2..d/2]).
//   4. Track the closest positive-t hit; ties broken by lower
//      instance index (deterministic for snapshot tests).
//
// What's HERE in W17:
//   • `pickTile()`           — main entry, returns the hit instance
//     index + world-space hit point or `null` on miss.
//   • `buildPickRay()`       — converts canvas-relative mouse XY to a
//     world-space ray (origin + direction).
//   • `invertMatrix4()`      — minimal 4×4 inverse helper (we can't
//     borrow the three.js one).
//
// What's NOT here (Phase L W4+):
//   • Per-instance bounding box override (W4 — non-tile picks).
//   • Picking-shader fast path for >200 instances (W6 if measurable).
//   • Touch-friendly hit thickening (W5 mobile UX pass).

import {
  TILE_DEPTH,
  TILE_HEIGHT,
  TILE_WIDTH,
  type TileMesh,
} from './tile-mesh';

const FLOATS_PER_INSTANCE_MATRIX = 16;

export interface PickRay {
  origin: [number, number, number];
  direction: [number, number, number];
}

export interface PickHit {
  instanceIndex: number;
  /** World-space point of the hit (origin + direction × t). */
  point: [number, number, number];
  /** Ray-parameter `t` at the hit (front-face distance from camera). */
  t: number;
}

/**
 * Convert a canvas-relative mouse position into a world-space ray
 * via the inverse view-projection matrix.  `canvas.clientWidth/Height`
 * (CSS pixels) drive the normalised-device-coordinate map so the ray
 * is independent of devicePixelRatio.
 */
export function buildPickRay(
  canvas: HTMLCanvasElement,
  clientX: number,
  clientY: number,
  viewProj: Float32Array,
): PickRay {
  const rect = canvas.getBoundingClientRect();
  const x = (clientX - rect.left) / Math.max(1, rect.width);
  const y = (clientY - rect.top) / Math.max(1, rect.height);
  const ndcX = x * 2 - 1;
  const ndcY = 1 - y * 2;

  const invVp = invertMatrix4(viewProj);
  if (invVp === null) {
    return {
      origin: [0, 0, 0],
      direction: [0, 0, -1],
    };
  }

  // Unproject the near + far points on the ray.
  const near = unprojectPoint(invVp, ndcX, ndcY, -1);
  const far = unprojectPoint(invVp, ndcX, ndcY, 1);
  const dx = far[0] - near[0];
  const dy = far[1] - near[1];
  const dz = far[2] - near[2];
  const len = Math.hypot(dx, dy, dz) || 1;
  return {
    origin: near,
    direction: [dx / len, dy / len, dz / len],
  };
}

/**
 * Ray-cast against every active instance in `mesh` and return the
 * closest hit.  Returns `null` when no instance is intersected.
 */
export function pickTile(
  mesh: TileMesh,
  ray: PickRay,
): PickHit | null {
  let bestHit: PickHit | null = null;
  for (let i = 0; i < mesh.instanceCount; i++) {
    const base = i * FLOATS_PER_INSTANCE_MATRIX;
    const model = mesh.modelData.subarray(base, base + FLOATS_PER_INSTANCE_MATRIX);
    const inv = invertMatrix4(model);
    if (inv === null) continue;
    // Transform the world-space ray into the instance's local space.
    const localOrigin = transformPoint(inv, ray.origin);
    const localDir = transformDirection(inv, ray.direction);
    const t = slabIntersect(localOrigin, localDir);
    if (t === null) continue;
    if (bestHit !== null && t >= bestHit.t) continue;
    bestHit = {
      instanceIndex: i,
      point: [
        ray.origin[0] + ray.direction[0] * t,
        ray.origin[1] + ray.direction[1] * t,
        ray.origin[2] + ray.direction[2] * t,
      ],
      t,
    };
  }
  return bestHit;
}

// ── Internal: 4×4 inverse + transform helpers ─────────────────────

/**
 * Compute the inverse of a 4×4 column-major matrix.  Returns null
 * when the matrix is singular (determinant ≈ 0).  Lifted from the
 * canonical Gauss-Jordan port that mesa / three.js / glmatrix all
 * derive from.
 */
export function invertMatrix4(m: Float32Array): Float32Array | null {
  const a = m;
  const a00 = a[0],  a01 = a[1],  a02 = a[2],  a03 = a[3];
  const a10 = a[4],  a11 = a[5],  a12 = a[6],  a13 = a[7];
  const a20 = a[8],  a21 = a[9],  a22 = a[10], a23 = a[11];
  const a30 = a[12], a31 = a[13], a32 = a[14], a33 = a[15];

  const b00 = a00 * a11 - a01 * a10;
  const b01 = a00 * a12 - a02 * a10;
  const b02 = a00 * a13 - a03 * a10;
  const b03 = a01 * a12 - a02 * a11;
  const b04 = a01 * a13 - a03 * a11;
  const b05 = a02 * a13 - a03 * a12;
  const b06 = a20 * a31 - a21 * a30;
  const b07 = a20 * a32 - a22 * a30;
  const b08 = a20 * a33 - a23 * a30;
  const b09 = a21 * a32 - a22 * a31;
  const b10 = a21 * a33 - a23 * a31;
  const b11 = a22 * a33 - a23 * a32;

  let det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
  if (Math.abs(det) < 1e-10) return null;
  det = 1.0 / det;

  const out = new Float32Array(16);
  out[0] = (a11 * b11 - a12 * b10 + a13 * b09) * det;
  out[1] = (a02 * b10 - a01 * b11 - a03 * b09) * det;
  out[2] = (a31 * b05 - a32 * b04 + a33 * b03) * det;
  out[3] = (a22 * b04 - a21 * b05 - a23 * b03) * det;
  out[4] = (a12 * b08 - a10 * b11 - a13 * b07) * det;
  out[5] = (a00 * b11 - a02 * b08 + a03 * b07) * det;
  out[6] = (a32 * b02 - a30 * b05 - a33 * b01) * det;
  out[7] = (a20 * b05 - a22 * b02 + a23 * b01) * det;
  out[8] = (a10 * b10 - a11 * b08 + a13 * b06) * det;
  out[9] = (a01 * b08 - a00 * b10 - a03 * b06) * det;
  out[10] = (a30 * b04 - a31 * b02 + a33 * b00) * det;
  out[11] = (a21 * b02 - a20 * b04 - a23 * b00) * det;
  out[12] = (a11 * b07 - a10 * b09 - a12 * b06) * det;
  out[13] = (a00 * b09 - a01 * b07 + a02 * b06) * det;
  out[14] = (a31 * b01 - a30 * b03 - a32 * b00) * det;
  out[15] = (a20 * b03 - a21 * b01 + a22 * b00) * det;
  return out;
}

function unprojectPoint(
  invViewProj: Float32Array,
  ndcX: number,
  ndcY: number,
  ndcZ: number,
): [number, number, number] {
  const x = invViewProj[0] * ndcX + invViewProj[4] * ndcY + invViewProj[8]  * ndcZ + invViewProj[12];
  const y = invViewProj[1] * ndcX + invViewProj[5] * ndcY + invViewProj[9]  * ndcZ + invViewProj[13];
  const z = invViewProj[2] * ndcX + invViewProj[6] * ndcY + invViewProj[10] * ndcZ + invViewProj[14];
  const w = invViewProj[3] * ndcX + invViewProj[7] * ndcY + invViewProj[11] * ndcZ + invViewProj[15];
  const inv = 1 / (w || 1);
  return [x * inv, y * inv, z * inv];
}

function transformPoint(m: Float32Array, p: [number, number, number]): [number, number, number] {
  return [
    m[0] * p[0] + m[4] * p[1] + m[8]  * p[2] + m[12],
    m[1] * p[0] + m[5] * p[1] + m[9]  * p[2] + m[13],
    m[2] * p[0] + m[6] * p[1] + m[10] * p[2] + m[14],
  ];
}

function transformDirection(m: Float32Array, d: [number, number, number]): [number, number, number] {
  // Direction transform — drop translation component.
  return [
    m[0] * d[0] + m[4] * d[1] + m[8]  * d[2],
    m[1] * d[0] + m[5] * d[1] + m[9]  * d[2],
    m[2] * d[0] + m[6] * d[1] + m[10] * d[2],
  ];
}

/**
 * Ray-AABB slab intersection in local space.  The AABB is the
 * canonical tile box [-w/2..w/2] × [-h/2..h/2] × [-d/2..d/2] from
 * `tile-mesh.ts`.  Returns the parametric `t` at first entry, or
 * null on miss.
 */
function slabIntersect(
  origin: [number, number, number],
  direction: [number, number, number],
): number | null {
  const hw = TILE_WIDTH * 0.5;
  const hh = TILE_HEIGHT * 0.5;
  const hd = TILE_DEPTH * 0.5;
  let tMin = -Infinity;
  let tMax = Infinity;
  const mins = [-hw, -hh, -hd];
  const maxs = [hw, hh, hd];
  for (let axis = 0; axis < 3; axis++) {
    const o = origin[axis];
    const d = direction[axis];
    if (Math.abs(d) < 1e-8) {
      if (o < mins[axis] || o > maxs[axis]) return null;
      continue;
    }
    const t1 = (mins[axis] - o) / d;
    const t2 = (maxs[axis] - o) / d;
    const tNear = Math.min(t1, t2);
    const tFar = Math.max(t1, t2);
    if (tNear > tMin) tMin = tNear;
    if (tFar < tMax) tMax = tFar;
    if (tMin > tMax) return null;
  }
  // Only register hits in front of the ray origin.
  if (tMax < 0) return null;
  return Math.max(tMin, 0);
}
