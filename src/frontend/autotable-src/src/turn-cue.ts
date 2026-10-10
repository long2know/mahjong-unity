// Stuck-turn fix (Hicks) — pure turn-cue resolver.
//
// Extracted so the "your turn / waiting for seat N / spectating / no-open-seat"
// decision is a deterministic pure function covered by a browser-free contract
// test (turn-cue.contract.spec.ts), mirroring hand-accounting.ts.
//
// It resolves the LOW-priority turn banner states (everything AFTER the
// claim-window and pickup affordances, which game-ui.ts handles first).  The
// design goal is to make three things unambiguous, which the shipped bundle
// conflated into a single empty banner:
//   1. It is MY turn to discard   → "Your turn — click a tile to discard".
//   2. It is ANOTHER seat's turn  → "Waiting for Seat N…" (auto-draw + bots
//      are handled server-side; the human just waits, and now SEES that).
//   3. I hold no actionable seat  → distinguish an intentional spectator from
//      landing on an in-progress game whose seats are all taken (the stale-
//      gameId-reuse deadlock: Hudson/Ripley Design Review defect 1) so the UI
//      can offer "New Game" instead of a frozen table + misleading "Not your
//      turn".
//
// Authoritative-first, geometry-as-defence-in-depth: when Bishop's `turn`
// signal is present it wins; the meld-aware geometry heuristic only fills in
// when the signal is absent (older backend) or still in flight.

export type TurnCue =
  | { kind: 'discard' }
  | { kind: 'waiting'; seat: number }
  | { kind: 'waiting-unknown' }
  | { kind: 'spectating'; seat: number | null }
  | { kind: 'no-open-seat' }
  | { kind: 'none' };

export interface TurnCueInput {
  /** Local seat (0..3), or null when the viewer holds no seat. */
  mySeat: number | null;
  /** True when the page URL declares an intentional spectator (?seat=-1). */
  isSpectatorUrl: boolean;
  /** True once a hand has been dealt / is live (tiles are in play). */
  inProgress: boolean;
  /**
   * Bishop's authoritative active seat: 0..3, null (no seat on the clock), or
   * undefined when the signal has not been received at all (older backend).
   */
  activeSeatSignal: number | null | undefined;
  /**
   * Bishop's authoritative "active seat must discard" flag; undefined when the
   * signal is absent.
   */
  awaitingDiscardSignal: boolean | undefined;
  /** Geometry defence-in-depth: my hand holds an extra (>13 effective) tile. */
  myHasExtraTile: boolean;
  /**
   * Geometry heuristic for whose turn it is when the signal is absent: the
   * seat currently holding 14 effective tiles (owes a discard), or null.
   */
  activeSeatByGeometry: number | null;
  /** True when all four seats are currently occupied (human or bot). */
  allSeatsOccupied: boolean;
}

/**
 * True when it is the local seat's turn to discard.
 *
 * Authoritative-first (Bishop C-1): when the turn signal is PRESENT
 * (`activeSeatSignal !== undefined`, i.e. the backend emits the `turn` cue) we
 * trust it EXCLUSIVELY — `activeSeat === mySeat && awaitingDiscard`.  This
 * makes the discard cue fire the instant the runtime hands us the turn
 * regardless of `things` snapshot timing, AND honours the retraction
 * (`activeSeat: null` / `awaitingDiscard: false` on every non-AwaitingDiscard
 * phase) so stale geometry can never resurrect the cue during Scoring / a
 * claim window.
 *
 * Meld-aware geometry is the DEFENCE-IN-DEPTH fallback: it is consulted only
 * when the signal is absent (an older backend that has not landed the cue, or
 * before the first `turn` UPDATE arrives), preserving the shipped behaviour.
 */
export function isMyDiscardTurn(input: {
  mySeat: number | null;
  activeSeatSignal: number | null | undefined;
  awaitingDiscardSignal: boolean | undefined;
  myHasExtraTile: boolean;
}): boolean {
  if (input.mySeat === null || input.mySeat < 0) return false;
  if (input.activeSeatSignal !== undefined) {
    // Signal present (incl. an explicit null retraction) ⇒ authoritative.
    return input.activeSeatSignal === input.mySeat && input.awaitingDiscardSignal === true;
  }
  // Signal absent ⇒ meld-aware geometry fallback.
  return input.myHasExtraTile;
}

/** Resolve who is on the clock (0..3) or null, authoritative-first. */
export function resolveActiveSeat(input: {
  activeSeatSignal: number | null | undefined;
  activeSeatByGeometry: number | null;
}): number | null {
  if (input.activeSeatSignal !== undefined && input.activeSeatSignal !== null) {
    return input.activeSeatSignal;
  }
  // A signal that is explicitly null means "no seat on the clock" — trust it
  // over stale geometry.  Only fall back to geometry when the signal is absent.
  if (input.activeSeatSignal === null) return null;
  return input.activeSeatByGeometry;
}

/**
 * R-1 §D9 — claim and discard/pickup are MUTUALLY EXCLUSIVE. Defends the
 * stale-claim bug: the backend `EncodeClaimWindowClosed` tombstone may never be
 * emitted (esp. in the server-timed `deadline<=0` mode), leaving a closed claim
 * window's `activeClaim` — and its banner + buttons — live. A claim is only
 * ACTIONABLE while it is NOT authoritatively this seat's turn to discard or pick
 * up; once the turn advances to my discard, any lingering claim is stale and
 * must be suppressed so claim controls never sit live during my discard turn.
 */
export function claimActionable(
  hasClaim: boolean,
  isMyDiscardTurn: boolean,
  isMyPickupTurn: boolean,
): boolean {
  return hasClaim && !isMyDiscardTurn && !isMyPickupTurn;
}

/** Result of {@link selectSelfClaim}. */
export interface SelfClaimSelection<V> {
  /** True when THIS update batch carried an entry for the viewer's own seat. */
  targeted: boolean;
  /** The viewer's own claim window from this batch (`null` on a close tombstone). */
  value: V | null;
}

/**
 * D4 (Vasquez ruling) — deterministic claim-overlay teardown. Given a `claim`
 * update batch and the viewer's own seat key, decide the viewer's claim window
 * from THIS batch alone.
 *
 * The key property: when the batch explicitly targets the viewer's seat —
 * INCLUDING the server's `EncodeClaimWindowClosed` tombstone (value `null`) —
 * `targeted` is true and `value` is honored VERBATIM (null ⇒ tear the overlay
 * down). The caller must NOT then re-read the collection, so a closed window can
 * never be resurrected by a stale/racy collection read; the collection fallback
 * is reserved for batches that did not mention the viewer (reconnect / full-sync
 * / other-seat updates). Last write for the seat wins within the batch.
 *
 * Pure ⇒ browser-free contract-tested.
 */
export function selectSelfClaim<V>(
  entries: ReadonlyArray<[string, V | null]>,
  selfKey: string,
): SelfClaimSelection<V> {
  let targeted = false;
  let value: V | null = null;
  for (const [key, v] of entries) {
    if (key !== selfKey) continue;
    targeted = true;
    value = v ?? null;
  }
  return { targeted, value };
}

export function computeTurnCue(input: TurnCueInput): TurnCue {
  const seated = input.mySeat !== null && input.mySeat >= 0;

  if (seated) {
    if (
      isMyDiscardTurn({
        mySeat: input.mySeat,
        activeSeatSignal: input.activeSeatSignal,
        awaitingDiscardSignal: input.awaitingDiscardSignal,
        myHasExtraTile: input.myHasExtraTile,
      })
    ) {
      return { kind: 'discard' };
    }
    // Seated but not my turn: surface who we're waiting on so the table never
    // reads as "frozen with nothing happening".
    const active = resolveActiveSeat(input);
    if (active !== null && active !== input.mySeat) {
      return { kind: 'waiting', seat: active };
    }
    if (input.inProgress) return { kind: 'waiting-unknown' };
    return { kind: 'none' };
  }

  // No seat.  Distinguish an intentional spectator from the stale-game
  // deadlock (all seats taken, one by an absent human) so we can offer a way
  // out instead of a misleading empty/"not your turn" table.
  if (input.isSpectatorUrl) {
    // Surface whose turn it is so a spectator can follow the play.
    return { kind: 'spectating', seat: resolveActiveSeat(input) };
  }
  if (input.inProgress && input.allSeatsOccupied) return { kind: 'no-open-seat' };
  return { kind: 'none' };
}

// ── Actionable "no-open-seat → New Game" banner a11y descriptor ───────────────
//
// The turn banner (#turn-banner) is normally a non-interactive status pill:
// `pointer-events: none` in style.css so it never eats clicks meant for the 3D
// table.  For the ONE actionable state (no-open-seat New Game) the banner must
// become genuinely clickable/tappable AND keyboard-operable — which requires
// re-enabling pointer events (Hudson found the shipped attempt was visually
// actionable but DEAD because pointer-events was never overridden).
//
// This pure descriptor is the single source of truth for that state so the
// exact regression ("pointerEvents must be 'auto' when actionable, and reset
// otherwise") is guarded by a browser-free contract test; game-ui.ts applies
// it and binds/unbinds the click + Enter/Space handlers idempotently.
export interface NewGameBannerA11y {
  /** Overrides the base `pointer-events: none` so clicks/taps land. */
  pointerEvents: 'auto';
  role: 'button';
  tabIndex: 0;
  cursor: 'pointer';
  ariaLabel: string;
}

/**
 * Descriptor for the actionable New Game banner, or `null` when the banner is
 * NOT actionable (game-ui.ts then resets pointer-events/role/tabindex/cursor/
 * aria-label + unbinds handlers so normal status banners stay click-through).
 */
export function newGameBannerA11y(actionable: boolean): NewGameBannerA11y | null {
  if (!actionable) return null;
  return {
    pointerEvents: 'auto',
    role: 'button',
    tabIndex: 0,
    cursor: 'pointer',
    ariaLabel: 'Start a New Game',
  };
}
