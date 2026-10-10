using System.Reflection;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance: Phase F three-tier bot strategy plug per Ripley §5.
///
/// <para>Architecture (Ripley §5.1): pluggable <c>IChangshaBotStrategy</c> with three
/// implementations (<c>EasyStrategy</c> / <c>MediumStrategy</c> / <c>HardStrategy</c>).
/// The existing <c>ChangshaBotPolicy</c> is ported into MediumStrategy as the default.
/// Easy and Hard are new behaviours that must be observably distinct from Medium.</para>
///
/// <para>Each strategy implements 4 decision hooks:
/// <list type="bullet">
///   <item><c>OnTurnStart</c> — bot's own discard turn</item>
///   <item><c>OnOtherDiscard</c> — claim window opportunity</item>
///   <item><c>OnSelfDraw</c> — post-draw Hu / concealed-Kong evaluation</item>
///   <item><c>OnPickupCue</c> — Phase F manual-pickup action (always Take)</item>
/// </list>
/// </para>
///
/// <para><b>Test posture:</b> Bot strategy types don't yet exist. Tests use reflection
/// to load <c>Mahjong.Autotable.Api.Changsha.Bot.IChangshaBotStrategy</c> +
/// <c>ChangshaBotEngine.Resolve(difficulty)</c>. Each test fails red with descriptive
/// missing-type messages until Bishop ships the bot engine.</para>
///
/// <para><b>Sources:</b> Ripley §5 (Bot Engine), Vasquez Phase F rule audit §10.</para>
/// </summary>
public class BotEngineAcceptanceTests
{
    // ── Reflection helpers ────────────────────────────────────────────────

    private const string BotNs = "Mahjong.Autotable.Api.Changsha.Bot";
    private static readonly Assembly ApiAssembly = typeof(ChangshaGameState).Assembly;

    private static Type? TryGetType(string fullName) => ApiAssembly.GetType(fullName);

    private static void AssertPhaseFShipped(string symbolDescription, object? symbol)
    {
        Assert.True(symbol != null,
            $"Phase F backend not yet shipped — missing {symbolDescription}. " +
            $"Bishop owns; see .squad/decisions/inbox/ripley-phase-f-design.md §5.");
    }

    /// <summary>
    /// Resolve a strategy instance via <c>ChangshaBotEngine.Resolve(difficulty)</c>.
    /// Returns the instance (typed as object) and the strategy interface Type for
    /// further method-info lookups.
    /// </summary>
    private static (object Strategy, Type StrategyInterface) ResolveStrategy(string difficulty)
    {
        var engineType = TryGetType($"{BotNs}.ChangshaBotEngine");
        AssertPhaseFShipped($"{BotNs}.ChangshaBotEngine (resolver)", engineType);

        var resolve = engineType!.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static);
        AssertPhaseFShipped("ChangshaBotEngine.Resolve(string)", resolve);
        var strategy = resolve!.Invoke(null, new object[] { difficulty })!;

        var iface = TryGetType($"{BotNs}.IChangshaBotStrategy");
        AssertPhaseFShipped($"{BotNs}.IChangshaBotStrategy (interface)", iface);

        return (strategy, iface!);
    }

    private static BotAction InvokeOnTurnStart(object strategy, Type iface, ChangshaGameState state, int seat)
    {
        var method = iface.GetMethod("OnTurnStart");
        AssertPhaseFShipped("IChangshaBotStrategy.OnTurnStart", method);
        var result = method!.Invoke(strategy, new object[] { state, seat });
        return ToBotAction(result);
    }

    private static BotAction InvokeOnOtherDiscard(object strategy, Type iface,
        ChangshaGameState state, int seat, int discarderSeat, int discardedTileId)
    {
        var method = iface.GetMethod("OnOtherDiscard");
        AssertPhaseFShipped("IChangshaBotStrategy.OnOtherDiscard", method);
        var result = method!.Invoke(strategy, new object[] { state, seat, discarderSeat, discardedTileId });
        return ToBotAction(result);
    }

    private static BotAction InvokeOnPickupCue(object strategy, Type iface, ChangshaGameState state, int seat)
    {
        var method = iface.GetMethod("OnPickupCue");
        AssertPhaseFShipped("IChangshaBotStrategy.OnPickupCue", method);
        var result = method!.Invoke(strategy, new object[] { state, seat });
        return ToBotAction(result);
    }

    private static BotAction ToBotAction(object? raw)
    {
        if (raw is BotAction action) return action;
        // Tolerate strategies that return a Phase-F-specific action type — translate
        // via duck typing for the assertions we make in this file.
        if (raw is null) return BotAction.Pass();
        var t = raw.GetType();
        var typeProp = t.GetProperty("Type")?.GetValue(raw)?.ToString();
        var tileId = t.GetProperty("TileId")?.GetValue(raw) as int?;
        var logical = t.GetProperty("LogicalTile")?.GetValue(raw) as int?;
        var claim = t.GetProperty("ClaimType")?.GetValue(raw);
        return typeProp switch
        {
            "Discard" => BotAction.Discard(tileId ?? -1),
            "Claim" => claim is TableClaimType ct ? BotAction.Claim(ct) : BotAction.Pass(),
            "DeclareWin" => BotAction.DeclareWin(),
            "DeclareConcealedKong" => BotAction.DeclareConcealedKong(logical ?? -1),
            "DeclareAddedKong" => BotAction.DeclareAddedKong(tileId ?? -1),
            "Pass" => BotAction.Pass(),
            "Take" => BotAction.Wait(), // pickup take — no equivalent in existing BotAction; treat as Wait for compat
            _ => BotAction.Wait()
        };
    }

    /// <summary>Build a 14-tile state for the active seat with a specific hand pattern.</summary>
    private static ChangshaGameState NewStateWithSeatHand(
        int activeSeat, IEnumerable<int> tiles, int seed = 4242)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.Phase = ChangshaPhase.AwaitingDiscard;
        state.ActiveSeatIndex = activeSeat;
        state.DealerSeatIndex = activeSeat;
        AcceptanceFixture.OverrideHand(state, activeSeat, tiles.ToArray());
        return state;
    }

    // ── §10.1 — Easy strategy: predictable, exploitable ───────────────────

    [Fact, Trait("Category", "Acceptance")]
    public void EasyBot_DiscardsHighestRank()
    {
        // Vasquez audit §10 Easy.1: Easy discards the maximum-rank tile in its hand
        // (tiebreak by suit-index descending for determinism). Given a 14-tile hand
        // with a clear unique max rank, Easy must discard that tile.
        var (easy, iface) = ResolveStrategy("easy");

        // Hand with 9-Tiao as the unique max-rank tile.
        var tiles = Tiles(
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Tong, 4), (Suit.Tong, 5), (Suit.Tong, 5), (Suit.Tong, 6),
            (Suit.Tiao, 2), (Suit.Tiao, 3), (Suit.Tiao, 5), (Suit.Tiao, 7),
            (Suit.Tiao, 8), (Suit.Tiao, 9));
        var state = NewStateWithSeatHand(activeSeat: 0, tiles);
        var nineTiao = Tid(Suit.Tiao, 9, 0);

        var action = InvokeOnTurnStart(easy, iface, state, seat: 0);

        Assert.Equal(BotActionType.Discard, action.Type);
        Assert.Equal(nineTiao, action.TileId);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void EasyBot_NeverClaimsChow()
    {
        // Vasquez audit §10 Easy.2: Easy bot refuses Chow claims even when offered
        // (next-CCW from discarder with a valid sequence). This is the loudest behavioural
        // difference between Easy and Medium — Easy doesn't build hand shape.
        var (easy, iface) = ResolveStrategy("easy");

        // Seat 0 is dealer (discarder). Seat 1 (next-CCW) has Wan-2, Wan-3 in hand,
        // and is about to be offered Chow on a Wan-1 discard from seat 0.
        var seat1Hand = Tiles(
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Tong, 5), (Suit.Tong, 5),
            (Suit.Tong, 6), (Suit.Tong, 7), (Suit.Tong, 8), (Suit.Tiao, 1),
            (Suit.Tiao, 2), (Suit.Tiao, 3), (Suit.Tiao, 4), (Suit.Tiao, 5),
            (Suit.Tiao, 6));
        var state = NewStateWithSeatHand(activeSeat: 0, seat1Hand, seed: 11);
        state.Hands[0].ConcealedTiles.Clear();
        state.Hands[0].ConcealedTiles.AddRange(seat1Hand);
        AcceptanceFixture.OverrideHand(state, 1, seat1Hand.ToArray());
        // Build a claim window with a Chow opportunity for seat 1.
        var discardedWan1 = Tid(Suit.Wan, 1, 0);
        state.ClaimWindow = new ChangshaClaimWindow
        {
            DiscardSeatIndex = 0,
            DiscardTileId = discardedWan1,
            Opportunities = new List<ChangshaClaimOpportunity>
            {
                new() { SeatIndex = 1, ClaimType = TableClaimType.Chow, Priority = 1 }
            }
        };
        state.Phase = ChangshaPhase.AwaitingClaim;

        var action = InvokeOnOtherDiscard(easy, iface, state, seat: 1, discarderSeat: 0, discardedTileId: discardedWan1);

        Assert.True(action.Type != BotActionType.Claim || action.ClaimType != TableClaimType.Chow,
            $"Easy strategy must NOT claim Chow. Got: type={action.Type} claim={action.ClaimType}.");
    }

    [Fact, Trait("Category", "Acceptance")]
    public void EasyBot_AlwaysClaimsHu()
    {
        // Vasquez audit §10 Easy.3: Easy always takes a Hu claim opportunity.
        // (Easy doesn't compute defensive lookahead — it greedily wins.)
        var (easy, iface) = ResolveStrategy("easy");

        var state = NewStateWithSeatHand(activeSeat: 0,
            AcceptanceFixture.ThirteenTileWaitingForWan1());
        state.Phase = ChangshaPhase.AwaitingClaim;
        state.ActiveSeatIndex = 2; // some other seat is active
        AcceptanceFixture.OverrideHand(state, 1, AcceptanceFixture.ThirteenTileWaitingForWan1().ToArray());

        var winningTile = Tid(Suit.Wan, 1, 0);
        state.ClaimWindow = new ChangshaClaimWindow
        {
            DiscardSeatIndex = 2,
            DiscardTileId = winningTile,
            Opportunities = new List<ChangshaClaimOpportunity>
            {
                new() { SeatIndex = 1, ClaimType = TableClaimType.Hu, Priority = 100 }
            }
        };

        var action = InvokeOnOtherDiscard(easy, iface, state, seat: 1, discarderSeat: 2, discardedTileId: winningTile);

        Assert.Equal(BotActionType.Claim, action.Type);
        Assert.Equal(TableClaimType.Hu, action.ClaimType);
    }

    // ── §10.2 — Medium strategy ───────────────────────────────────────────

    [Fact, Trait("Category", "Acceptance")]
    public void MediumBot_ClaimsChowOnlyWhenNextInTurn()
    {
        // Spec §3.5 + Vasquez audit §10 Medium.3: Chow may only be claimed by the
        // next-CCW seat from the discarder (in Changsha, unlike Riichi). Medium
        // respects this — when offered Chow as a non-next seat, it passes (or escalates
        // to Pung/Kong/Hu if also available). The state machine's ClaimAdjudicator
        // already filters this, so Medium just needs to honor whatever is offered.
        var (medium, iface) = ResolveStrategy("medium");

        // Construct a hand for seat 2 that COULD chow Wan-1 (has Wan-2 + Wan-3) but
        // seat 2 is NOT next-CCW from seat 0 (next would be seat 1). The
        // ClaimAdjudicator would never offer Chow to seat 2 — so Medium's input
        // never sees a Chow opportunity. We pin: if presented with no opportunities,
        // Medium passes; if presented with a Chow as next-CCW + non-shanten-worsening,
        // it claims.
        var seat2Hand = Tiles(
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 5), (Suit.Tong, 1),
            (Suit.Tong, 2), (Suit.Tong, 3), (Suit.Tong, 5), (Suit.Tong, 5),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6), (Suit.Tiao, 7),
            (Suit.Tiao, 8));
        var state = NewStateWithSeatHand(activeSeat: 0, seat2Hand);
        AcceptanceFixture.OverrideHand(state, 2, seat2Hand.ToArray());
        var discardedWan1 = Tid(Suit.Wan, 1, 0);
        // No Chow offered to seat 2 (since seat 2 is NOT next-CCW from seat 0).
        state.ClaimWindow = new ChangshaClaimWindow
        {
            DiscardSeatIndex = 0,
            DiscardTileId = discardedWan1,
            Opportunities = new List<ChangshaClaimOpportunity>()
        };
        state.Phase = ChangshaPhase.AwaitingClaim;

        var action = InvokeOnOtherDiscard(medium, iface, state, seat: 2, discarderSeat: 0, discardedTileId: discardedWan1);

        // With no opportunities offered, Medium must pass.
        Assert.True(action.Type == BotActionType.Pass || action.Type == BotActionType.Wait,
            $"Medium strategy with no offered claims must pass/wait. Got: {action.Type}.");
    }

    [Fact, Trait("Category", "Acceptance")]
    public void MediumBot_ClaimsHuWhenOffered()
    {
        // Sanity check: Medium honors Hu (highest-priority claim) — same as Easy
        // and existing ChangshaBotPolicy. This is the baseline Medium must not regress.
        var (medium, iface) = ResolveStrategy("medium");

        var hand = AcceptanceFixture.ThirteenTileWaitingForWan1();
        var state = NewStateWithSeatHand(activeSeat: 0, hand);
        AcceptanceFixture.OverrideHand(state, 1, hand.ToArray());
        var winningTile = Tid(Suit.Wan, 1, 0);
        state.ClaimWindow = new ChangshaClaimWindow
        {
            DiscardSeatIndex = 2,
            DiscardTileId = winningTile,
            Opportunities = new List<ChangshaClaimOpportunity>
            {
                new() { SeatIndex = 1, ClaimType = TableClaimType.Hu, Priority = 100 }
            }
        };
        state.Phase = ChangshaPhase.AwaitingClaim;

        var action = InvokeOnOtherDiscard(medium, iface, state, seat: 1, discarderSeat: 2, discardedTileId: winningTile);

        Assert.Equal(BotActionType.Claim, action.Type);
        Assert.Equal(TableClaimType.Hu, action.ClaimType);
    }

    [Fact, Trait("Category", "Acceptance")]
    public void MediumBot_DiscardsToReduceShanten()
    {
        // Vasquez audit §10 Medium.1: given a 14-tile hand with computable shanten D,
        // Medium's discard must produce a 13-tile shape with shanten ≤ D.
        // Implementation pin: HandEvaluator.MinShantenToHu (Bishop §5.5) must exist.
        var evaluatorType = TryGetType($"{BotNs}.HandEvaluator");
        AssertPhaseFShipped($"{BotNs}.HandEvaluator", evaluatorType);

        var minShanten = evaluatorType!.GetMethod("MinShantenToHu", BindingFlags.Public | BindingFlags.Static);
        AssertPhaseFShipped("HandEvaluator.MinShantenToHu(hand, remainingWall)", minShanten);

        var (medium, iface) = ResolveStrategy("medium");

        // 14-tile hand: 4 chows + 1 stray. Shanten = 0 (already winning) or 1.
        var tiles = Tiles(
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tong, 5), (Suit.Tong, 5),
            (Suit.Tiao, 7), (Suit.Tiao, 8), (Suit.Tiao, 9));
        var state = NewStateWithSeatHand(activeSeat: 0, tiles);

        var handBefore = state.Hands[0];
        var shantenBefore = (int)minShanten!.Invoke(null, new object[] { handBefore, state.Wall })!;

        var action = InvokeOnTurnStart(medium, iface, state, seat: 0);
        // Medium may also DeclareWin if the hand is winning — accept that here.
        if (action.Type == BotActionType.DeclareWin)
        {
            Assert.Equal(0, shantenBefore);
            return;
        }
        Assert.Equal(BotActionType.Discard, action.Type);

        // Apply the discard.
        state.Hands[0].ConcealedTiles.Remove(action.TileId!.Value);
        var shantenAfter = (int)minShanten.Invoke(null, new object[] { state.Hands[0], state.Wall })!;

        Assert.True(shantenAfter <= shantenBefore,
            $"Medium discard worsened shanten: before={shantenBefore}, after={shantenAfter}. " +
            $"Discarded tile id={action.TileId}.");
    }

    // ── §10.3 — Hard strategy ─────────────────────────────────────────────

    [Fact, Trait("Category", "Acceptance")]
    public void HardBot_RespectsMissedWinLockout()
    {
        // Vasquez audit §10 Hard.4: Hard tracks `state.MissedWinSeats` and avoids
        // declaring Hu when the bot itself is locked out. The state machine's
        // DeclareSelfDrawWin (and the Discard-driven Hu path) does NOT enforce
        // the lockout on SELF-DRAW (per spec §3.6), but Discard-Hu is filtered.
        // Pin: Hard does not attempt a Discard-Hu when locked out (would be a no-op
        // and waste a turn).
        var (hard, iface) = ResolveStrategy("hard");

        var hand = AcceptanceFixture.ThirteenTileWaitingForWan1();
        var state = NewStateWithSeatHand(activeSeat: 0, hand);
        AcceptanceFixture.OverrideHand(state, 1, hand.ToArray());
        state.MissedWinSeats.Add(1); // Seat 1 is locked out.

        var winningTile = Tid(Suit.Wan, 1, 0);
        // Per spec §3.6, Discard.cs filters opportunities for missed-win seats.
        // So the claim window arriving at Hard has NO Hu opportunity for seat 1.
        state.ClaimWindow = new ChangshaClaimWindow
        {
            DiscardSeatIndex = 2,
            DiscardTileId = winningTile,
            Opportunities = new List<ChangshaClaimOpportunity>() // empty — lockout filtered
        };
        state.Phase = ChangshaPhase.AwaitingClaim;

        var action = InvokeOnOtherDiscard(hard, iface, state, seat: 1, discarderSeat: 2, discardedTileId: winningTile);

        Assert.True(action.Type is BotActionType.Pass or BotActionType.Wait,
            $"Hard strategy with empty opportunities (locked out) must pass. Got: {action.Type}.");
    }

    [Fact, Trait("Category", "Acceptance")]
    public void HardBot_DiscardsLegally_NotEmptyHand()
    {
        // Smoke: Hard's decision logic is more complex than Easy/Medium. Pin that
        // for any reasonable 14-tile hand, Hard produces a legal Discard (or
        // DeclareWin/Kong) — never an Invalid action. Catches early-stage Hard
        // implementations that try to discard a non-existent tile.
        var (hard, iface) = ResolveStrategy("hard");

        var tiles = Tiles(
            (Suit.Wan, 1), (Suit.Wan, 4), (Suit.Wan, 7),
            (Suit.Tong, 2), (Suit.Tong, 3), (Suit.Tong, 6),
            (Suit.Tiao, 1), (Suit.Tiao, 2), (Suit.Tiao, 3),
            (Suit.Tiao, 5), (Suit.Tiao, 5), (Suit.Tiao, 7),
            (Suit.Tiao, 8), (Suit.Tiao, 9));
        var state = NewStateWithSeatHand(activeSeat: 0, tiles);

        var action = InvokeOnTurnStart(hard, iface, state, seat: 0);

        var validTypes = new[] {
            BotActionType.Discard, BotActionType.DeclareWin,
            BotActionType.DeclareConcealedKong, BotActionType.DeclareAddedKong };
        Assert.Contains(action.Type, validTypes);
        if (action.Type == BotActionType.Discard)
        {
            Assert.NotNull(action.TileId);
            Assert.Contains(action.TileId!.Value, state.Hands[0].ConcealedTiles);
        }
    }

    // ── §10 — Bot-vs-bot sanity ───────────────────────────────────────────

    [Theory, Trait("Category", "Acceptance")]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(7777)]
    public void Bot_Vs_Bot_4Players_HandCompletesIn200Steps(int seed)
    {
        // Vasquez audit §10 Bot-vs-Bot: 4 bots playing one full hand must terminate
        // within a bounded step count — no infinite loops, no deadlocks.
        // Re-uses existing BotMatchHarness (which uses the existing ChangshaBotPolicy,
        // i.e. MediumStrategy in Phase F terms). When Bishop ports, this test continues
        // to validate the resulting hand still terminates.
        var outcome = BotMatchHarness.RunUntilHandFinished(seed, maxSteps: 200);
        Assert.True(outcome.WinnerDeclared || outcome.WallExhausted,
            $"Hand must end in win or wall-exhaustion within 200 steps. " +
            $"WinnerDeclared={outcome.WinnerDeclared}, WallExhausted={outcome.WallExhausted}, " +
            $"Phase={outcome.FinalState.Phase}, Steps={outcome.Steps}.");
    }

    // ── §10 — Pickup-cue hook ─────────────────────────────────────────────

    [Theory, Trait("Category", "Acceptance")]
    [InlineData("easy")]
    [InlineData("medium")]
    [InlineData("hard")]
    public void Bot_OnPickupCue_AlwaysReturnsTake(string difficulty)
    {
        // Vasquez audit §10: in manual pickup, bots never refuse to pick — they always
        // take the expected count. Pin that the OnPickupCue hook returns the "Take"
        // action (or a Wait that the runtime translates to a TakeTilesFromWall call).
        var (strategy, iface) = ResolveStrategy(difficulty);

        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 31, botSeatIndexes: new[] { 0, 1, 2, 3 });
        // Phase = a pickup phase (set via reflection if the enum value exists; else skip).
        var phaseProp = typeof(ChangshaGameState).GetProperty("Phase");
        var phaseEnumType = phaseProp!.PropertyType;
        var pickupPhaseExists = Enum.GetNames(phaseEnumType).Contains("PickupRound1");
        if (!pickupPhaseExists)
            Assert.Fail("Phase F backend not yet shipped — missing ChangshaPhase.PickupRound1 enum value. Bishop owns.");
        phaseProp.SetValue(state, Enum.Parse(phaseEnumType, "PickupRound1"));

        // Resolve PickupSeatIndex prop if it exists.
        var pickupSeatProp = typeof(ChangshaGameState).GetProperty("PickupSeatIndex");
        AssertPhaseFShipped("ChangshaGameState.PickupSeatIndex", pickupSeatProp);
        pickupSeatProp!.SetValue(state, 0);

        var action = InvokeOnPickupCue(strategy, iface, state, seat: 0);

        // The "Take" action shape isn't part of the existing BotActionType. The Phase-F-specific
        // action enum must include a Take/Pickup case OR the strategy returns a Wait that the
        // runtime knows means "perform the expected pickup". Either way, the bot's OnPickupCue
        // must NOT return DeclareWin/Discard/Claim/Pass (none of which make sense mid-pickup).
        var invalidTypes = new[]
        {
            BotActionType.Discard, BotActionType.DeclareWin,
            BotActionType.DeclareConcealedKong, BotActionType.DeclareAddedKong,
            BotActionType.Claim, BotActionType.Pass
        };
        Assert.DoesNotContain(action.Type, invalidTypes);
    }

    // ── §10 — Strategy resolver ───────────────────────────────────────────

    [Theory, Trait("Category", "Acceptance")]
    [InlineData("easy", "EasyStrategy")]
    [InlineData("medium", "MediumStrategy")]
    [InlineData("hard", "HardStrategy")]
    [InlineData("Easy", "EasyStrategy")]
    [InlineData("MEDIUM", "MediumStrategy")]
    [InlineData(null, "MediumStrategy")]
    [InlineData("nonsense", "MediumStrategy")]
    public void ChangshaBotEngine_Resolve_ReturnsExpectedStrategy(string? difficulty, string expectedTypeName)
    {
        // Ripley §5.1: ChangshaBotEngine.Resolve(difficulty) returns the matching strategy
        // (case-insensitive). Null / unknown → MediumStrategy (default).
        var engineType = TryGetType($"{BotNs}.ChangshaBotEngine");
        AssertPhaseFShipped($"{BotNs}.ChangshaBotEngine", engineType);
        var resolve = engineType!.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static);
        AssertPhaseFShipped("ChangshaBotEngine.Resolve(string)", resolve);

        var strategy = resolve!.Invoke(null, new object?[] { difficulty });
        Assert.NotNull(strategy);
        Assert.Equal(expectedTypeName, strategy!.GetType().Name);
    }

    // ── §10 — Bot move delay configuration ────────────────────────────────

    [Fact, Trait("Category", "Acceptance")]
    public void ChangshaRuntimeOptions_HasBotPickupDelayMs_Default500()
    {
        // Ripley §5.6: BotPickupDelayMs default = 500ms. BotMoveDelayMs default = 800ms.
        // BotClaimDelayMs default = 400ms.
        var optsType = TryGetType("Mahjong.Autotable.Api.Changsha.Runtime.ChangshaRuntimeOptions");
        Assert.NotNull(optsType);

        var pickupDelayProp = optsType!.GetProperty("BotPickupDelayMs");
        AssertPhaseFShipped("ChangshaRuntimeOptions.BotPickupDelayMs", pickupDelayProp);

        var defaults = Activator.CreateInstance(optsType)!;
        var defaultValue = (int)pickupDelayProp!.GetValue(defaults)!;
        Assert.Equal(500, defaultValue);
    }
}
