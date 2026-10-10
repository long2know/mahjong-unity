using System.Reflection;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance: Phase F manual-pickup state machine per Ripley's design doc
/// (<c>.squad/decisions/inbox/ripley-phase-f-design.md</c> §2) layered onto the
/// v1.2 rules (<c>docs/rules/changsha-spec.md</c> §2.4 / §2.5) and the canonical
/// sources (MahjongPros + Baidu).
///
/// <para>The Phase F deal splits into a 6-step pickup sequence:
/// <list type="number">
///   <item>Dealer rolls 2d6 → <see cref="ChangshaPhase"/>.BreakPointMarked</item>
///   <item>Round 1 — 4 tiles per seat (CCW from dealer) → PickupRound1</item>
///   <item>Round 2 — 4 tiles per seat → PickupRound2</item>
///   <item>Round 3 — 4 tiles per seat → PickupRound3</item>
///   <item>Single-tile round — 1 tile per seat → SingleTilePickup</item>
///   <item>Dealer extra — 1 tile to dealer → DealerExtra → AwaitingDiscard</item>
/// </list>
/// Each step is owned by Bishop's backend; this file pins the behaviour Bishop must hit.</para>
///
/// <para><b>Test posture:</b> tests reference Phase F symbols via reflection so the
/// assembly compiles cleanly before Bishop ships. Tests fail red with descriptive
/// "Phase F backend not yet shipped — missing TYPE/METHOD …" messages until the
/// production code lands; they go green automatically as Bishop wires symbols.</para>
///
/// <para><b>Sources:</b>
/// MahjongPros §"Setting up the Wall" + §"Dealing the Hand",
/// Baidu §"摸牌前掷骰" + §"摸牌顺序",
/// <c>docs/rules/changsha-spec.md</c> §2.4 + §2.5,
/// Vasquez Phase F rule audit (<c>.squad/decisions/inbox/vasquez-phase-f-rule-audit.md</c>).</para>
/// </summary>
public class ManualPickupAcceptanceTests
{
    // ── Reflection helpers (Phase F backend not yet shipped) ──────────────

    private static readonly Assembly ApiAssembly = typeof(ChangshaGameState).Assembly;

    private static Type? TryGetType(string fullName) => ApiAssembly.GetType(fullName);

    private static MethodInfo? TryGetStaticMethod(Type type, string methodName, params Type[] argTypes)
    {
        return argTypes.Length == 0
            ? type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                  .FirstOrDefault(m => m.Name == methodName)
            : type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, binder: null, types: argTypes, modifiers: null);
    }

    private static PropertyInfo? TryGetProperty(Type type, string propertyName) =>
        type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);

    private static string PhaseName(ChangshaGameState state) => state.Phase.ToString();

    private static void AssertPhaseFShipped(string symbolDescription, object? symbol)
    {
        Assert.True(symbol != null,
            $"Phase F backend not yet shipped — missing {symbolDescription}. " +
            $"Bishop owns; see .squad/decisions/inbox/ripley-phase-f-design.md.");
    }

    /// <summary>
    /// Set <c>state.DealMode = DealMode.Manual</c> via reflection if both the property
    /// and enum value exist; otherwise the test is unable to drive Phase F. Returns
    /// true if successfully set, false (with a fail-fast Assert.True) otherwise.
    /// </summary>
    private static bool TrySetDealModeManual(ChangshaGameState state)
    {
        var prop = TryGetProperty(typeof(ChangshaGameState), "DealMode");
        if (prop is null) return false;
        var enumType = TryGetType("Mahjong.Autotable.Api.Changsha.DealMode");
        if (enumType is null) return false;
        var manualVal = Enum.Parse(enumType, "Manual");
        prop.SetValue(state, manualVal);
        return true;
    }

    /// <summary>
    /// Drive the manual-pickup state machine from <c>Seating</c> through the dealer's
    /// RollDice click. Returns the post-roll state (expected to be in <c>BreakPointMarked</c>).
    /// </summary>
    private static (ChangshaGameState State, MethodInfo BeginManualDeal, MethodInfo TakeTilesFromWall) PrepareManualPickup(
        int seed = 42, int dealerSeat = 0)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.DealerSeatIndex = dealerSeat;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == dealerSeat;
        ChangshaGameStateMachine.StartGame(state);

        var dealModeSet = TrySetDealModeManual(state);
        AssertPhaseFShipped("ChangshaGameState.DealMode (property) and Mahjong.Autotable.Api.Changsha.DealMode (enum, value 'Manual')", dealModeSet ? state : null);

        var beginManualDeal = TryGetStaticMethod(typeof(ChangshaGameStateMachine), "BeginManualDeal");
        AssertPhaseFShipped("ChangshaGameStateMachine.BeginManualDeal(state, DiceRoll)", beginManualDeal);
        var takeTilesFromWall = TryGetStaticMethod(typeof(ChangshaGameStateMachine), "TakeTilesFromWall");
        AssertPhaseFShipped("ChangshaGameStateMachine.TakeTilesFromWall(state, int seat, int count)", takeTilesFromWall);

        return (state, beginManualDeal!, takeTilesFromWall!);
    }

    private static void BeginManualDeal(MethodInfo method, ChangshaGameState state, DiceRoll roll)
    {
        method.Invoke(null, new object[] { state, roll });
    }

    private static void TakeTiles(MethodInfo method, ChangshaGameState state, int seat, int count)
    {
        method.Invoke(null, new object[] { state, seat, count });
    }

    private static int? GetPickupSeatIndex(ChangshaGameState state)
    {
        var prop = TryGetProperty(typeof(ChangshaGameState), "PickupSeatIndex");
        return prop?.GetValue(state) as int?;
    }

    // ── §1 — Dice mechanism ───────────────────────────────────────────────

    [Theory, Trait("Category", "Acceptance")]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(5150)]
    [InlineData(12345)]
    [InlineData(-99)]
    public void DiceRoll_TwoD6_SumIsBetween2And12(int seed)
    {
        // MahjongPros: "Two dice are also required." Baidu confirms 2d6 inclusive.
        // The dice service must produce two independent 1..6 rolls, sum 2..12.
        var dice = new DiceService(seed);
        var roll = dice.Roll();

        Assert.InRange(roll.Die1, 1, 6);
        Assert.InRange(roll.Die2, 1, 6);
        Assert.InRange(roll.Sum, 2, 12);
        Assert.Equal(roll.Die1 + roll.Die2, roll.Sum);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void DiceRoll_Deterministic_WithSeed()
    {
        // Test pin: identical seeds → identical rolls. Enables replay tests, snapshot
        // determinism, and Phase F's deterministic bot-vs-bot acceptance suite.
        var d1 = new DiceService(20260519);
        var d2 = new DiceService(20260519);
        Assert.Equal(d1.Roll(), d2.Roll());

        // Different seeds typically produce different rolls (probabilistic — pin one
        // known pair to keep the assertion deterministic across xUnit runs).
        var d3 = new DiceService(20260519);
        var d4 = new DiceService(20260520);
        var r3 = d3.Roll();
        var r4 = d4.Roll();
        // We don't assert inequality (collisions exist) — but the SEQUENCE differs:
        var r3b = d3.Roll();
        var r4b = d4.Roll();
        Assert.NotEqual((r3, r3b), (r4, r4b));
    }

    // ── §2 — Break-point math (already-shipped BreakPointService) ──────────

    [Theory, Trait("Category", "Acceptance")]
    [InlineData(2, 0)]
    [InlineData(7, 0)]
    [InlineData(12, 0)]
    [InlineData(7, 2)]
    public void BreakPoint_DealerWallRight_CountsFromRight(int diceSum, int dealerSeat)
    {
        // MahjongPros §"Breaking the Wall": "Starting on the end of the wall segment
        // closest to you, count out [sum] tiles." Canonical reading (Baidu + spec
        // §2.4): count STACKS from the RIGHT end of the chosen wall. Existing
        // BreakPointService implements this correctly — pin the contract here so
        // Bishop's new BeginManualDeal does not silently drift.
        var svc = new BreakPointService();
        var bp = svc.ComputeBreakPoint(diceSum, dealerSeat);

        // Result is internally consistent: tileIndex = (tilesBeforeWall + stackIndex*2).
        Assert.InRange(bp.WallIndex, 0, 3);
        Assert.InRange(bp.StackIndex, 0, 13); // max stacks per wall is 14
        Assert.True(bp.TileIndex >= 0 && bp.TileIndex < 108,
            $"Break-point tileIndex {bp.TileIndex} must be a valid wall index 0..107.");
        // The chosen wall is determined by `(sum - 1) % 4` offset from dealer (CCW).
        var expectedWall = (dealerSeat + (diceSum - 1) % 4) % 4;
        Assert.Equal(expectedWall, bp.WallIndex);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void BreakPoint_AllSums_LandWithinThatWall()
    {
        // Negative pin: max sum (12) is less than min stack count (13) — so the
        // break-point always lands INSIDE the chosen wall and never wraps into
        // the next wall mid-break-computation. The pickup later may straddle walls
        // (the flat tile list wraps), but the break-point itself is contained.
        var svc = new BreakPointService();
        for (var dealer = 0; dealer < 4; dealer++)
        {
            for (var sum = 2; sum <= 12; sum++)
            {
                var bp = svc.ComputeBreakPoint(sum, dealer);
                Assert.True(bp.StackIndex >= 0,
                    $"sum={sum} dealer={dealer} produced negative stackIndex={bp.StackIndex} — break point wrapped walls (forbidden in v1).");
            }
        }
    }

    // ── §3 — Phase transitions ────────────────────────────────────────────

    [Fact, Trait("Category", "Acceptance")]
    public void BeginManualDeal_TransitionsRollingDice_To_BreakPointMarked()
    {
        // Ripley §2.4: BeginManualDeal consumes a DiceRoll, sets state.BreakPoint,
        // builds + rotates the wall, clears hands/discards, and transitions to
        // BreakPointMarked (NOT directly to PickupRound1).
        var (state, beginManualDeal, _) = PrepareManualPickup(seed: 11);
        Assert.Equal(ChangshaPhase.RollingDice, state.Phase);

        BeginManualDeal(beginManualDeal, state, new DiceRoll(3, 4));

        Assert.Equal("BreakPointMarked", PhaseName(state));
        Assert.NotNull(state.LastDiceRoll);
        Assert.Equal(7, state.LastDiceRoll!.Value.Sum);
        Assert.NotNull(state.BreakPoint);
        // No hand has been dealt yet — hands are empty, wall has 108 tiles.
        Assert.All(state.Hands, h => Assert.Empty(h.ConcealedTiles));
        Assert.Equal(108, state.Wall.Count);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Pickup_FirstRound_Dealer4Tiles()
    {
        // Round 1, first pickup: dealer takes 4 tiles. Hand goes from 0 → 4.
        // Wall shrinks 108 → 104. Phase moves from BreakPointMarked into PickupRound1.
        // PickupSeatIndex advances to the next CCW seat (dealer + 1).
        var (state, beginManualDeal, takeTiles) = PrepareManualPickup(seed: 17, dealerSeat: 0);
        BeginManualDeal(beginManualDeal, state, new DiceRoll(3, 4));

        TakeTiles(takeTiles, state, seat: 0, count: 4);

        Assert.Equal(4, state.Hands[0].ConcealedTiles.Count);
        Assert.Equal(104, state.Wall.Count);
        // After dealer's first 4-pickup, the cursor advances to seat 1 (CCW).
        Assert.Equal(1, GetPickupSeatIndex(state));
        // Phase is still in the round-1 cycle (or PickupRound1 after first advance).
        Assert.Contains(PhaseName(state), new[] { "PickupRound1", "BreakPointMarked" });
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Pickup_Order_DealerThenXiajiaThenOppositeThenShangjia()
    {
        // MahjongPros: "Continuing to the right (counterclockwise), deal each player
        // four tiles in the same manner." Baidu: "The player to the right of the
        // Dealer (the player on the right) has the right to Chow or Pung that tile"
        // — confirming right-of-dealer == next-CCW player. For seat indices, CCW
        // from dealer D = D → D+1 → D+2 → D+3 (mod 4).
        var (state, beginManualDeal, takeTiles) = PrepareManualPickup(seed: 23, dealerSeat: 1);
        BeginManualDeal(beginManualDeal, state, new DiceRoll(2, 5));

        // Round 1: 4 pickups, CCW from dealer (seat 1) → 1, 2, 3, 0.
        var expectedOrder = new[] { 1, 2, 3, 0 };
        foreach (var expectedSeat in expectedOrder)
        {
            var actualSeat = GetPickupSeatIndex(state);
            Assert.True(actualSeat == expectedSeat,
                $"PickupSeatIndex={actualSeat}, expected {expectedSeat} (CCW from dealer=1). Phase={PhaseName(state)}.");
            TakeTiles(takeTiles, state, seat: expectedSeat, count: 4);
        }

        // After all 4 pickups of round 1, every seat has 4 tiles.
        Assert.All(state.Hands, h => Assert.Equal(4, h.ConcealedTiles.Count));
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Pickup_RequiresActiveSeatTurn()
    {
        // Out-of-order pickup must be rejected. Dealer (seat 0) is the first to act;
        // any non-dealer trying to pick first throws.
        var (state, beginManualDeal, takeTiles) = PrepareManualPickup(seed: 31, dealerSeat: 0);
        BeginManualDeal(beginManualDeal, state, new DiceRoll(1, 1));

        // PickupSeatIndex is 0 (dealer). Seat 2 trying to pick out-of-turn is rejected.
        var ex = Assert.Throws<TargetInvocationException>(() =>
            TakeTiles(takeTiles, state, seat: 2, count: 4));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("2", ex.InnerException!.Message);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Pickup_RequiresExpectedCount()
    {
        // Round 1 expected count = 4. Dealer trying to pick 1 (or 5) is rejected.
        var (state, beginManualDeal, takeTiles) = PrepareManualPickup(seed: 37, dealerSeat: 0);
        BeginManualDeal(beginManualDeal, state, new DiceRoll(2, 2));

        var ex = Assert.Throws<TargetInvocationException>(() =>
            TakeTiles(takeTiles, state, seat: 0, count: 1));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Pickup_AllRounds1To3_Each_Seat_Has_12_Tiles()
    {
        // Three rounds of 4-tile pickups (CCW each round). After round 3, each seat
        // has exactly 12 tiles. Wall: 108 - 48 = 60 tiles remaining.
        var (state, beginManualDeal, takeTiles) = PrepareManualPickup(seed: 41, dealerSeat: 0);
        BeginManualDeal(beginManualDeal, state, new DiceRoll(4, 4));

        for (var round = 0; round < 3; round++)
        for (var i = 0; i < 4; i++)
        {
            var seat = (state.DealerSeatIndex + i) % 4;
            var seatBefore = GetPickupSeatIndex(state);
            Assert.True(seatBefore == seat, $"Round {round + 1} pickup {i}: cursor at {seatBefore}, expected {seat}.");
            TakeTiles(takeTiles, state, seat, count: 4);
        }

        Assert.All(state.Hands, h => Assert.Equal(12, h.ConcealedTiles.Count));
        Assert.Equal(60, state.Wall.Count);
        // Phase has now moved into the single-tile pickup round.
        Assert.Equal("SingleTilePickup", PhaseName(state));
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Pickup_SingleTileRound_GivesEachSeat13()
    {
        // §5: After 4-rounds×3, each player has 12 tiles. A single-tile round
        // brings every seat to 13. Dealer goes first in this round too.
        var (state, beginManualDeal, takeTiles) = PrepareManualPickup(seed: 47, dealerSeat: 2);
        BeginManualDeal(beginManualDeal, state, new DiceRoll(3, 3));

        // Drive through three 4-pickup rounds.
        for (var round = 0; round < 3; round++)
        for (var i = 0; i < 4; i++)
            TakeTiles(takeTiles, state, (state.DealerSeatIndex + i) % 4, count: 4);

        Assert.Equal("SingleTilePickup", PhaseName(state));

        // Single-tile round: 4 picks of 1 tile each, CCW from dealer.
        for (var i = 0; i < 4; i++)
        {
            var seat = (state.DealerSeatIndex + i) % 4;
            TakeTiles(takeTiles, state, seat, count: 1);
        }

        Assert.All(state.Hands, h => Assert.Equal(13, h.ConcealedTiles.Count));
        // Phase transitioned to DealerExtra (only the dealer's last +1 remains).
        Assert.Equal("DealerExtra", PhaseName(state));
        Assert.Equal(state.DealerSeatIndex, GetPickupSeatIndex(state));
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Pickup_DealerExtra_Gives14_Then_AwaitingDiscard()
    {
        // §6: Only the dealer takes one more tile, ending with 14. Phase transitions
        // to AwaitingDiscard with ActiveSeatIndex = dealer and TurnNumber = 1.
        // Wall: 108 - 53 = 55 tiles remaining.
        var (state, beginManualDeal, takeTiles) = PrepareManualPickup(seed: 53, dealerSeat: 3);
        BeginManualDeal(beginManualDeal, state, new DiceRoll(6, 6));

        for (var round = 0; round < 3; round++)
        for (var i = 0; i < 4; i++)
            TakeTiles(takeTiles, state, (state.DealerSeatIndex + i) % 4, count: 4);
        for (var i = 0; i < 4; i++)
            TakeTiles(takeTiles, state, (state.DealerSeatIndex + i) % 4, count: 1);

        // Dealer's +1 — the final pickup.
        TakeTiles(takeTiles, state, state.DealerSeatIndex, count: 1);

        Assert.Equal(14, state.Hands[state.DealerSeatIndex].ConcealedTiles.Count);
        foreach (var hand in state.Hands)
            if (hand.SeatIndex != state.DealerSeatIndex)
                Assert.Equal(13, hand.ConcealedTiles.Count);
        Assert.Equal(55, state.Wall.Count);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        Assert.Equal(state.DealerSeatIndex, state.ActiveSeatIndex);
        Assert.Equal(1, state.TurnNumber);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void Pickup_HuMidPickup_IsRejected()
    {
        // §8: Players cannot declare Hu during pickup — the hand is incomplete.
        // DeclareSelfDrawWin requires Phase=AwaitingDiscard; any pickup phase
        // must reject it via the phase-gate.
        var (state, beginManualDeal, _) = PrepareManualPickup(seed: 59, dealerSeat: 0);
        BeginManualDeal(beginManualDeal, state, new DiceRoll(3, 4));

        // DeclareSelfDrawWin requires AwaitingDiscard — we're in BreakPointMarked.
        Assert.Throws<InvalidOperationException>(() =>
            ChangshaGameStateMachine.DeclareSelfDrawWin(state, state.DealerSeatIndex));
    }

    // ── §7 — Auto-deal mode regression check ──────────────────────────────

    [Fact, Trait("Category", "Acceptance")]
    public void AutoDeal_Mode_SkipsPickup_GoesStraightToAwaitingDiscard()
    {
        // Ripley §3.3: when dealMode=auto (the Wave-3 behaviour), StartGame +
        // RollDice + Deal still run atomically and land in AwaitingDiscard
        // immediately. No pickup phases are visited. This is the regression gate
        // for users who add `?dealMode=auto` to revert Phase F's UX change.
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 61, botSeatIndexes: new[] { 0, 1, 2, 3 });

        // If DealMode property exists, leave it at its default (Auto OR not-yet-extant)
        // — auto-mode must be the no-op path so existing tests stay green.
        var dealModeProp = TryGetProperty(typeof(ChangshaGameState), "DealMode");
        if (dealModeProp is not null)
        {
            var enumType = TryGetType("Mahjong.Autotable.Api.Changsha.DealMode");
            if (enumType is not null)
            {
                var autoVal = Enum.Parse(enumType, "Auto");
                dealModeProp.SetValue(state, autoVal);
            }
        }

        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(61));
        ChangshaGameStateMachine.Deal(state);

        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        Assert.Equal(14, state.Hands[state.DealerSeatIndex].ConcealedTiles.Count);
        Assert.Equal(55, state.Wall.Count);
    }

    // ── §12 — Privacy during pickup (Wave-3 viewer-aware translator) ──────

    [Fact, Trait("Category", "Acceptance")]
    public void Pickup_PrivacyMask_OpposingHandsHaveFacesStripped()
    {
        // Privacy is inherited from Wave 3: ChangshaToAutotableTranslator.BuildThingEntries
        // uses viewerSeat to flip rotation between HandRotFaceUp (1) and HandRotFaceDown (2).
        // The translator runs the same code path during pickup — no new privacy logic.
        var (state, beginManualDeal, takeTiles) = PrepareManualPickup(seed: 67, dealerSeat: 0);
        BeginManualDeal(beginManualDeal, state, new DiceRoll(2, 3));
        // Drive one full round so every seat has some tiles to inspect.
        for (var i = 0; i < 4; i++)
            TakeTiles(takeTiles, state, (state.DealerSeatIndex + i) % 4, count: 4);

        // Two viewers: seat 0 (sees own hand face-up) and seat 2 (sees own hand face-up,
        // sees all OTHER hands face-down — including seat 0's).
        var entriesForSeat0 = Mahjong.Autotable.Api.Autotable
            .ChangshaToAutotableTranslator.Translate(state, viewerSeat: 0);
        var entriesForSeat2 = Mahjong.Autotable.Api.Autotable
            .ChangshaToAutotableTranslator.Translate(state, viewerSeat: 2);

        // Filter to hand things (slotName starts with "hand.").
        bool IsHandThing(Mahjong.Autotable.Api.Autotable.CollectionEntry e) =>
            e.Kind == "things" && e.Value is not null &&
            (e.Value.GetType().GetProperty("slotName")?.GetValue(e.Value) as string ?? "").StartsWith("hand.", StringComparison.Ordinal);

        int RotationOf(Mahjong.Autotable.Api.Autotable.CollectionEntry e) =>
            (int)(e.Value!.GetType().GetProperty("rotationIndex")!.GetValue(e.Value)!);

        // For viewer=0: own hand (seat 0, slot suffix "@0") face-up (rotation 1); others face-down (2).
        foreach (var e in entriesForSeat0.Where(IsHandThing))
        {
            var slot = (string)e.Value!.GetType().GetProperty("slotName")!.GetValue(e.Value)!;
            var ownHand = slot.EndsWith("@0", StringComparison.Ordinal);
            var expectedRot = ownHand ? 1 : 2;
            Assert.True(RotationOf(e) == expectedRot,
                $"viewer=0 slot={slot}: expected rotation {expectedRot}, got {RotationOf(e)}.");
        }
        // For viewer=2: own hand (seat 2, slot suffix "@2") face-up; others face-down.
        foreach (var e in entriesForSeat2.Where(IsHandThing))
        {
            var slot = (string)e.Value!.GetType().GetProperty("slotName")!.GetValue(e.Value)!;
            var ownHand = slot.EndsWith("@2", StringComparison.Ordinal);
            var expectedRot = ownHand ? 1 : 2;
            Assert.True(RotationOf(e) == expectedRot,
                $"viewer=2 slot={slot}: expected rotation {expectedRot}, got {RotationOf(e)}.");
        }
    }

    // ── §11 — Pickup translator collection emission ───────────────────────

    [Fact, Trait("Category", "Acceptance")]
    public void Pickup_Translator_Emits_PickupCollectionEntry()
    {
        // Ripley §2.5 + §2.6: while phase is one of the pickup phases, the translator
        // emits a `pickup` collection entry with { phase, seatIndex, count, dealMode,
        // breakPoint, wallIndex }. On AwaitingDiscard the runtime tombstones it.
        // Pin: a `pickup` entry must appear during pickup; it must be cleared after.
        var (state, beginManualDeal, takeTiles) = PrepareManualPickup(seed: 71, dealerSeat: 1);
        BeginManualDeal(beginManualDeal, state, new DiceRoll(5, 1));

        var pickupKindField = TryGetType("Mahjong.Autotable.Api.Autotable.ChangshaCollectionKinds")
            ?.GetField("Pickup", BindingFlags.Public | BindingFlags.Static);
        AssertPhaseFShipped("ChangshaCollectionKinds.Pickup (const)", pickupKindField);

        var pickupKind = (string)pickupKindField!.GetValue(null)!;

        var midDealEntries = Mahjong.Autotable.Api.Autotable.ChangshaToAutotableTranslator.Translate(state, viewerSeat: 1);
        var pickupEntry = midDealEntries.FirstOrDefault(e => e.Kind == pickupKind);
        Assert.NotNull(pickupEntry);
        Assert.NotNull(pickupEntry.Value);

        // Drive the full pickup; verify the translator emits a tombstone (value=null)
        // for the pickup collection once we're in AwaitingDiscard.
        for (var round = 0; round < 3; round++)
        for (var i = 0; i < 4; i++)
            TakeTiles(takeTiles, state, (state.DealerSeatIndex + i) % 4, count: 4);
        for (var i = 0; i < 4; i++)
            TakeTiles(takeTiles, state, (state.DealerSeatIndex + i) % 4, count: 1);
        TakeTiles(takeTiles, state, state.DealerSeatIndex, count: 1);

        Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
        var postDealEntries = Mahjong.Autotable.Api.Autotable.ChangshaToAutotableTranslator.Translate(state, viewerSeat: 1);
        var postPickup = postDealEntries.FirstOrDefault(e => e.Kind == pickupKind);
        Assert.True(postPickup is null || postPickup.Value is null,
            $"After deal, pickup collection must be cleared (tombstone or absent). Got: {postPickup?.Value}");
    }
}
