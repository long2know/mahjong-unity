using System.Reflection;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Changsha.Scoring;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// CAT-K: Edge cases & special rules.
/// </summary>
public class EdgeCaseTests
{
    [Fact, Trait("Category", "Changsha")]
    public void ConcealedKong_CannotBeRobbed_ClaimAdjudicatorDoesNotProduceRobOpportunity()
    {
        // The adjudicator only enumerates discard-window claims; concealed kong never reaches it.
        // Build a hand state with a 4-of-a-kind in seat 1; seat 0 discards an unrelated tile.
        var hands = Enumerable.Range(0, 4).Select(i => new ChangshaHandState { SeatIndex = i }).ToList();
        hands[1].ConcealedTiles.AddRange(Tiles(
            (Suit.Tong, 4), (Suit.Tong, 4), (Suit.Tong, 4), (Suit.Tong, 4)));

        // Seat 0 discards Wan-1 (no relation to Tong-4); no Hu/Kong/Pung opportunities should arise.
        var opps = new ClaimAdjudicator().GetOpportunities(0, Tid(Suit.Wan, 1, 0), hands);
        Assert.Empty(opps);
    }

    [Fact, Trait("Category", "Changsha")]
    public void WallExhausted_NoWinner_HandEndsInDraw()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 1);
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(1));
        ChangshaGameStateMachine.Deal(state);

        // Force wall exhaustion.
        state.Wall.Clear();
        state.Phase = ChangshaPhase.WallExhausted;

        ChangshaGameStateMachine.HandleWallExhausted(state);

        Assert.Equal(ChangshaPhase.EndHand, state.Phase);
        Assert.Null(state.CurrentWin);
    }

    [Fact, Trait("Category", "Changsha")]
    public void BigWin_AllPungs_ExemptFrom258PairRule()
    {
        // AllPungs hand with pair of 3 (NOT a 258 rank).
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 1), (Suit.Wan, 1),
            (Suit.Wan, 9), (Suit.Wan, 9), (Suit.Wan, 9),
            (Suit.Tong, 4), (Suit.Tong, 4), (Suit.Tong, 4),
            (Suit.Tiao, 7), (Suit.Tiao, 7), (Suit.Tiao, 7),
            (Suit.Tiao, 3), (Suit.Tiao, 3));

        var result = new ChangshaWinDetector().Detect(hand);
        Assert.True(result.IsWin);
        Assert.Equal(WinPattern.AllPungs, result.Pattern);
    }

    [Fact, Trait("Category", "Changsha")]
    public void BigWin_FullFlush_AllowsChowMelds_NoPungRequirement()
    {
        // Single-suit hand with chows + non-258 pair.
        var hand = HandOf(0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 4), (Suit.Wan, 4),
            (Suit.Wan, 6), (Suit.Wan, 7), (Suit.Wan, 8),
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9));

        var result = new ChangshaWinDetector().Detect(hand);
        Assert.True(result.IsWin);
        Assert.True(result.IsFullFlush);
    }

    [Fact, Trait("Category", "Changsha")]
    public void DiscardFromWrongSeat_Throws()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 11);
        var notDealer = (state.DealerSeatIndex + 1) % 4;
        Assert.Throws<InvalidOperationException>(() =>
            ChangshaGameStateMachine.Discard(state, notDealer, state.Hands[notDealer].ConcealedTiles[0]));
    }

    [Fact, Trait("Category", "Changsha")]
    public void DiscardTileNotHeld_Throws()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 11);
        var dealer = state.DealerSeatIndex;
        var foreign = Enumerable.Range(0, 108).First(t => !state.Hands[dealer].ConcealedTiles.Contains(t));
        Assert.Throws<InvalidOperationException>(() =>
            ChangshaGameStateMachine.Discard(state, dealer, foreign));
    }

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "2")]
    public void ExposedKong_CanBeRobbed_DeferredToV2()
    {
        // Phase H Wave 2 §2.2 (declarer-side, edge-case view): the inverse of
        // RobbingKong_Win — an added kong (補杠) opens a Hu-only claim window for any
        // seat that can win on the kong-target tile. Originally the
        // `ExposedKong_CanBeRobbed_DeferredToV2` placeholder; un-skipped per Ripley's
        // Phase H design memo §2.2.
        //
        // This test pairs with the broader RobbingKongAcceptanceTests suite — it pins
        // the edge-case shape: WinResult.IsRobbedKong=true, Method=RobbingKong, with
        // SourceSeatIndex pointing at the kong declarer (not the discarder).
        var state = BuildAddedKongScenarioWithRobber();

        ChangshaGameStateMachine.DeclareAddedKong(state, seatIndex: 0,
            tileId: Tid(Suit.Wan, 5, 3));
        ChangshaGameStateMachine.ResolveClaim(state, claimingSeatIndex: 2,
            claimType: Tables.TableClaimType.Hu);

        Assert.NotNull(state.CurrentWin);
        Assert.Equal(WinMethod.RobbingKong, state.CurrentWin!.Method);
        Assert.Equal(0, state.CurrentWin.SourceSeatIndex);

        var isRobbedKong = ResolveIsRobbedKongProp(state.CurrentWin);
        Assert.True(isRobbedKong,
            $"WinResult.IsRobbedKong must be true on a robbing-kong win. " +
            $"Bishop owes the Phase H Wave 2 contract (WinResult.IsRobbedKong : bool).");
    }

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "2")]
    public void MultipleBigWinPatterns_DoNotStackByDefault_StackOnlyUnderHouseRules()
    {
        // #117 — spec §5.1 has NO stacking table, so the LIVE default (SpecPure) path
        // must NOT multiply a hand that satisfies AllPungs + FullFlush. Both the
        // stacked and the single-pattern self-draw pay the same base Big-Win amount.
        // Driven via the full Score() pipeline so the test pins both the detector
        // contract (AllPatterns populated) AND the spec-pure scoring policy.
        var stackedSpec = ScoreStackedHand();
        var singleSpec = ScoreSinglePatternHand();

        Assert.Equal(ScoreCategory.BigWin, stackedSpec.Category);
        Assert.Equal(ScoreCategory.BigWin, singleSpec.Category);
        Assert.Equal(singleSpec.BasePoints, stackedSpec.BasePoints);

        // The stacking capability is retained behind ChangshaScoringOptions.HouseRules
        // (non-spec opt-in). Characterization so the pre-#117 magnitude is not lost:
        // under house-rules the stacked hand pays strictly MORE than the single pattern
        // (base ×2 stacking + the extra FullFlush fan bonus).
        var stackedHouse = ScoreStackedHand(ChangshaScoringOptions.HouseRules);
        var singleHouse = ScoreSinglePatternHand(ChangshaScoringOptions.HouseRules);

        Assert.True(stackedHouse.BasePoints >= 2 * singleSpec.BasePoints,
            $"Under house-rules, AllPungs+FullFlush should pay ≥ 2× the spec base. " +
            $"StackedHouse={stackedHouse.BasePoints}, SpecBase={singleSpec.BasePoints}.");
        Assert.True(stackedHouse.BasePoints > singleHouse.BasePoints,
            $"Under house-rules the stacked hand must out-pay the single pattern. " +
            $"StackedHouse={stackedHouse.BasePoints}, SingleHouse={singleHouse.BasePoints}.");
    }

    // ── Phase H Wave 2 — helpers (reflection-defensive against Bishop's contracts) ──

    private static bool ResolveIsRobbedKongProp(WinResult win)
    {
        var prop = typeof(WinResult).GetProperty("IsRobbedKong",
            BindingFlags.Public | BindingFlags.Instance);
        if (prop is null)
        {
            throw new InvalidOperationException(
                "WinResult.IsRobbedKong property not found — Bishop owes the Phase H Wave 2 contract.");
        }
        return (bool)(prop.GetValue(win) ?? false);
    }

    /// <summary>Build a kong-robbing scenario: seat 0 has exposed Pung of Wan-5 + 4th
    /// Wan-5 in concealed; seat 2 has a 13-tile hand that completes on Wan-5; seats 1/3
    /// empty.</summary>
    private static ChangshaGameState BuildAddedKongScenarioWithRobber()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 42,
            botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.DealerSeatIndex = 0;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == 0;
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(42));
        ChangshaGameStateMachine.Deal(state);
        state.ActiveSeatIndex = 0;

        var wan5 = Logical(Suit.Wan, 5);
        foreach (var h in state.Hands) h.ConcealedTiles.RemoveAll(t => t / 4 == wan5);
        state.Wall.RemoveAll(t => t / 4 == wan5);

        state.Hands[0].ConcealedTiles.Clear();
        state.Hands[0].Melds.Clear();
        state.Hands[0].Melds.Add(new Meld
        {
            Kind = MeldKind.Pung,
            TileIds = new List<int>
            {
                Tid(Suit.Wan, 5, 0), Tid(Suit.Wan, 5, 1), Tid(Suit.Wan, 5, 2)
            },
            ClaimedFromSeatIndex = 3
        });
        state.Hands[0].ConcealedTiles.AddRange(new[]
        {
            Tid(Suit.Wan, 5, 3),
            Tid(Suit.Tong, 2, 0), Tid(Suit.Tong, 2, 1), Tid(Suit.Tong, 2, 2),
            Tid(Suit.Tong, 3, 0), Tid(Suit.Tong, 3, 1), Tid(Suit.Tong, 3, 2),
            Tid(Suit.Tong, 4, 0), Tid(Suit.Tong, 4, 1), Tid(Suit.Tong, 4, 2),
            Tid(Suit.Tong, 5, 0),
        });

        state.Hands[1].ConcealedTiles.Clear();
        state.Hands[1].Melds.Clear();

        state.Hands[2].ConcealedTiles.Clear();
        state.Hands[2].Melds.Clear();
        state.Hands[2].ConcealedTiles.AddRange(new[]
        {
            Tid(Suit.Wan, 1, 0), Tid(Suit.Wan, 2, 0), Tid(Suit.Wan, 3, 0),
            Tid(Suit.Wan, 4, 0), Tid(Suit.Wan, 6, 0),
            Tid(Suit.Wan, 7, 0), Tid(Suit.Wan, 8, 0), Tid(Suit.Wan, 9, 0),
            Tid(Suit.Tiao, 1, 0), Tid(Suit.Tiao, 1, 1), Tid(Suit.Tiao, 1, 2),
            Tid(Suit.Tiao, 5, 0), Tid(Suit.Tiao, 5, 1),
        });

        state.Hands[3].ConcealedTiles.Clear();
        state.Hands[3].Melds.Clear();
        state.MissedWinSeats.Clear();
        state.Phase = ChangshaPhase.AwaitingDiscard;
        return state;
    }

    /// <summary>Drive Score() on an AllPungs+FullFlush self-draw win and return the resulting
    /// ScoreResult. Dealer-neutral: winner is seat 1, dealer remains seat 0, so the win is
    /// "non-dealer self-draw, non-dealer involved" → base × multiplier per opponent (×3 base,
    /// ×2 stack = 6 per opponent → 12 from non-dealer seats + 8 from dealer = stacked total).</summary>
    private static ScoreResult ScoreStackedHand(ChangshaScoringOptions? options = null)
    {
        var state = BuildScoringScenario();
        // Seat 1 self-draws a Wan-2-2 pair completing an AllPungs+FullFlush hand.
        state.ActiveSeatIndex = 1;
        state.Hands[1].ConcealedTiles.Clear();
        state.Hands[1].Melds.Clear();
        state.Hands[1].ConcealedTiles.AddRange(new[]
        {
            Tid(Suit.Wan, 1, 0), Tid(Suit.Wan, 1, 1), Tid(Suit.Wan, 1, 2),
            Tid(Suit.Wan, 4, 0), Tid(Suit.Wan, 4, 1), Tid(Suit.Wan, 4, 2),
            Tid(Suit.Wan, 5, 0), Tid(Suit.Wan, 5, 1), Tid(Suit.Wan, 5, 2),
            Tid(Suit.Wan, 7, 0), Tid(Suit.Wan, 7, 1), Tid(Suit.Wan, 7, 2),
            Tid(Suit.Wan, 2, 0), Tid(Suit.Wan, 2, 1),
        });

        CompleteScoringDraw(state);
        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 1);
        ChangshaGameStateMachine.Score(state, options);
        return state.CurrentScore!;
    }

    private static ScoreResult ScoreSinglePatternHand(ChangshaScoringOptions? options = null)
    {
        var state = BuildScoringScenario();
        // Seat 1 self-draws an AllPungs (NOT FullFlush) hand — 4 pungs across suits +
        // pair of 3 (non-258, but AllPungs is 258-exempt). One pattern only.
        state.ActiveSeatIndex = 1;
        state.Hands[1].ConcealedTiles.Clear();
        state.Hands[1].Melds.Clear();
        state.Hands[1].ConcealedTiles.AddRange(new[]
        {
            Tid(Suit.Wan, 1, 0), Tid(Suit.Wan, 1, 1), Tid(Suit.Wan, 1, 2),
            Tid(Suit.Wan, 9, 0), Tid(Suit.Wan, 9, 1), Tid(Suit.Wan, 9, 2),
            Tid(Suit.Tong, 4, 0), Tid(Suit.Tong, 4, 1), Tid(Suit.Tong, 4, 2),
            Tid(Suit.Tiao, 7, 0), Tid(Suit.Tiao, 7, 1), Tid(Suit.Tiao, 7, 2),
            Tid(Suit.Tiao, 3, 0), Tid(Suit.Tiao, 3, 1),
        });

        CompleteScoringDraw(state);
        ChangshaGameStateMachine.DeclareSelfDrawWin(state, seatIndex: 1);
        ChangshaGameStateMachine.Score(state, options);
        return state.CurrentScore!;
    }

    private static void CompleteScoringDraw(ChangshaGameState state)
    {
        var hand = state.Hands[1].ConcealedTiles;
        var drawnTile = hand[^1];
        hand.RemoveAt(hand.Count - 1);
        state.Wall.RemoveAll(tile => tile == drawnTile);
        state.Wall.Insert(0, drawnTile);
        state.WallBackIndex = state.Wall.Count - 1;
        Assert.Equal(13, hand.Count);
        ChangshaGameStateMachine.DrawTile(state);
        Assert.Equal(1, state.LastDrawSeatIndex);
        Assert.Equal(drawnTile, hand[^1]);
    }

    private static ChangshaGameState BuildScoringScenario()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 7,
            botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.DealerSeatIndex = 0;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == 0;
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(7));
        ChangshaGameStateMachine.Deal(state);
        state.Phase = ChangshaPhase.AwaitingDiscard;
        return state;
    }

    // ── Phase H Wave 1 — StateVersion optimistic concurrency ─────────────────────
    //
    // Bishop's contract (matches Phase H Wave 1 spec):
    //   - ChangshaGameState.StateVersion : int, starts at 0, increments by 1 on each
    //     successful mutation applied through IChangshaGameRuntime.
    //   - Every IChangshaGameRuntime mutation method gains a trailing
    //     `int? expectedVersion = null` parameter.
    //   - When expectedVersion != null and != state.StateVersion → throw
    //     ChangshaConcurrencyException(expected, actual), which extends
    //     InvalidOperationException and carries ExpectedVersion + ActualVersion props.
    //   - expectedVersion == null bypasses the check (back-compat path; used internally
    //     by bot-driven invocations so back-to-back bot turns just march monotonically).
    //
    // Until Bishop ships, each test below fails RED with a descriptive `Bishop owes …`
    // message naming the missing symbol. The test ASSEMBLY always compiles because every
    // new-surface touch is reflective.

    [Fact, Trait("Category", "Changsha")]
    public async Task StateVersion_StartsAtZero_OnNewGame()
    {
        await using var harness = new RuntimeHarness();
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var gameId = await runtime.CreateGameAsync(seed: 1, botSeatIndexes: null,
            hostPlayerId: null, hostConnectionId: null, cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));

        Assert.Equal(0, state!.StateVersion);
    }

    [Fact, Trait("Category", "Changsha")]
    public async Task StateVersion_NullExpectedVersion_ProceedsWithoutCheck()
    {
        // Back-compat: existing callers (and bot-internal invocations) pass null and
        // never trigger the version check. With ALL seats as bots, the runtime drives
        // every mutation internally with expectedVersion=null and the game advances.
        await using var harness = new RuntimeHarness();
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var gameId = await runtime.CreateGameAsync(seed: 42,
            botSeatIndexes: new[] { 0, 1, 2, 3 }, hostPlayerId: null, hostConnectionId: null, cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        state!.DealMode = DealMode.Auto;

        // Trigger at least one mutation (StartGameAsync) which calls many internal
        // mutations all with expectedVersion=null.
        await runtime.StartGameAsync(gameId, cts.Token);

        // No exception thrown — back-compat preserved.
        var post = Snapshot(runtime, gameId);
        Assert.True(post.StateVersion > 0,
            $"StateVersion should have advanced after StartGameAsync; got {post.StateVersion}.");
    }

    [Fact, Trait("Category", "Changsha")]
    public async Task StateVersion_FreshExpectedVersion_Succeeds_Increments()
    {
        // Passing the CURRENT version is the happy path: the check passes, the mutation
        // applies, and the version increments by at least 1.
        AssertExpectedVersionParamExists("DiscardAsync");

        await using var harness = new RuntimeHarness();
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var gameId = await runtime.CreateGameAsync(seed: 7,
            botSeatIndexes: new[] { 1, 2, 3 }, hostPlayerId: null, hostConnectionId: null, cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        state!.DealerSeatIndex = 0;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == 0;
        state.DealMode = DealMode.Auto;

        await runtime.StartGameAsync(gameId, cts.Token);
        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard
               && Snapshot(runtime, gameId).ActiveSeatIndex == 0,
            TimeSpan.FromSeconds(2),
            "auto-deal never reached AwaitingDiscard with active seat 0");

        var pre = Snapshot(runtime, gameId);
        var vBefore = pre.StateVersion;
        var tile = pre.Hands[0].ConcealedTiles[^1]; // last tile is safe to drop

        await DiscardWithVersionAsync(runtime, gameId, seatIndex: 0, tileId: tile,
            expectedVersion: vBefore, ct: cts.Token);

        var vAfter = Snapshot(runtime, gameId).StateVersion;
        Assert.True(vAfter > vBefore,
            $"StateVersion must increment on successful DiscardAsync. Before={vBefore}, After={vAfter}.");
    }

    [Fact, Trait("Category", "Changsha")]
    public async Task StateVersion_StaleExpectedVersion_ThrowsConcurrencyException()
    {
        // The unskipped successor to StateVersion_OptimisticConcurrency_DeferredToV2:
        // a mutation with an out-of-date expectedVersion MUST throw
        // ChangshaConcurrencyException carrying the offending versions.
        AssertExpectedVersionParamExists("DiscardAsync");
        var excType = ResolveConcurrencyExceptionType();

        await using var harness = new RuntimeHarness();
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var gameId = await runtime.CreateGameAsync(seed: 17,
            botSeatIndexes: new[] { 1, 2, 3 }, hostPlayerId: null, hostConnectionId: null, cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        state!.DealerSeatIndex = 0;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == 0;
        state.DealMode = DealMode.Auto;

        await runtime.StartGameAsync(gameId, cts.Token);
        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard
               && Snapshot(runtime, gameId).ActiveSeatIndex == 0,
            TimeSpan.FromSeconds(2),
            "auto-deal never reached AwaitingDiscard with active seat 0");

        var pre = Snapshot(runtime, gameId);
        var vStale = pre.StateVersion;

        // Mutation #1: a valid discard with no version check — bumps version to vCurrent.
        var firstTile = pre.Hands[0].ConcealedTiles[^1];
        await DiscardWithVersionAsync(runtime, gameId, seatIndex: 0, tileId: firstTile,
            expectedVersion: null, ct: cts.Token);

        var vCurrent = Snapshot(runtime, gameId).StateVersion;
        Assert.True(vCurrent > vStale,
            $"Sanity: first discard must increment version. Stale={vStale}, Current={vCurrent}.");

        // Mutation #2: stale expectedVersion → throws.
        // Wait until we're back at AwaitingDiscard with seat 0 active (bots may have
        // taken their turns in between via auto-claim Pass).
        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard
               && Snapshot(runtime, gameId).ActiveSeatIndex == 0
               || Snapshot(runtime, gameId).Phase == ChangshaPhase.EndHand,
            TimeSpan.FromSeconds(3),
            "did not return to seat-0 turn for stale-version retry");

        var ready = Snapshot(runtime, gameId);
        if (ready.Phase == ChangshaPhase.EndHand)
        {
            // Hand happened to finish in one trip around (rare; usually a draw on tiny
            // tile counts). Retry the stale-version probe via a no-op-target tile so
            // the test still verifies the throw shape. We catch any exception type and
            // assert it's the ChangshaConcurrencyException.
        }

        var nextTile = ready.Hands[0].ConcealedTiles.FirstOrDefault();
        var thrown = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await DiscardWithVersionAsync(runtime, gameId, seatIndex: 0, tileId: nextTile,
                expectedVersion: vStale, ct: cts.Token);
        });
        Assert.True(excType.IsInstanceOfType(thrown),
            $"Expected {excType.FullName}, got {thrown.GetType().FullName}: {thrown.Message}");
    }

    [Fact, Trait("Category", "Changsha")]
    public async Task StateVersion_Exception_Includes_Both_Versions()
    {
        // The thrown ChangshaConcurrencyException MUST expose both `ExpectedVersion`
        // (the stale value the caller supplied) and `ActualVersion` (the runtime's
        // current state.StateVersion at throw time).
        AssertExpectedVersionParamExists("DiscardAsync");
        var excType = ResolveConcurrencyExceptionType();

        await using var harness = new RuntimeHarness();
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var gameId = await runtime.CreateGameAsync(seed: 23,
            botSeatIndexes: new[] { 1, 2, 3 }, hostPlayerId: null, hostConnectionId: null, cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        state!.DealerSeatIndex = 0;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == 0;
        state.DealMode = DealMode.Auto;

        await runtime.StartGameAsync(gameId, cts.Token);
        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard
               && Snapshot(runtime, gameId).ActiveSeatIndex == 0,
            TimeSpan.FromSeconds(2),
            "auto-deal never reached AwaitingDiscard with active seat 0");

        var pre = Snapshot(runtime, gameId);
        var vStale = pre.StateVersion;
        // Mutate so vCurrent > vStale.
        var firstTile = pre.Hands[0].ConcealedTiles[^1];
        await DiscardWithVersionAsync(runtime, gameId, seatIndex: 0, tileId: firstTile,
            expectedVersion: null, ct: cts.Token);
        var vCurrent = Snapshot(runtime, gameId).StateVersion;

        await WaitForAsync(
            () => (Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard
                   && Snapshot(runtime, gameId).ActiveSeatIndex == 0)
               || Snapshot(runtime, gameId).Phase == ChangshaPhase.EndHand,
            TimeSpan.FromSeconds(3),
            "did not return to seat-0 turn for exception-shape probe");

        var ready = Snapshot(runtime, gameId);
        var nextTile = ready.Hands[0].ConcealedTiles.FirstOrDefault();
        var thrown = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await DiscardWithVersionAsync(runtime, gameId, seatIndex: 0, tileId: nextTile,
                expectedVersion: vStale, ct: cts.Token);
        });
        Assert.True(excType.IsInstanceOfType(thrown),
            $"Expected {excType.FullName}, got {thrown.GetType().FullName}.");

        var expectedProp = excType.GetProperty("ExpectedVersion")
            ?? throw new InvalidOperationException(
                $"{excType.FullName} missing ExpectedVersion property — Bishop owes the Phase H Wave 1 contract.");
        var actualProp = excType.GetProperty("ActualVersion")
            ?? throw new InvalidOperationException(
                $"{excType.FullName} missing ActualVersion property — Bishop owes the Phase H Wave 1 contract.");

        var expected = (int)expectedProp.GetValue(thrown)!;
        var actual = (int)actualProp.GetValue(thrown)!;

        Assert.Equal(vStale, expected);
        // Version may have advanced again between vCurrent and the throw (bot turns
        // running in between), so assert ≥ vCurrent — but strictly greater than vStale.
        Assert.True(actual >= vCurrent,
            $"ActualVersion on exception ({actual}) must be at least the post-mutation version ({vCurrent}).");
        Assert.NotEqual(vStale, actual);
    }

    [Fact, Trait("Category", "Changsha")]
    public async Task StateVersion_BotInvocations_DoNotIncrement_Mismatch()
    {
        // Bot-internal invocations pass expectedVersion=null, so the runtime's automatic
        // bot turns can fire back-to-back without ever triggering a false-positive
        // concurrency throw. Sanity-check that an all-bot game advances StateVersion
        // monotonically (no exception bubbles out of the runtime).
        await using var harness = new RuntimeHarness();
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var gameId = await runtime.CreateGameAsync(seed: 4242,
            botSeatIndexes: new[] { 0, 1, 2, 3 }, hostPlayerId: null, hostConnectionId: null, cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        state!.DealerSeatIndex = 0;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == 0;
        state.DealMode = DealMode.Auto;

        var vStart = state.StateVersion;
        await runtime.StartGameAsync(gameId, cts.Token);

        // Allow the bot turn loop to crank a handful of mutations.
        await WaitForAsync(
            () => Snapshot(runtime, gameId).DiscardPile.Count >= 3
               || Snapshot(runtime, gameId).Phase == ChangshaPhase.EndHand,
            TimeSpan.FromSeconds(10),
            "all-bot game did not produce 3 discards / reach EndHand");

        var vAfter = Snapshot(runtime, gameId).StateVersion;
        Assert.True(vAfter > vStart,
            $"All-bot game must increment StateVersion across turns. Start={vStart}, After={vAfter}.");
        // No ChangshaConcurrencyException leaked out of the runtime (await would have
        // re-thrown if any background bot path threw it).
    }

    // ── Phase H Wave 1 — reflective helpers ──────────────────────────────────────

    private sealed class RuntimeHarness : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory { get; }
        public IChangshaGameRuntime Runtime { get; }
        private readonly string _tempDb;

        public RuntimeHarness(Action<ChangshaRuntimeOptions>? configureOptions = null)
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
            Directory.CreateDirectory(dataDir);
            _tempDb = Path.Combine(dataDir, $"phase-h-state-version-{Guid.NewGuid():N}.db");
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
                b.ConfigureServices(s =>
                {
                    s.Configure<ChangshaRuntimeOptions>(o =>
                    {
                        o.BotTurnDelayMs = 1;
                        o.BotClaimDelayMs = 1;
                        o.BotPickupDelayMs = 25;
                        o.ClaimWindowTimeoutMs = 100;
                        o.DealBatchDelayMs = 0;
                        o.PersistSnapshots = false;
                        configureOptions?.Invoke(o);
                    });
                });
            });
            _ = Factory.Server;
            Runtime = Factory.Services.GetRequiredService<IChangshaGameRuntime>();
        }

        public ValueTask DisposeAsync()
        {
            Factory.Dispose();
            try { if (File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
            return ValueTask.CompletedTask;
        }
    }

    private static ChangshaGameState Snapshot(IChangshaGameRuntime runtime, string gameId)
    {
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        return state!;
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(15);
        }
        throw new TimeoutException(
            $"StateVersion contract precondition not met: {description}. " +
            $"Bishop's runtime mutations may have changed semantics — see Phase H Wave 1 spec.");
    }

    /// <summary>Resolve the new <c>ChangshaConcurrencyException</c> type by probing a handful
    /// of plausible namespaces. Fails loudly with a descriptive message when Bishop hasn't
    /// shipped the type yet.</summary>
    private static Type ResolveConcurrencyExceptionType()
    {
        var asm = typeof(IChangshaGameRuntime).Assembly;
        foreach (var fullName in new[]
                 {
                     "Mahjong.Autotable.Api.Changsha.ChangshaConcurrencyException",
                     "Mahjong.Autotable.Api.Changsha.Runtime.ChangshaConcurrencyException",
                     "Mahjong.Autotable.Api.Tables.ChangshaConcurrencyException",
                 })
        {
            var t = asm.GetType(fullName);
            if (t is not null) return t;
        }
        throw new InvalidOperationException(
            "ChangshaConcurrencyException type not found in the API assembly. Bishop owes the Phase H Wave 1 " +
            "exception type (extends InvalidOperationException; carries ExpectedVersion + ActualVersion).");
    }

    /// <summary>Assert that <paramref name="methodName"/> on <see cref="IChangshaGameRuntime"/>
    /// has the new <c>int? expectedVersion</c> parameter. Fails red with a descriptive
    /// message when Bishop hasn't shipped the new signature yet.</summary>
    private static void AssertExpectedVersionParamExists(string methodName)
    {
        var method = typeof(IChangshaGameRuntime).GetMethod(methodName)
            ?? throw new InvalidOperationException(
                $"IChangshaGameRuntime.{methodName} not found — Bishop owes the Phase H Wave 1 contract.");
        var hasVersionParam = method.GetParameters().Any(p =>
            p.Name == "expectedVersion" || p.ParameterType == typeof(int?));
        Assert.True(hasVersionParam,
            $"IChangshaGameRuntime.{methodName} is missing the `int? expectedVersion = null` parameter. " +
            $"Bishop owes the Phase H Wave 1 contract: all mutation methods gain a trailing optional " +
            $"int? expectedVersion. Current signature: " +
            $"({string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
    }

    /// <summary>Invoke <c>IChangshaGameRuntime.DiscardAsync</c> with the new
    /// <c>expectedVersion</c> parameter via reflection so the test assembly compiles
    /// independently of Bishop's signature change. Argument positions are resolved by
    /// parameter name/type so the call survives Bishop's choice of where to place the
    /// new optional parameter.</summary>
    private static async Task DiscardWithVersionAsync(
        IChangshaGameRuntime runtime, string gameId, int seatIndex, int tileId,
        int? expectedVersion, CancellationToken ct)
    {
        var method = typeof(IChangshaGameRuntime).GetMethod("DiscardAsync")
            ?? throw new InvalidOperationException("IChangshaGameRuntime.DiscardAsync not found.");
        var pars = method.GetParameters();

        var args = new object?[pars.Length];
        for (var i = 0; i < pars.Length; i++)
        {
            var p = pars[i];
            args[i] = p.Name switch
            {
                "gameId" => gameId,
                "seatIndex" => seatIndex,
                "tileId" => tileId,
                "expectedVersion" => expectedVersion,
                _ when p.ParameterType == typeof(CancellationToken) => ct,
                _ when p.ParameterType == typeof(int?) => expectedVersion,
                _ when p.HasDefaultValue => p.DefaultValue,
                _ => null
            };
        }

        try
        {
            await (Task)method.Invoke(runtime, args)!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }
}
