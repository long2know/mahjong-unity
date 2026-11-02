// Contract tests — New Game UX P0 (Hicks). Browser-free coverage of the
// authoritative one-click fresh-game URL: fresh gameId, config + identity
// preservation, seat handoff, single navigation, no stale reuse. The DOM-level
// behaviour (debounce, WS teardown on reload, no relay UPDATE) is Hudson's
// browser lane; these lock the pure URL contract the handler applies.
import { test, expect } from '@playwright/test';
import { buildFreshGameUrl, mintFreshGameId, resolveHandoffSeat } from '../../src/session-url';

const PATH = '/autotable/';
const FULL = '?variant=changsha&dealMode=auto&botCount=3&botDifficulty=Medium&handCount=4&gameId=changsha-OLD1234&seat=2';
const parse = (url: string): URLSearchParams => new URLSearchParams(url.split('?')[1]);

test.describe('new-game P0 — fresh gameId (no stale reuse)', () => {
  test('mints a fresh gameId, never reuses the URL gameId', () => {
    const url = buildFreshGameUrl(PATH, FULL, 'changsha-NEW9999', 2);
    const p = parse(url);
    expect(p.get('gameId')).toBe('changsha-NEW9999');
    expect(p.get('gameId')).not.toBe('changsha-OLD1234');
  });

  test('mintFreshGameId is a changsha-<hex> id (unpredictable, unique per call)', () => {
    const a = mintFreshGameId();
    const b = mintFreshGameId();
    expect(a).toMatch(/^changsha-[0-9a-f]{8}$/);
    expect(a).not.toBe(b);          // fresh each activation
  });
});

test.describe('new-game P0 — config + identity preservation', () => {
  test('preserves variant/dealMode/botCount/botDifficulty/handCount verbatim', () => {
    const p = parse(buildFreshGameUrl(PATH, FULL, 'changsha-NEW', 2));
    expect(p.get('variant')).toBe('changsha');
    expect(p.get('dealMode')).toBe('auto');
    expect(p.get('botCount')).toBe('3');
    expect(p.get('botDifficulty')).toBe('Medium');   // user's value, not the default
    expect(p.get('handCount')).toBe('4');
  });

  test('a manual deep-link keeps dealMode=manual (begins the human ceremony)', () => {
    const p = parse(buildFreshGameUrl(PATH, '?variant=changsha&dealMode=manual', 'changsha-NEW'));
    expect(p.get('dealMode')).toBe('manual');
  });

  test('missing config gains the New Game defaults (changsha / auto / 3 / Hard / 4)', () => {
    const p = parse(buildFreshGameUrl(PATH, '', 'changsha-NEW'));
    expect(p.get('variant')).toBe('changsha');
    expect(p.get('dealMode')).toBe('auto');
    expect(p.get('botCount')).toBe('3');
    expect(p.get('botDifficulty')).toBe('Hard');
    expect(p.get('handCount')).toBe('4');
  });
});

test.describe('new-game P0 — seat handoff', () => {
  test('an owned seat (0..3) is stamped for auto-take in the fresh game', () => {
    for (const seat of [0, 1, 2, 3]) {
      expect(parse(buildFreshGameUrl(PATH, FULL, 'changsha-NEW', seat)).get('seat')).toBe(String(seat));
    }
  });

  test('explicit spectator (-1) is honoured', () => {
    expect(parse(buildFreshGameUrl(PATH, FULL, 'changsha-NEW', -1)).get('seat')).toBe('-1');
  });

  test('no owned seat (null/undefined) preserves the URL seat verbatim', () => {
    // seat=2 already on FULL ⇒ preserved when the caller has no live seat.
    expect(parse(buildFreshGameUrl(PATH, FULL, 'changsha-NEW', null)).get('seat')).toBe('2');
    expect(parse(buildFreshGameUrl(PATH, '?variant=changsha', 'changsha-NEW', null)).get('seat')).toBeNull();
  });
});

test.describe('new-game P0 — owned-seat resolution (resolveHandoffSeat: preserve same seat, no re-pick)', () => {
  test('the LIVE seat is preferred (seated ⇒ keep this chair)', () => {
    expect(resolveHandoffSeat(2, null)).toBe(2);
    expect(resolveHandoffSeat(2, 3)).toBe(2);          // live wins over a stale reconnect seat
  });

  test('seat 0 is a REAL seat — never treated as "no seat" (the `||` footgun)', () => {
    expect(resolveHandoffSeat(0, null)).toBe(0);       // must NOT fall through to reconnect/null
    expect(resolveHandoffSeat(0, 3)).toBe(0);
    expect(resolveHandoffSeat(null, 0)).toBe(0);       // reconnect seat 0 is likewise preserved
  });

  test('falls back to the pre-disconnect seat when not currently seated', () => {
    expect(resolveHandoffSeat(null, 1)).toBe(1);       // New Game while disconnected re-takes the chair
    expect(resolveHandoffSeat(undefined, 1)).toBe(1);
  });

  test('explicit spectator (-1) is preserved through the resolution', () => {
    expect(resolveHandoffSeat(-1, null)).toBe(-1);
    expect(resolveHandoffSeat(null, -1)).toBe(-1);
  });

  test('no owned seat anywhere ⇒ null (fresh game assigns / spectates)', () => {
    expect(resolveHandoffSeat(null, null)).toBeNull();
    expect(resolveHandoffSeat(undefined, undefined)).toBeNull();
  });
});

test.describe('new-game P0 — seat handoff end-to-end (resolveHandoffSeat → buildFreshGameUrl)', () => {
  // Mirrors newGame(): the resolved owned seat is stamped onto the fresh URL so
  // one click re-takes the SAME chair with no Take-Seat interaction.
  const freshSeatParam = (live: number | null, recon: number | null, search = FULL): string | null =>
    parse(buildFreshGameUrl(PATH, search, 'changsha-NEW', resolveHandoffSeat(live, recon))).get('seat');

  test('a live seat (incl. 0) is handed off verbatim', () => {
    expect(freshSeatParam(0, null)).toBe('0');
    expect(freshSeatParam(3, null)).toBe('3');
  });

  test('disconnected New Game re-takes the pre-disconnect seat', () => {
    expect(freshSeatParam(null, 2)).toBe('2');
  });

  test('no owned seat leaves the fresh URL without an injected seat (bare search ⇒ null)', () => {
    expect(freshSeatParam(null, null, '?variant=changsha')).toBeNull();
  });
});

test.describe('new-game P0 — single, well-formed navigation URL', () => {
  test('exactly one gameId and one seat (single navigation target)', () => {
    const url = buildFreshGameUrl(PATH, FULL, 'changsha-NEW', 1);
    expect(url.startsWith(`${PATH}?`)).toBe(true);
    expect(url.split('?').length).toBe(2);                 // one query
    expect((url.match(/gameId=/g) ?? []).length).toBe(1);  // one gameId
    expect((url.match(/(^|&|\?)seat=/g) ?? []).length).toBe(1); // one seat
  });

  test('the user\'s other params (e.g. seed) survive the fresh navigation', () => {
    const p = parse(buildFreshGameUrl(PATH, `${FULL}&seed=abc`, 'changsha-NEW', 0));
    expect(p.get('seed')).toBe('abc');
    expect(p.get('gameId')).toBe('changsha-NEW');
  });
});
