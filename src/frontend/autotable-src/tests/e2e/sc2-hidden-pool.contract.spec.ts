// Contract tests — SC-2 (G19) anonymous hidden-back pool reconciliation core.
// Browser-free; the render/raycast integration (instanced capacity, real-pointer
// pickup on a back) is Hudson's browser G17/G19. These lock the "what should
// happen" logic: mixed keys, reveal, reconnect, no duplicate physical count.
import { test, expect } from '@playwright/test';
import {
  reconcileHiddenBacks,
  reconcileRealVisibility,
  physicalTileCount,
  occupiedSlots,
  hasUniquePhysicalSlots,
  HiddenSlotInfo,
} from '../../src/sc2-hidden-pool';

const POOL = 108;
const wall = (i: number): [string, HiddenSlotInfo | null] =>
  [`h:${i}`, { slotName: `wall.${i}@${i % 4}`, rotationIndex: 2 }];

test.describe('sc2-hidden-pool — 108 opaque wall (pre-deal: everything hidden)', () => {
  const entries: Array<[string, HiddenSlotInfo | null]> = Array.from({ length: 108 }, (_, i) => wall(i));

  test('all 108 become backs; every real Thing is hidden; count is exactly 108', () => {
    const back = reconcileHiddenBacks(entries, [], /*full*/ true);
    expect(back.place).toHaveLength(108);
    expect(back.release).toHaveLength(0);

    const real = reconcileRealVisibility([], POOL, true);
    expect(real.show).toHaveLength(0);
    expect(real.hide).toHaveLength(108);

    // no duplicate physical count: 108 backs + 0 reals = 108 tiles rendered once.
    expect(physicalTileCount(real.show.length, back.place.length)).toBe(108);
  });
});

test.describe('sc2-hidden-pool — mixed own / foreign / public', () => {
  // My hand (13 real), a public discard (1 real), rest hidden (94 handles).
  const numeric = [...Array(13).keys(), 40];                 // 14 entitled ids
  const hidden: Array<[string, HiddenSlotInfo | null]> =
    Array.from({ length: 94 }, (_, i) => wall(100 + i));

  test('entitled ids shown, all others hidden, handles → backs, total 108', () => {
    const real = reconcileRealVisibility(numeric, POOL, true);
    const back = reconcileHiddenBacks(hidden, [], true);

    expect(real.show.sort((a, b) => a - b)).toEqual(numeric.sort((a, b) => a - b));
    expect(real.hide).toHaveLength(POOL - numeric.length);      // 94 hidden reals
    // hidden reals and shown reals are disjoint and cover the pool
    expect(new Set([...real.show, ...real.hide]).size).toBe(POOL);
    expect(back.place).toHaveLength(94);

    expect(physicalTileCount(real.show.length, back.place.length)).toBe(108); // 14 + 94
  });
});

test.describe('sc2-hidden-pool — handle→numeric reveal (atomic, no tombstone)', () => {
  test('a handle absent in the new full snapshot is released; the revealed real id shows', () => {
    // snap1: tile is a hidden wall back under handle h:draw.
    const snap1Hidden: Array<[string, HiddenSlotInfo | null]> = [['h:draw', { slotName: 'wall.0.1@0', rotationIndex: 2 }]];
    const p1 = reconcileHiddenBacks(snap1Hidden, [], true);
    expect(p1.place.map(p => p.handle)).toEqual(['h:draw']);

    // snap2 (full): h:draw is ABSENT (no explicit tombstone) and real id 40 now
    // appears in the hand ⇒ the back is released, the real id is shown.
    const snap2Hidden: Array<[string, HiddenSlotInfo | null]> = []; // h:draw gone
    const p2 = reconcileHiddenBacks(snap2Hidden, ['h:draw'], true);
    expect(p2.place).toHaveLength(0);
    expect(p2.release).toEqual(['h:draw']);

    const real2 = reconcileRealVisibility([40], POOL, true);
    expect(real2.show).toContain(40);
    expect(real2.hide).not.toContain(40);
  });

  test('incremental (non-full) does NOT release absent handles (only explicit null)', () => {
    const p = reconcileHiddenBacks([['h:x', null]], ['h:x', 'h:y'], /*full*/ false);
    expect(p.release).toEqual(['h:x']);          // explicit null released
    expect(p.release).not.toContain('h:y');      // absent-but-not-null kept (incremental)
  });
});

test.describe('sc2-hidden-pool — reconnect reconciliation (stable, no leaks)', () => {
  test('same full snapshot re-applied (reconnect) yields an identical plan', () => {
    const hidden: Array<[string, HiddenSlotInfo | null]> = [wall(1), wall(2), wall(3)];
    const numeric = [10, 11, 12];

    const firstBacks = reconcileHiddenBacks(hidden, [], true);
    const firstReal = reconcileRealVisibility(numeric, POOL, true);
    // reconnect: full snapshot replays; the same stable handles are already assigned.
    const againBacks = reconcileHiddenBacks(hidden, firstBacks.place.map(p => p.handle), true);
    const againReal = reconcileRealVisibility(numeric, POOL, true);

    expect(againBacks.place).toEqual(firstBacks.place);   // same handle→slot, no churn
    expect(againBacks.release).toEqual([]);               // nothing spuriously released
    expect(againReal).toEqual(firstReal);                 // identical real visibility
  });
});

test.describe('sc2-hidden-pool — no duplicate physical tile count (full snapshot)', () => {
  test('shown reals + placed backs always total the 108-tile deck', () => {
    for (const entitled of [0, 14, 42, 108]) {
      const numeric = [...Array(entitled).keys()];
      const hidden: Array<[string, HiddenSlotInfo | null]> =
        Array.from({ length: 108 - entitled }, (_, i) => wall(1000 + i));
      const real = reconcileRealVisibility(numeric, POOL, true);
      const back = reconcileHiddenBacks(hidden, [], true);
      expect(physicalTileCount(real.show.length, back.place.length)).toBe(108);
      // every physical tile is EITHER a shown real OR a back — never both.
      expect(real.show.length + real.hide.length).toBe(POOL);
    }
  });
});

test.describe('sc2-hidden-pool — order-independence (key+slot, NOT tuple order)', () => {
  // The backend sorts slot-canonical, but the client must derive identity/draw
  // from (key, slot) ONLY — a shuffled snapshot must reconcile identically.
  const shuffle = <T>(a: ReadonlyArray<T>, seed: number): T[] => {
    const arr = [...a];
    for (let i = arr.length - 1; i > 0; i--) {
      seed = (seed * 1103515245 + 12345) & 0x7fffffff;
      const j = seed % (i + 1);
      [arr[i], arr[j]] = [arr[j], arr[i]];
    }
    return arr;
  };
  const keySlot = (p: { handle: string; slotName: string }): string => `${p.handle}@${p.slotName}`;

  test('shuffling the incoming tuple order yields the SAME placements + releases (as sets)', () => {
    const hidden: Array<[string, HiddenSlotInfo | null]> =
      Array.from({ length: 40 }, (_, i) => wall(i));
    const a = reconcileHiddenBacks(hidden, [], true);
    const b = reconcileHiddenBacks(shuffle(hidden, 7), [], true);
    expect(new Set(a.place.map(keySlot))).toEqual(new Set(b.place.map(keySlot)));
    expect(new Set(a.release)).toEqual(new Set(b.release));
  });

  test('real visibility is a pure function of the numeric SET, not order', () => {
    const numeric = [3, 1, 2, 99, 40];
    const p1 = reconcileRealVisibility(numeric, POOL, true);
    const p2 = reconcileRealVisibility(shuffle(numeric, 99), POOL, true);
    expect(new Set(p1.show)).toEqual(new Set(p2.show));
    expect(new Set(p1.hide)).toEqual(new Set(p2.hide));
  });
});

test.describe('sc2-hidden-pool — 108 unique physical slots', () => {
  test('a full 108-entry mixed snapshot occupies exactly 108 DISTINCT slots', () => {
    const numeric: Array<[number, HiddenSlotInfo | null]> =
      Array.from({ length: 14 }, (_, i) => [i, { slotName: `hand.${i}@0` }]);
    const hidden: Array<[string, HiddenSlotInfo | null]> =
      Array.from({ length: 94 }, (_, i) => wall(500 + i));
    const snap: Array<[string | number, HiddenSlotInfo | null]> = [...numeric, ...hidden];
    expect(hasUniquePhysicalSlots(snap, 108)).toBe(true);
    expect(occupiedSlots(snap).size).toBe(108);
  });

  test('a duplicate slot (two tiles claiming one physical slot) is rejected by the invariant', () => {
    const dup: Array<[string | number, HiddenSlotInfo | null]> = [
      [0, { slotName: 'wall.0.0@0' }],
      ['h:x', { slotName: 'wall.0.0@0' }], // same slot ⇒ not 108 unique
    ];
    expect(hasUniquePhysicalSlots(dup, 2)).toBe(false);
    expect(occupiedSlots(dup).size).toBe(1);
  });
});
