// Phase K Wave 22 — WebGL2 score-display HUD module (Hicks, Frontend).
//
// Phase L W7 — points + dora-indicator HUD overlay.  W21 landed
// the claim animation + meld-display row; W22's sister module
// `./discard-pile` handles the per-seat discard piles.  This
// module owns the *HUD* layer: a thin overlay rendered in screen-
// space (NOT in the 3D scene graph) that shows:
//
//   • Per-seat point totals (4 chips, one per seat, anchored at the
//     screen corners).
//   • Dora indicator row (1-4 tiles flipped face-up on the dead
//     wall, shown as small tile thumbnails in the top-centre).
//   • Round wind + dealer marker chip (top-centre, between the
//     dora indicators and the seat chips).
//
// Why HUD-not-scene: the HUD is camera-independent (the player
// shouldn't have to crane the camera to read their point total),
// and the HUD elements are 2D-shaped anyway (text chips +
// orthographic tile thumbnails) — projecting them through the
// 3D pipeline costs more than rendering them as a screen-space
// canvas overlay.  We expose a single mutable `ScoreDisplayState`
// object that the scene runtime updates whenever scores change;
// the renderer reads it once per redraw and rebuilds the canvas.
//
// Design constraints (mirror the W17+ scene runtime):
//   • Zero three.js dependency — the HUD uses a 2D canvas; the
//     `renderer-webgl2` chunk stays free-standing.
//   • Allocation-free per-frame: every redraw reuses the canvas
//     context's strokes/fills; no per-frame allocations beyond
//     the constant-size string templates `Intl.NumberFormat`
//     internally allocates.
//   • Idempotent `redrawScoreDisplay()` — calling twice with the
//     same state is a no-op (we cache the last-rendered hash).
//
// What's HERE in W22:
//   • `SeatScoreEntry`         — per-seat HUD record (points,
//     wind, isDealer flag).
//   • `DoraIndicator`          — single tile-id slot.
//   • `ScoreDisplayState`      — full HUD state (4 seat entries
//     + up to 4 dora indicators + round-wind + round-number).
//   • `createScoreDisplay()`   — build default state (zeros).
//   • `setSeatScore()`         — mutate per-seat entry; returns
//     true if the value changed (caller can skip redraw).
//   • `setDoraIndicators()`    — replace the dora indicator slots
//     (1-4 tile-ids); empty slots are hidden.
//   • `setRoundContext()`      — set round wind + round number +
//     dealer seat.
//   • `redrawScoreDisplay()`   — paint the HUD onto a 2D canvas.
//     Returns true if anything was repainted; false if cached.
//
// What's NOT here (Phase L W8+):
//   • Hand-history / yaku breakdown popups.
//   • Score-change animation (delta floaters above each chip).
//
// Bundle math: target ≤ 52 KB for `renderer-webgl2` at W22.  W21
// baseline was 40,292 B; the W22 budget is +~12 KB across this
// module and `./discard-pile`.

import type { SeatIndex } from './meld-display';

/** Per-seat HUD entry. */
export interface SeatScoreEntry {
  /** Current point total (signed; negative permitted for crash-out). */
  points: number;
  /** Seat wind ('E', 'S', 'W', 'N'). */
  wind: WindKind;
  /** True when this seat holds the dealer button this hand. */
  isDealer: boolean;
}

/** Round wind / seat wind kind. */
export type WindKind = 'E' | 'S' | 'W' | 'N';

/** Single dora-indicator slot. */
export interface DoraIndicator {
  /** Tile-id (atlas index) for the flipped indicator tile. */
  readonly tileId: number;
}

/** Maximum dora indicators (1 base + up to 3 added via kong reveals). */
export const DORA_MAX = 4;

/** Full HUD state. */
export interface ScoreDisplayState {
  /** Per-seat entries indexed by seat (0..3). */
  seats: [SeatScoreEntry, SeatScoreEntry, SeatScoreEntry, SeatScoreEntry];
  /** Dora-indicator row; 1-4 entries, oldest first. */
  dora: DoraIndicator[];
  /** Round wind (changes once per round-of-rounds). */
  roundWind: WindKind;
  /** 1-based hand number within the round. */
  roundNumber: number;
  /** Last-rendered hash; redrawScoreDisplay short-circuits when
   *  the state hasn't changed since the previous paint. */
  lastRenderedHash: string;
}

/** Default starting points (standard 4-player mahjong: 25,000). */
export const DEFAULT_START_POINTS = 25000;

/** Build a fresh HUD state at the start of a match. */
export function createScoreDisplay(): ScoreDisplayState {
  return {
    seats: [
      { points: DEFAULT_START_POINTS, wind: 'E', isDealer: true  },
      { points: DEFAULT_START_POINTS, wind: 'S', isDealer: false },
      { points: DEFAULT_START_POINTS, wind: 'W', isDealer: false },
      { points: DEFAULT_START_POINTS, wind: 'N', isDealer: false },
    ],
    dora: [],
    roundWind: 'E',
    roundNumber: 1,
    lastRenderedHash: '',
  };
}

/**
 * Update a single seat's HUD entry.  Returns true iff any field
 * actually changed (caller can skip redraw on a no-op update).
 */
export function setSeatScore(
  state: ScoreDisplayState,
  seat: SeatIndex,
  patch: Partial<SeatScoreEntry>,
): boolean {
  const entry = state.seats[seat];
  let changed = false;
  if (patch.points !== undefined && patch.points !== entry.points) {
    entry.points = patch.points;
    changed = true;
  }
  if (patch.wind !== undefined && patch.wind !== entry.wind) {
    entry.wind = patch.wind;
    changed = true;
  }
  if (patch.isDealer !== undefined && patch.isDealer !== entry.isDealer) {
    entry.isDealer = patch.isDealer;
    changed = true;
  }
  return changed;
}

/**
 * Replace the dora-indicator row.  Pass 1-4 tile-ids; empty
 * elements are dropped.  Returns the new dora count.
 */
export function setDoraIndicators(
  state: ScoreDisplayState,
  tileIds: ReadonlyArray<number>,
): number {
  if (tileIds.length > DORA_MAX) {
    throw new Error(`dora indicators capped at ${DORA_MAX}`);
  }
  state.dora.length = 0;
  for (const tid of tileIds) {
    state.dora.push({ tileId: tid });
  }
  return state.dora.length;
}

/**
 * Set round-context fields (round wind, round number, dealer seat).
 * The dealer flag is auto-propagated to the per-seat entries: the
 * specified `dealerSeat` is marked `isDealer:true` and the others
 * are cleared.
 */
export function setRoundContext(
  state: ScoreDisplayState,
  roundWind: WindKind,
  roundNumber: number,
  dealerSeat: SeatIndex,
): void {
  state.roundWind = roundWind;
  state.roundNumber = Math.max(1, Math.floor(roundNumber));
  for (let i = 0 as SeatIndex; i < 4; i = (i + 1) as SeatIndex) {
    state.seats[i].isDealer = (i === dealerSeat);
  }
}

/** Compute a cheap deterministic hash over the HUD state. */
function stateHash(state: ScoreDisplayState): string {
  const seatStr = state.seats
    .map((s) => `${s.points}|${s.wind}|${s.isDealer ? 'D' : '-'}`)
    .join(';');
  const doraStr = state.dora.map((d) => d.tileId).join(',');
  return `${state.roundWind}${state.roundNumber}@${seatStr}#${doraStr}`;
}

/**
 * Paint the HUD onto a 2D canvas.  Returns true when the canvas
 * was actually repainted; false when the state was unchanged
 * since the last call (cached).  The caller is responsible for
 * sizing + appending the canvas; we draw onto the supplied
 * context relative to the canvas's full extent.
 *
 * The canvas is laid out as a 4-row column on the right edge:
 *   • Top:  round-wind chip + round-number badge.
 *   • Next: dora-indicator strip (1-4 tile thumbnails).
 *   • Next: seat 0 score chip (player perspective).
 *   • Bottom three: seat 1/2/3 score chips, anchored at the
 *     right/opposite/left edges respectively.
 */
export function redrawScoreDisplay(
  state: ScoreDisplayState,
  ctx: CanvasRenderingContext2D,
): boolean {
  const hash = stateHash(state);
  if (hash === state.lastRenderedHash) return false;
  state.lastRenderedHash = hash;

  const w = ctx.canvas.width;
  const h = ctx.canvas.height;
  ctx.clearRect(0, 0, w, h);

  // ── Round-context chip (top-centre) ────────────────────────────
  const roundLabel = `${state.roundWind}${state.roundNumber}`;
  paintChip(ctx, w / 2 - 36, 8, 72, 28, roundLabel, '#1e293b', '#e2e8f0');

  // ── Dora indicators (top-centre, below round chip) ─────────────
  const doraStartX = w / 2 - (state.dora.length * 22) / 2;
  for (let i = 0; i < state.dora.length; i++) {
    paintTileThumb(ctx, doraStartX + i * 22, 44, 18, 24, state.dora[i].tileId);
  }

  // ── Per-seat score chips (corners) ─────────────────────────────
  // Seat 0 = self (bottom-centre).
  paintScoreChip(ctx, w / 2 - 48, h - 40, 96, 32, state.seats[0]);
  // Seat 1 = right (right-centre, vertical text).
  paintScoreChip(ctx, w - 110, h / 2 - 16, 96, 32, state.seats[1]);
  // Seat 2 = opposite (top-centre, below dora row).
  paintScoreChip(ctx, w / 2 - 48, 78, 96, 32, state.seats[2]);
  // Seat 3 = left (left-centre).
  paintScoreChip(ctx, 14, h / 2 - 16, 96, 32, state.seats[3]);

  return true;
}

/** Paint a generic chip with text. */
function paintChip(
  ctx: CanvasRenderingContext2D,
  x: number, y: number, w: number, h: number,
  label: string, bg: string, fg: string,
): void {
  ctx.fillStyle = bg;
  ctx.beginPath();
  ctx.roundRect(x, y, w, h, 6);
  ctx.fill();
  ctx.fillStyle = fg;
  ctx.font = '600 14px system-ui, sans-serif';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillText(label, x + w / 2, y + h / 2);
}

/** Paint a per-seat score chip with wind + points + dealer mark. */
function paintScoreChip(
  ctx: CanvasRenderingContext2D,
  x: number, y: number, w: number, h: number,
  entry: SeatScoreEntry,
): void {
  const bg = entry.isDealer ? '#fde68a' : '#1e293b';
  const fg = entry.isDealer ? '#1e293b' : '#e2e8f0';
  ctx.fillStyle = bg;
  ctx.beginPath();
  ctx.roundRect(x, y, w, h, 6);
  ctx.fill();
  ctx.fillStyle = fg;
  ctx.font = '600 13px system-ui, sans-serif';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillText(`${entry.wind} · ${formatPoints(entry.points)}`, x + w / 2, y + h / 2);
}

/** Paint a small dora-indicator tile thumbnail. */
function paintTileThumb(
  ctx: CanvasRenderingContext2D,
  x: number, y: number, w: number, h: number,
  tileId: number,
): void {
  ctx.fillStyle = '#fef3c7';
  ctx.fillRect(x, y, w, h);
  ctx.strokeStyle = '#92400e';
  ctx.lineWidth = 1;
  ctx.strokeRect(x + 0.5, y + 0.5, w - 1, h - 1);
  ctx.fillStyle = '#92400e';
  ctx.font = '600 9px system-ui, sans-serif';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillText(String(tileId), x + w / 2, y + h / 2);
}

/** Format a point total with thousands-separator (locale-agnostic). */
function formatPoints(n: number): string {
  // Use a simple manual format so we don't pull in Intl.NumberFormat
  // (which adds ~3 KB after polyfill).  The format is "1,234" /
  // "-12,345" / "0".
  const sign = n < 0 ? '-' : '';
  const abs = Math.abs(Math.floor(n));
  const s = String(abs);
  let out = '';
  for (let i = 0; i < s.length; i++) {
    if (i > 0 && (s.length - i) % 3 === 0) out += ',';
    out += s[i];
  }
  return sign + out;
}
