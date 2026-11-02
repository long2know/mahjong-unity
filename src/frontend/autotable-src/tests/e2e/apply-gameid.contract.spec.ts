// Stuck-turn fix (Hicks) — contract test for the Apply/reconfigure gameId
// resolution (src/session-url.ts).  Browser-free coverage of the Ripley Design
// Review defect-1 decision: changing variant/config must mint a FRESH gameId
// (start a new game) rather than silently re-opening an in-progress game whose
// seats belong to other/absent connections; an UNCHANGED reload of an explicit
// concrete game URL must preserve the gameId (deliberate reconnect).
//
// No `page` fixture ⇒ does not overlap Hudson's live browser specs.

import { test, expect } from '@playwright/test';
import {
  resolveApplyGameId,
  gameConfigDiffersFromUrl,
  GameDefiningConfig,
} from '../../src/session-url';

const MINT = (): string => 'FRESH';

// The lobby/New-Game defaults (kept in lock-step with NEW_GAME_DEFAULTS).
const DEFAULTS: GameDefiningConfig = {
  variant: 'changsha',
  dealMode: 'auto',
  botCount: 3,
  botDifficulty: 'Hard',
  handCount: 4,
  seed: null,
};

test.describe('resolveApplyGameId — mint-fresh-on-reconfigure vs reuse-on-reload', () => {
  test('no concrete gameId on URL ⇒ mint fresh', () => {
    expect(resolveApplyGameId('?variant=changsha', DEFAULTS, MINT)).toBe('FRESH');
    expect(resolveApplyGameId('', DEFAULTS, MINT)).toBe('FRESH');
  });

  test('concrete gameId + identical config ⇒ reuse (deliberate reconnect)', () => {
    const search = '?gameId=abc123&variant=changsha&dealMode=auto&botCount=3&botDifficulty=Hard&handCount=4';
    expect(resolveApplyGameId(search, DEFAULTS, MINT)).toBe('abc123');
  });

  test('minimal ?gameId= URL + default config ⇒ reuse (defaults normalised, no spurious mint)', () => {
    expect(resolveApplyGameId('?gameId=abc123', DEFAULTS, MINT)).toBe('abc123');
  });

  test('changed variant ⇒ mint fresh', () => {
    const search = '?gameId=abc123&variant=changsha&dealMode=auto&botCount=3&botDifficulty=Hard&handCount=4';
    expect(resolveApplyGameId(search, { ...DEFAULTS, variant: 'four_player' }, MINT)).toBe('FRESH');
  });

  test('changed dealMode (Changsha) ⇒ mint fresh', () => {
    const search = '?gameId=abc123&variant=changsha&dealMode=auto&botCount=3&botDifficulty=Hard&handCount=4';
    expect(resolveApplyGameId(search, { ...DEFAULTS, dealMode: 'manual' }, MINT)).toBe('FRESH');
  });

  test('changed botCount ⇒ mint fresh', () => {
    const search = '?gameId=abc123&variant=changsha&dealMode=auto&botCount=3&botDifficulty=Hard&handCount=4';
    expect(resolveApplyGameId(search, { ...DEFAULTS, botCount: 2 }, MINT)).toBe('FRESH');
  });

  test('changed botDifficulty (bots present) ⇒ mint fresh', () => {
    const search = '?gameId=abc123&variant=changsha&dealMode=auto&botCount=3&botDifficulty=Hard&handCount=4';
    expect(resolveApplyGameId(search, { ...DEFAULTS, botDifficulty: 'Easy' }, MINT)).toBe('FRESH');
  });

  test('changed handCount ⇒ mint fresh', () => {
    const search = '?gameId=abc123&variant=changsha&dealMode=auto&botCount=3&botDifficulty=Hard&handCount=4';
    expect(resolveApplyGameId(search, { ...DEFAULTS, handCount: 8 }, MINT)).toBe('FRESH');
  });

  test('changed seed ⇒ mint fresh', () => {
    const search = '?gameId=abc123&variant=changsha&dealMode=auto&botCount=3&botDifficulty=Hard&handCount=4&seed=7';
    expect(resolveApplyGameId(search, { ...DEFAULTS, seed: 9 }, MINT)).toBe('FRESH');
    // clearing an explicit seed is also a reconfiguration
    expect(resolveApplyGameId(search, { ...DEFAULTS, seed: null }, MINT)).toBe('FRESH');
  });
});

test.describe('gameConfigDiffersFromUrl — variant-scoped normalisation', () => {
  test('dealMode is ignored for non-Changsha variants (Riichi reload reuses)', () => {
    const search = '?gameId=abc123&variant=four_player&botCount=3&botDifficulty=Hard&handCount=4';
    const cfg: GameDefiningConfig = { variant: 'four_player', dealMode: 'manual', botCount: 3, botDifficulty: 'Hard', handCount: 4, seed: null };
    expect(gameConfigDiffersFromUrl(search, cfg)).toBe(false);
    expect(resolveApplyGameId(search, cfg, MINT)).toBe('abc123');
  });

  test('botDifficulty is ignored when there are zero bots', () => {
    const search = '?gameId=abc123&variant=changsha&dealMode=auto&botCount=0&handCount=4';
    const cfg: GameDefiningConfig = { variant: 'changsha', dealMode: 'auto', botCount: 0, botDifficulty: 'Easy', handCount: 4, seed: null };
    expect(gameConfigDiffersFromUrl(search, cfg)).toBe(false);
  });

  test('identical config ⇒ not different', () => {
    const search = '?gameId=abc123&variant=changsha&dealMode=auto&botCount=3&botDifficulty=Hard&handCount=4';
    expect(gameConfigDiffersFromUrl(search, DEFAULTS)).toBe(false);
  });
});
