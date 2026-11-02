using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// WS-level progression regression: drives the SAME wire commands the shipped bundle emits
/// (seat take, pickup take) over the real <c>/autotable/ws</c> endpoint while reading the
/// AUTHORITATIVE runtime snapshot out of DI. Pins the invariant that a human NON-dealer on
/// seat 1 with a BOT dealer auto-starts the hand-1 ceremony (the bot dealer auto-rolls; no
/// human roll is sent) and, once the human takes only its own batches, deals [14,13,13,13].
/// </summary>
public sealed class WsDrivenManualProgressionRegressionTests(ITestOutputHelper output)
{
    private static string NewRelayGameId() => $"drake-wsprobe-{Guid.NewGuid():N}";

    private sealed class Harness : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory { get; }
        public IChangshaGameRuntime Runtime { get; }
        private readonly string _tempDb;

        public Harness(Action<ChangshaRuntimeOptions> configure)
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
            Directory.CreateDirectory(dataDir);
            _tempDb = Path.Combine(dataDir, $"drake-wsprobe-{Guid.NewGuid():N}.db");
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
                b.ConfigureServices(s => s.Configure(configure));
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

    private static string Describe(ChangshaGameState s) =>
        $"hand={s.HandNumber} phase={s.Phase} dealer={s.DealerSeatIndex} active={s.ActiveSeatIndex} "
        + $"pickupSeat={(s.PickupSeatIndex?.ToString() ?? "null")} pickupRound={s.PickupRoundIndex} "
        + $"wall={s.Wall.Count} version={s.StateVersion} "
        + $"claim={(s.ClaimWindow is null ? "none" : string.Join("/", s.ClaimWindow.Opportunities.Select(o => $"{o.SeatIndex}:{o.ClaimType}")))} "
        + $"hands=[{string.Join(",", s.Hands.Select(h => h.ConcealedTiles.Count))}] "
        + $"bots=[{string.Join(",", s.Seats.Select(x => x.IsBot ? "B" : "H"))}] "
        + $"complete={s.IsGameComplete}";

    /// <summary>
    /// Hudson vs Vasquez conflict: human NON-dealer (seat 1) with a BOT dealer (seat 0)
    /// on HAND 1. The runtime must auto-roll for the bot dealer and drive the ceremony to
    /// a playable 13-tile human hand without any human dice roll.
    /// </summary>
    [Fact, Trait("Category", "Regression")]
    public async Task WsDriven_HumanNonDealer_BotDealer_Hand1_CeremonyStarts()
    {
        await using var harness = new Harness(o =>
        {
            o.BotTurnDelayMs = 5;
            o.BotClaimDelayMs = 5;
            o.BotPickupDelayMs = 5;
            o.ClaimWindowTimeoutMs = 150;
            o.DealBatchDelayMs = 0;
            o.PersistSnapshots = false;
        });
        var runtime = harness.Runtime;
        var server = harness.Factory.Server;
        var relayGameId = NewRelayGameId();

        var wsClient = server.CreateWebSocketClient();
        var uri = new Uri(server.BaseAddress,
            $"autotable/ws?variant=changsha&dealMode=manual&botCount=3&botDifficulty=Hard"
            + $"&handCount=4&seat=1&seed=4100&gameId={relayGameId}");
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);
        var reader = Task.Run(async () =>
        {
            var buffer = new byte[256 * 1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { }
        });

        async Task SendAsync(object payload)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        await SendAsync(new { type = "JOIN", gameId = relayGameId });
        await Task.Delay(200);
        await SendAsync(new
        {
            type = "UPDATE",
            entries = new object[] { new object[] { "seats", "drake-player", new { seat = 1 } } }
        });

        var manager = harness.Factory.Services.GetRequiredService<AutotableConnectionManager>();
        string? runtimeGameId = null;
        for (var i = 0; i < 100 && runtimeGameId is null; i++)
        {
            runtimeGameId = manager.GetRuntimeGameIdBoundTo(relayGameId);
            if (runtimeGameId is null) await Task.Delay(50);
        }
        Assert.NotNull(runtimeGameId);

        ChangshaGameState Snap()
        {
            Assert.True(runtime.TryGetSnapshot(runtimeGameId!, out var s) && s is not null);
            return s!;
        }

        // Dealer must be a bot and the human must own seat 1. Wait for the auto seat-fill
        // + auto-start to land (the WS endpoint fills bots then starts the game).
        ChangshaGameState start = Snap();
        for (var i = 0; i < 200; i++)
        {
            start = Snap();
            if (start.Phase != ChangshaPhase.Seating && start.Seats.Count(x => x.IsBot) == 3) break;
            await Task.Delay(50);
        }
        Assert.False(start.Seats[1].IsBot, $"seat 1 must be the human: {Describe(start)}");
        Assert.True(start.Seats[start.DealerSeatIndex].IsBot,
            $"expected a bot dealer, got {Describe(start)}");

        // The bot dealer must auto-roll: NO human roll is sent below.
        // Race-free latch: StateChanged fires once per mutation with a snapshot frozen at that
        // instant, so we capture hand 1's FIRST AwaitingDiscard even though the bot dealer
        // discards milliseconds later.
        int[]? dealtCounts = null;
        var dealtDealer = -1;
        var latchGate = new object();
        void OnChanged(string gid, ChangshaGameState snap)
        {
            if (!string.Equals(gid, runtimeGameId, StringComparison.Ordinal)) return;
            if (snap.Phase != ChangshaPhase.AwaitingDiscard || snap.HandNumber != 1) return;
            lock (latchGate)
            {
                if (dealtCounts is not null) return;
                dealtCounts = snap.Hands.Select(h => h.ConcealedTiles.Count).ToArray();
                dealtDealer = snap.DealerSeatIndex;
            }
        }
        runtime.StateChanged += OnChanged;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        var trace = new List<string>();
        var lastDesc = string.Empty;
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                var s = Snap();
                var desc = Describe(s);
                if (desc != lastDesc) { trace.Add(desc); lastDesc = desc; }
                if (s.Phase == ChangshaPhase.AwaitingDiscard) break;

                if (ChangshaGameStateMachine.IsPickupPhase(s.Phase) && s.PickupSeatIndex == 1)
                {
                    var count = ChangshaGameStateMachine.ExpectedPickupCount(s.Phase);
                    await SendAsync(new
                    {
                        type = "UPDATE",
                        entries = new object[] { new object[] { "pickup", "take", new { seatIndex = 1, count } } }
                    });
                    await Task.Delay(30);
                    continue;
                }
                await Task.Delay(10);
            }

            // TryGetSnapshot is lock-free, so the poll above can observe the mutated live state a
            // few microseconds before StateChanged fires. Wait (bounded) for the latch to catch up.
            var latchDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < latchDeadline)
            {
                lock (latchGate) { if (dealtCounts is not null) break; }
                await Task.Delay(5);
            }
        }
        finally { runtime.StateChanged -= OnChanged; }

        foreach (var line in trace.TakeLast(40)) output.WriteLine(line);
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        await reader.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(dealtCounts);
        // Deterministic (seed 4100): the bot dealer sits on seat 0 and draws its 14th; the human
        // non-dealer (seat 1) and the other two bots hold 13 — the human never rolled the dice
        // and only took its own four batches. Full trace is dumped above on failure.
        Assert.Equal(0, dealtDealer);
        Assert.Equal(new[] { 14, 13, 13, 13 }, dealtCounts);
    }
}
