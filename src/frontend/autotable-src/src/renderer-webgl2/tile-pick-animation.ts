// Phase K Wave 20 — WebGL2 tile-pick animation graph (Hicks, Frontend).
//
// Phase L W5 spike — lift-from-wall animation for a single tile.  The
// W17 scene runtime + W19 wall geometry shipped click-to-pick that
// instantly bumped the picked tile +y by 0.75 units; W20 replaces the
// instant bump with a smooth tween so the picked tile reads as an
// in-hand draw.  This module ships a tiny animation scheduler that
// the production renderer (Phase L W6+) can drop straight into the
// real game's draw / deal / discard animations.
//
// Design constraints:
//   • Zero three.js dependency — the renderer-webgl2 path stays
//     free-standing (W15 directive).
//   • Allocation-free per-frame: every tween updates an existing
//     Float32Array(16) model matrix in place; no new buffers
//     per redraw.
//   • Easing functions inlined (no external interpolation lib).
//   • One-shot tween: `startPickAnimation()` returns a handle whose
//     `step(now)` method advances the tween to the wall-clock time
//     `now` and returns true while the tween is still active.  The
//     scene consumer is responsible for calling `setTileInstance()`
//     with the updated matrix + scheduling the next rAF redraw.
//
// What's HERE in W20:
//   • `PickAnimationKind`     — `'lift'` (W20) | `'drop'` (W20).
//   • `PickAnimationHandle`   — public handle with `step()` +
//     `finish()`.
//   • `startPickAnimation()`  — build a tween from a base 16-float
//     matrix to a target offset, with optional rotation around X
//     (so the labelled face tips toward the camera as the tile
//     rises).
//   • `easeOutCubic` / `easeInOutSine` — the two easing curves the
//     production renderer needs (snappy lift, smooth drop).
//
// What's NOT here (Phase L W6+):
//   • Multi-tile chain animation (deal-out wave).
//   • Bezier curves / spring physics.
//   • Animation scheduler queue (we ship one-shot only; the consumer
//     stacks handles itself).
//
// Bundle math: target ≤ 45 KB for `renderer-webgl2` at W20.  W19
// baseline was 30,174 B; this module + `./tile-drag` together must
// stay under ~15 KB to clear the target with headroom.

import { TILE_HEIGHT } from './tile-mesh';

/** Tween shape — `lift` raises the tile; `drop` sets it back. */
export type PickAnimationKind = 'lift' | 'drop';

/** Public handle returned by `startPickAnimation()`. */
export interface PickAnimationHandle {
  /** Kind of tween (lift vs. drop). */
  readonly kind: PickAnimationKind;
  /** Wall-clock start time (ms; matches `performance.now()`). */
  readonly startedAt: number;
  /** Tween duration in milliseconds. */
  readonly durationMs: number;
  /** Out parameter: a fresh 16-float matrix the consumer can hand
   *  to `setTileInstance()`.  Reused frame-to-frame so callers
   *  must NOT retain the array reference across frames. */
  readonly out: Float32Array;
  /**
   * Advance the tween to `nowMs`.  Returns true while the tween is
   * still in flight, false once it has settled at the final frame.
   * Idempotent after settlement — repeated `step()` calls return
   * false without mutating the matrix.
   */
  step(nowMs: number): boolean;
  /** Force the tween to its final frame and mark it settled. */
  finish(): void;
}

/** Total lift distance (scene units, top-of-tile clearance over wall). */
export const PICK_LIFT_DELTA_Y = TILE_HEIGHT * 0.45;

/** Default tween duration for `lift` (snappy in-hand pickup). */
export const PICK_LIFT_DURATION_MS = 220;

/** Default tween duration for `drop` (slower set-back). */
export const PICK_DROP_DURATION_MS = 280;

/** Default X-axis tip angle (radians) — tile faces the camera at apex. */
export const PICK_LIFT_TIP_ANGLE_X = 0.18;

/**
 * Build a lift / drop tween from `baseMatrix` (a 16-float column-
 * major model matrix).  The tween animates a vertical translation
 * + an optional rotation around the local X-axis (so the labelled
 * face tips toward the camera).  The returned handle's `out`
 * Float32Array is mutated in place each call to `step()`; callers
 * write it through `setTileInstance(mesh, idx, handle.out, tileId)`
 * + request a redraw.
 */
export function startPickAnimation(
  baseMatrix: Float32Array,
  kind: PickAnimationKind,
  startedAt: number,
  options: {
    durationMs?: number;
    deltaY?: number;
    tipAngleX?: number;
  } = {},
): PickAnimationHandle {
  if (baseMatrix.length !== 16) {
    throw new Error(`[tile-pick-animation] baseMatrix must be 16 floats, got ${baseMatrix.length}`);
  }
  const durationMs = options.durationMs ?? (
    kind === 'lift' ? PICK_LIFT_DURATION_MS : PICK_DROP_DURATION_MS
  );
  const deltaY = options.deltaY ?? PICK_LIFT_DELTA_Y;
  const tipAngleX = options.tipAngleX ?? PICK_LIFT_TIP_ANGLE_X;
  // The out matrix starts as a copy of the base so the consumer can
  // hand it off immediately for frame 0 (no flicker before the rAF
  // callback advances the tween).
  const out = new Float32Array(baseMatrix);
  let settled = false;

  return {
    kind,
    startedAt,
    durationMs,
    out,
    step(nowMs: number): boolean {
      if (settled) return false;
      const elapsed = nowMs - startedAt;
      let t = elapsed / durationMs;
      if (t <= 0) t = 0;
      if (t >= 1) {
        t = 1;
        settled = true;
      }
      // `lift` ramps 0→1 with ease-out (snappy initial pop), `drop`
      // ramps 0→1 with ease-in-out (settle).  The `progress` value
      // is the normalised "how raised" parameter — 1 = fully lifted,
      // 0 = baseline.  `drop` flips the curve so progress = 1 - eased.
      const eased = kind === 'lift'
        ? easeOutCubic(t)
        : easeInOutSine(t);
      const progress = kind === 'lift' ? eased : (1 - eased);

      // Refresh the base + apply incremental transforms.
      out.set(baseMatrix);
      // Translate along world +y by `progress * deltaY`.
      out[13] += progress * deltaY;
      // Rotate around the local X-axis by `progress * tipAngleX`.
      // Local rotation: m := m × Rx(theta).  We apply it after the
      // translation so the lifted tile tips forward at its lifted
      // position.
      rotateXMatrix4InPlace(out, progress * tipAngleX);
      return !settled;
    },
    finish(): void {
      if (settled) return;
      settled = true;
      const progress = kind === 'lift' ? 1 : 0;
      out.set(baseMatrix);
      out[13] += progress * deltaY;
      rotateXMatrix4InPlace(out, progress * tipAngleX);
    },
  };
}

/**
 * In-place rotation around the local X-axis: `m := m × Rx(theta)`.
 * Mirrors the `rotateYMatrix4` shape in `./math.ts`.  Kept private
 * to this module so the math chunk stays minimal (the W20 use of
 * X-rotation is exclusively for the pick animation).
 */
function rotateXMatrix4InPlace(m: Float32Array, theta: number): void {
  if (theta === 0) return;
  const c = Math.cos(theta);
  const s = Math.sin(theta);
  const m10 = m[4], m11 = m[5], m12 = m[6], m13 = m[7];
  const m20 = m[8], m21 = m[9], m22 = m[10], m23 = m[11];
  m[4] = m10 * c + m20 * s;
  m[5] = m11 * c + m21 * s;
  m[6] = m12 * c + m22 * s;
  m[7] = m13 * c + m23 * s;
  m[8] = -m10 * s + m20 * c;
  m[9] = -m11 * s + m21 * c;
  m[10] = -m12 * s + m22 * c;
  m[11] = -m13 * s + m23 * c;
}

// ── Easing curves ──────────────────────────────────────────────────
//
// We ship exactly the two curves the production renderer needs:
// ease-out-cubic for the snappy lift, ease-in-out-sine for the
// settle.  Both are allocation-free + branchless.  Tween input `t`
// is assumed clamped to [0, 1] by the caller (we don't re-clamp
// here; the only call site in `startPickAnimation` clamps before
// invoking).

/** Cubic ease-out: f(t) = 1 - (1-t)^3.  Snappy then decays. */
export function easeOutCubic(t: number): number {
  const inv = 1 - t;
  return 1 - inv * inv * inv;
}

/** Sine ease-in-out: f(t) = 0.5 · (1 - cos(πt)).  Smooth either side. */
export function easeInOutSine(t: number): number {
  return 0.5 * (1 - Math.cos(Math.PI * t));
}
