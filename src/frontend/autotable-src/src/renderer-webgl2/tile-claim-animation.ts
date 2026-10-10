// Phase K Wave 21 — WebGL2 tile-claim animation graph (Hicks, Frontend).
//
// Phase L W6 spike — claim animation for pung / kong / chi melds.
// W20 layered pick-and-drag on top of the W17 scene runtime (lift
// from wall, hover outline, pointer drag-and-drop).  W21 adds the
// *claim* animation — the visual flourish a player sees when an
// opponent announces "pung" / "kong" / "chi" and three or four
// tiles snap together as a meld at the table edge.
//
// Design constraints (mirror the W20 pick-animation module):
//   • Zero three.js dependency — `renderer-webgl2` chunk stays
//     free-standing (W15 directive).
//   • Allocation-free per-frame: every tween updates an existing
//     Float32Array(16) model matrix in place; no per-redraw
//     allocations beyond the constant-size buffers the handle owns.
//   • Easing functions imported from `./tile-pick-animation` so we
//     don't ship duplicate curve definitions.  The W20 module
//     exports `easeOutCubic` + `easeInOutSine`; W21 reuses both and
//     adds `easeOutBack` (a tiny inline curve) for the "settle with
//     a hint of bounce" feel at the apex of the slide.
//   • Multi-tile orchestration: a claim animates 2-3 source tiles
//     (the claimer's hand tiles that complete the meld) plus the
//     1 discard tile being claimed, all flying to the meld slot.
//     The handle owns one tween per tile; `step()` returns true
//     while ANY tween is still in flight.
//
// What's HERE in W21:
//   • `ClaimKind`              — `'pung'` (3-tile, identical) |
//     `'kong'` (4-tile, identical) | `'chi'` (3-tile, sequential).
//   • `ClaimAnimationHandle`   — public handle with `step()` +
//     `finish()` + per-tile out-matrices.
//   • `startClaimAnimation()`  — build a claim tween from N source
//     model matrices to N target matrices at the meld slot, with
//     staggered start times so the tiles fan into the slot rather
//     than crashing in together.
//   • `claimDurationMs()`      — wave-public derivation of the
//     total animation duration for a claim kind (used by the
//     scene runtime to schedule the redraw stop).
//
// What's NOT here (Phase L W7+):
//   • Network-driven claim arbitration (Bishop's claim window).
//   • Audio cue (pung / kong / chi callout).
//   • Slow-motion replay rendering of the claim.
//
// Bundle math: target ≤ 48 KB for `renderer-webgl2` at W21.  W20
// baseline was 35,258 B; this module + `./meld-display` together
// must stay under ~13 KB to clear the target with headroom.

import { easeInOutSine, easeOutCubic } from './tile-pick-animation';
import { TILE_HEIGHT, TILE_WIDTH } from './tile-mesh';

/** Claim kind — controls tile count + duration. */
export type ClaimKind = 'pung' | 'kong' | 'chi';

/** Tile count for a given claim kind (pung=3, kong=4, chi=3). */
export function claimTileCount(kind: ClaimKind): 3 | 4 {
  return kind === 'kong' ? 4 : 3;
}

/** Per-tile entry within a claim tween — source + target matrix. */
export interface ClaimTileTween {
  /** Source 16-float column-major matrix (the tile's current pose). */
  readonly source: Float32Array;
  /** Target 16-float column-major matrix (the meld-slot pose). */
  readonly target: Float32Array;
  /** Out matrix (mutated in place every frame). */
  readonly out: Float32Array;
  /** Per-tile start delay in ms (staggers the fan-in). */
  readonly delayMs: number;
}

/** Public handle returned by `startClaimAnimation()`. */
export interface ClaimAnimationHandle {
  readonly kind: ClaimKind;
  readonly startedAt: number;
  /** Total animation duration including the per-tile stagger. */
  readonly durationMs: number;
  readonly tiles: ReadonlyArray<ClaimTileTween>;
  /**
   * Advance every per-tile tween to `nowMs`.  Returns true while
   * ANY tween is still in flight, false once all tiles have
   * settled at their target poses.  Idempotent after settlement.
   */
  step(nowMs: number): boolean;
  /** Force every tween to its final frame. */
  finish(): void;
}

/** Default per-tile slide duration in ms. */
export const CLAIM_TILE_SLIDE_MS = 360;

/** Per-tile stagger delay in ms (so the tiles fan into the slot). */
export const CLAIM_TILE_STAGGER_MS = 90;

/** Apex height (scene units) at which tiles arc before settling. */
export const CLAIM_ARC_PEAK_Y = TILE_HEIGHT * 0.85;

/**
 * Total duration in ms for a claim of the given kind:
 *   slide + (count-1) * stagger.
 */
export function claimDurationMs(kind: ClaimKind): number {
  const count = claimTileCount(kind);
  return CLAIM_TILE_SLIDE_MS + (count - 1) * CLAIM_TILE_STAGGER_MS;
}

/**
 * Cubic ease-out with a small overshoot at t=1 — the tile slides
 * into the slot and "ticks" the last few percent of the slide as a
 * subtle bounce.  Branchless + allocation-free.  Bounds: f(0)=0,
 * f(1)=1, peak at ~t=0.85 of ~1.04.  Kept private to this module.
 */
function easeOutBack(t: number): number {
  // Equivalent to https://easings.net/#easeOutBack with c1=1.20.
  const c1 = 1.20;
  const c3 = c1 + 1;
  const inv = t - 1;
  return 1 + c3 * inv * inv * inv + c1 * inv * inv;
}

/**
 * Build a claim animation across N source/target matrix pairs.  The
 * `kind` controls the tile count (pung=3, kong=4, chi=3) — the
 * caller is responsible for providing exactly that many source
 * + target matrices.  Throws on count mismatch.
 *
 * Each tile arcs from its `source` pose to `target`, lifting through
 * `CLAIM_ARC_PEAK_Y` at the midpoint of its slide.  The per-tile
 * start is staggered by `CLAIM_TILE_STAGGER_MS` so the tiles fan
 * into the meld slot instead of crashing together.  Out matrices
 * are mutated in place each call to `step()`; callers write each
 * through `setTileInstance()` + request a redraw.
 */
export function startClaimAnimation(
  kind: ClaimKind,
  sources: ReadonlyArray<Float32Array>,
  targets: ReadonlyArray<Float32Array>,
  startedAt: number,
): ClaimAnimationHandle {
  const count = claimTileCount(kind);
  if (sources.length !== count) {
    throw new Error(
      `[tile-claim-animation] kind=${kind} expects ${count} source matrices, got ${sources.length}`,
    );
  }
  if (targets.length !== count) {
    throw new Error(
      `[tile-claim-animation] kind=${kind} expects ${count} target matrices, got ${targets.length}`,
    );
  }
  for (let i = 0; i < count; i++) {
    if (sources[i].length !== 16) {
      throw new Error(`[tile-claim-animation] source[${i}] must be 16 floats`);
    }
    if (targets[i].length !== 16) {
      throw new Error(`[tile-claim-animation] target[${i}] must be 16 floats`);
    }
  }

  const tiles: ClaimTileTween[] = new Array(count);
  for (let i = 0; i < count; i++) {
    tiles[i] = {
      source: sources[i],
      target: targets[i],
      out: new Float32Array(sources[i]),
      delayMs: i * CLAIM_TILE_STAGGER_MS,
    };
  }

  let settled = false;
  const totalMs = claimDurationMs(kind);

  const handle: ClaimAnimationHandle = {
    kind,
    startedAt,
    durationMs: totalMs,
    tiles,
    step(nowMs: number): boolean {
      if (settled) return false;
      let anyInFlight = false;
      for (let i = 0; i < count; i++) {
        const tile = tiles[i];
        const localElapsed = nowMs - startedAt - tile.delayMs;
        if (localElapsed <= 0) {
          // Not started yet — hold at source pose.
          tile.out.set(tile.source);
          anyInFlight = true;
          continue;
        }
        let t = localElapsed / CLAIM_TILE_SLIDE_MS;
        if (t >= 1) {
          // Settled at target.
          t = 1;
          tile.out.set(tile.target);
          continue;
        }
        anyInFlight = true;
        // Linear interpolate every matrix element with an
        // ease-out-cubic curve, then layer an arc on top so the
        // tile lifts at mid-slide and settles with a hint of back-
        // ease.  We compose two curves: `eased` for X/Z translation,
        // `arcY` for the lift over the floor.
        const eased = easeOutCubic(t);
        const arc = easeInOutSine(t) * (1 - t) * 4; // peaks 1.0 at t=0.5
        const settle = easeOutBack(t);
        // Interpolate per-element with the eased parameter so the
        // rotation+scale of the matrix lerps cleanly (acceptable for
        // small angle deltas — claims rotate < ~30°).
        for (let j = 0; j < 16; j++) {
          tile.out[j] = tile.source[j] + (tile.target[j] - tile.source[j]) * eased;
        }
        // Apex lift on Y: add `arc * peak` to the translation row.
        tile.out[13] += arc * CLAIM_ARC_PEAK_Y;
        // Settle multiplier — used to dampen the apex on the back-
        // half so the bounce is subtle (max ~4% overshoot then
        // back to 1.0 at t=1).
        const damp = 1 + (settle - 1) * 0.15;
        // Apply a small in-plane "snap" toward target on damp > 1
        // so the visible motion catches up just past t≈0.85.
        tile.out[12] += (tile.target[12] - tile.source[12]) * (damp - 1);
        tile.out[14] += (tile.target[14] - tile.source[14]) * (damp - 1);
      }
      if (!anyInFlight) settled = true;
      return anyInFlight;
    },
    finish(): void {
      if (settled) return;
      settled = true;
      for (let i = 0; i < count; i++) {
        tiles[i].out.set(tiles[i].target);
      }
    },
  };
  return handle;
}

/**
 * Build a canonical meld-slot target matrix for the i-th tile in
 * a meld at a given table-edge slot.  Helper for the scene-runtime
 * (Phase L W7+) to derive `target` matrices when calling
 * `startClaimAnimation()`.  Slot 0 is the player's own meld row;
 * 1, 2, 3 are the right / opposite / left seats.  Tiles are laid
 * out along the local x-axis, separated by one tile width.
 *
 * The matrix is column-major, identity-scaled, rotated -π/2 around
 * X (so the labelled face lies flat against the table), translated
 * to the slot's anchor.  Implementation kept tight to fit the
 * W21 ≤ 48 KB chunk budget — no general-purpose transform builder.
 */
export function meldSlotMatrix(
  slot: 0 | 1 | 2 | 3,
  tileIndexInMeld: number,
  meldOriginXZ: readonly [number, number],
): Float32Array {
  const m = new Float32Array(16);
  // Rotation -π/2 around X (tile face flat on the table).
  m[0] = 1;
  m[5] = 0;  m[6] = -1;
  m[9] = 1;  m[10] = 0;
  m[15] = 1;
  // Per-slot rotation around Y so each seat's meld row is aligned
  // with the player's table edge.  slot 0 = no rotation; slot 1 =
  // +π/2; slot 2 = π; slot 3 = -π/2.  We pre-multiply only the
  // x/z columns since the X-rotation has already been applied.
  const angle = slot * (Math.PI / 2);
  const c = Math.cos(angle);
  const s = Math.sin(angle);
  // m_xz_rot * m_x_rot — flatten into the final 4×4 in place.
  const m00 = m[0];
  const m08 = m[8], m10v = m[10];
  m[0] = m00 * c;
  m[2] = m00 * -s;
  m[8] = m08 * c + m10v * 0;
  m[10] = m08 * -s + m10v * 0;
  // The translation: meldOriginXZ + (tileIndex * tile-width) along
  // the local x-axis of the meld row (which is the world-space
  // basis rotated by `angle`).
  const dx = tileIndexInMeld * TILE_WIDTH * c;
  const dz = tileIndexInMeld * TILE_WIDTH * -s;
  m[12] = meldOriginXZ[0] + dx;
  m[13] = 0;
  m[14] = meldOriginXZ[1] + dz;
  return m;
}
