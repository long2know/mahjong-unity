// Phase K Wave 16 — WebGL2 4×4 matrix helpers (Hicks).
//
// Phase L W2 spike scaffolding.  The renderer-webgl2 path doesn't
// take a three.js dependency, so we hand-roll the minimum surface of
// 4×4 matrix math the tile-mesh + camera + post-fx modules need.
//
// All matrices are column-major Float32Array(16), matching WebGL2's
// uniformMatrix4fv convention (transpose = false).
//
// Helpers HERE: identity, perspective, translate, scale, rotate-y,
// multiply.  Other matrix flavours (lookAt, ortho, quat-to-matrix)
// live with their consumer module so the math chunk stays tight.

export type WebglNullable<T> = T | null;

/** Column-major identity matrix. */
export function identity4(): Float32Array {
  const m = new Float32Array(16);
  m[0] = 1; m[5] = 1; m[10] = 1; m[15] = 1;
  return m;
}

/**
 * Right-handed perspective projection matrix in column-major order.
 * Mirrors `three.PerspectiveCamera.projectionMatrix`.
 */
export function perspective4(
  fovYRadians: number,
  aspect: number,
  near: number,
  far: number,
): Float32Array {
  const f = 1 / Math.tan(fovYRadians / 2);
  const nf = 1 / (near - far);
  const m = new Float32Array(16);
  m[0] = f / aspect;
  m[5] = f;
  m[10] = (far + near) * nf;
  m[11] = -1;
  m[14] = 2 * far * near * nf;
  return m;
}

/** In-place translate: `m := m × T(x,y,z)`. */
export function translateMatrix4(m: Float32Array, x: number, y: number, z: number): void {
  m[12] += m[0] * x + m[4] * y + m[8] * z;
  m[13] += m[1] * x + m[5] * y + m[9] * z;
  m[14] += m[2] * x + m[6] * y + m[10] * z;
  m[15] += m[3] * x + m[7] * y + m[11] * z;
}

/** In-place uniform/non-uniform scale: `m := m × S(sx,sy,sz)`. */
export function scaleMatrix4(m: Float32Array, sx: number, sy: number, sz: number): void {
  m[0] *= sx; m[1] *= sx; m[2] *= sx; m[3] *= sx;
  m[4] *= sy; m[5] *= sy; m[6] *= sy; m[7] *= sy;
  m[8] *= sz; m[9] *= sz; m[10] *= sz; m[11] *= sz;
}

/** In-place rotate around +Y: `m := m × Ry(theta)`. */
export function rotateYMatrix4(m: Float32Array, theta: number): void {
  const c = Math.cos(theta);
  const s = Math.sin(theta);
  const m00 = m[0], m01 = m[1], m02 = m[2], m03 = m[3];
  const m20 = m[8], m21 = m[9], m22 = m[10], m23 = m[11];
  m[0] = m00 * c - m20 * s;
  m[1] = m01 * c - m21 * s;
  m[2] = m02 * c - m22 * s;
  m[3] = m03 * c - m23 * s;
  m[8] = m00 * s + m20 * c;
  m[9] = m01 * s + m21 * c;
  m[10] = m02 * s + m22 * c;
  m[11] = m03 * s + m23 * c;
}

/**
 * Allocate-and-return `a × b` (column-major, standard GL multiply).
 * Both inputs left untouched.
 */
export function multiplyMatrix4(a: Float32Array, b: Float32Array): Float32Array {
  const m = new Float32Array(16);
  for (let i = 0; i < 4; i++) {
    const ai0 = a[i], ai1 = a[i + 4], ai2 = a[i + 8], ai3 = a[i + 12];
    m[i]      = ai0 * b[0]  + ai1 * b[1]  + ai2 * b[2]  + ai3 * b[3];
    m[i + 4]  = ai0 * b[4]  + ai1 * b[5]  + ai2 * b[6]  + ai3 * b[7];
    m[i + 8]  = ai0 * b[8]  + ai1 * b[9]  + ai2 * b[10] + ai3 * b[11];
    m[i + 12] = ai0 * b[12] + ai1 * b[13] + ai2 * b[14] + ai3 * b[15];
  }
  return m;
}
