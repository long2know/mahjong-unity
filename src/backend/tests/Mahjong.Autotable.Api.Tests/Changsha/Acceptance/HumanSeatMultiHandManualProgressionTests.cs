using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Multi-hand manual-deal progression (human seat 0 + three bots), pinning the SERVER-side
/// contract behind the corrected real-play gate's hand-2 wedge (the banker rotated onto a BOT
/// dealer after a Hu and hand 2 appeared to hang). Two deterministic invariants:
/// <list type="bullet">
///   <item>hand 1's Hu by a bot rotates the banker onto that bot seat, and hand 2's ceremony
///   re-arms — the bot dealer auto-rolls (a human dealer is never auto-played) and, once the
///   human takes only its own batches, the deal is [13,13,14,13];</item>
///   <item>a disconnect mid-ceremony keeps the seat human (never botified / auto-played) and
///   the ceremony resumes to a full deal on reconnect.</item>
/// </list>
/// The human driver acts ONLY on its own seat (take / roll), exactly like the bundle: nothing
/// auto-plays the human, so a stall would mean the runtime failed to schedule an entitled bot.
/// </summary>
[Collection("HumanSeatMultiHandManualProgression")]
public sealed class HumanSeatMultiHandManualProgressionTests
{
    private sealed class RuntimeHarness : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory { get; }
        public IChangshaGameRuntime Runtime { get; }
        private readonly string _tempDb;

        public RuntimeHarness(Action<ChangshaRuntimeOptions> configureOptions)
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
            Directory.CreateDirectory(dataDir);
            _tempDb = Path.Combine(dataDir, $"drake-multihand-{Guid.NewGuid():N}.db");
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

    private static ChangshaGameState Snapshot(IChangshaGameRuntime runtime, string gameId)
    {
        Assert.True(runtime.TryGetSnapshot(gameId, out var state));
        return state!;
    }

    private static string Describe(ChangshaGameState s) =>
        $"hand={s.HandNumber} phase={s.Phase} dealer={s.DealerSeatIndex} active={s.ActiveSeatIndex} "
        + $"pickupSeat={(s.PickupSeatIndex?.ToString() ?? "null")} pickupRound={s.PickupRoundIndex} "
        + $"wall={s.Wall.Count} version={s.StateVersion} claim={(s.ClaimWindow is null ? "none" : "open")} "
        + $"hands=[{string.Join(",", s.Hands.Select(h => h.ConcealedTiles.Count))}] "
        + $"bots=[{string.Join(",", s.Seats.Select(x => x.IsBot ? "B" : "H"))}]";

    /// <summary>Race-free latch capturing the FIRST <see cref="ChangshaPhase.AwaitingDiscard"/>
    /// snapshot of each hand off the runtime's <c>StateChanged</c> stream — polling the live state
    /// misses it because bots discard within milliseconds of the deal finishing.</summary>
    private sealed class DealLatch : IDisposable
    {
        private readonly IChangshaGameRuntime _runtime;
        private readonly string _gameId;
        private readonly Action<string, ChangshaGameState> _handler;
        private readonly Dictionary<int, int[]> _handCountsByHand = new();
        private readonly Dictionary<int, int> _dealerByHand = new();
        private readonly object _gate = new();

        public DealLatch(IChangshaGameRuntime runtime, string gameId)
        {
            _runtime = runtime;
            _gameId = gameId;
            _handler = OnChanged;
            _runtime.StateChanged += _handler;
        }

        private void OnChanged(string gameId, ChangshaGameState snap)
        {
            if (!string.Equals(gameId, _gameId, StringComparison.Ordinal)) return;
            if (snap.Phase != ChangshaPhase.AwaitingDiscard) return;
            lock (_gate)
            {
                if (_handCountsByHand.ContainsKey(snap.HandNumber)) return;
                _handCountsByHand[snap.HandNumber] = snap.Hands.Select(h => h.ConcealedTiles.Count).ToArray();
                _dealerByHand[snap.HandNumber] = snap.DealerSeatIndex;
            }
        }

        public int[]? HandCounts(int handNumber)
        {
            lock (_gate) return _handCountsByHand.TryGetValue(handNumber, out var v) ? v : null;
        }

        public int? Dealer(int handNumber)
        {
            lock (_gate) return _dealerByHand.TryGetValue(handNumber, out var v) ? v : null;
        }

        /// <summary>Bounded wait for the latch to observe <paramref name="handNumber"/>'s deal.</summary>
        public async Task<bool> WaitForAsync(int handNumber, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (HandCounts(handNumber) is not null) return true;
                await Task.Delay(5, ct);
            }
            return false;
        }

        public void Dispose() => _runtime.StateChanged -= _handler;
    }

    /// <summary>Frozen snapshot at the instant the ceremony reaches AwaitingDiscard.</summary>
    private sealed record CeremonyOutcome(
        bool Completed, ChangshaPhase Phase, int Dealer, int[] HandCounts, string Description);

    /// <summary>Drives the human seat's own manual-deal takes (nothing else) while the runtime
    /// schedules the bots; returns the state when the ceremony reaches AwaitingDiscard.</summary>
    private static async Task<CeremonyOutcome> DriveHumanCeremonyAsync(
        IChangshaGameRuntime runtime, string gameId, int humanSeat, TimeSpan timeout,
        List<string> trace, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            var s = Snapshot(runtime, gameId);
            var now = Describe(s);
            if (now != last) { trace.Add(now); last = now; }
            if (s.Phase == ChangshaPhase.AwaitingDiscard)
            {
                return new CeremonyOutcome(true, s.Phase, s.DealerSeatIndex,
                    s.Hands.Select(h => h.ConcealedTiles.Count).ToArray(), now);
            }
            if (ChangshaGameStateMachine.IsPickupPhase(s.Phase) && s.PickupSeatIndex == humanSeat)
            {
                var expected = ChangshaGameStateMachine.ExpectedPickupCount(s.Phase);
                await runtime.TakeTilesFromWallAsync(gameId, humanSeat, expected, ct);
                continue;
            }
            await Task.Delay(5, ct);
        }
        var stuck = Snapshot(runtime, gameId);
        return new CeremonyOutcome(false, stuck.Phase, stuck.DealerSeatIndex,
            stuck.Hands.Select(h => h.ConcealedTiles.Count).ToArray(), Describe(stuck));
    }

    [Fact, Trait("Category", "Acceptance")]
    public async Task Manual_HumanSeat0_ThreeBots_HandTwoWithBotDealer_CompletesCeremony()
    {
        await using var harness = new RuntimeHarness(o =>
        {
            o.BotPickupDelayMs = 5;
            o.BotTurnDelayMs = 1;
            o.BotClaimDelayMs = 1;
            o.ClaimWindowTimeoutMs = 20;
            o.DealBatchDelayMs = 0;
            o.PersistSnapshots = false;
        });
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        const string playerId = "manual-human-player";
        var gameId = await runtime.CreateGameAsync(
            seed: 4100, botSeatIndexes: new[] { 1, 2, 3 },
            hostPlayerId: playerId, hostConnectionId: null, cts.Token, maxHands: 4,
            publicRoom: new($"manual-progress-{Guid.NewGuid():N}", DealMode.Manual, "medium"));
        await runtime.TakeSeatAsync(gameId, playerId, "conn-0", 0, cts.Token);

        var state = Snapshot(runtime, gameId);
        Assert.Equal(DealMode.Manual, state.DealMode);
        Assert.True(state.RequireHandResultAcknowledgements);
        Assert.False(state.Seats[0].IsBot);
        Assert.Equal(0, state.DealerSeatIndex);

        using var latch = new DealLatch(runtime, gameId);

        await runtime.StartGameAsync(gameId, cts.Token);
        Assert.Equal(ChangshaPhase.RollingDice, Snapshot(runtime, gameId).Phase);

        // Human dealer rolls (hand 1).
        var trace1 = new List<string>();
        await runtime.RollDiceAsync(gameId, 0, cts.Token);
        var hand1 = await DriveHumanCeremonyAsync(runtime, gameId, 0, TimeSpan.FromSeconds(20), trace1, cts.Token);
        Assert.True(hand1.Completed,
            "hand 1 ceremony stalled: " + hand1.Description + "\nTRACE:\n" + string.Join("\n", trace1));
        Assert.True(await latch.WaitForAsync(1, TimeSpan.FromSeconds(5), cts.Token),
            "hand 1 deal-complete snapshot never observed: " + hand1.Description);
        Assert.Equal(new[] { 14, 13, 13, 13 }, latch.HandCounts(1));
        Assert.Equal(0, latch.Dealer(1));

        // A conserved pre-draw position: seat 1 discards, then bot 2 really draws its winning tile.
        var s1 = Snapshot(runtime, gameId);
        const int discard = 104;
        var winningTile = _TestHarness.ChangshaTestHelpers.Tid(Suit.Wan, 1, 0);
        var winning = AcceptanceFixture.ThirteenTileWaitingForWan1();
        var used = winning.Append(winningTile).Append(discard).ToHashSet();
        foreach (var hand in s1.Hands)
        {
            hand.ConcealedTiles.Clear();
            hand.Melds.Clear();
        }
        AcceptanceFixture.OverrideHand(s1, 2, winning.ToArray());
        int[] fillerKinds = [0, 1, 2, 3, 5, 6, 7, 8, 9, 14, 15, 16, 17];
        foreach (var hand in s1.Hands.Where(hand => hand.SeatIndex != 2))
        {
            foreach (var kind in fillerKinds)
            {
                var tile = Enumerable.Range(kind * 4, 4).First(tile => !used.Contains(tile));
                Assert.True(used.Add(tile));
                hand.ConcealedTiles.Add(tile);
            }
        }
        s1.Hands[1].ConcealedTiles.Add(discard);
        s1.Wall = new[] { winningTile }.Concat(Enumerable.Range(0, 108).Where(tile => !used.Contains(tile))).ToList();
        s1.WallDrawIndex = s1.WallBackDrawn = 0;
        s1.WallBackIndex = s1.Wall.Count - 1;
        s1.DiscardPile.Clear();
        s1.ClaimWindow = null;
        s1.ActiveSeatIndex = 1;
        Assert.Empty(new ClaimAdjudicator().GetOpportunities(1, discard, s1.Hands));
        Assert.Equal(Enumerable.Range(0, 108),
            s1.Wall.Concat(s1.Hands.SelectMany(hand => hand.ConcealedTiles)).Order());

        var finished = new TaskCompletionSource<ChangshaGameState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void CaptureResult(string id, ChangshaGameState snapshot)
        {
            if (id == gameId && snapshot.Phase == ChangshaPhase.EndHand && snapshot.HandNumber == 1)
                finished.TrySetResult(snapshot);
        }
        runtime.StateChanged += CaptureResult;
        try
        {
            await runtime.DiscardAsync(gameId, 1, discard, cts.Token);
            var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
            Assert.Equal(2, result.CurrentWin!.WinningSeatIndex);
            Assert.Equal(WinMethod.SelfDraw, result.CurrentWin.Method);
            Assert.Equal(winningTile, result.CurrentWin.WinningTileId);
            Assert.Contains(result.EventLog, entry => entry.EventType == "tile-drawn"
                && entry.SeatIndex == 2 && entry.TileId == winningTile);
            var continuation = Assert.IsType<ChangshaHandResultContinuation>(result.HandResultContinuation);
            Assert.Equal(new[] { 0 }, continuation.WaitingSeats(result));
            Assert.Equal(0, result.DealerSeatIndex);
            Assert.Null(latch.HandCounts(2));
            await runtime.AcknowledgeHandResultAsync(gameId, playerId, "conn-0",
                continuation.HandNumber, continuation.ResultToken, cts.Token);
        }
        finally { runtime.StateChanged -= CaptureResult; }

        var afterRotate = Snapshot(runtime, gameId);
        Assert.Equal(2, afterRotate.DealerSeatIndex);
        Assert.Equal(2, afterRotate.HandNumber);

        // Hand 2: dealer is a BOT — the runtime must auto-roll, and the ceremony must
        // complete once the human takes its own batches. No human roll is issued below.
        var trace2 = new List<string>();
        var hand2 = await DriveHumanCeremonyAsync(runtime, gameId, 0, TimeSpan.FromSeconds(30), trace2, cts.Token);
        Assert.True(hand2.Completed, "hand 2 ceremony stalled: " + hand2.Description
            + "\nTRACE:\n" + string.Join("\n", trace2));
        Assert.True(await latch.WaitForAsync(2, TimeSpan.FromSeconds(5), cts.Token),
            "hand 2 deal-complete snapshot never observed: " + hand2.Description);
        Assert.Equal(2, latch.Dealer(2));
        Assert.Equal(new[] { 13, 13, 14, 13 }, latch.HandCounts(2));
    }

    /// <summary>Reconnect must not botify a human seat mid-ceremony nor auto-play its pickup;
    /// after disconnect + reconnect the ceremony resumes on the human's batch and completes.</summary>
    [Fact, Trait("Category", "Acceptance")]
    public async Task Manual_ReconnectMidCeremony_KeepsSeatHuman_AndCeremonyResumes()
    {
        await using var harness = new RuntimeHarness(o =>
        {
            o.BotPickupDelayMs = 5;
            o.BotTurnDelayMs = 1;
            o.BotClaimDelayMs = 1;
            o.ClaimWindowTimeoutMs = 20;
            o.DealBatchDelayMs = 0;
            o.PersistSnapshots = false;
        });
        var runtime = harness.Runtime;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var gameId = await runtime.CreateGameAsync(
            seed: 7316, botSeatIndexes: new[] { 1, 2, 3 },
            hostPlayerId: "human-0", hostConnectionId: null, cts.Token, maxHands: 4);
        await runtime.TakeSeatAsync(gameId, "human-0", "conn-0", 0, cts.Token);
        var created = Snapshot(runtime, gameId);
        created.DealMode = DealMode.Manual;

        using var latch = new DealLatch(runtime, gameId);
        await runtime.StartGameAsync(gameId, cts.Token);
        await runtime.RollDiceAsync(gameId, 0, cts.Token);

        // Wait until the cursor parks on the human's first batch, then drop the socket.
        var parked = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var s = Snapshot(runtime, gameId);
            if (ChangshaGameStateMachine.IsPickupPhase(s.Phase) && s.PickupSeatIndex == 0) { parked = true; break; }
            await Task.Delay(5, cts.Token);
        }
        Assert.True(parked, $"cursor never parked on the human: {Describe(Snapshot(runtime, gameId))}");

        await runtime.HandleDisconnectAsync("human-0", "conn-0", cts.Token);

        // The runtime must NOT auto-play the disconnected human's pickup.
        var beforeIdle = Describe(Snapshot(runtime, gameId));
        await Task.Delay(200, cts.Token);
        var afterIdle = Snapshot(runtime, gameId);
        Assert.False(afterIdle.Seats[0].IsBot, "disconnect must not convert the human seat to a bot.");
        Assert.Equal(0, afterIdle.PickupSeatIndex);
        Assert.Equal(beforeIdle, Describe(afterIdle));

        Assert.True(await runtime.ReconnectAsync(gameId, 0, "human-0", "conn-0b", cts.Token));
        Assert.False(Snapshot(runtime, gameId).Seats[0].IsBot);

        var trace = new List<string>();
        var resumed = await DriveHumanCeremonyAsync(runtime, gameId, 0, TimeSpan.FromSeconds(30), trace, cts.Token);
        Assert.True(resumed.Completed, "ceremony did not resume after reconnect: " + resumed.Description
            + "\nTRACE:\n" + string.Join("\n", trace));
        Assert.True(await latch.WaitForAsync(1, TimeSpan.FromSeconds(5), cts.Token),
            "post-reconnect deal-complete snapshot never observed: " + resumed.Description);
        Assert.Equal(new[] { 14, 13, 13, 13 }, latch.HandCounts(1));
    }
}
