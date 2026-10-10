// Stuck-turn fix (Hicks) — contract test for the pure turn-cue resolver.
//
// Browser-free, deterministic coverage of computeTurnCue / isMyDiscardTurn /
// resolveActiveSeat (src/turn-cue.ts): the authoritative-first, geometry-as-
// defence-in-depth decision that drives the "Your turn / Waiting for Seat N /
// Spectating / No open seat" banner and the click-to-discard gate.  Runs in
// the Playwright/Node process (no `page`), so it does NOT overlap Hudson's
// live browser specs (c5-stale-game-actionable-seat, setup-select-contrast).

import { test, expect } from '@playwright/test';
import {
  computeTurnCue,
  isMyDiscardTurn,
  resolveActiveSeat,
  claimActionable,
  selectSelfClaim,
  newGameBannerA11y,
  TurnCueInput,
} from '../../src/turn-cue';

// A seated, mid-hand baseline; individual tests override just what they probe.
function input(overrides: Partial<TurnCueInput> = {}): TurnCueInput {
  return {
    mySeat: 0,
    isSpectatorUrl: false,
    inProgress: true,
    activeSeatSignal: undefined,
    awaitingDiscardSignal: undefined,
    myHasExtraTile: false,
    activeSeatByGeometry: null,
    allSeatsOccupied: true,
    ...overrides,
  };
}

test.describe('turn-cue — isMyDiscardTurn (authoritative ∪ geometry)', () => {
  test('signal: active seat is me AND awaitingDiscard ⇒ true', () => {
    expect(isMyDiscardTurn({
      mySeat: 0, activeSeatSignal: 0, awaitingDiscardSignal: true, myHasExtraTile: false,
    })).toBe(true);
  });

  test('signal: active seat is me but NOT awaitingDiscard ⇒ false', () => {
    expect(isMyDiscardTurn({
      mySeat: 0, activeSeatSignal: 0, awaitingDiscardSignal: false, myHasExtraTile: false,
    })).toBe(false);
  });

  test('signal present but says another seat ⇒ false (retraction overrides stale geometry)', () => {
    // Bishop C-1: when the authoritative signal is present it wins, even if a
    // stale `things` snapshot momentarily shows our 14th tile.
    expect(isMyDiscardTurn({
      mySeat: 0, activeSeatSignal: 1, awaitingDiscardSignal: true, myHasExtraTile: true,
    })).toBe(false);
  });

  test('signal retracted (activeSeat null, awaitingDiscard false) ⇒ false even with extra tile', () => {
    // e.g. Scoring / claim window: the cue is retracted; geometry must not
    // resurrect a "your turn to discard" during the result modal.
    expect(isMyDiscardTurn({
      mySeat: 0, activeSeatSignal: null, awaitingDiscardSignal: false, myHasExtraTile: true,
    })).toBe(false);
  });

  test('no signal, geometry shows extra tile ⇒ true (older backend fallback)', () => {
    expect(isMyDiscardTurn({
      mySeat: 0, activeSeatSignal: undefined, awaitingDiscardSignal: undefined, myHasExtraTile: true,
    })).toBe(true);
  });

  test('no signal, no extra tile ⇒ false', () => {
    expect(isMyDiscardTurn({
      mySeat: 0, activeSeatSignal: undefined, awaitingDiscardSignal: undefined, myHasExtraTile: false,
    })).toBe(false);
  });

  test('no seat ⇒ false', () => {
    expect(isMyDiscardTurn({
      mySeat: null, activeSeatSignal: 0, awaitingDiscardSignal: true, myHasExtraTile: true,
    })).toBe(false);
  });
});

test.describe('turn-cue — resolveActiveSeat (authoritative-first)', () => {
  test('signal seat wins over geometry', () => {
    expect(resolveActiveSeat({ activeSeatSignal: 2, activeSeatByGeometry: 1 })).toBe(2);
  });
  test('explicit null signal ⇒ null (trusts "no seat on clock" over stale geometry)', () => {
    expect(resolveActiveSeat({ activeSeatSignal: null, activeSeatByGeometry: 3 })).toBe(null);
  });
  test('absent signal ⇒ geometry heuristic', () => {
    expect(resolveActiveSeat({ activeSeatSignal: undefined, activeSeatByGeometry: 3 })).toBe(3);
  });
});

test.describe('turn-cue — computeTurnCue', () => {
  test('my discard turn via signal ⇒ discard', () => {
    expect(computeTurnCue(input({ activeSeatSignal: 0, awaitingDiscardSignal: true })))
      .toEqual({ kind: 'discard' });
  });

  test('my discard turn via geometry only ⇒ discard', () => {
    expect(computeTurnCue(input({ myHasExtraTile: true }))).toEqual({ kind: 'discard' });
  });

  test('another seat on the clock (signal) ⇒ waiting for that seat', () => {
    expect(computeTurnCue(input({ activeSeatSignal: 2, awaitingDiscardSignal: true })))
      .toEqual({ kind: 'waiting', seat: 2 });
  });

  test('another seat on the clock (geometry heuristic) ⇒ waiting for that seat', () => {
    expect(computeTurnCue(input({ activeSeatByGeometry: 1 })))
      .toEqual({ kind: 'waiting', seat: 1 });
  });

  test('seated, no one determinable on the clock, in progress ⇒ waiting-unknown', () => {
    expect(computeTurnCue(input({ activeSeatSignal: null }))).toEqual({ kind: 'waiting-unknown' });
  });

  test('seated, not in progress (pre-deal / complete) ⇒ none', () => {
    expect(computeTurnCue(input({ inProgress: false, activeSeatSignal: null })))
      .toEqual({ kind: 'none' });
  });

  test('no seat + spectator URL ⇒ spectating', () => {
    expect(computeTurnCue(input({ mySeat: null, isSpectatorUrl: true })))
      .toEqual({ kind: 'spectating', seat: null });
  });

  test('spectator sees whose turn it is (active seat surfaced)', () => {
    expect(computeTurnCue(input({ mySeat: null, isSpectatorUrl: true, activeSeatSignal: 2, awaitingDiscardSignal: true })))
      .toEqual({ kind: 'spectating', seat: 2 });
    expect(computeTurnCue(input({ mySeat: null, isSpectatorUrl: true, activeSeatByGeometry: 1 })))
      .toEqual({ kind: 'spectating', seat: 1 });
  });

  test('no seat + in-progress + all seats occupied ⇒ no-open-seat (stale-game deadlock)', () => {
    expect(computeTurnCue(input({ mySeat: null, isSpectatorUrl: false, inProgress: true, allSeatsOccupied: true })))
      .toEqual({ kind: 'no-open-seat' });
  });

  test('no seat + not-yet-in-progress ⇒ none (lobby / connecting)', () => {
    expect(computeTurnCue(input({ mySeat: null, isSpectatorUrl: false, inProgress: false, allSeatsOccupied: false })))
      .toEqual({ kind: 'none' });
  });

  test('no seat + in-progress but a seat is open ⇒ none (can still take-seat)', () => {
    expect(computeTurnCue(input({ mySeat: null, isSpectatorUrl: false, inProgress: true, allSeatsOccupied: false })))
      .toEqual({ kind: 'none' });
  });

  test('spectator URL never reads as no-open-seat even with all seats taken', () => {
    expect(computeTurnCue(input({ mySeat: null, isSpectatorUrl: true, inProgress: true, allSeatsOccupied: true })))
      .toEqual({ kind: 'spectating', seat: null });
  });
});

test.describe('turn-cue — newGameBannerA11y (actionable New Game banner)', () => {
  test('actionable ⇒ pointer-events auto (overrides the base pointer-events:none) + button a11y', () => {
    const a = newGameBannerA11y(true);
    expect(a).not.toBeNull();
    // The exact regression Hudson caught: the control was visually live but
    // DEAD because pointer-events was never re-enabled.  Guard it here.
    expect(a!.pointerEvents).toBe('auto');
    expect(a!.role).toBe('button');
    expect(a!.tabIndex).toBe(0);
    expect(a!.cursor).toBe('pointer');
    expect(typeof a!.ariaLabel).toBe('string');
    expect(a!.ariaLabel.length).toBeGreaterThan(0);
  });

  test('non-actionable ⇒ null (game-ui resets to the click-through status pill)', () => {
    expect(newGameBannerA11y(false)).toBeNull();
  });
});

test.describe('turn-cue — claimActionable (R-1 §D9 claim/discard exclusivity)', () => {
  test('a genuine claim (not my discard/pickup turn) is actionable', () => {
    expect(claimActionable(true, false, false)).toBe(true);
  });

  test('no claim ⇒ never actionable', () => {
    expect(claimActionable(false, false, false)).toBe(false);
  });

  test('STALE claim during my discard turn ⇒ suppressed (the D9 stale-window bug)', () => {
    expect(claimActionable(true, true, false)).toBe(false);
  });

  test('claim during my pickup turn ⇒ suppressed (mutually exclusive)', () => {
    expect(claimActionable(true, false, true)).toBe(false);
  });
});

test.describe('turn-cue — D4 deterministic claim teardown (selectSelfClaim)', () => {
  type Claim = { available: string[] };
  const open: Claim = { available: ['Pung'] };

  test('a close tombstone for my seat is honored VERBATIM (targeted + null ⇒ tear down)', () => {
    // EncodeClaimWindowClosed ⇒ ["claim", selfKey, null]. Must NOT fall back to
    // the collection — the overlay tears down deterministically.
    const r = selectSelfClaim<Claim>([['0', null]], '0');
    expect(r.targeted).toBe(true);
    expect(r.value).toBeNull();
  });

  test('an open window for my seat is surfaced', () => {
    const r = selectSelfClaim<Claim>([['0', open]], '0');
    expect(r.targeted).toBe(true);
    expect(r.value).toBe(open);
  });

  test('a batch that only touches OTHER seats does not target me (⇒ caller keeps/reads collection)', () => {
    const r = selectSelfClaim<Claim>([['1', open], ['2', null]], '0');
    expect(r.targeted).toBe(false);
    expect(r.value).toBeNull();
  });

  test('last write for my seat wins within a batch (open then close ⇒ closed)', () => {
    const r = selectSelfClaim<Claim>([['0', open], ['0', null]], '0');
    expect(r.targeted).toBe(true);
    expect(r.value).toBeNull();   // the trailing close tombstone wins ⇒ tear down
  });

  test('empty batch ⇒ not targeted (reconnect/full-sync handled by the collection fallback)', () => {
    const r = selectSelfClaim<Claim>([], '0');
    expect(r.targeted).toBe(false);
    expect(r.value).toBeNull();
  });
});
