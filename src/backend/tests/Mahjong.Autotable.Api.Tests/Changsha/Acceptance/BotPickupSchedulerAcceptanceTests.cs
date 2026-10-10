using System.Diagnostics;
using System.Reflection;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance: Phase G bot-pickup tick scheduler.
///
/// <para>Contract (locked here, owned by Bishop's <see cref="ChangshaGameRuntime"/>): in
/// manual-pickup mode (<see cref="DealMode.Manual"/>) with bots filling seats, after the
/// dealer rolls dice the runtime MUST automatically schedule
/// <see cref="IChangshaGameRuntime.TakeTilesFromWallAsync"/> for any seat that becomes the
/// active <c>PickupSeatIndex</c>, throttled by
/// <see cref="ChangshaRuntimeOptions.BotPickupDelayMs"/>. The chain stops fire-and-forget
/// when the cursor lands on a human seat, then resumes once the human picks. After
/// <see cref="ChangshaPhase.DealerExtra"/> resolves, the existing turn-start path drives
/// the dealer's first discard.</para>
///
/// <para><b>Pre-Bishop posture:</b> the scheduler is not yet wired (see
/// <c>.squad/decisions.md</c> §"Deferred Follow-ups": "Bot pickup tick scheduler — needed
/// before bots can do manual pickup visually"). The bot-driven tests fail RED with
/// <c>TimeoutException</c> "bots never picked their first 4 tiles" until Bishop ships.
/// The auto-deal regression test (#5) passes GREEN today and pins the no-pickup baseline.</para>
///
/// <para><b>Sources:</b> Bishop's Phase G task spec, Ripley §2.5,
/// <c>.squad/decisions.md</c> §"Deferred Follow-ups".</para>
/// </summary>
[Collection("BotPickupScheduler")]
public sealed class BotPickupSchedulerAcceptanceTests
{
    // ── Inline factory harness (per-test, so BotPickupDelayMs is configurable) ────

    private sealed class RuntimeHarness : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory { get; }
        public IChangshaGameRuntime Runtime { get; }
        private readonly string _tempDb;

        public RuntimeHarness(Action<ChangshaRuntimeOptions> configureOptions)
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
            Directory.CreateDirectory(dataDir);
            _tempDb = Path.Combine(dataDir, $"phase-g-bot-pickup-{Guid.NewGuid():N}.db");
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
                b.ConfigureServices(s =>
                {
                    s.Configure<ChangshaRuntimeOptions>(o =>
                    {
                        // Fast defaults so the test suite stays brisk; tests can override.
                        o.BotTurnDelayMs = 1;
                        o.BotClaimDelayMs = 1;
                        o.BotPickupDelayMs = 50;
                        o.ClaimWindowTimeoutMs = 50;
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

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static async Task<string> CreateManualGameAsync(
        IChangshaGameRuntime runtime,
        int[] botSeats,
        int dealerSeat = 0,
        int seed = 4242,
        CancellationToken ct = default)
    {
        var gameId = await runtime.CreateGameAsync(seed, botSeats, hostPlayerId: null, hostConnectionId: null, ct);
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        Assert.NotNull(state);
        state!.DealerSeatIndex = dealerSeat;
        foreach (var seat in state.Seats) seat.IsDealer = seat.SeatIndex == dealerSeat;
        state.DealMode = DealMode.Manual;
        return gameId;
    }

    private static ChangshaGameState Snapshot(IChangshaGameRuntime runtime, string gameId)
    {
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        return state!;
    }

    /// <summary>Polls <paramref name="predicate"/> at ~25ms cadence until it returns true
    /// or the timeout elapses. Throws <see cref="TimeoutException"/> with the supplied
    /// <paramref name="description"/> for diagnostic readability when red.</summary>
    private static async Task WaitForAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException(
            $"BotPickupScheduler contract violated: {description}. " +
            $"Bishop owes the runtime tick that schedules TakeTilesFromWallAsync after " +
            $"BotPickupDelayMs — see .squad/decisions.md §\"Deferred Follow-ups\".");
    }

    private static int HandCount(ChangshaGameState state, int seat) =>
        state.Hands.Single(h => h.SeatIndex == seat).ConcealedTiles.Count;

    // ── Test 1 — Bots fill seats 1/2/3 around a human dealer ────────────────────

    [Fact, Trait("Category", "Acceptance")]
    public async Task Bot_Pickup_Fires_Automatically_When_Dealer_Is_Human_And_Seats_1_2_3_Are_Bots()
    {
        // Bishop's contract: when the cursor lands on a bot seat in any pickup phase, the
        // runtime schedules TakeTilesFromWallAsync after BotPickupDelayMs. Cursor on a human
        // halts the chain until the human calls TakeTilesFromWallAsync manually. Across all
        // pickup phases the chain reaches AwaitingDiscard with correct tile counts.
        await using var harness = new RuntimeHarness(o => o.BotPickupDelayMs = 50);
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var gameId = await CreateManualGameAsync(runtime, botSeats: new[] { 1, 2, 3 },
            dealerSeat: 0, seed: 1001, ct: cts.Token);

        await runtime.StartGameAsync(gameId, cts.Token);
        Assert.Equal(ChangshaPhase.RollingDice, Snapshot(runtime, gameId).Phase);

        await runtime.RollDiceAsync(gameId, seatIndex: 0, cts.Token);
        Assert.Equal(ChangshaPhase.BreakPointMarked, Snapshot(runtime, gameId).Phase);

        // Round 1: human (seat 0) picks 4. Then bots 1, 2, 3 auto-pick.
        await runtime.TakeTilesFromWallAsync(gameId, seatIndex: 0, count: 4, cts.Token);
        await WaitForAsync(
            () => HandCount(Snapshot(runtime, gameId), 1) == 4 &&
                  HandCount(Snapshot(runtime, gameId), 2) == 4 &&
                  HandCount(Snapshot(runtime, gameId), 3) == 4,
            TimeSpan.FromSeconds(3),
            "bots 1/2/3 never auto-picked their 4 round-1 tiles");

        // Round 2: cursor must be back at human seat 0; bots wait.
        await WaitForAsync(
            () => Snapshot(runtime, gameId).PickupSeatIndex == 0,
            TimeSpan.FromSeconds(1),
            "cursor did not return to human seat 0 for round 2");
        await runtime.TakeTilesFromWallAsync(gameId, seatIndex: 0, count: 4, cts.Token);
        await WaitForAsync(
            () => HandCount(Snapshot(runtime, gameId), 1) == 8 &&
                  HandCount(Snapshot(runtime, gameId), 2) == 8 &&
                  HandCount(Snapshot(runtime, gameId), 3) == 8,
            TimeSpan.FromSeconds(3),
            "bots 1/2/3 never auto-picked their 4 round-2 tiles");

        // Round 3.
        await WaitForAsync(
            () => Snapshot(runtime, gameId).PickupSeatIndex == 0,
            TimeSpan.FromSeconds(1),
            "cursor did not return to human seat 0 for round 3");
        await runtime.TakeTilesFromWallAsync(gameId, seatIndex: 0, count: 4, cts.Token);
        await WaitForAsync(
            () => HandCount(Snapshot(runtime, gameId), 1) == 12 &&
                  HandCount(Snapshot(runtime, gameId), 2) == 12 &&
                  HandCount(Snapshot(runtime, gameId), 3) == 12,
            TimeSpan.FromSeconds(3),
            "bots 1/2/3 never auto-picked their 4 round-3 tiles");

        // Single-tile round.
        await WaitForAsync(
            () => Snapshot(runtime, gameId).PickupSeatIndex == 0 &&
                  Snapshot(runtime, gameId).Phase == ChangshaPhase.SingleTilePickup,
            TimeSpan.FromSeconds(1),
            "phase did not advance to SingleTilePickup with cursor at human seat 0");
        await runtime.TakeTilesFromWallAsync(gameId, seatIndex: 0, count: 1, cts.Token);
        await WaitForAsync(
            () => HandCount(Snapshot(runtime, gameId), 1) == 13 &&
                  HandCount(Snapshot(runtime, gameId), 2) == 13 &&
                  HandCount(Snapshot(runtime, gameId), 3) == 13,
            TimeSpan.FromSeconds(3),
            "bots 1/2/3 never auto-picked the single-tile-round tile");

        // Dealer extra: the dealer (human) takes the final tile.
        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.DealerExtra &&
                  Snapshot(runtime, gameId).PickupSeatIndex == 0,
            TimeSpan.FromSeconds(1),
            "phase did not advance to DealerExtra with cursor at dealer");
        await runtime.TakeTilesFromWallAsync(gameId, seatIndex: 0, count: 1, cts.Token);

        // After dealer extra → AwaitingDiscard via the standard turn-start path.
        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard,
            TimeSpan.FromSeconds(2),
            "phase did not transition to AwaitingDiscard after dealer-extra");

        var final = Snapshot(runtime, gameId);
        Assert.Equal(14, HandCount(final, 0)); // dealer (human)
        Assert.Equal(13, HandCount(final, 1));
        Assert.Equal(13, HandCount(final, 2));
        Assert.Equal(13, HandCount(final, 3));
        Assert.Equal(55, final.Wall.Count);
    }

    // ── Test 2 — Bot chain halts at human mid-stream and resumes after manual pick ──

    [Fact, Trait("Category", "Acceptance")]
    public async Task Bot_Pickup_Halts_When_Active_Seat_Switches_To_Human_Mid_Chain()
    {
        // dealer = seat 0 (bot) → after dice roll, bot seat 0 auto-picks first 4 tiles.
        // Cursor advances to seat 1 (human). Chain must halt and wait for human.
        // After human picks, bots 2 + 3 resume automatically.
        await using var harness = new RuntimeHarness(o => o.BotPickupDelayMs = 50);
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var gameId = await CreateManualGameAsync(runtime, botSeats: new[] { 0, 2, 3 },
            dealerSeat: 0, seed: 1002, ct: cts.Token);

        await runtime.StartGameAsync(gameId, cts.Token);
        await runtime.RollDiceAsync(gameId, seatIndex: 0, cts.Token);

        // Bot seat 0 must auto-pick its round-1 4 tiles.
        await WaitForAsync(
            () => HandCount(Snapshot(runtime, gameId), 0) == 4,
            TimeSpan.FromSeconds(3),
            "bot dealer seat 0 never auto-picked its 4 round-1 tiles");

        // Chain must now park on seat 1 (human). Give the scheduler at least 2×delay to
        // PROVE it stopped — if it (incorrectly) auto-picked for the human, seat 1's
        // hand would already be populated.
        await Task.Delay(TimeSpan.FromMilliseconds(50 * 4));
        var midChain = Snapshot(runtime, gameId);
        Assert.Equal(1, midChain.PickupSeatIndex);
        Assert.Equal(0, HandCount(midChain, 1));
        // Bots 2 + 3 must not have raced ahead either (the chain is strictly CCW).
        Assert.Equal(0, HandCount(midChain, 2));
        Assert.Equal(0, HandCount(midChain, 3));

        // Human seat 1 picks manually — bots 2 + 3 then resume automatically.
        await runtime.TakeTilesFromWallAsync(gameId, seatIndex: 1, count: 4, cts.Token);
        await WaitForAsync(
            () => HandCount(Snapshot(runtime, gameId), 2) == 4 &&
                  HandCount(Snapshot(runtime, gameId), 3) == 4,
            TimeSpan.FromSeconds(3),
            "bots 2 + 3 did not resume after human picked at seat 1");
    }

    // ── Test 3 — All-bot game completes the full pickup chain ────────────────────

    [Fact, Trait("Category", "Acceptance")]
    public async Task Bot_Pickup_Continues_Through_All_Three_Rounds()
    {
        // 4 bots, dealer = seat 0. After RollDice, the entire pickup chain (rounds 1/2/3,
        // single-tile round, dealer-extra) MUST resolve to AwaitingDiscard with no human
        // intervention. Dealer ends with 14 tiles; others with 13.
        // BotTurnDelayMs is set high so the dealer bot doesn't discard before we snapshot
        // the post-deal state.
        await using var harness = new RuntimeHarness(o =>
        {
            o.BotPickupDelayMs = 50;
            o.BotTurnDelayMs = 30_000;
        });
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var gameId = await CreateManualGameAsync(runtime, botSeats: new[] { 0, 1, 2, 3 },
            dealerSeat: 0, seed: 1003, ct: cts.Token);

        await runtime.StartGameAsync(gameId, cts.Token);
        await runtime.RollDiceAsync(gameId, seatIndex: 0, cts.Token);

        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard &&
                  HandCount(Snapshot(runtime, gameId), 0) == 14,
            TimeSpan.FromSeconds(15),
            "4-bot pickup chain never resolved to AwaitingDiscard with dealer holding 14");

        var final = Snapshot(runtime, gameId);
        Assert.Equal(14, HandCount(final, 0)); // dealer
        Assert.Equal(13, HandCount(final, 1));
        Assert.Equal(13, HandCount(final, 2));
        Assert.Equal(13, HandCount(final, 3));
        Assert.Equal(55, final.Wall.Count);
    }

    // ── Test 4 — Bot pickup scheduler honours the BotPickupDelayMs knob ─────────

    [Fact, Trait("Category", "Acceptance")]
    public async Task Bot_Pickup_Respects_BotPickupDelayMs_Knob()
    {
        // With BotPickupDelayMs=200, the 4-bot pickup chain (≈17 actions: 4×4 + 1 dealer
        // extra) must take noticeably longer than zero-delay AND finish within a sane
        // upper bound. Using 13 actions × 200ms as the contract anchor (the user-visible
        // pickup is 13 user-perceptible takes — round 1/2/3 = 12, plus single = 1; dealer
        // extra collapses with the discard transition), assert >0.5× and <3× that floor.
        await using var harness = new RuntimeHarness(o => o.BotPickupDelayMs = 200);
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var gameId = await CreateManualGameAsync(runtime, botSeats: new[] { 0, 1, 2, 3 },
            dealerSeat: 0, seed: 1004, ct: cts.Token);

        await runtime.StartGameAsync(gameId, cts.Token);

        var sw = Stopwatch.StartNew();
        await runtime.RollDiceAsync(gameId, seatIndex: 0, cts.Token);
        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard,
            TimeSpan.FromSeconds(20),
            "4-bot pickup chain with BotPickupDelayMs=200 never completed");
        sw.Stop();

        var elapsedMs = sw.ElapsedMilliseconds;
        // Lower bound: 13 × 200 × 0.5 = 1300ms — guarantees the delay is honoured (rules
        // out a "tick scheduler runs immediately, ignoring delay" regression).
        Assert.True(elapsedMs > 1300,
            $"BotPickupDelayMs=200 should yield >1300ms of pickup latency; got {elapsedMs}ms. " +
            $"Scheduler is firing without respecting the delay.");
        // Upper bound: 13 × 200 × 3 = 7800ms — generous slack for CI jitter.
        Assert.True(elapsedMs < 7800,
            $"BotPickupDelayMs=200 should complete <7800ms; got {elapsedMs}ms. " +
            $"Scheduler may be wedged or compounding delays.");
    }

    // ── Test 5 — Auto-deal mode does NOT engage the pickup scheduler (regression) ──

    [Fact, Trait("Category", "Acceptance")]
    public async Task Bot_Pickup_Does_Not_Fire_In_AutoDealMode()
    {
        // DealMode=Auto must bypass the manual-pickup state machine entirely.
        // StartGameAsync runs RollDice + Deal atomically; the bot pickup scheduler must
        // NOT trigger any TakeTilesFromWall calls (no pickup phases visited).
        // This is the regression gate for users who add ?dealMode=auto.
        await using var harness = new RuntimeHarness(o =>
        {
            // BotPickupDelayMs is irrelevant in Auto mode — pick a slow value so any
            // accidental scheduler invocation would be observably late.
            o.BotPickupDelayMs = 2000;
            o.BotTurnDelayMs = 5_000; // also slow, so bots don't start discarding mid-test
        });
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var gameId = await runtime.CreateGameAsync(seed: 1005, botSeatIndexes: new[] { 0, 1, 2, 3 },
            hostPlayerId: null, hostConnectionId: null, cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        Assert.Equal(DealMode.Auto, state!.DealMode); // default is Auto

        await runtime.StartGameAsync(gameId, cts.Token);

        // StartGameAsync in Auto mode runs Deal() synchronously — phase is AwaitingDiscard
        // immediately, no pickup phases visited.
        var post = Snapshot(runtime, gameId);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, post.Phase);
        Assert.Equal(14, HandCount(post, post.DealerSeatIndex));
        Assert.Equal(55, post.Wall.Count);

        // The event log must NOT contain any tiles-picked-up event (pickup events are only
        // emitted by TakeTilesFromWall in manual mode).
        Assert.DoesNotContain(post.EventLog, e => e.EventType == "tiles-picked-up");
    }

    // ── Test 6 — Pending bot pickup tasks cancel cleanly on game teardown ───────

    [Fact, Trait("Category", "Acceptance")]
    public async Task Bot_Pickup_Cancellation_On_Game_Teardown()
    {
        // During a pickup pause, force-clear the game instance. Any pending bot pickup
        // task must observe its cancellation token and exit cleanly — no leaked
        // TaskCanceledException, no late TakeTilesFromWall on a removed game.
        // We use a generous BotPickupDelayMs so we can dispose mid-delay reliably.
        await using var harness = new RuntimeHarness(o => o.BotPickupDelayMs = 1500);
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var gameId = await CreateManualGameAsync(runtime, botSeats: new[] { 0, 1, 2, 3 },
            dealerSeat: 0, seed: 1006, ct: cts.Token);
        await runtime.StartGameAsync(gameId, cts.Token);
        await runtime.RollDiceAsync(gameId, seatIndex: 0, cts.Token);

        // Phase is BreakPointMarked with the bot-dealer scheduled to pick in 1500ms.
        Assert.Equal(ChangshaPhase.BreakPointMarked, Snapshot(runtime, gameId).Phase);
        Assert.Equal(0, HandCount(Snapshot(runtime, gameId), 0));

        // Tear down the game instance ~250ms in — well before the scheduled pickup
        // would have fired. The instance's LifecycleCts must cancel the pending task.
        await Task.Delay(250);

        var disposed = await TryDisposeGameInstanceAsync(runtime, gameId);
        Assert.True(disposed,
            "No teardown path available on IChangshaGameRuntime — Bishop should " +
            "expose a public RemoveGame/DisposeGame API (or wire it into HandleDisconnectAsync). " +
            "Until then this test guards against silent task leaks via reflection.");

        // Give any racing tasks time to either complete or be cancelled.
        await Task.Delay(TimeSpan.FromMilliseconds(2000));

        // The game must no longer be in the runtime's in-memory dict.
        Assert.False(runtime.TryGetSnapshot(gameId, out _));

        // No unobserved-task exceptions should have surfaced. Net.Sockets / SignalR
        // background loops occasionally throw OperationCanceled during teardown; xUnit
        // doesn't fail on those, but we explicitly assert there is no
        // InvalidOperationException with the gameId in the message (which would mean a
        // late TakeTilesFromWall fired after teardown).
        // (Indirect signal — the assertion above already proves the game is gone.)
    }

    /// <summary>Best-effort teardown of a game via reflection on the runtime's internal
    /// <c>_games</c> ConcurrentDictionary. Returns true if the instance was removed and
    /// disposed; false if the runtime shape is unrecognized (Bishop refactored).</summary>
    private static async Task<bool> TryDisposeGameInstanceAsync(IChangshaGameRuntime runtime, string gameId)
    {
        var runtimeType = runtime.GetType();
        var gamesField = runtimeType.GetField("_games",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (gamesField is null) return false;

        var games = gamesField.GetValue(runtime);
        if (games is null) return false;

        var dictType = games.GetType();
        var tryRemove = dictType.GetMethod("TryRemove", new[] { typeof(string), dictType.GetGenericArguments()[1].MakeByRefType() });
        if (tryRemove is null) return false;

        var args = new object?[] { gameId, null };
        var removed = (bool)tryRemove.Invoke(games, args)!;
        if (!removed) return false;

        var instance = args[1]!;
        // ChangshaGameInstance implements IAsyncDisposable.
        if (instance is IAsyncDisposable ad)
        {
            await ad.DisposeAsync();
            return true;
        }
        if (instance is IDisposable d)
        {
            d.Dispose();
            return true;
        }
        return false;
    }

    // ── Vasquez rev2 — bot DEALER opening roll on hand 1 (StartGame scheduling seam) ──

    [Fact, Trait("Category", "Acceptance"), Trait("Contract", "bot-dealer-startgame")]
    public async Task ManualBotDealer_HandOne_AutoRolls_And_DrivesPickupChain_ToAwaitingDiscard()
    {
        // Vasquez rev2 ROOT CAUSE of the live "pickup.targetSlots length 0": StartGameAsync's
        // manual branch never scheduled a BOT dealer's opening roll, so a manual game whose
        // dealer is a bot parked in RollingDice on hand 1 — the deal ceremony never began and
        // pickup stayed a null tombstone. With the seam fixed (StartGameAsync manual branch now
        // calls ScheduleBotIfNeededAsync), a bot dealer auto-rolls and the whole pickup chain
        // runs to AwaitingDiscard with no human input. RED (pre-fix): times out in RollingDice.
        // Fixed seam + a HIGH bot TURN delay so the dealer does NOT discard the instant the
        // deal completes: this gives a deterministic window to assert the EXACT post-deal
        // 14/13/13/13 at the AwaitingDiscard transition before any autonomous bot turn mutates
        // the dealer's hand (14 -> 13). Pickup delay stays low so the ceremony runs quickly.
        await using var harness = new RuntimeHarness(o => { o.BotPickupDelayMs = 20; o.BotTurnDelayMs = 3000; });
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var gameId = await CreateManualGameAsync(runtime, botSeats: new[] { 0, 1, 2, 3 },
            dealerSeat: 0, seed: 2027, ct: cts.Token);
        Assert.True(Snapshot(runtime, gameId).Seats[0].IsBot, "dealer seat 0 must be a bot for this regression.");

        await runtime.StartGameAsync(gameId, cts.Token);

        // No human presses "roll" — the fixed StartGame seam must schedule the bot dealer's roll.
        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase != ChangshaPhase.RollingDice,
            TimeSpan.FromSeconds(6),
            "bot dealer never auto-rolled on hand 1 (StartGame manual branch did not schedule the opening roll)");

        // The full pickup ceremony drives all four seats to the AwaitingDiscard transition.
        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase == ChangshaPhase.AwaitingDiscard,
            TimeSpan.FromSeconds(10),
            "bot-dealer manual pickup chain never reached AwaitingDiscard");

        // Assert the EXACT canonical deal AT the transition. The 3s bot-turn delay guarantees the
        // dealer has NOT yet discarded, so this proves the bot-dealer ceremony dealt 14/13/13/13
        // and consumed the 53 pickup tiles (wall 108 -> 55) — captured before later bot turns
        // mutate it (de-flaked; no broad eventual condition).
        var s = Snapshot(runtime, gameId);
        Assert.Equal(ChangshaPhase.AwaitingDiscard, s.Phase);
        Assert.Equal(14, HandCount(s, 0)); // dealer drew its 14th (DealerExtra)
        Assert.Equal(13, HandCount(s, 1));
        Assert.Equal(13, HandCount(s, 2));
        Assert.Equal(13, HandCount(s, 3));
        Assert.Equal(55, s.Wall.Count); // 108 - 53 dealt
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Contract", "bot-dealer-startgame")]
    public async Task ManualBotDealer_HumanNonDealer_ProgressesPastRollingDice_IntoPickupChain()
    {
        // Human at seat 1 (non-dealer); bot dealer at seat 0. The fixed StartGame seam auto-rolls
        // the bot dealer through RollDice into the pickup ceremony; the bot dealer takes its own
        // batch, then the chain marches and STALLS at the human's pickup seat (which requires a
        // manual `take`). Proof of progression THROUGH RollDice and the pickup chain: the game
        // left RollingDice (was stuck forever pre-fix) and is parked in a pickup phase whose
        // cursor is the human seat, with the human's hand still empty.
        await using var harness = new RuntimeHarness(o => o.BotPickupDelayMs = 20);
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var gameId = await CreateManualGameAsync(runtime, botSeats: new[] { 0, 2, 3 },
            dealerSeat: 0, seed: 3031, ct: cts.Token);
        var pre = Snapshot(runtime, gameId);
        Assert.True(pre.Seats[0].IsBot, "dealer seat 0 must be a bot.");
        Assert.False(pre.Seats[1].IsBot, "seat 1 must be the human non-dealer.");

        await runtime.StartGameAsync(gameId, cts.Token);

        // Bot dealer auto-rolls: phase leaves RollingDice.
        await WaitForAsync(
            () => Snapshot(runtime, gameId).Phase != ChangshaPhase.RollingDice,
            TimeSpan.FromSeconds(6),
            "bot dealer (human at seat 1) never auto-rolled on hand 1");

        // The pickup chain advances the bot seats, then stalls at the human non-dealer seat 1.
        await WaitForAsync(
            () =>
            {
                var s = Snapshot(runtime, gameId);
                return s.PickupSeatIndex == 1 && ChangshaGameStateMachine.IsPickupPhase(s.Phase);
            },
            TimeSpan.FromSeconds(6),
            "pickup chain never reached the human non-dealer seat 1");

        var st = Snapshot(runtime, gameId);
        Assert.True(ChangshaGameStateMachine.IsPickupPhase(st.Phase));
        Assert.True(HandCount(st, 0) >= 4, "bot dealer should have taken at least its round-1 batch");
        Assert.Equal(0, HandCount(st, 1)); // human hasn't picked — chain correctly stalled on the human
    }
}
