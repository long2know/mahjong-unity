using System.Reflection;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Bot;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Tables;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// CAT-J: Bot Behavior — drives ChangshaBotPolicy through ChangshaGameStateMachine.
/// </summary>
public class BotBehaviorTests
{
    [Fact, Trait("Category", "Changsha")]
    public void Bot_CompletesFullHand_WithoutIllegalMoves()
    {
        var outcome = BotMatchHarness.RunUntilHandFinished(seed: 42);
        Assert.True(outcome.WinnerDeclared || outcome.WallExhausted);
        Assert.Equal(ChangshaPhase.EndHand, outcome.FinalState.Phase);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Bot_DiscardsLegalTile_FromOwnHand()
    {
        var state = ChangshaTestHelpers.NewGameDealtTo(seed: 21, botSeats: new[] { 0, 1, 2, 3 });
        var dealer = state.DealerSeatIndex;

        var action = new ChangshaBotPolicy().DecideAction(state, dealer);

        Assert.Equal(BotActionType.Discard, action.Type);
        Assert.NotNull(action.TileId);
        Assert.Contains(action.TileId!.Value, state.Hands[dealer].ConcealedTiles);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Bot_RecognizesWinningHand_DeclaresWin()
    {
        // Construct a state where the active seat already holds a 14-tile winning hand.
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 99, botSeatIndexes: new[] { 0, 1, 2, 3 });
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(99));
        ChangshaGameStateMachine.Deal(state);

        var dealer = state.DealerSeatIndex;
        // Replace dealer's hand with a known Standard winner.
        state.Hands[dealer].ConcealedTiles = ChangshaTestHelpers.Tiles(
            (Suit.Wan, 1), (Suit.Wan, 2), (Suit.Wan, 3),
            (Suit.Wan, 4), (Suit.Wan, 5), (Suit.Wan, 6),
            (Suit.Tong, 1), (Suit.Tong, 2), (Suit.Tong, 3),
            (Suit.Tiao, 4), (Suit.Tiao, 5), (Suit.Tiao, 6),
            (Suit.Tong, 5), (Suit.Tong, 5));
        state.Hands[dealer].Melds.Clear();

        var action = new ChangshaBotPolicy().DecideAction(state, dealer);

        Assert.Equal(BotActionType.DeclareWin, action.Type);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Bot_ClaimWindow_PrefersHuOverKongOverPung()
    {
        // Make claim window with a winning opportunity for one bot and a kong for another.
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 17, botSeatIndexes: new[] { 0, 1, 2, 3 });
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(17));
        ChangshaGameStateMachine.Deal(state);

        // Manually open a claim window with a Hu opportunity for seat 1.
        state.ClaimWindow = new ChangshaClaimWindow
        {
            DiscardSeatIndex = 0,
            DiscardTileId = ChangshaTestHelpers.Tid(Suit.Tong, 5, 0),
            Opportunities = new()
            {
                new ChangshaClaimOpportunity { SeatIndex = 1, ClaimType = Tables.TableClaimType.Hu, Priority = 4 },
                new ChangshaClaimOpportunity { SeatIndex = 1, ClaimType = Tables.TableClaimType.Pung, Priority = 2 }
            }
        };
        state.Phase = ChangshaPhase.AwaitingClaim;

        var action = new ChangshaBotPolicy().DecideAction(state, 1);

        Assert.Equal(BotActionType.Claim, action.Type);
        Assert.Equal(Tables.TableClaimType.Hu, action.ClaimType);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Bot_ClaimWindow_PassesWhenNoEligibleOpportunity()
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 17, botSeatIndexes: new[] { 0, 1, 2, 3 });
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(17));
        ChangshaGameStateMachine.Deal(state);

        state.ClaimWindow = new ChangshaClaimWindow
        {
            DiscardSeatIndex = 0,
            DiscardTileId = 0,
            Opportunities = new()
            {
                new ChangshaClaimOpportunity { SeatIndex = 2, ClaimType = Tables.TableClaimType.Pung, Priority = 2 }
            }
        };
        state.Phase = ChangshaPhase.AwaitingClaim;

        // Seat 1 has no opportunity for itself.
        var action = new ChangshaBotPolicy().DecideAction(state, 1);
        Assert.Equal(BotActionType.Pass, action.Type);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Bot_DeterministicWithSeed_ProducesSameEventLog()
    {
        var o1 = BotMatchHarness.RunUntilHandFinished(seed: 12345);
        var o2 = BotMatchHarness.RunUntilHandFinished(seed: 12345);

        Assert.Equal(
            o1.FinalState.EventLog.Select(e => e.EventType).ToList(),
            o2.FinalState.EventLog.Select(e => e.EventType).ToList());
        Assert.Equal(o1.WinnerDeclared, o2.WinnerDeclared);
    }

    [Fact, Trait("Category", "Changsha")]
    public void Bot_DiscardSelection_PrefersIsolatedTilesOverPairs()
    {
        // Hand contains a clear pair plus an isolated honor-rank tile.
        // SelectDiscardTile is deterministic; it should not pick a tile that's part of a pair.
        var hand = new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = ChangshaTestHelpers.Tiles(
                (Suit.Wan, 5), (Suit.Wan, 5),  // pair — keep
                (Suit.Tong, 4), (Suit.Tong, 5), // sequence partial — keep
                (Suit.Tiao, 1), // isolated — discard
                (Suit.Wan, 9))  // isolated — discard candidate
        };

        var picked = ChangshaBotPolicy.SelectDiscardTile(hand);
        var pickedLogical = ChangshaDeckBuilder.GetLogicalTile(picked);

        Assert.NotEqual(ChangshaTestHelpers.Logical(Suit.Wan, 5), pickedLogical);
    }

    [Fact, Trait("Category", "Changsha")]
    public async Task Bot_TimeoutFallback_FallsBackToSafeAction()
    {
        // Bishop's Phase H Wave 1 contract: when a bot strategy's DecideAction exceeds
        // ChangshaRuntimeOptions.BotDecisionTimeoutMs the runtime substitutes a safe-default
        // action — Discard(HandEvaluator.SelectDiscardTile(hand)) on its own turn — and the
        // background strategy task is allowed to complete out-of-band (its result is discarded).
        AssertBotDecisionTimeoutOptionExists();

        await using var harness = new RuntimeHarness(o =>
        {
            o.BotDecisionTimeoutMs = 100;
            o.BotTurnDelayMs = 5;
            o.BotClaimDelayMs = 5;
            o.ClaimWindowTimeoutMs = 2000;
        });
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var gameId = await runtime.CreateGameAsync(seed: 13579,
            botSeatIndexes: new[] { 0, 1, 2, 3 }, hostPlayerId: null, hostConnectionId: null, cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        state!.DealerSeatIndex = 0;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == 0;
        state.DealMode = DealMode.Auto;

        // Inject a strategy that blocks for 5x the timeout. The Discard-fallback would-return
        // value is irrelevant — Bishop's wrapper must discard whatever the slow task eventually
        // produces and substitute the safe default.
        var slow = new SlowBotStrategy(delayMs: 600);
        InjectStrategyOrFail(runtime, slow);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await runtime.StartGameAsync(gameId, cts.Token);

        // Auto-deal lands us in AwaitingDiscard with dealer (seat 0, a bot) active. The bot
        // turn loop will fire RunBotTurnAsync(seat 0) → strategy times out → safe-default
        // discard fires. Discard advances the phase (or opens a claim window).
        await WaitForAsync(
            () => Snapshot(runtime, gameId).DiscardPile.Count >= 1
               || Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingClaim,
            TimeSpan.FromMilliseconds(2_000),
            "bot dealer never discarded — timeout fallback never fired");
        sw.Stop();

        // The runtime call should return within ~timeout + generous buffer; the background
        // strategy's 600ms sleep must NOT block the runtime past timeout+200ms (the spec).
        Assert.True(sw.ElapsedMilliseconds < 1_500,
            $"Runtime did not return within BotDecisionTimeoutMs + buffer. " +
            $"Took {sw.ElapsedMilliseconds}ms; spec is timeout(100) + ~200ms buffer. " +
            $"Bishop's timeout wrapper must not await the slow strategy task.");

        Assert.True(slow.InvocationCount >= 1,
            "Slow strategy was never invoked — Bishop's runtime is not consulting the injected " +
            "IChangshaBotStrategy. Verify the injection seam is wired to the decision path.");
    }

    [Fact, Trait("Category", "Changsha")]
    public async Task Bot_Timeout_Discard_PicksLowestRankSafe()
    {
        // The safe-default discard is deterministic. Bishop's spec names it
        // HandEvaluator.SelectDiscardTile(hand); for parity with the existing Medium
        // implementation that ChangshaBotPolicy already exposes, the result must equal
        // MediumStrategy.SelectDiscardTile(hand) computed on the pre-timeout hand snapshot.
        AssertBotDecisionTimeoutOptionExists();

        await using var harness = new RuntimeHarness(o =>
        {
            o.BotDecisionTimeoutMs = 100;
            o.BotTurnDelayMs = 5;
            o.BotClaimDelayMs = 5;
            o.ClaimWindowTimeoutMs = 2000;
        });
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var gameId = await runtime.CreateGameAsync(seed: 24680,
            botSeatIndexes: new[] { 0, 1, 2, 3 }, hostPlayerId: null, hostConnectionId: null, cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        state!.DealerSeatIndex = 0;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == 0;
        state.DealMode = DealMode.Auto;

        var slow = new SlowBotStrategy(delayMs: 600);
        InjectStrategyOrFail(runtime, slow);

        await runtime.StartGameAsync(gameId, cts.Token);

        // Compute the expected safe-default discard from the dealer's hand BEFORE any
        // discard fires. The hand is deterministic by seed; the SelectDiscardTile output
        // is the canonical fallback.
        var dealerHandSnapshot = new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = new List<int>(state.Hands[0].ConcealedTiles),
        };
        var expectedSafeDiscard = MediumStrategy.SelectDiscardTile(dealerHandSnapshot);

        await WaitForAsync(
            () => Snapshot(runtime, gameId).DiscardPile.Count >= 1,
            TimeSpan.FromMilliseconds(2_000),
            "bot dealer never discarded after slow-strategy timeout");

        var actualDiscard = Snapshot(runtime, gameId).DiscardPile[0].TileId;
        Assert.Equal(expectedSafeDiscard, actualDiscard);
    }

    [Fact, Trait("Category", "Changsha")]
    public async Task Bot_Timeout_DuringClaim_PassesNotFalseHu()
    {
        // Claim-window safe-default is Pass — NEVER a fake Hu. We construct a state where
        // bot seat 1 holds a 13-tile Hu-wait on Wan-1, then the (human-driven, no-bot)
        // dealer discards Wan-1. The runtime opens a claim window with a Hu opportunity
        // for seat 1; BotClaimAsync fires, slow strategy times out, safe-default Pass
        // resolves the window with no winner.
        AssertBotDecisionTimeoutOptionExists();

        await using var harness = new RuntimeHarness(o =>
        {
            o.BotDecisionTimeoutMs = 100;
            o.BotTurnDelayMs = 5;
            o.BotClaimDelayMs = 5;
            // Window timeout must be FAR larger than the decision timeout so we observe the
            // decision-timeout fallback (not the window timeout) closing the window. A wide
            // gap (100ms decision vs 10s window) keeps the timing assertion below robust to
            // CI thread-pool jitter without ever colliding with the window-timeout path.
            o.ClaimWindowTimeoutMs = 10_000;
        });
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var gameId = await runtime.CreateGameAsync(seed: 11,
            botSeatIndexes: new[] { 1, 2, 3 }, hostPlayerId: null, hostConnectionId: null, cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        state!.DealerSeatIndex = 0;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == 0;
        state.DealMode = DealMode.Auto;

        await runtime.StartGameAsync(gameId, cts.Token);
        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard,
            TimeSpan.FromSeconds(2),
            "auto-deal never reached AwaitingDiscard");

        // Surgically override hands so that a Wan-1 discard from seat 0 opens a Hu-only
        // claim window for bot seat 1 (and no one else has a competing Hu opportunity).
        var live = Snapshot(runtime, gameId);
        var wan1Logical = ChangshaTestHelpers.Logical(Suit.Wan, 1);
        var wan1Copy0 = ChangshaTestHelpers.Tid(Suit.Wan, 1, 0);

        live.Hands[1].ConcealedTiles.Clear();
        live.Hands[1].ConcealedTiles.AddRange(Acceptance.AcceptanceFixture.ThirteenTileWaitingForWan1());
        live.Hands[1].Melds.Clear();

        // Strip every Wan-1 from seats 2 and 3 so they can't claim/Pung/Hu on the discard.
        foreach (var h in live.Hands.Where(h => h.SeatIndex is 2 or 3))
            h.ConcealedTiles.RemoveAll(t => ChangshaDeckBuilder.GetLogicalTile(t) == wan1Logical);

        // Give dealer (seat 0) a hand of exactly 14 tiles including Wan-1 copy 0.
        live.Hands[0].ConcealedTiles.Clear();
        live.Hands[0].ConcealedTiles.AddRange(new[]
        {
            ChangshaTestHelpers.Tid(Suit.Tong, 9, 0), ChangshaTestHelpers.Tid(Suit.Tong, 9, 1),
            ChangshaTestHelpers.Tid(Suit.Tong, 8, 0), ChangshaTestHelpers.Tid(Suit.Tong, 7, 0),
            ChangshaTestHelpers.Tid(Suit.Tong, 6, 0), ChangshaTestHelpers.Tid(Suit.Tiao, 9, 0),
            ChangshaTestHelpers.Tid(Suit.Tiao, 9, 1), ChangshaTestHelpers.Tid(Suit.Tiao, 8, 0),
            ChangshaTestHelpers.Tid(Suit.Tiao, 7, 0), ChangshaTestHelpers.Tid(Suit.Tiao, 6, 0),
            ChangshaTestHelpers.Tid(Suit.Wan, 9, 0), ChangshaTestHelpers.Tid(Suit.Wan, 8, 0),
            ChangshaTestHelpers.Tid(Suit.Wan, 7, 0),
            wan1Copy0,
        });

        // SlowBotStrategy with a would-claim-Hu delegate makes failure mode loud: if Bishop
        // ever surfaces the slow task's result instead of the safe-default Pass, the test
        // catches it (CurrentWin would be populated).
        var slow = new SlowBotStrategy(delayMs: 600, decision: (_, _) => BotAction.Claim(TableClaimType.Hu));
        InjectStrategyOrFail(runtime, slow);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await runtime.DiscardAsync(gameId, seatIndex: 0, tileId: wan1Copy0, cts.Token);

        // The claim window must close via Pass-fallback within timeout + buffer.
        await WaitForAsync(
            () => Snapshot(runtime, gameId).ClaimWindow is null
               && Snapshot(runtime, gameId).Phase != ChangshaPhase.AwaitingClaim,
            TimeSpan.FromMilliseconds(8_000),
            "claim window did not resolve via Pass-fallback after BotDecisionTimeoutMs");
        sw.Stop();

        var final = Snapshot(runtime, gameId);
        Assert.Null(final.CurrentWin);
        Assert.DoesNotContain(final.EventLog, e => e.EventType == "win-declared");
        Assert.True(sw.ElapsedMilliseconds < 9_000,
            $"Claim-window Pass-fallback did not fire within BotDecisionTimeoutMs + buffer. " +
            $"Took {sw.ElapsedMilliseconds}ms; the decision-timeout fallback (100ms) must close " +
            $"the window well before the 10s ClaimWindowTimeoutMs path.");
    }

    [Fact, Trait("Category", "Changsha")]
    public async Task Bot_Decision_Within_Timeout_ProceedsNormally()
    {
        // Control test: with BotDecisionTimeoutMs comfortably above the strategy's runtime,
        // the strategy's NATURAL action is what lands — not the safe-default. We pin a
        // deterministic dealer hand and have the fast strategy return a specific tile that
        // is provably NOT the MediumStrategy.SelectDiscardTile safe-default for that hand.
        AssertBotDecisionTimeoutOptionExists();

        await using var harness = new RuntimeHarness(o =>
        {
            o.BotDecisionTimeoutMs = 5_000;
            // Give the test a 300ms window after observing AwaitingDiscard to override
            // the dealer's hand and swap in the scripted strategy before the bot turn fires.
            o.BotTurnDelayMs = 300;
            o.BotClaimDelayMs = 5;
            o.ClaimWindowTimeoutMs = 2000;
        });
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var gameId = await runtime.CreateGameAsync(seed: 99,
            botSeatIndexes: new[] { 0, 1, 2, 3 }, hostPlayerId: null, hostConnectionId: null, cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        state!.DealerSeatIndex = 0;
        foreach (var s in state.Seats) s.IsDealer = s.SeatIndex == 0;
        state.DealMode = DealMode.Auto;

        await runtime.StartGameAsync(gameId, cts.Token);
        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard
               && Snapshot(runtime, gameId).ActiveSeatIndex == 0,
            TimeSpan.FromSeconds(2),
            "auto-deal never reached AwaitingDiscard with bot dealer active");

        // Pin a deterministic dealer hand. Pick a 14-tile hand whose safe-default discard
        // (lowest keep-score) is provably different from our scripted "preferred" tile.
        var live = Snapshot(runtime, gameId);
        live.Hands[0].ConcealedTiles.Clear();
        live.Hands[0].ConcealedTiles.AddRange(new[]
        {
            ChangshaTestHelpers.Tid(Suit.Wan, 5, 0), ChangshaTestHelpers.Tid(Suit.Wan, 5, 1),
            ChangshaTestHelpers.Tid(Suit.Wan, 4, 0), ChangshaTestHelpers.Tid(Suit.Wan, 6, 0),
            ChangshaTestHelpers.Tid(Suit.Tong, 2, 0), ChangshaTestHelpers.Tid(Suit.Tong, 3, 0),
            ChangshaTestHelpers.Tid(Suit.Tong, 4, 0), ChangshaTestHelpers.Tid(Suit.Tong, 5, 0),
            ChangshaTestHelpers.Tid(Suit.Tiao, 7, 0), ChangshaTestHelpers.Tid(Suit.Tiao, 8, 0),
            ChangshaTestHelpers.Tid(Suit.Tiao, 9, 0), ChangshaTestHelpers.Tid(Suit.Tiao, 1, 0),
            ChangshaTestHelpers.Tid(Suit.Wan, 1, 0), ChangshaTestHelpers.Tid(Suit.Tong, 9, 0),
        });
        live.Hands[0].Melds.Clear();

        var safeDefault = MediumStrategy.SelectDiscardTile(new ChangshaHandState
        {
            SeatIndex = 0,
            ConcealedTiles = new List<int>(live.Hands[0].ConcealedTiles),
        });
        // Pick a "scripted" tile that is provably NOT the safe-default: the highest-id
        // tile in the hand (different ordering rule than SelectDiscardTile).
        var scripted = live.Hands[0].ConcealedTiles.OrderByDescending(t => t).First(t => t != safeDefault);

        var fast = new SlowBotStrategy(delayMs: 0,
            decision: (_, _) => BotAction.Discard(scripted));
        InjectStrategyOrFail(runtime, fast);

        // Re-trigger seat 0's turn loop so the injected fast strategy fires for the dealer.
        // The runtime is already in AwaitingDiscard from StartGame; an explicit no-op
        // mutation isn't available, so we rely on the in-flight RunBotTurnAsync to have
        // captured the injected strategy via field read on every DecideAction call.
        await WaitForAsync(
            () => Snapshot(runtime, gameId).DiscardPile.Count >= 1
               || Snapshot(runtime, gameId).Phase == ChangshaPhase.EndHand,
            TimeSpan.FromSeconds(3),
            "fast strategy never produced a discard within control-test window");

        Assert.True(fast.InvocationCount >= 1,
            "Fast strategy was not invoked — runtime is not consulting the injected IChangshaBotStrategy.");
        // First discard MUST be the scripted tile (not the safe-default). This proves the
        // timeout wrapper did NOT fire spuriously on a fast strategy.
        var firstDiscard = Snapshot(runtime, gameId).DiscardPile[0].TileId;
        Assert.NotEqual(safeDefault, firstDiscard);
        Assert.Equal(scripted, firstDiscard);
    }

    // ── Phase H Wave 1 — slow-strategy harness & reflection helpers ─────────────

    /// <summary>Test-double IChangshaBotStrategy that blocks <see cref="DecideAction"/>
    /// for <paramref name="delayMs"/> via <see cref="Thread.Sleep"/> (deliberate: a hung
    /// strategy is what Bishop's timeout wrapper must defeat). The result the strategy
    /// would have returned after the sleep is supplied by <paramref name="decision"/>;
    /// when Bishop's wrapper substitutes the safe-default, the slow task's result is
    /// expected to be discarded entirely.</summary>
    private sealed class SlowBotStrategy : IChangshaBotStrategy
    {
        private readonly int _delayMs;
        private readonly Func<ChangshaGameState, int, BotAction> _decision;
        public int InvocationCount;
        public string Difficulty => "slow-test";

        public SlowBotStrategy(int delayMs, Func<ChangshaGameState, int, BotAction>? decision = null)
        {
            _delayMs = delayMs;
            _decision = decision ?? ((_, _) => BotAction.Pass());
        }

        public BotAction OnTurnStart(ChangshaGameState state, int seat) => DecideAction(state, seat);
        public BotAction OnOtherDiscard(ChangshaGameState state, int seat, int discarder, int tile) => DecideAction(state, seat);
        public BotAction OnSelfDraw(ChangshaGameState state, int seat) => DecideAction(state, seat);
        public BotAction OnPickupCue(ChangshaGameState state, int seat) => BotAction.Wait();

        public BotAction DecideAction(ChangshaGameState state, int seat)
        {
            Interlocked.Increment(ref InvocationCount);
            if (_delayMs > 0) Thread.Sleep(_delayMs);
            return _decision(state, seat);
        }
    }

    /// <summary>Per-test factory harness — mirrors the shape used in
    /// <c>BotPickupSchedulerAcceptanceTests</c> so options can be tuned per test
    /// (BotDecisionTimeoutMs especially).</summary>
    private sealed class RuntimeHarness : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory { get; }
        public IChangshaGameRuntime Runtime { get; }
        private readonly string _tempDb;

        public RuntimeHarness(Action<ChangshaRuntimeOptions> configureOptions)
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
            Directory.CreateDirectory(dataDir);
            _tempDb = Path.Combine(dataDir, $"phase-h-bot-timeout-{Guid.NewGuid():N}.db");
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
                        o.BotPickupDelayMs = 50;
                        o.ClaimWindowTimeoutMs = 200;
                        o.DealBatchDelayMs = 0;
                        o.PersistSnapshots = false;
                        configureOptions(o);
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
            $"Bot decision-timeout contract violated: {description}. " +
            $"Bishop owes ChangshaRuntimeOptions.BotDecisionTimeoutMs + safe-default fallback wiring " +
            $"in BotClaimAsync / RunBotTurnAsync — see Phase H Wave 1 spec.");
    }

    private static void AssertBotDecisionTimeoutOptionExists()
    {
        var prop = typeof(ChangshaRuntimeOptions).GetProperty("BotDecisionTimeoutMs",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.True(prop is not null,
            "ChangshaRuntimeOptions.BotDecisionTimeoutMs missing. Bishop owns Phase H Wave 1 — add the new " +
            "option (int, default 2000ms) and wrap _botPolicy.DecideAction with a Task.WhenAny race.");
        Assert.Equal(typeof(int), prop!.PropertyType);
    }

    /// <summary>Reflectively swap the runtime's bot-decision strategy. Bishop's Phase H
    /// refactor MUST expose an injection seam so tests can supply a slow strategy. We
    /// probe (in order) for: any private IChangshaBotStrategy field on the runtime, a
    /// retyped <c>_botPolicy</c> assignable from the interface, or a static override
    /// hook on <see cref="ChangshaBotEngine"/>. Fails red with a descriptive message
    /// (naming the missing seam) until Bishop ships.</summary>
    private static void InjectStrategyOrFail(IChangshaGameRuntime runtime, IChangshaBotStrategy strategy)
    {
        var runtimeType = runtime.GetType();
        var fields = runtimeType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        // 1) Any field whose type is assignable to IChangshaBotStrategy (Bishop refactored
        //    the legacy _botPolicy : ChangshaBotPolicy to _strategy : IChangshaBotStrategy).
        var ifaceField = fields.FirstOrDefault(f => typeof(IChangshaBotStrategy).IsAssignableFrom(f.FieldType));
        if (ifaceField is not null)
        {
            ifaceField.SetValue(runtime, strategy);
            return;
        }

        // 2) A static override on ChangshaBotEngine ("TestOverride" / "Override").
        foreach (var memberName in new[] { "TestOverride", "Override", "StrategyOverride" })
        {
            var staticField = typeof(ChangshaBotEngine).GetField(memberName,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (staticField is not null && typeof(IChangshaBotStrategy).IsAssignableFrom(staticField.FieldType))
            {
                staticField.SetValue(null, strategy);
                return;
            }
            var staticProp = typeof(ChangshaBotEngine).GetProperty(memberName,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (staticProp is not null && typeof(IChangshaBotStrategy).IsAssignableFrom(staticProp.PropertyType))
            {
                staticProp.SetValue(null, strategy);
                return;
            }
        }

        Assert.Fail(
            "ChangshaGameRuntime exposes no IChangshaBotStrategy injection seam. " +
            "Bishop's Phase H Wave 1 timeout-fallback contract is untestable without one. " +
            "Either retype the existing _botPolicy field to IChangshaBotStrategy (or rename to _strategy) " +
            "or add a ChangshaBotEngine.TestOverride static hook. Probe scanned: " +
            string.Join(", ", fields.Select(f => $"{f.Name}:{f.FieldType.Name}")));
    }
}
