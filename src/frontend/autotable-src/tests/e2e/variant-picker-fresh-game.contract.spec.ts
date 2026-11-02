// Stuck-turn fix (Ferro) — contract test for the variant picker's fresh-game
// wiring (src/ui/variant-picker.ts → computeVariantNavigation).
//
// The bug (ddc72e1): choosing a variant only rewrote `?variant=` and kept the
// existing `?gameId=`, so "Setting to Changsha" re-JOINed the SAME already-
// running game (stale seats / stalled turn) instead of starting a fresh one.
//
// This spec covers the picker's URL COMPOSITION only — that switching variant
// mints a FRESH, isolated gameId (via Hicks's resolveApplyGameId), stamps the
// honest New-Game defaults (buildFreshGameUrl), preserves the user's other
// params, and no-ops on an unchanged concrete URL (deliberate reconnect).
//
// It deliberately does NOT re-test resolveApplyGameId / gameConfigDiffersFromUrl
// branch logic — that is owned by apply-gameid.contract.spec.ts.  No `page`
// fixture ⇒ does not overlap Hudson's live browser specs.

import { test, expect } from '@playwright/test';
import { computeVariantNavigation } from '../../src/ui/variant-picker';

// Deterministic mint so the fresh gameId is assertable.
const MINT = (): string => 'FRESH';

// A fully-specified, running Changsha game URL (concrete gameId + default cfg).
const CONCRETE_CHANGSHA =
  'http://localhost/autotable/?gameId=abc123&variant=changsha&dealMode=auto&botCount=3&botDifficulty=Hard&handCount=4';

function query(target: string): URLSearchParams {
  // buildFreshGameUrl returns a root-relative URL; resolve against any origin.
  return new URL(target, 'http://localhost').searchParams;
}

test.describe('variant-picker computeVariantNavigation — mint-fresh-on-switch', () => {
  test('switching variant on a concrete game mints a FRESH gameId (never reuses the old one)', () => {
    const target = computeVariantNavigation(CONCRETE_CHANGSHA, 'four-player', MINT);
    expect(target).not.toBeNull();
    const q = query(target as string);
    expect(q.get('variant')).toBe('four-player');
    expect(q.get('gameId')).toBe('FRESH');
    expect(q.get('gameId')).not.toBe('abc123');
  });

  test('"Setting to Changsha" from another variant starts a fresh game AND stamps dealMode=auto', () => {
    // The exact complaint: switching TO Changsha must not re-open the stale
    // game, and the fresh game must boot Auto (not the runtime's Manual
    // default) — buildFreshGameUrl stamps dealMode=auto for a bare Changsha.
    const target = computeVariantNavigation(
      'http://localhost/autotable/?gameId=oldgame&variant=four-player&botCount=3&handCount=4',
      'changsha',
      MINT,
    );
    expect(target).not.toBeNull();
    const q = query(target as string);
    expect(q.get('variant')).toBe('changsha');
    expect(q.get('gameId')).toBe('FRESH');
    expect(q.get('gameId')).not.toBe('oldgame');
    expect(q.get('dealMode')).toBe('auto');
  });

  test('bare URL (no concrete gameId) gains a fresh explicit gameId on switch', () => {
    const target = computeVariantNavigation(
      'http://localhost/autotable/?variant=changsha',
      'bamboo',
      MINT,
    );
    expect(target).not.toBeNull();
    const q = query(target as string);
    expect(q.get('variant')).toBe('bamboo');
    expect(q.get('gameId')).toBe('FRESH');
  });

  test('unchanged concrete URL (same variant/config) is a no-op — reconnect preserved', () => {
    const target = computeVariantNavigation(CONCRETE_CHANGSHA, 'changsha', MINT);
    expect(target).toBeNull();
  });

  test('the user\'s other params survive the switch (config + unrelated seat)', () => {
    const target = computeVariantNavigation(
      'http://localhost/autotable/?gameId=abc123&variant=changsha&botCount=2&seat=1',
      'minefield',
      MINT,
    );
    expect(target).not.toBeNull();
    const q = query(target as string);
    expect(q.get('variant')).toBe('minefield');
    expect(q.get('gameId')).toBe('FRESH');
    expect(q.get('botCount')).toBe('2'); // preserved config
    expect(q.get('seat')).toBe('1');     // unrelated param preserved
  });

  test('default (uninjected) mint produces a fresh changsha-<hex> id, not the old one', () => {
    const target = computeVariantNavigation(CONCRETE_CHANGSHA, 'four-player');
    expect(target).not.toBeNull();
    const q = query(target as string);
    expect(q.get('variant')).toBe('four-player');
    expect(q.get('gameId')).toMatch(/^changsha-[0-9a-f]{8}$/);
    expect(q.get('gameId')).not.toBe('abc123');
  });
});
