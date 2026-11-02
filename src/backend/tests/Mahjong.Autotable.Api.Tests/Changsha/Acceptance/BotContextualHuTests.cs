using System.Diagnostics;
using System.Reflection;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Bot;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Mahjong.Autotable.Api.Tests.Changsha._TestHarness.ChangshaTestHelpers;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Phase I Wave 2 — bot contextual Hu acceptance suite.
///
/// Closes the loop on Phase I Wave 1 (SpecialContextWinsTests): those verified
/// the state machine derives <see cref="WinContext"/> correctly when called
/// directly; these verify that the **bot decision pipeline** actually reaches
/// the state machine on each of the 5 contextual Big Win triggers (天和 /
/// 地和 / 海底捞月 / 河底捞鱼 / 杠上开花).
///
/// <list type="bullet">
///   <item>Uses <see cref="IChangshaGameRuntime"/> end-to-end via a per-test
///         <see cref="RuntimeHarness"/> (mirrors <c>BotBehaviorTests</c>).</item>
///   <item>Default strategy is <see cref="MediumStrategy"/>; tests that need a
///         specific dealer discard inject a <see cref="SeatRouterStrategy"/>
///         via reflection on the runtime's <c>_strategy</c> field (same
///         injection seam Bishop landed in Phase H Wave 1 for the timeout
///         fallback tests).</item>
///   <item>State is surgically mutated AFTER <c>StartGameAsync</c> returns and
///         BEFORE the bot's <see cref="ChangshaRuntimeOptions.BotTurnDelayMs"/>-
///         delayed turn fires — same window-of-opportunity pattern as
///         <c>Bot_Decision_Within_Timeout_ProceedsNormally</c>.</item>
///   <item>Reflection-defensive: resolves each new <see cref="WinPattern"/>
///         member via <see cref="Enum.GetNames(Type)"/> + Skip with a contract
///         message if Bishop's Phase I Wave 1 enum drift removes a value.</item>
/// </list>
///
/// IMPORTANT: per Vasquez's directive these tests do NOT modify production
/// code. If a contextual flag fails to surface in <see cref="WinResult.AllPatterns"/>
/// the test fails RED with a descriptive message — production-side fixes are
/// Bishop's lane.
/// </summary>
public class BotContextualHuTests
{
    // ────────────────────────────────────────────────────────────────────
    //  1. HeavenlyHand (天和) — dealer bot wins on initial 14-tile deal
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-2")]
    public async Task Bot_DeclaresHeavenlyHand_OnDealerInitialDeal()
    {
        var heavenly = ResolveContextualPattern("HeavenlyHand");

        await using var harness = new RuntimeHarness();
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var gameId = await CreateAllBotGameAsync(runtime, seed: 0xBEEF, dealerSeat: 0, cts.Token);
        using var observer = new WinObserver(runtime, gameId);

        var sw = Stopwatch.StartNew();
        await runtime.StartGameAsync(gameId, cts.Token);

        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard
               && Snapshot(runtime, gameId).ActiveSeatIndex == 0,
            TimeSpan.FromSeconds(2),
            "auto-deal never reached AwaitingDiscard with dealer (bot seat 0) active");

        // Override dealer's hand to a 14-tile winning Standard structure (3 chows +
        // 1 chow + pair Tong-5). All other seats: clear (no claim interference).
        var state = Snapshot(runtime, gameId);
        OverrideHandWith14Tiles(state, seatIndex: 0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 5), (Suit.Tong, 5));
        ClearOtherSeats(state, keepSeat: 0);

        // Capture via StateChanged: runtime fires Win → Score → PersistSnapshot (which
        // triggers StateChanged with CurrentWin set) BEFORE StartNextHandOrEndAsync
        // re-deals and clears CurrentWin. Polling state.CurrentWin would miss this window.
        await WaitForAsync(
            () => observer.CapturedWin is not null,
            TimeSpan.FromMilliseconds(_options.BotDecisionTimeoutMs + 3_000),
            "dealer bot never declared Hu on the overridden winning hand",
            diag: () => DescribeState(Snapshot(runtime, gameId)));
        sw.Stop();

        var win = observer.CapturedWin!;
        Assert.Equal(WinMethod.SelfDraw, win.Method);
        Assert.Equal(0, win.WinningSeatIndex);
        Assert.Contains(heavenly, win.AllPatterns);
        Assert.True(sw.ElapsedMilliseconds < _options.BotDecisionTimeoutMs + 4_000,
            $"Bot HeavenlyHand decision exceeded BotDecisionTimeoutMs+buffer. Took {sw.ElapsedMilliseconds}ms.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. EarthlyHand (地和) — non-dealer bot Hus on dealer's first discard
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-2")]
    public async Task Bot_DeclaresEarthlyHand_OnDealerFirstDiscard()
    {
        var earthly = ResolveContextualPattern("EarthlyHand");
        var wan1 = Tid(Suit.Wan, 1, 0);

        await using var harness = new RuntimeHarness();
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var gameId = await CreateAllBotGameAsync(runtime, seed: 0xCAFE, dealerSeat: 0, cts.Token);
        using var observer = new WinObserver(runtime, gameId);

        // Inject a router: dealer (seat 0) discards Wan-1 on its first turn;
        // claimants (seats 1..3) delegate to MediumStrategy which will detect
        // and claim Hu when Wan-1 lands in the river.
        var medium = ChangshaBotEngine.Default;
        var router = new SeatRouterStrategy(medium,
            (0, (_, _) => BotAction.Discard(wan1)));
        InjectStrategy(runtime, router);

        await runtime.StartGameAsync(gameId, cts.Token);

        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard
               && Snapshot(runtime, gameId).ActiveSeatIndex == 0,
            TimeSpan.FromSeconds(2),
            "auto-deal never reached AwaitingDiscard with dealer active");

        var state = Snapshot(runtime, gameId);

        // Strip Wan-1 from all hands + wall so seat 1's wait is the unique source.
        StripLogicalFromAllSeatsAndWall(state, Logical(Suit.Wan, 1));

        // Dealer (seat 0): 14 tiles — 13 fillers + Wan-1 to discard. Fillers are
        // chosen so the rest is NOT a winning hand (the dealer must NOT pre-empt
        // the Earthly scenario by self-drawing Hu).
        OverrideHandWith14Tiles(state, seatIndex: 0,
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 6), (Suit.Wan, 7),
            (Suit.Tong, 6), (Suit.Tong, 7), (Suit.Tong, 8),
            (Suit.Tiao, 2), (Suit.Tiao, 3), (Suit.Tiao, 4),
            (Suit.Tiao, 8), (Suit.Wan, 1));

        // Seat 1: 13-tile waiting hand for Wan-1.
        OverrideHandWith13Tiles(state, seatIndex: 1,
            AcceptanceFixture.ThirteenTileWaitingForWan1());

        // Seats 2/3 cleared so they can't compete for any claim.
        state.Hands[2].ConcealedTiles.Clear();
        state.Hands[2].Melds.Clear();
        state.Hands[3].ConcealedTiles.Clear();
        state.Hands[3].Melds.Clear();

        await WaitForAsync(
            () => observer.CapturedWin is not null,
            TimeSpan.FromMilliseconds(_options.BotDecisionTimeoutMs + 4_000),
            "no bot declared Hu on dealer's first discard (Earthly scenario)",
            diag: () => DescribeState(Snapshot(runtime, gameId)));

        var win = observer.CapturedWin!;
        Assert.Equal(WinMethod.Discard, win.Method);
        Assert.Equal(1, win.WinningSeatIndex);
        Assert.Equal(0, win.SourceSeatIndex);
        Assert.Contains(earthly, win.AllPatterns);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. LastTileFromWall (海底捞月) — bot self-draws the final wall tile
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-2")]
    public async Task Bot_DeclaresLastTileFromWall_OnExhaustionSelfDraw()
    {
        var lastTile = ResolveContextualPattern("LastTileFromWall");
        var benignDiscard = Tid(Suit.Tiao, 9, 0); // dealer discards this; nobody wants it
        var winningTile = Tid(Suit.Wan, 1, 0);

        await using var harness = new RuntimeHarness();
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var gameId = await CreateAllBotGameAsync(runtime, seed: 0xF00D, dealerSeat: 0, cts.Token);
        using var observer = new WinObserver(runtime, gameId);

        // Router: dealer discards a benign Tiao-9 on its first turn; everyone else
        // is normal MediumStrategy (seat 1 will detect Hu on its self-drawn Wan-1).
        var medium = ChangshaBotEngine.Default;
        var router = new SeatRouterStrategy(medium,
            (0, (_, _) => BotAction.Discard(benignDiscard)));
        InjectStrategy(runtime, router);

        await runtime.StartGameAsync(gameId, cts.Token);

        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard
               && Snapshot(runtime, gameId).ActiveSeatIndex == 0,
            TimeSpan.FromSeconds(2),
            "auto-deal never reached AwaitingDiscard with dealer active");

        var state = Snapshot(runtime, gameId);

        // Strip Wan-1 from all hands; we'll add it back only as the single wall tile.
        StripLogicalFromAllSeatsAndWall(state, Logical(Suit.Wan, 1));
        // Strip Tiao-9 from non-dealer seats so the dealer's Tiao-9 discard opens
        // no claim window (no Pung/Kong/Chow possible).
        StripLogicalFromAllSeatsAndWall(state, Logical(Suit.Tiao, 9));

        // Dealer (seat 0): 14 tiles, including Tiao-9 to discard. Other 13 are
        // benign (NOT a winning hand and NOT including Wan-1).
        OverrideHandWith14Tiles(state, seatIndex: 0,
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 6), (Suit.Wan, 7),
            (Suit.Tong, 6), (Suit.Tong, 7),
            (Suit.Tiao, 2), (Suit.Tiao, 3), (Suit.Tiao, 4),
            (Suit.Tiao, 5), (Suit.Tiao, 8),
            (Suit.Tiao, 9));

        // Seat 1: 13-tile waiting hand on Wan-1.
        OverrideHandWith13Tiles(state, seatIndex: 1,
            AcceptanceFixture.ThirteenTileWaitingForWan1());

        // Seats 2/3 empty.
        state.Hands[2].ConcealedTiles.Clear();
        state.Hands[2].Melds.Clear();
        state.Hands[3].ConcealedTiles.Clear();
        state.Hands[3].Melds.Clear();

        // Wall = single tile = Wan-1 (DrawFromFront returns Wall[0]).
        state.Wall.Clear();
        state.Wall.Add(winningTile);
        state.WallDrawIndex = 0;
        state.WallBackIndex = 0;

        await WaitForAsync(
            () => observer.CapturedWin is not null,
            TimeSpan.FromMilliseconds(_options.BotDecisionTimeoutMs + 5_000),
            "no bot self-drew the last wall tile + declared Hu (LastTileFromWall scenario)",
            diag: () => DescribeState(Snapshot(runtime, gameId)));

        var win = observer.CapturedWin!;
        Assert.Equal(WinMethod.SelfDraw, win.Method);
        Assert.Equal(1, win.WinningSeatIndex);
        Assert.Contains(lastTile, win.AllPatterns);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. LastDiscardCatch (河底捞鱼) — bot Hus on discard after wall exhausted
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-2")]
    public async Task Bot_DeclaresLastDiscardCatch_OnExhaustionDiscardWin()
    {
        var lastDiscard = ResolveContextualPattern("LastDiscardCatch");
        var wan1 = Tid(Suit.Wan, 1, 0);

        await using var harness = new RuntimeHarness();
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var gameId = await CreateAllBotGameAsync(runtime, seed: 0xACED, dealerSeat: 0, cts.Token);
        using var observer = new WinObserver(runtime, gameId);

        // Router: dealer discards Wan-1 on its first turn; seat 1 (MediumStrategy)
        // will detect the Hu opportunity in the claim window and claim Hu.
        var medium = ChangshaBotEngine.Default;
        var router = new SeatRouterStrategy(medium,
            (0, (_, _) => BotAction.Discard(wan1)));
        InjectStrategy(runtime, router);

        await runtime.StartGameAsync(gameId, cts.Token);

        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard
               && Snapshot(runtime, gameId).ActiveSeatIndex == 0,
            TimeSpan.FromSeconds(2),
            "auto-deal never reached AwaitingDiscard with dealer active");

        var state = Snapshot(runtime, gameId);

        // Strip Wan-1 from all hands + wall; reintroduce only via the dealer's hand.
        StripLogicalFromAllSeatsAndWall(state, Logical(Suit.Wan, 1));

        // Dealer (seat 0): 14 tiles, last is Wan-1 (the soon-to-be discard). Other
        // 13 are intentionally NOT a winning shape (avoid dealer self-Hu pre-empting
        // the discard).
        OverrideHandWith14Tiles(state, seatIndex: 0,
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 6), (Suit.Wan, 7),
            (Suit.Tong, 6), (Suit.Tong, 7), (Suit.Tong, 8),
            (Suit.Tiao, 2), (Suit.Tiao, 3), (Suit.Tiao, 4),
            (Suit.Tiao, 8), (Suit.Wan, 1));

        // Seat 1: 13-tile waiting hand on Wan-1.
        OverrideHandWith13Tiles(state, seatIndex: 1,
            AcceptanceFixture.ThirteenTileWaitingForWan1());

        // Seats 2/3 cleared.
        state.Hands[2].ConcealedTiles.Clear();
        state.Hands[2].Melds.Clear();
        state.Hands[3].ConcealedTiles.Clear();
        state.Hands[3].Melds.Clear();

        // Wall exhausted — Wan-1 must come from the discard pile (post-state-machine
        // ResolveHuClaim reads Wall.Count == 0 → IsLastDiscardCatch).
        state.Wall.Clear();
        state.WallDrawIndex = 0;
        state.WallBackIndex = -1;

        await WaitForAsync(
            () => observer.CapturedWin is not null,
            TimeSpan.FromMilliseconds(_options.BotDecisionTimeoutMs + 4_000),
            "no bot claimed Hu on dealer's wall-exhausted discard (LastDiscardCatch scenario)",
            diag: () => DescribeState(Snapshot(runtime, gameId)));

        var win = observer.CapturedWin!;
        Assert.Equal(WinMethod.Discard, win.Method);
        Assert.Equal(1, win.WinningSeatIndex);
        Assert.Equal(0, win.SourceSeatIndex);
        Assert.Contains(lastDiscard, win.AllPatterns);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. KongReplacementWin (杠上开花) — bot wins on kong-replacement draw
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-2")]
    public async Task Bot_DeclaresKongReplacementWin_OnReplacementDraw()
    {
        var kongRepl = ResolveContextualPattern("KongReplacementWin");

        await using var harness = new RuntimeHarness();
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var gameId = await CreateAllBotGameAsync(runtime, seed: 0xB055, dealerSeat: 0, cts.Token);
        using var observer = new WinObserver(runtime, gameId);

        // Default MediumStrategy — it detects 4-of-a-kind on Tiao-9 and returns
        // DeclareConcealedKong; after the replacement draw the hand is winning
        // and the next bot turn returns DeclareWin.

        await runtime.StartGameAsync(gameId, cts.Token);

        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard
               && Snapshot(runtime, gameId).ActiveSeatIndex == 0,
            TimeSpan.FromSeconds(2),
            "auto-deal never reached AwaitingDiscard with dealer active");

        var state = Snapshot(runtime, gameId);

        // Strip Tiao-9 / Tong-5 from everywhere so the dealer's kong + replacement
        // are deterministic.
        StripLogicalFromAllSeatsAndWall(state, Logical(Suit.Tiao, 9));
        StripLogicalFromAllSeatsAndWall(state, Logical(Suit.Tong, 5));

        // Dealer (seat 0): 14 concealed tiles structured so a Tiao-9 ConcealedKong +
        // Tong-5 replacement → 3 chows (Wan-123, Wan-456, Tong-123) + kong + pair
        // on Tong-5. Pre-kong the hand is NOT winning (only 1 Tong-5; no pair on
        // a 2/5/8 rank).
        state.Hands[0].ConcealedTiles.Clear();
        state.Hands[0].Melds.Clear();
        state.Hands[0].ConcealedTiles.AddRange(new[]
        {
            Tid(Suit.Tiao, 9, 0), Tid(Suit.Tiao, 9, 1),
            Tid(Suit.Tiao, 9, 2), Tid(Suit.Tiao, 9, 3),
            Tid(Suit.Wan, 1, 0), Tid(Suit.Wan, 2, 0), Tid(Suit.Wan, 3, 0),
            Tid(Suit.Wan, 4, 0), Tid(Suit.Wan, 5, 0), Tid(Suit.Wan, 6, 0),
            Tid(Suit.Tong, 1, 0), Tid(Suit.Tong, 2, 0), Tid(Suit.Tong, 3, 0),
            Tid(Suit.Tong, 5, 0),
        });

        // Other seats cleared so they don't compete for any claim.
        ClearOtherSeats(state, keepSeat: 0);

        // Replacement = Tong-5 copy 1, placed at Wall[^1] so DrawFromBack returns it.
        var replacementTile = Tid(Suit.Tong, 5, 1);
        state.Wall.RemoveAll(t => t == replacementTile);
        state.Wall.Add(replacementTile);
        state.WallBackIndex = state.Wall.Count - 1;

        await WaitForAsync(
            () => observer.CapturedWin is not null,
            TimeSpan.FromMilliseconds(_options.BotDecisionTimeoutMs * 2 + 5_000),
            "dealer bot never completed the kong + Hu chain (KongReplacementWin scenario)",
            diag: () => DescribeState(Snapshot(runtime, gameId)));

        var win = observer.CapturedWin!;
        Assert.Equal(WinMethod.SelfDraw, win.Method);
        Assert.Equal(0, win.WinningSeatIndex);
        Assert.Contains(kongRepl, win.AllPatterns);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. Stacked contextual × structural — HeavenlyHand + FullFlush ⇒ ×2
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-I-2")]
    public async Task Bot_AllPatterns_StacksContextual()
    {
        var heavenly = ResolveContextualPattern("HeavenlyHand");

        await using var harness = new RuntimeHarness();
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var gameId = await CreateAllBotGameAsync(runtime, seed: 0xD00D, dealerSeat: 0, cts.Token);
        using var observer = new WinObserver(runtime, gameId);

        await runtime.StartGameAsync(gameId, cts.Token);

        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard
               && Snapshot(runtime, gameId).ActiveSeatIndex == 0,
            TimeSpan.FromSeconds(2),
            "auto-deal never reached AwaitingDiscard with dealer active");

        // Dealer hand: 14-tile all-Wan winning structure (4 chows + pair on Wan-5
        // for 258 ✓, FullFlush ✓). HeavenlyHand context: dealer + DiscardPile empty
        // + no melds + LastDrawWasKongReplacement=false — all true after fresh deal.
        var state = Snapshot(runtime, gameId);
        OverrideHandWith14Tiles(state, seatIndex: 0,
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 2), (Suit.Wan, 3), (Suit.Wan, 4),
            (Suit.Wan, 5), (Suit.Wan, 5),
            (Suit.Wan, 6), (Suit.Wan, 7), (Suit.Wan, 8),
            (Suit.Wan, 7), (Suit.Wan, 8), (Suit.Wan, 9));
        ClearOtherSeats(state, keepSeat: 0);

        await WaitForAsync(
            () => observer.CapturedWin is not null && observer.CapturedScore is not null,
            TimeSpan.FromMilliseconds(_options.BotDecisionTimeoutMs + 3_000),
            "dealer bot never declared Hu / scored on stacked HeavenlyHand + FullFlush",
            diag: () => DescribeState(Snapshot(runtime, gameId)));

        var win = observer.CapturedWin!;
        var score = observer.CapturedScore!;

        // Pattern (headline) is FullFlush — it precedes HeavenlyHand in
        // WinDetector's precedence chain (structural Big Win wins headline);
        // AllPatterns must contain both.
        Assert.Equal(WinPattern.FullFlush, win.Pattern);
        Assert.Contains(WinPattern.FullFlush, win.AllPatterns);
        Assert.Contains(heavenly, win.AllPatterns);
        Assert.True(win.AllPatterns.Count >= 2,
            $"HeavenlyHand + FullFlush must yield ≥2 entries in AllPatterns " +
            $"(stacking is opt-in per #117; AllPatterns still lists both). " +
            $"Got [{string.Join(",", win.AllPatterns)}].");

        // Scoring (#117 spec-pure default): dealer self-draw BigWin base =
        // BigWinSelfDrawDealer (4) per opponent × 3 opponents × ×1 (no stacking on the
        // live default path) = 12. The fan catalog is still evaluated and surfaced for
        // display/audit but is NOT folded into payments in the default SpecPure mode:
        //   SelfDraw (1) + FullFlush (6) + HeavenlyHand (8) + ConcealedHand (1) = 16
        //   FanPoints (query-only). 16 × 3 = 48 mirrors the per-payment breakdown.
        Assert.Equal(ScoreCategory.BigWin, score.Category);
        Assert.Equal(12, score.BasePoints);
        Assert.Equal(48, score.FanPoints * 3);
        Assert.Contains(score.Fans, f => f.Fan == Mahjong.Autotable.Api.Changsha.Scoring.Fan.SelfDraw);
        Assert.Contains(score.Fans, f => f.Fan == Mahjong.Autotable.Api.Changsha.Scoring.Fan.FullFlush);
        Assert.Contains(score.Fans, f => f.Fan == Mahjong.Autotable.Api.Changsha.Scoring.Fan.HeavenlyHand);
        Assert.Contains(score.Fans, f => f.Fan == Mahjong.Autotable.Api.Changsha.Scoring.Fan.ConcealedHand);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Per-test ChangshaRuntimeOptions snapshot — used to compute timeout
    /// budgets in the wait predicates without hard-coding the constant.</summary>
    private ChangshaRuntimeOptions _options = new();

    private async Task<string> CreateAllBotGameAsync(IChangshaGameRuntime runtime, int seed, int dealerSeat, CancellationToken ct)
    {
        var gameId = await runtime.CreateGameAsync(
            seed: seed,
            botSeatIndexes: new[] { 0, 1, 2, 3 },
            hostPlayerId: null, hostConnectionId: null,
            ct);
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        state!.DealerSeatIndex = dealerSeat;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == dealerSeat;
        state.DealMode = DealMode.Auto;
        return gameId;
    }

    private static ChangshaGameState Snapshot(IChangshaGameRuntime runtime, string gameId)
    {
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        return state!;
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout, string description,
        Func<string>? diag = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(15);
        }
        var diagSuffix = diag is null ? string.Empty : $"\n  Diagnostic: {diag()}";
        throw new TimeoutException(
            $"Bot contextual-Hu pipeline contract violated: {description}.{diagSuffix}");
    }

    private static string DescribeState(ChangshaGameState s)
    {
        string Hand(int seat) =>
            $"seat{seat}[{s.Hands[seat].ConcealedTiles.Count}t,{s.Hands[seat].Melds.Count}m]";
        return $"Phase={s.Phase} Active={s.ActiveSeatIndex} Discards={s.DiscardPile.Count} " +
               $"Wall={s.Wall.Count} ClaimWindow={(s.ClaimWindow is null ? "null" : "open")} " +
               $"CurrentWin={(s.CurrentWin is null ? "null" : "set")} " +
               $"{Hand(0)} {Hand(1)} {Hand(2)} {Hand(3)} " +
               $"LastDrawKong={s.LastDrawWasKongReplacement}";
    }

    private static void OverrideHandWith14Tiles(ChangshaGameState state, int seatIndex,
        params (Suit suit, int rank)[] tiles)
    {
        var copies = new Dictionary<int, int>();
        var tileIds = new List<int>(tiles.Length);
        foreach (var (s, r) in tiles)
        {
            var logical = Logical(s, r);
            copies.TryGetValue(logical, out var copy);
            tileIds.Add(Tid(s, r, copy));
            copies[logical] = copy + 1;
        }
        state.Hands[seatIndex].ConcealedTiles.Clear();
        state.Hands[seatIndex].ConcealedTiles.AddRange(tileIds);
        state.Hands[seatIndex].Melds.Clear();
    }

    private static void OverrideHandWith13Tiles(ChangshaGameState state, int seatIndex,
        IEnumerable<int> tileIds)
    {
        state.Hands[seatIndex].ConcealedTiles.Clear();
        state.Hands[seatIndex].ConcealedTiles.AddRange(tileIds);
        state.Hands[seatIndex].Melds.Clear();
    }

    private static void StripLogicalFromAllSeatsAndWall(ChangshaGameState state, int logicalTile)
    {
        foreach (var h in state.Hands)
            h.ConcealedTiles.RemoveAll(t => ChangshaDeckBuilder.GetLogicalTile(t) == logicalTile);
        state.Wall.RemoveAll(t => ChangshaDeckBuilder.GetLogicalTile(t) == logicalTile);
    }

    private static void ClearOtherSeats(ChangshaGameState state, int keepSeat)
    {
        for (var i = 0; i < state.Hands.Count; i++)
        {
            if (i == keepSeat) continue;
            state.Hands[i].ConcealedTiles.Clear();
            state.Hands[i].Melds.Clear();
        }
    }

    /// <summary>Resolve a Phase I Wave 1 <see cref="WinPattern"/> value via reflection
    /// so the test assembly compiles even if Bishop's enum drifts. RED-fails with a
    /// contract message naming the missing member.</summary>
    private static WinPattern ResolveContextualPattern(string name)
    {
        var names = Enum.GetNames(typeof(WinPattern));
        if (!names.Contains(name))
        {
            throw new InvalidOperationException(
                $"WinPattern.{name} missing — Phase I Wave 1 enum contract broken. " +
                $"Current values: [{string.Join(",", names)}].");
        }
        return (WinPattern)Enum.Parse(typeof(WinPattern), name);
    }

    /// <summary>Inject a custom <see cref="IChangshaBotStrategy"/> into the runtime via
    /// the same reflection seam used by <c>BotBehaviorTests.InjectStrategyOrFail</c>.
    /// Fails with a descriptive message if the seam is missing (would mean Bishop
    /// regressed the Phase H Wave 1 contract).</summary>
    private static void InjectStrategy(IChangshaGameRuntime runtime, IChangshaBotStrategy strategy)
    {
        var fields = runtime.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var field = fields.FirstOrDefault(f => typeof(IChangshaBotStrategy).IsAssignableFrom(f.FieldType));
        Assert.True(field is not null,
            "ChangshaGameRuntime has no IChangshaBotStrategy injection field. " +
            "Phase H Wave 1 contract regression — see Bishop's memo.");
        field!.SetValue(runtime, strategy);
    }

    /// <summary>
    /// Subscribes to <see cref="IChangshaGameRuntime.StateChanged"/> and captures the
    /// first state where <see cref="ChangshaGameState.CurrentWin"/> is populated.
    /// Required because <c>DeclareWinAsync</c> fires StateChanged (with the win set)
    /// and then immediately re-deals the next hand (which clears <c>CurrentWin</c>
    /// back to null). A poll on <c>state.CurrentWin</c> reliably misses this window.
    /// </summary>
    private sealed class WinObserver : IDisposable
    {
        private readonly IChangshaGameRuntime _runtime;
        private readonly Action<string, ChangshaGameState> _handler;

        public WinResult? CapturedWin { get; private set; }
        public ScoreResult? CapturedScore { get; private set; }
        public ChangshaPhase? CapturedPhase { get; private set; }

        public WinObserver(IChangshaGameRuntime runtime, string gameId)
        {
            _runtime = runtime;
            _handler = (id, s) =>
            {
                if (id != gameId) return;
                // #137 — use the snapshot StateChanged captured at the mutation instant.
                // Capture only the FIRST observed win. Subsequent StateChanged events
                // from the re-dealt next hand will have CurrentWin=null and be ignored.
                if (CapturedWin is null && s.CurrentWin is not null)
                {
                    CapturedWin = s.CurrentWin;
                    CapturedScore = s.CurrentScore;
                    CapturedPhase = s.Phase;
                }
            };
            _runtime.StateChanged += _handler;
        }

        public void Dispose() => _runtime.StateChanged -= _handler;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Test infrastructure
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Per-test runtime harness — collapses delays to a small but non-zero window
    /// so the test code can mutate state between StartGameAsync's return and the
    /// bot's first turn. Mirrors <c>BotBehaviorTests.RuntimeHarness</c>.
    /// </summary>
    private sealed class RuntimeHarness : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory { get; }
        public IChangshaGameRuntime Runtime { get; }
        private readonly string _tempDb;

        public RuntimeHarness(Action<ChangshaRuntimeOptions>? configure = null)
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
            Directory.CreateDirectory(dataDir);
            _tempDb = Path.Combine(dataDir, $"phase-i-w2-bot-ctx-{Guid.NewGuid():N}.db");
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
                b.ConfigureServices(s =>
                {
                    s.Configure<ChangshaRuntimeOptions>(o =>
                    {
                        // Long enough that the test code reliably wins the race to
                        // mutate state before the bot's first DecideAction fires.
                        // (StartGameAsync involves Deal + hub broadcasts + bot scheduling;
                        // we need this >> StartGameAsync's wall-clock cost.)
                        o.BotTurnDelayMs = 1_500;
                        o.BotClaimDelayMs = 50;
                        o.BotPickupDelayMs = 50;
                        o.ClaimWindowTimeoutMs = 2_000;
                        o.DealBatchDelayMs = 0;
                        o.PersistSnapshots = false;
                        // Keep production default — these tests aren't exercising
                        // the timeout path, just verifying the bot decision lands
                        // well within budget.
                        o.BotDecisionTimeoutMs = 2_000;
                        configure?.Invoke(o);
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

    /// <summary>
    /// <see cref="IChangshaBotStrategy"/> that routes per-seat: each entry in
    /// <paramref name="overrides"/> takes precedence for its seat; all other seats
    /// delegate to <paramref name="fallback"/>. Used in Earthly / LastTile /
    /// LastDiscard scenarios where the dealer must discard a specific tile while
    /// the claimant bots keep the production MediumStrategy logic for Hu / Pass
    /// decisions in the claim window.
    /// </summary>
    private sealed class SeatRouterStrategy : IChangshaBotStrategy
    {
        private readonly IChangshaBotStrategy _fallback;
        private readonly Dictionary<int, Func<ChangshaGameState, int, BotAction>> _overrides;

        public string Difficulty => "test-router";

        public SeatRouterStrategy(IChangshaBotStrategy fallback,
            params (int seat, Func<ChangshaGameState, int, BotAction> action)[] overrides)
        {
            _fallback = fallback;
            _overrides = overrides.ToDictionary(o => o.seat, o => o.action);
        }

        public BotAction OnTurnStart(ChangshaGameState state, int seat) => DecideAction(state, seat);
        public BotAction OnOtherDiscard(ChangshaGameState state, int seat, int discarder, int tile) => DecideAction(state, seat);
        public BotAction OnSelfDraw(ChangshaGameState state, int seat) => DecideAction(state, seat);
        public BotAction OnPickupCue(ChangshaGameState state, int seat) => BotAction.Wait();

        public BotAction DecideAction(ChangshaGameState state, int seat)
        {
            if (_overrides.TryGetValue(seat, out var fn))
            {
                // Only honour the seat-specific override when this seat is in fact the
                // actor for the current phase. Otherwise (e.g., dealer seat 0 during a
                // claim window opened by seat 1's discard) fall through to the fallback
                // so Pass / Hu decisions still come from MediumStrategy.
                var isOurTurn = state.Phase == ChangshaPhase.AwaitingDiscard
                                && state.ActiveSeatIndex == seat;
                if (isOurTurn) return fn(state, seat);
            }
            return _fallback.DecideAction(state, seat);
        }
    }
}
