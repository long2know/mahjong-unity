// #147 (Hicks) — contract test for the pure discard-readiness accounting.
//
// Covers EVERY meld variant deterministically without a browser: Pung, Chow,
// exposed Kong, concealed Kong, added Kong, multi-meld, rest states, the
// `hand.extra@` preview exclusion, orphan-slot skipping, foreign-seat
// isolation, and the relay (`meldAware === false`) gating.  Runs in the
// Playwright/Node process (no `page`), so it is fully deterministic and covers
// the Kong variants that are impractical to force via live real-pointer play.

import { test, expect } from '@playwright/test';
import { hasExtraDiscardTile, HandSlotView } from '../../src/hand-accounting';

const SEAT = 0;

function hand(n: number, seat = SEAT, ownsSlot = true): HandSlotView[] {
  return Array.from({ length: n }, (_, i) => ({
    group: 'hand',
    seat,
    name: `hand.${i}@${seat}`,
    ownsSlot,
  }));
}

// A meld of `tiles` tiles at meld index `m` (3 = Pung/Chow, 4 = any Kong).
function meld(m: number, tiles: number, seat = SEAT): HandSlotView[] {
  return Array.from({ length: tiles }, (_, t) => ({
    group: 'meld',
    seat,
    name: `meld.${m}.${t}@${seat}`,
    ownsSlot: true,
  }));
}

test.describe('#147 hasExtraDiscardTile — meld-aware discard readiness (Changsha)', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium', 'pure accounting — run once');
  });

  test('normal draw (14 concealed, 0 melds) owes a discard', () => {
    expect(hasExtraDiscardTile(hand(14), SEAT, true)).toBe(true);
  });

  test('rest hand (13 concealed, 0 melds) owes nothing', () => {
    expect(hasExtraDiscardTile(hand(13), SEAT, true)).toBe(false);
  });

  test('Pung (11 concealed + 1 meld×3) owes a discard', () => {
    expect(hasExtraDiscardTile([...hand(11), ...meld(0, 3)], SEAT, true)).toBe(true);
  });

  test('Chow (11 concealed + 1 meld×3) owes a discard', () => {
    expect(hasExtraDiscardTile([...hand(11), ...meld(0, 3)], SEAT, true)).toBe(true);
  });

  test('exposed Kong (11 concealed + 1 meld×4, post-replacement) owes a discard', () => {
    expect(hasExtraDiscardTile([...hand(11), ...meld(0, 4)], SEAT, true)).toBe(true);
  });

  test('concealed Kong (11 concealed + 1 meld×4) owes a discard', () => {
    expect(hasExtraDiscardTile([...hand(11), ...meld(0, 4)], SEAT, true)).toBe(true);
  });

  test('added Kong (11 concealed + 1 upgraded meld×4) owes a discard', () => {
    expect(hasExtraDiscardTile([...hand(11), ...meld(0, 4)], SEAT, true)).toBe(true);
  });

  test('post-meld rest (10 concealed + 1 meld) owes nothing', () => {
    expect(hasExtraDiscardTile([...hand(10), ...meld(0, 3)], SEAT, true)).toBe(false);
    expect(hasExtraDiscardTile([...hand(10), ...meld(0, 4)], SEAT, true)).toBe(false);
  });

  test('two melds must-discard (8 concealed + 2 melds) owes a discard', () => {
    expect(hasExtraDiscardTile([...hand(8), ...meld(0, 3), ...meld(1, 3)], SEAT, true)).toBe(true);
  });

  test('two melds rest (7 concealed + 2 melds) owes nothing', () => {
    expect(hasExtraDiscardTile([...hand(7), ...meld(0, 3), ...meld(1, 3)], SEAT, true)).toBe(false);
  });

  test('Pung + exposed Kong (8 concealed + 3 + 4) owes a discard', () => {
    expect(hasExtraDiscardTile([...hand(8), ...meld(0, 3), ...meld(1, 4)], SEAT, true)).toBe(true);
  });

  test('the `hand.extra@` preview slot is excluded from the count', () => {
    const withPreview: HandSlotView[] = [
      ...hand(13),
      { group: 'hand', seat: SEAT, name: `hand.extra@${SEAT}`, ownsSlot: true },
    ];
    expect(hasExtraDiscardTile(withPreview, SEAT, true)).toBe(false);
  });

  test('orphan tiles (ownsSlot === false) are skipped', () => {
    // 14 hand entries but one is an orphan → 13 counted → no discard owed.
    expect(hasExtraDiscardTile([...hand(13), ...hand(1, SEAT, false)], SEAT, true)).toBe(false);
  });

  test("other seats' tiles do not count", () => {
    const mixed: HandSlotView[] = [
      ...hand(11),
      ...meld(0, 3),
      ...hand(14, 1), // seat 1 owes a discard, but we ask about seat 0
      ...meld(0, 4, 2), // seat 2 meld
    ];
    expect(hasExtraDiscardTile(mixed, SEAT, true)).toBe(true);
    // And seat 1 is independently correct.
    expect(hasExtraDiscardTile(mixed, 1, true)).toBe(true);
  });

  test('malformed meld slot name is ignored (no NaN/undefined index)', () => {
    const weird: HandSlotView[] = [
      ...hand(11),
      { group: 'meld', seat: SEAT, name: `meld@${SEAT}`, ownsSlot: true },
    ];
    // No valid meld index parsed → meld not counted → 11 → false (fails safe).
    expect(hasExtraDiscardTile(weird, SEAT, true)).toBe(false);
  });
});

test.describe('#147 hasExtraDiscardTile — relay variants keep concealed-only behaviour', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium', 'pure accounting — run once');
  });

  test('relay: melds are NOT counted (11 + meld → false)', () => {
    expect(hasExtraDiscardTile([...hand(11), ...meld(0, 3)], SEAT, false)).toBe(false);
    expect(hasExtraDiscardTile([...hand(11), ...meld(0, 4)], SEAT, false)).toBe(false);
  });

  test('relay: a plain 14-concealed hand still owes a discard', () => {
    expect(hasExtraDiscardTile(hand(14), SEAT, false)).toBe(true);
  });

  test('relay: 13 concealed owes nothing', () => {
    expect(hasExtraDiscardTile(hand(13), SEAT, false)).toBe(false);
  });
});
