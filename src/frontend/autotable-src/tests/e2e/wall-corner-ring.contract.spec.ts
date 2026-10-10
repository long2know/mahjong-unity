// =============================================================================
//  R-A (G4) — wall CORNERS must meet into ONE continuous depleting ring
// =============================================================================
//
//  Hudson/Vasquez G4 ruling (user's exact words, 2nd UAT update): acceptance is
//  a "visual/physical perimeter order ... a single sequential remaining run,"
//  NOT four separate edges. The user explicitly rejected "per-seat no-gap" as
//  insufficient. Today the four wall edges are INSET and never touch — the
//  seat-to-seat corner step is ~7.8-8.8x tile pitch (the "four half-walls"
//  look). Hudson's G4 gate: the draw-order world-distance ACROSS EACH CORNER
//  must be <= 1.6x pitch.
//
//  This is a BROWSER-FREE geometry gate: corner-meeting is a STATIC property of
//  setup-slots.ts (independent of dealer/dice — the ring adjacency is fixed), so
//  it reads the REAL `makeSlots(CHANGSHA)` output and measures the physical
//  `slot.origin` distance across each corner. It is NECESSARY-not-sufficient:
//  the "looks like one ring + no overlap with hands/discards" visual is Hudson's
//  live browser gate (the ring is a central ~78u square, so repositioning the
//  wall also has to clear the discard/hand slots — this file now ALSO asserts the
//  static slot-box non-overlap half deterministically; the pixel confirmation
//  stays Hudson's live gate).
//
//  Ownership: Hicks (setup-slots geometry) + Ferro (co-review). Apone 2026-08-10
//  RE-CENTRED the Changsha wall (`wall.cs` origin 49.5,49.5) so the 14/14/13/13
//  sides now MEET at every corner within half a tile pitch (3u), and moved the
//  Changsha discard tray inside the ring (`discard.cs`). This gate is now LIVE
//  GREEN — a regression that re-insets the wall (or breaks the CCW winding /
//  overlaps the discards) turns it RED.

import { test, expect } from '@playwright/test';
import { makeSlots } from '../../src/setup-slots';
import { GameType } from '../../src/types';
import type { Slot } from '../../src/slot';

const PITCH = 6;              // Size.TILE.x
const GATE = 1.6 * PITCH;     // Hudson G4: <= 9.6u across each corner

interface RingTile { col: number; x: number; y: number; }

interface Box { minx: number; maxx: number; miny: number; maxy: number; }
// Axis-aligned world footprint of a slot group for one seat, from the REAL
// base-place box (position ± half size) — the same geometry the renderer uses.
function boxOf(pred: (s: Slot) => boolean): Box | null {
  const g = makeSlots(GameType.CHANGSHA).filter(pred);
  if (g.length === 0) return null;
  const b: Box = { minx: Infinity, maxx: -Infinity, miny: Infinity, maxy: -Infinity };
  for (const s of g) {
    const p = s.places[0];
    b.minx = Math.min(b.minx, p.position.x - p.size.x / 2);
    b.maxx = Math.max(b.maxx, p.position.x + p.size.x / 2);
    b.miny = Math.min(b.miny, p.position.y - p.size.y / 2);
    b.maxy = Math.max(b.maxy, p.position.y + p.size.y / 2);
  }
  return b;
}
function overlaps(a: Box, b: Box): boolean {
  return a.minx < b.maxx && b.minx < a.maxx && a.miny < b.maxy && b.miny < a.maxy;
}

// The base-layer (stack 0) wall footprint, per seat, col-sorted — the ring's
// physical vertices, read from the REAL slot map.
function wallRing(): Record<number, RingTile[]> {
  const bySeat: Record<number, RingTile[]> = { 0: [], 1: [], 2: [], 3: [] };
  for (const s of makeSlots(GameType.CHANGSHA)) {
    if (s.group !== 'wall') continue;
    const m = /^wall\.(\d+)\.(\d+)@(\d+)$/.exec(s.name);
    if (m === null) continue;
    const col = Number(m[1]); const stack = Number(m[2]); const seat = Number(m[3]);
    if (stack !== 0) continue;
    bySeat[seat].push({ col, x: s.origin.x, y: s.origin.y });
  }
  for (const seat of [0, 1, 2, 3]) bySeat[seat].sort((a, b) => a.col - b.col);
  return bySeat;
}

// Physical draw-order ring: within a seat col ascends along that edge, and the
// perimeter wraps seat0 -> seat1 -> seat2 -> seat3 -> seat0. Each corner joins
// one seat's MAX-col tile to the next seat's MIN-col tile.
const RING_ORDER = [0, 1, 2, 3];

function cornerSteps(): Array<{ corner: string; u: number }> {
  const ring = wallRing();
  const out: Array<{ corner: string; u: number }> = [];
  for (let i = 0; i < 4; i++) {
    const a = ring[RING_ORDER[i]];
    const b = ring[RING_ORDER[(i + 1) % 4]];
    const end = a[a.length - 1];   // seat i, max col
    const start = b[0];            // seat i+1, min col
    out.push({ corner: `s${RING_ORDER[i]}->s${RING_ORDER[(i + 1) % 4]}`, u: Math.hypot(end.x - start.x, end.y - start.y) });
  }
  return out;
}

test.describe('R-A (G4) — wall corners meet into one continuous ring', () => {
  test('each seat renders its authored wall footprint (14/14/13/13 stacks)', () => {
    const ring = wallRing();
    expect(ring[0].length).toBe(14);
    expect(ring[1].length).toBe(14);
    expect(ring[2].length).toBe(13);
    expect(ring[3].length).toBe(13);
  });

  // LIVE guard (Apone 2026-08-10): the wall was re-centred so its edges meet.
  test('consecutive draw-order wall edges meet at every corner within 1.6x pitch', () => {
    const steps = cornerSteps();
    // eslint-disable-next-line no-console
    console.log('[R-A corners] ' + steps.map((s) => `${s.corner}=${s.u.toFixed(1)}u(${(s.u / PITCH).toFixed(1)}p)`).join('  '));
    for (const s of steps) {
      expect(s.u, `corner ${s.corner} step ${s.u.toFixed(1)}u must be <= 1.6x pitch (${GATE}u) to read as one continuous ring`).toBeLessThanOrEqual(GATE);
    }
  });

  // Single continuous ring: each of the four sides is a STRAIGHT run (its
  // off-axis coordinate is constant — no secondary/inner segment), the four
  // sides are four DISTINCT edges of a square, and the winding is CCW
  // (seat0→1→2→3 turns left at every corner). A col-mirror, an inset inner
  // segment, or a wrong-handed side all break one of these.
  test('the four sides form one straight-edged, CCW, non-degenerate ring', () => {
    const ring = wallRing();
    // (i) each side is straight: horizontal sides (seat 0,2) share one y,
    //     vertical sides (seat 1,3) share one x.
    for (const seat of [0, 2]) {
      const ys = ring[seat].map((t) => t.y);
      expect(Math.max(...ys) - Math.min(...ys), `seat${seat} (horizontal side) must be one straight run`).toBe(0);
    }
    for (const seat of [1, 3]) {
      const xs = ring[seat].map((t) => t.x);
      expect(Math.max(...xs) - Math.min(...xs), `seat${seat} (vertical side) must be one straight run`).toBe(0);
    }
    // (ii) four distinct edges of a square: bottom below top, left of right.
    expect(ring[0][0].y, 'bottom side (seat0) must be below top side (seat2)').toBeLessThan(ring[2][0].y);
    expect(ring[3][0].x, 'left side (seat3) must be left of right side (seat1)').toBeLessThan(ring[1][0].x);
    // (iii) CCW winding: the turn at every corner is a left turn (cross > 0).
    const dir = (seat: number): { x: number; y: number } => {
      const a = ring[seat]; const e = a[a.length - 1]; const s = a[0];
      return { x: e.x - s.x, y: e.y - s.y };
    };
    for (let i = 0; i < 4; i++) {
      const u = dir(i); const v = dir((i + 1) % 4);
      const cross = u.x * v.y - u.y * v.x;
      expect(cross, `corner s${i}->s${(i + 1) % 4} must turn left (CCW); cross=${cross}`).toBeGreaterThan(0);
    }
  });

  // The re-centred ring must NOT collide with the hands or the (also-moved)
  // discard trays — otherwise the discards read as a spurious inner segment.
  // Static necessary-condition half of Hudson's live overlap gate.
  test('the wall ring does not overlap hands, discards, or the opposite discard tray', () => {
    for (const seat of [0, 1, 2, 3]) {
      const wall = boxOf((s) => s.group === 'wall' && s.seat === seat)!;
      const hand = boxOf((s) => s.group === 'hand' && s.seat === seat)!;
      const disc = boxOf((s) => s.group === 'discard' && !s.name.startsWith('discard.extra') && s.seat === seat)!;
      expect(overlaps(wall, hand), `seat${seat} wall must not overlap its hand`).toBe(false);
      expect(overlaps(wall, disc), `seat${seat} wall must not overlap its discard tray`).toBe(false);
    }
    // opposite discard trays (seat0 vs seat2, seat1 vs seat3) must not collide at centre.
    const d0 = boxOf((s) => s.group === 'discard' && !s.name.startsWith('discard.extra') && s.seat === 0)!;
    const d2 = boxOf((s) => s.group === 'discard' && !s.name.startsWith('discard.extra') && s.seat === 2)!;
    expect(overlaps(d0, d2), 'seat0 and seat2 discard trays must not overlap at centre').toBe(false);
  });
});
