using System.Collections.Concurrent;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Tables;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// #137 (P0) — a human-vs-bots Manual game must play every hand to authoritative
/// <see cref="ChangshaPhase.GameComplete"/>. Hudson's real-UI 4-hand gate stalls at
/// <c>handEnds=0</c>: after #135 accepted the human's Hu wire-format, integrated main still
/// never finalises/advances hands through real seat-0-driven play.
///
/// <para>The all-bot ceremony test (<c>ManualDealPerHandCeremonyTests</c>) proves the runtime
/// auto-drives a fully-bot table to completion, but that path never exercises the seat-0 HUMAN
/// steps the gate performs: the human dealer rolls the dice, the human picks up their batch in
/// each manual-deal round, the human discards on their own turn, and the human answers claim
/// windows (Hu when offered, else pass). This test reproduces exactly that: seat 0 is a human
/// (not a bot) whose actions are scripted through the same runtime entry points the WS endpoint
/// routes to, while seats 1..3 are Hard bots. If any hand fails to finalise or the ceremony
/// fails to re-enter for a human dealer, the game never reaches GameComplete and the test fails
/// with the exact stuck phase/seat — the deterministic analogue of the gate's handEnds=0 stall.</para>
/// </summary>
public sealed class HumanVsBotsManualPlaythroughTests(ITestOutputHelper output)
{
    private const int HumanSeat = 0;

    private sealed class RuntimeHarness : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory { get; }
        public IChangshaGameRuntime Runtime { get; }
        private readonly string _tempDb;

        public RuntimeHarness(Action<ChangshaRuntimeOptions> configureOptions)
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
            Directory.CreateDirectory(dataDir);
            _tempDb = Path.Combine(dataDir, $"human-vs-bots-{Guid.NewGuid():N}.db");
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
                b.ConfigureServices(s => s.Configure<ChangshaRuntimeOptions>(configureOptions));
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

    [Theory, Trait("Category", "Acceptance"), Trait("Contract", "C-1")]
    [InlineData(4100)]  // the seed #137 called out on integrated main
    [InlineData(7316)]
    [InlineData(2024)]
    [InlineData(99)]
    public async Task HumanVsBots_Manual_PlaysEveryHand_ToGameComplete(int seed)
    {
        await using var harness = new RuntimeHarness(o =>
        {
            // Moderate delays keep the fire-and-forget bot scheduling genuinely concurrent with
            // the seat-0 driver (so scheduling/finalisation races can surface) while staying fast.
            o.BotTurnDelayMs = 5;
            o.BotClaimDelayMs = 5;
            o.BotPickupDelayMs = 5;
            o.ClaimWindowTimeoutMs = 150;
            o.DealBatchDelayMs = 0;
            o.PersistSnapshots = false;
        });
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Seat 0 = human; seats 1..3 = Hard bots. Manual deal, MaxHands = 4.
        var gameId = await runtime.CreateGameAsync(
            seed: seed, botSeatIndexes: new[] { 1, 2, 3 },
            hostPlayerId: "human-0", hostConnectionId: null, cts.Token, maxHands: 4);
        await runtime.SetBotStrategyAsync(gameId, "hard", cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var created) && created is not null);
        created!.DealMode = DealMode.Manual;
        Assert.False(created.Seats[HumanSeat].IsBot, "seat 0 must be the human.");

        // Start the game the same way the bundle's "Deal" click does (match[0] dealCommand:start →
        // StartGameAsync). Manual mode parks at RollingDice awaiting the (human) dealer's roll.
        await runtime.StartGameAsync(gameId, cts.Token);
        Assert.Equal(ChangshaPhase.RollingDice, Snapshot(runtime, gameId).Phase);

        var handEndsObserved = new ConcurrentDictionary<int, byte>();
        using var driverCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

        // Background monitor: latch each hand number that reaches a finished phase (EndHand /
        // WallExhausted). This is the deterministic analogue of the gate's result['current'] latch.
        var monitor = Task.Run(async () =>
        {
            while (!driverCts.IsCancellationRequested)
            {
                if (runtime.TryGetSnapshot(gameId, out var s) && s is not null)
                {
                    if (s.Phase is ChangshaPhase.EndHand or ChangshaPhase.WallExhausted)
                        handEndsObserved.TryAdd(s.HandNumber, 0);
                    if (s.IsGameComplete) break;
                }
                try { await Task.Delay(3, driverCts.Token); } catch (OperationCanceledException) { break; }
            }
        }, driverCts.Token);

        // Background driver: the scripted human (seat 0). Reacts to the authoritative snapshot and
        // drives exactly the actions the shipped bundle performs — nothing else auto-moves seat 0.
        var driver = Task.Run(() => DriveHumanSeatAsync(runtime, gameId, handEndsObserved, driverCts.Token));

        var completed = await WaitForAsync(
            () => runtime.TryGetSnapshot(gameId, out var s) && s is not null && s.IsGameComplete,
            TimeSpan.FromSeconds(110));

        driverCts.Cancel();
        try { await driver; } catch (OperationCanceledException) { }
        try { await monitor; } catch (OperationCanceledException) { }

        Assert.True(runtime.TryGetSnapshot(gameId, out var final) && final is not null);
        if (!completed)
        {
            var h = final!.Hands.FirstOrDefault(x => x.SeatIndex == HumanSeat);
            output.WriteLine(
                $"#137 STALL seed={seed}: phase={final.Phase} hand={final.HandNumber} " +
                $"active={final.ActiveSeatIndex} pickupSeat={final.PickupSeatIndex} " +
                $"dealer={final.DealerSeatIndex} seat0Tiles={h?.ConcealedTiles.Count} " +
                $"handEndsObserved=[{string.Join(",", handEndsObserved.Keys.OrderBy(k => k))}]");
        }

        Assert.True(completed,
            $"#137: human-vs-bots Manual game (seed={seed}) never reached GameComplete — it stalled at " +
            $"phase={final!.Phase} hand={final.HandNumber} active={final.ActiveSeatIndex} " +
            $"pickupSeat={final.PickupSeatIndex} dealer={final.DealerSeatIndex}. " +
            $"handEndsObserved=[{string.Join(",", handEndsObserved.Keys.OrderBy(k => k))}]. " +
            "Every hand must finalise and the ceremony must re-enter for a human dealer so play advances.");
        Assert.Equal(ChangshaPhase.GameComplete, final.Phase);
        Assert.Equal(final.MaxHands + 1, final.HandNumber);
    }

    /// <summary>
    /// Drives seat 0 (the human) through the whole game by reacting to the live snapshot, mirroring
    /// the shipped bundle: roll dice when the human is the dealer awaiting a roll; pick up the human's
    /// batch in each manual-deal round; discard on the human's own turn; and answer claim windows
    /// (Hu when offered, otherwise pass). Every action is guarded so a fast poll never double-sends.
    /// </summary>
    private async Task DriveHumanSeatAsync(
        IChangshaGameRuntime runtime, string gameId,
        ConcurrentDictionary<int, byte> handEndsObserved, CancellationToken ct)
    {
        int? rolledForHand = null;
        var pickedUp = new HashSet<string>();
        var discardedTurns = new HashSet<string>();
        var respondedWindows = new HashSet<string>();

        while (!ct.IsCancellationRequested)
        {
            ChangshaGameState? snap = null;
            try { snap = await runtime.TryGetSnapshotCopyAsync(gameId); } catch { }
            if (snap is null) { await SafeDelay(ct); continue; }
            if (snap.IsGameComplete) return;

            try
            {
                switch (snap.Phase)
                {
                    case ChangshaPhase.RollingDice:
                        if (snap.DealerSeatIndex == HumanSeat && rolledForHand != snap.HandNumber)
                        {
                            rolledForHand = snap.HandNumber;
                            await runtime.RollDiceAsync(gameId, HumanSeat, ct);
                        }
                        break;

                    case ChangshaPhase.BreakPointMarked:
                    case ChangshaPhase.PickupRound1:
                    case ChangshaPhase.PickupRound2:
                    case ChangshaPhase.PickupRound3:
                    case ChangshaPhase.SingleTilePickup:
                    case ChangshaPhase.DealerExtra:
                        if (snap.PickupSeatIndex == HumanSeat)
                        {
                            var sig = $"h{snap.HandNumber}:{snap.Phase}";
                            if (pickedUp.Add(sig))
                            {
                                var expected = ChangshaGameStateMachine.ExpectedPickupCount(snap.Phase);
                                if (expected > 0)
                                    await runtime.TakeTilesFromWallAsync(gameId, HumanSeat, expected, ct);
                            }
                        }
                        break;

                    case ChangshaPhase.AwaitingDiscard:
                        if (snap.ActiveSeatIndex == HumanSeat)
                        {
                            var sig = $"h{snap.HandNumber}:v{snap.StateVersion}";
                            if (discardedTurns.Add(sig))
                            {
                                var hand = snap.Hands.Single(x => x.SeatIndex == HumanSeat);
                                if (hand.ConcealedTiles.Count > 0)
                                    await runtime.DiscardAsync(gameId, HumanSeat, hand.ConcealedTiles[^1], ct);
                            }
                        }
                        break;

                    case ChangshaPhase.AwaitingClaim:
                        var window = snap.ClaimWindow;
                        if (window is not null)
                        {
                            var myOpps = window.Opportunities.Where(o => o.SeatIndex == HumanSeat).ToList();
                            if (myOpps.Count > 0)
                            {
                                var wsig = $"h{snap.HandNumber}:{window.DiscardSeatIndex}:{window.DiscardTileId}";
                                if (respondedWindows.Add(wsig))
                                {
                                    if (myOpps.Any(o => o.ClaimType == TableClaimType.Hu))
                                        await runtime.ClaimAsync(gameId, HumanSeat, "Hu", null, ct);
                                    else
                                        await runtime.PassAsync(gameId, HumanSeat, ct);
                                }
                            }
                        }
                        break;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // The bundle also swallows rejected clicks; the stall (if any) surfaces as no progress.
                output.WriteLine($"seat-0 action rejected in phase {snap.Phase}: {ex.GetType().Name}: {ex.Message}");
            }

            await SafeDelay(ct);
        }
    }

    private static async Task SafeDelay(CancellationToken ct)
    {
        try { await Task.Delay(4, ct); } catch (OperationCanceledException) { }
    }

    private static ChangshaGameState Snapshot(IChangshaGameRuntime runtime, string gameId)
    {
        Assert.True(runtime.TryGetSnapshot(gameId, out var state) && state is not null);
        return state!;
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(20);
        }
        return predicate();
    }
}
