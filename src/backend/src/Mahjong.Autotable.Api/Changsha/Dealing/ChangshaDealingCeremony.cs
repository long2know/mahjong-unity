namespace Mahjong.Autotable.Api.Changsha.Dealing;

/// <summary>
/// Phase of the Changsha dealing ceremony state machine.
/// <list type="bullet">
///   <item><see cref="WaitingForDice"/> — initial; only valid transition is
///   <see cref="ChangshaDealingCeremony.ApplyDiceRoll"/>.</item>
///   <item><see cref="PickingFour"/> — one of the three batch-of-4 rounds is
///   in progress. Each seat picks 4 tiles, counter-clockwise from dealer.</item>
///   <item><see cref="PickingOne"/> — the final single-tile round. Each seat
///   picks 1 tile, counter-clockwise from dealer.</item>
///   <item><see cref="DealerExtra"/> — dealer takes the 14th tile to bring
///   the dealer hand to 14 and arm the first discard.</item>
///   <item><see cref="Complete"/> — terminal phase. Dealer has 14 tiles,
///   non-dealers each have 13.</item>
/// </list>
/// </summary>
public enum ChangshaDealingPhase
{
    WaitingForDice,
    PickingFour,
    PickingOne,
    DealerExtra,
    Complete,
}

/// <summary>
/// Immutable snapshot of the Changsha dealing ceremony — turn order,
/// per-seat hand sizes, current phase, plus the wall + break-index derived
/// from the dice roll. The ceremony engine is a pure-function transducer:
/// it consumes a state + an event (dice roll / pickup request) and produces
/// a new state. It does NOT own tiles — the runtime layer assigns tile-ids
/// to the seats based on the wall slots referenced by
/// <see cref="StartingWall"/> + <see cref="BreakIndex"/>.
/// </summary>
/// <param name="DealerSeat">Seat index (0..3) of the dealer for this hand.</param>
/// <param name="DiceRoll">The two-dice roll, or <c>null</c> while the
/// ceremony is in <see cref="ChangshaDealingPhase.WaitingForDice"/>. Each
/// element is in the range 1..6.</param>
/// <param name="StartingWall">Index (0..3) of the wall to break, counted
/// counter-clockwise from the dealer using the dice sum. <c>null</c> until
/// dice are rolled.</param>
/// <param name="BreakIndex">Number of TILES in from the right end of
/// <see cref="StartingWall"/> at which the break occurs (= 2 ×
/// stack-count = 2 × diceSum). <c>null</c> until dice are rolled.</param>
/// <param name="CurrentPickerSeat">Seat whose turn it is to call
/// <see cref="ChangshaDealingCeremony.ValidateAndApplyPickup"/>. Defaults
/// to <paramref name="DealerSeat"/> at every round boundary and rotates
/// counter-clockwise within a round.</param>
/// <param name="TilesTakenThisRound">Count of pickup operations that have
/// completed in the current round. Ranges 0..4 for the normal batch-of-4
/// and single-tile rounds (4 = round complete → phase advances); 0..1 for
/// the <see cref="ChangshaDealingPhase.DealerExtra"/> phase.</param>
/// <param name="RoundIndex">0..3. <c>0,1,2</c> = batch-of-4 rounds;
/// <c>3</c> = final single-tile round. The <see cref="ChangshaDealingPhase.DealerExtra"/>
/// phase keeps <c>RoundIndex == 3</c>; <see cref="ChangshaDealingPhase.Complete"/>
/// also keeps the last round index.</param>
/// <param name="HandSizes">Number of concealed tiles currently held by
/// each seat (index = seat). At <see cref="ChangshaDealingPhase.Complete"/>
/// the dealer slot holds 14 and the others 13.</param>
/// <param name="Phase">Current ceremony phase.</param>
public sealed record ChangshaDealingState(
    int DealerSeat,
    int[]? DiceRoll,
    int? StartingWall,
    int? BreakIndex,
    int CurrentPickerSeat,
    int TilesTakenThisRound,
    int RoundIndex,
    int[] HandSizes,
    ChangshaDealingPhase Phase);

/// <summary>
/// Result of <see cref="ChangshaDealingCeremony.ValidateAndApplyPickup"/>.
/// On <c>Valid == false</c>, <see cref="NewState"/> is the input state
/// unchanged and <see cref="RejectReason"/> describes the violation. On
/// success, <see cref="TilesPickedUp"/> equals the count consumed from the
/// wall (4 for batch rounds, 1 for the single-tile / dealer-extra rounds).
/// </summary>
public sealed record ChangshaDealingResult(
    bool Valid,
    string? RejectReason,
    ChangshaDealingState NewState,
    int TilesPickedUp);

/// <summary>
/// Pure-function rule engine for the Changsha (长沙) Mahjong dealing
/// ceremony. Captures the canonical 抓牌 procedure:
/// <list type="number">
///   <item>Dealer rolls two dice (sum 2..12).</item>
///   <item>Starting wall = dealer + ((sum − 1) mod 4) counter-clockwise.</item>
///   <item>Break point = <c>sum</c> stacks in from the right end of the
///   starting wall (= <c>sum × 2</c> tiles).</item>
///   <item>Picking proceeds counter-clockwise from dealer. Three rounds
///   of 4 tiles each → every seat has 12. Final round of 1 tile each →
///   every seat has 13. Dealer takes one more → dealer has 14.</item>
/// </list>
///
/// <para>Lane discipline: this class owns ONLY the turn-order + count
/// arithmetic + phase progression. Tile-id assignment from physical wall
/// slots is the runtime layer's responsibility — the runtime consults
/// <see cref="ChangshaDealingState.StartingWall"/>,
/// <see cref="ChangshaDealingState.BreakIndex"/>, and the per-pickup
/// <see cref="ChangshaDealingResult.TilesPickedUp"/> to slice the wall.</para>
///
/// <para>Pure-function contract: every method either returns a new
/// <see cref="ChangshaDealingState"/> or a
/// <see cref="ChangshaDealingResult"/>. The input <c>state</c> is never
/// mutated; the returned state is a fresh record. Throws only for
/// programmer errors (out-of-range arguments at <see cref="Start"/> or
/// <see cref="ApplyDiceRoll"/>) — runtime violations like out-of-turn
/// pickups or wrong counts are surfaced via
/// <see cref="ChangshaDealingResult.Valid"/> = <c>false</c>.</para>
///
/// <para>References:
/// <list type="bullet">
///   <item><c>https://baike.baidu.com/en/item/Changsha%20Mahjong/36618</c></item>
///   <item><c>https://mahjongpros.com/blogs/how-to-play/beginners-guide-to-changsha-mahjong</c></item>
/// </list></para>
/// </summary>
public static class ChangshaDealingCeremony
{
    /// <summary>Number of seats at a Changsha table.</summary>
    public const int SeatCount = 4;

    /// <summary>Number of tiles each non-dealer holds when the ceremony
    /// completes (12 from batch rounds + 1 from the final single-tile
    /// round).</summary>
    public const int NonDealerFinalHandSize = 13;

    /// <summary>Number of tiles the dealer holds when the ceremony
    /// completes (13 + 1 dealer-extra).</summary>
    public const int DealerFinalHandSize = 14;

    /// <summary>Tiles taken per pickup during a batch-of-4 round.</summary>
    public const int BatchPickupSize = 4;

    /// <summary>Tiles taken per pickup during the single-tile / dealer-extra rounds.</summary>
    public const int SinglePickupSize = 1;

    /// <summary>Round index that holds the final single-tile pickups.</summary>
    public const int FinalRoundIndex = 3;

    /// <summary>
    /// Initialises a fresh ceremony with no dice rolled yet. The picker
    /// cursor sits at <paramref name="dealerSeat"/> because that seat will
    /// drive the dice roll and is the first to pick.
    /// </summary>
    /// <param name="dealerSeat">Seat index (0..3) of the dealer for this hand.</param>
    /// <exception cref="ArgumentOutOfRangeException">When
    /// <paramref name="dealerSeat"/> is outside [0, 3].</exception>
    public static ChangshaDealingState Start(int dealerSeat)
    {
        if (dealerSeat < 0 || dealerSeat >= SeatCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dealerSeat),
                dealerSeat,
                $"Dealer seat must be in [0, {SeatCount - 1}].");
        }

        return new ChangshaDealingState(
            DealerSeat: dealerSeat,
            DiceRoll: null,
            StartingWall: null,
            BreakIndex: null,
            CurrentPickerSeat: dealerSeat,
            TilesTakenThisRound: 0,
            RoundIndex: 0,
            HandSizes: new int[SeatCount],
            Phase: ChangshaDealingPhase.WaitingForDice);
    }

    /// <summary>
    /// Applies a two-die roll and transitions
    /// <see cref="ChangshaDealingPhase.WaitingForDice"/> →
    /// <see cref="ChangshaDealingPhase.PickingFour"/>. Computes and stores
    /// <see cref="ChangshaDealingState.StartingWall"/> and
    /// <see cref="ChangshaDealingState.BreakIndex"/>.
    /// </summary>
    /// <param name="s">Current ceremony state. Must be in
    /// <see cref="ChangshaDealingPhase.WaitingForDice"/>.</param>
    /// <param name="dice">Exactly two integers in [1, 6].</param>
    /// <exception cref="ArgumentNullException">If <paramref name="dice"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="dice"/> does not have
    /// exactly two elements or either die is outside [1, 6].</exception>
    /// <exception cref="InvalidOperationException">If the ceremony is not
    /// in <see cref="ChangshaDealingPhase.WaitingForDice"/>.</exception>
    public static ChangshaDealingState ApplyDiceRoll(ChangshaDealingState s, int[] dice)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(dice);

        if (dice.Length != 2)
        {
            throw new ArgumentException(
                $"Dice roll must contain exactly two dice; got {dice.Length}.",
                nameof(dice));
        }
        if (dice[0] is < 1 or > 6 || dice[1] is < 1 or > 6)
        {
            throw new ArgumentException(
                $"Each die must be in [1, 6]; got [{dice[0]}, {dice[1]}].",
                nameof(dice));
        }
        if (s.Phase != ChangshaDealingPhase.WaitingForDice)
        {
            throw new InvalidOperationException(
                $"ApplyDiceRoll requires phase {ChangshaDealingPhase.WaitingForDice}; was {s.Phase}.");
        }

        var sum = dice[0] + dice[1];
        var startingWall = ComputeStartingWall(s.DealerSeat, sum);
        var breakIndex = ComputeBreakIndex(sum);

        return s with
        {
            DiceRoll = (int[])dice.Clone(),
            StartingWall = startingWall,
            BreakIndex = breakIndex,
            Phase = ChangshaDealingPhase.PickingFour,
            CurrentPickerSeat = s.DealerSeat,
            TilesTakenThisRound = 0,
            RoundIndex = 0,
        };
    }

    /// <summary>
    /// Validates a pickup request and, on success, produces a new state
    /// with the seat's <see cref="ChangshaDealingState.HandSizes"/>
    /// incremented, the picker cursor rotated counter-clockwise, and the
    /// phase / round advanced when the current round / step completes.
    /// </summary>
    /// <param name="s">Current ceremony state.</param>
    /// <param name="seatIndex">Seat requesting the pickup. Must equal
    /// <see cref="ChangshaDealingState.CurrentPickerSeat"/>.</param>
    /// <param name="requestedCount">Number of tiles to take. Must match
    /// the count expected for the current phase: 4 for
    /// <see cref="ChangshaDealingPhase.PickingFour"/>, 1 for
    /// <see cref="ChangshaDealingPhase.PickingOne"/> and
    /// <see cref="ChangshaDealingPhase.DealerExtra"/>.</param>
    /// <returns>A <see cref="ChangshaDealingResult"/> with <c>Valid=true</c>
    /// and the new state on success, or <c>Valid=false</c> with the input
    /// state and a non-null <see cref="ChangshaDealingResult.RejectReason"/>
    /// on a rule violation.</returns>
    public static ChangshaDealingResult ValidateAndApplyPickup(
        ChangshaDealingState s,
        int seatIndex,
        int requestedCount)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (seatIndex < 0 || seatIndex >= SeatCount)
        {
            return Reject(s, $"Seat index {seatIndex} out of range [0, {SeatCount - 1}].");
        }
        if (s.Phase is ChangshaDealingPhase.WaitingForDice)
        {
            return Reject(s, "Cannot pick up — dice have not been rolled.");
        }
        if (s.Phase is ChangshaDealingPhase.Complete)
        {
            return Reject(s, "Ceremony is already complete — no more pickups allowed.");
        }
        if (seatIndex != s.CurrentPickerSeat)
        {
            return Reject(s,
                $"Seat {seatIndex} is not the active picker (expected seat {s.CurrentPickerSeat}).");
        }

        var expected = ExpectedPickupCount(s.Phase);
        if (requestedCount != expected)
        {
            return Reject(s,
                $"Pickup count mismatch: requested {requestedCount}, expected {expected} for phase {s.Phase}.");
        }

        var newHandSizes = (int[])s.HandSizes.Clone();
        newHandSizes[seatIndex] += expected;

        var nextState = AdvanceCursor(s, newHandSizes);

        return new ChangshaDealingResult(
            Valid: true,
            RejectReason: null,
            NewState: nextState,
            TilesPickedUp: expected);
    }

    /// <summary>
    /// Pure helper: the dice-sum → wall mapping. Counts counter-clockwise
    /// from <paramref name="dealerSeat"/>: sums 1/5/9 → dealer wall,
    /// 2/6/10 → next CCW wall, 3/7/11 → opposite wall, 4/8/12 → preceding
    /// wall. Concretely: <c>(dealerSeat + (diceSum − 1) mod 4) mod 4</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">If
    /// <paramref name="dealerSeat"/> is outside [0, 3] or
    /// <paramref name="diceSum"/> is outside [2, 12].</exception>
    public static int ComputeStartingWall(int dealerSeat, int diceSum)
    {
        if (dealerSeat < 0 || dealerSeat >= SeatCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dealerSeat),
                dealerSeat,
                $"Dealer seat must be in [0, {SeatCount - 1}].");
        }
        if (diceSum is < 2 or > 12)
        {
            throw new ArgumentOutOfRangeException(
                nameof(diceSum),
                diceSum,
                "Dice sum must be in [2, 12].");
        }

        var offset = (diceSum - 1) % SeatCount;
        return (dealerSeat + offset) % SeatCount;
    }

    /// <summary>
    /// Pure helper: dice-sum → break-tile offset from the right end of
    /// the chosen wall, measured in TILES (= 2 × stacks). The break point
    /// sits immediately to the LEFT of this tile offset — drawing then
    /// proceeds counter-clockwise from the break.
    /// <para>The runtime translates this offset into a physical wall slot
    /// when reading tile ids; the ceremony engine is wall-storage-agnostic
    /// and only emits the count.</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">If
    /// <paramref name="diceSum"/> is outside [2, 12].</exception>
    public static int ComputeBreakIndex(int diceSum)
    {
        if (diceSum is < 2 or > 12)
        {
            throw new ArgumentOutOfRangeException(
                nameof(diceSum),
                diceSum,
                "Dice sum must be in [2, 12].");
        }
        // 2 tiles per stack — Changsha walls are stacked two high.
        return diceSum * 2;
    }

    /// <summary>The number of tiles the active seat must take while parked
    /// in <paramref name="phase"/>. Returns 0 for phases that don't permit
    /// a pickup (<see cref="ChangshaDealingPhase.WaitingForDice"/> and
    /// <see cref="ChangshaDealingPhase.Complete"/>).</summary>
    public static int ExpectedPickupCount(ChangshaDealingPhase phase) => phase switch
    {
        ChangshaDealingPhase.PickingFour => BatchPickupSize,
        ChangshaDealingPhase.PickingOne => SinglePickupSize,
        ChangshaDealingPhase.DealerExtra => SinglePickupSize,
        _ => 0,
    };

    // ── internals ─────────────────────────────────────────────────────

    private static ChangshaDealingResult Reject(ChangshaDealingState s, string reason)
        => new(Valid: false, RejectReason: reason, NewState: s, TilesPickedUp: 0);

    /// <summary>Advances the cursor after a successful pickup. Handles
    /// intra-round rotation (next CCW seat) and end-of-round phase
    /// progression (PickingFour ×3 → PickingOne → DealerExtra → Complete).</summary>
    private static ChangshaDealingState AdvanceCursor(
        ChangshaDealingState s,
        int[] newHandSizes)
    {
        var tilesTaken = s.TilesTakenThisRound + 1;
        var seatsPerRound = s.Phase == ChangshaDealingPhase.DealerExtra ? 1 : SeatCount;

        if (tilesTaken < seatsPerRound)
        {
            // Still mid-round — rotate CCW from dealer.
            var nextPicker = (s.DealerSeat + tilesTaken) % SeatCount;
            return s with
            {
                CurrentPickerSeat = nextPicker,
                TilesTakenThisRound = tilesTaken,
                HandSizes = newHandSizes,
            };
        }

        // Round complete — choose the next phase + round index.
        var (nextPhase, nextRoundIndex) = NextPhaseAndRound(s.Phase, s.RoundIndex);

        if (nextPhase == ChangshaDealingPhase.Complete)
        {
            return s with
            {
                CurrentPickerSeat = s.DealerSeat,
                TilesTakenThisRound = 0,
                RoundIndex = nextRoundIndex,
                HandSizes = newHandSizes,
                Phase = nextPhase,
            };
        }

        // Next round starts at the dealer.
        return s with
        {
            CurrentPickerSeat = s.DealerSeat,
            TilesTakenThisRound = 0,
            RoundIndex = nextRoundIndex,
            HandSizes = newHandSizes,
            Phase = nextPhase,
        };
    }

    /// <summary>Computes the phase + round index that follow a completed
    /// round. Round 0 and 1 stay in PickingFour and bump the round index;
    /// round 2 moves into PickingOne (round 3); round 3 (PickingOne) moves
    /// into DealerExtra; DealerExtra moves into Complete.</summary>
    private static (ChangshaDealingPhase nextPhase, int nextRoundIndex) NextPhaseAndRound(
        ChangshaDealingPhase phase,
        int roundIndex) => phase switch
        {
            ChangshaDealingPhase.PickingFour when roundIndex < 2
                => (ChangshaDealingPhase.PickingFour, roundIndex + 1),
            ChangshaDealingPhase.PickingFour
                => (ChangshaDealingPhase.PickingOne, FinalRoundIndex),
            ChangshaDealingPhase.PickingOne
                => (ChangshaDealingPhase.DealerExtra, FinalRoundIndex),
            ChangshaDealingPhase.DealerExtra
                => (ChangshaDealingPhase.Complete, FinalRoundIndex),
            _ => (phase, roundIndex),
        };
}
