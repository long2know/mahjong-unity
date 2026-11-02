using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Tests.Changsha._TestHarness;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Vasquez integration audit A2/A3 — the dealer's first discard after a
/// manual-deal DealerExtra take landed authoritatively in the runtime
/// (move-log echoed it, <see cref="ChangshaGameState.DiscardPile"/> contained
/// it) but the WS broadcast snapshot omitted it, so the local
/// <c>world.things</c> map never grew a <c>discard.*@0</c> entry. The drift
/// surfaced as <c>dealerPileGrew = false</c> immediately after the discard
/// and a partial <c>discardBySeat = [1,2,1,0]</c> after a round-robin.
///
/// <para>Root cause: the <see cref="AutotableConnectionManager"/> broadcast
/// pipeline read state via <see cref="IChangshaGameRuntime.TryGetSnapshot"/>,
/// which returns the LIVE <see cref="ChangshaGameInstance.State"/> reference.
/// Subsequent post-discard runtime work
/// (<c>DriveAfterAdvanceAsync → DrawTile</c> mutating <c>state.Wall</c> and
/// the next seat's <c>ConcealedTiles</c>; bot scheduler racing to discard
/// next) ran on a worker thread while the translator iterated those same
/// <c>List&lt;T&gt;</c> collections without holding the instance lock,
/// producing a torn snapshot.</para>
///
/// <para>Fix: <see cref="IChangshaGameRuntime.TryGetSnapshotCopyAsync"/>
/// returns a JSON deep clone produced under the instance lock — the
/// translator iterates an isolated graph, broadcasts are deterministic.</para>
///
/// <para>This test pins the wire contract: after the dealer's discard, at
/// least one subsequent WS UPDATE envelope MUST contain a
/// <c>discard.{row}.{col}@0</c> things entry whose key matches the discarded
/// tile id.</para>
/// </summary>
public sealed class DealerDiscardBroadcastAuditA2Tests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"discardbcast-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    // Tight bot timing to provoke the race aggressively — the
                    // bot's discard fires immediately after the dealer's so
                    // multiple broadcasts pile up on the wire.
                    o.BotTurnDelayMs = 1;
                    o.BotClaimDelayMs = 1;
                    o.BotPickupDelayMs = 25;
                    o.ClaimWindowTimeoutMs = 50;
                    o.DealBatchDelayMs = 0;
                    o.PersistSnapshots = false;
                });
            });
        });
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try { if (_tempDb is not null && File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
        return Task.CompletedTask;
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Audit", "Vasquez-A2-A3")]
    public async Task DealerDiscardWsBroadcast_LandsTileIdInDiscardSlot()
    {
        var gameId = $"discard-broadcast-{Guid.NewGuid():N}";
        await using var session = await OpenAsync(seat: 0, gameId: gameId, dealMode: "manual");
        await session.SendJoinAsync(gameId);
        _ = await session.ReadEnvelopeAsync(); // JOINED
        _ = await session.ReadEnvelopeAsync(); // initial UPDATE

        await session.SendUpdateAsync(new object[]
        {
            new object[] { "seats", 0, new { seat = 0 } }
        });

        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();
        var runtimeGameId = await WaitForBindingAsync(manager, gameId, timeoutMs: 3000);
        Assert.NotNull(runtimeGameId);

        await DrainAsync(session, timeoutMs: 200);

        await session.SendUpdateAsync(new object[]
        {
            new object[] { "match", 0, new { dealCommand = "start" } }
        });
        Assert.True(await WaitForAsync(() =>
        {
            if (!runtime.TryGetSnapshot(runtimeGameId!, out var s) || s is null) return false;
            return s.DealMode == DealMode.Manual && s.Phase == ChangshaPhase.RollingDice;
        }, timeoutMs: 3000), "manual deal did not reach RollingDice");

        await DrainAsync(session, timeoutMs: 150);

        await session.SendUpdateAsync(new object[]
        {
            new object[] { "pickup", "rollDice", new { seatIndex = 0 } }
        });
        Assert.True(await WaitForAsync(() =>
        {
            if (!runtime.TryGetSnapshot(runtimeGameId!, out var s) || s is null) return false;
            return s.Phase == ChangshaPhase.BreakPointMarked
                || s.Phase == ChangshaPhase.PickupRound1;
        }, timeoutMs: 3000), "dice roll did not park runtime at BreakPointMarked");

        await DriveTakeAsync(session, runtime, runtimeGameId!, count: 4,
            untilPhase: ChangshaPhase.PickupRound2);
        await DriveTakeAsync(session, runtime, runtimeGameId!, count: 4,
            untilPhase: ChangshaPhase.PickupRound3);
        await DriveTakeAsync(session, runtime, runtimeGameId!, count: 4,
            untilPhase: ChangshaPhase.SingleTilePickup);
        await DriveTakeAsync(session, runtime, runtimeGameId!, count: 1,
            untilPhase: ChangshaPhase.DealerExtra);
        await DriveTakeAsync(session, runtime, runtimeGameId!, count: 1,
            untilPhase: ChangshaPhase.AwaitingDiscard);

        Assert.True(runtime.TryGetSnapshot(runtimeGameId!, out var ready));
        var dealerHand = ready!.Hands.Single(h => h.SeatIndex == 0);
        Assert.Equal(14, dealerHand.ConcealedTiles.Count);
        var discardTileId = dealerHand.ConcealedTiles[^1];

        await DrainAsync(session, timeoutMs: 200);

        // The wire push the bundle emits on a dealer tile click.
        await session.SendUpdateAsync(new object[]
        {
            new object[] { "discard", 0, new { tileId = discardTileId } }
        });

        // Now drain WS envelopes for up to ~3s and watch for at least one
        // UPDATE whose `things` payload contains the discard tile in a
        // `discard.{row}.{col}@0` slot. This is the contract the audit pins.
        var sawDiscardOnWire = false;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && !sawDiscardOnWire)
        {
            JsonElement env;
            try { env = await session.ReadEnvelopeAsync(timeoutMs: 250); }
            catch { continue; }

            if (!env.TryGetProperty("type", out var typeEl)) continue;
            if (typeEl.GetString() != "UPDATE") continue;
            if (!env.TryGetProperty("entries", out var entriesEl)) continue;
            if (entriesEl.ValueKind != JsonValueKind.Array) continue;

            foreach (var entry in entriesEl.EnumerateArray())
            {
                // Entry shape: ["things", <tileId>, { slotName: "discard.0.0@0", ... }]
                if (entry.ValueKind != JsonValueKind.Array) continue;
                if (entry.GetArrayLength() < 3) continue;
                var kind = entry[0];
                if (kind.ValueKind != JsonValueKind.String) continue;
                if (kind.GetString() != "things") continue;

                var keyEl = entry[1];
                if (keyEl.ValueKind != JsonValueKind.Number) continue;
                if (!keyEl.TryGetInt32(out var tileKey)) continue;
                if (tileKey != discardTileId) continue;

                var valueEl = entry[2];
                if (valueEl.ValueKind != JsonValueKind.Object) continue;
                if (!valueEl.TryGetProperty("slotName", out var slotNameEl)) continue;
                if (slotNameEl.ValueKind != JsonValueKind.String) continue;
                var slotName = slotNameEl.GetString() ?? "";
                if (!slotName.StartsWith("discard.", StringComparison.Ordinal)) continue;
                if (!slotName.EndsWith("@0", StringComparison.Ordinal)) continue;

                sawDiscardOnWire = true;
                break;
            }
        }

        // Final sanity: the runtime DID accept the discard (rules out a
        // state-machine regression masquerading as a broadcast bug).
        // Assert the PERMANENT `tile-discarded` event on a lock-protected deep copy, not the mutable
        // DiscardPile: after the dealer's discard the turn passes to bot seat 1 whose worker thread
        // both appends to DiscardPile (racing live enumeration → "Collection was modified") and may
        // legitimately CLAIM this discard (removing it from the pile → "Filter not matched"). The
        // tile-discarded event is appended once and never removed, so it is the stable proof that
        // the discard reached the runtime. Same root-cause family as the #133 DealerExtra fix.
        var afterDiscard = await runtime.TryGetSnapshotCopyAsync(runtimeGameId!);
        Assert.NotNull(afterDiscard);
        Assert.Contains(afterDiscard!.EventLog,
            e => e.EventType == "tile-discarded" && e.SeatIndex == 0 && e.TileId == discardTileId);

        Assert.True(sawDiscardOnWire,
            "Vasquez A2/A3 regression: dealer's discard reached the runtime but " +
            "the WS broadcast never surfaced the tile in a `discard.*@0` slot. " +
            "Translator likely raced concurrent runtime mutations (DrawTile / bot " +
            "discard) on lock-free List<T> iteration.");
    }

    // ── Helpers (mirror DealerExtra test pattern) ───────────────────────

    private async Task DriveTakeAsync(
        WsSession session,
        IChangshaGameRuntime runtime,
        string runtimeGameId,
        int count,
        ChangshaPhase untilPhase)
    {
        await session.SendUpdateAsync(new object[]
        {
            new object[] { "pickup", "take", new { seatIndex = 0, count } }
        });

        // #142: seat 0 (human dealer) took via WS above; the bot seats' pickups are
        // normally auto-scheduled by the runtime on a Task.Delay chain that STARVES
        // under loaded full-suite/SqlServer CI, wedging the deal at this pickup round
        // (0 discards). Deterministically drive the bot takes via the same production
        // pickup API so the round advances on observable progress, not the thread pool.
        await ManualDealPickupDriver.DriveBotPickupsToPhaseAsync(
            runtime, runtimeGameId, untilPhase, humanSeat: 0);

        var advanced = await WaitForAsync(() =>
        {
            if (!runtime.TryGetSnapshot(runtimeGameId, out var s) || s is null) return false;
            return s.Phase == untilPhase;
        }, timeoutMs: 4000);

        Assert.True(advanced, $"runtime did not reach {untilPhase} after take({count})");
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    private async Task<WsSession> OpenAsync(int seat, string gameId, string dealMode)
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var path = $"autotable/ws?seat={seat}&gameId={Uri.EscapeDataString(gameId)}&dealMode={dealMode}&botCount=3";
        var uri = new Uri(server.BaseAddress, path);
        var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);
        return new WsSession(ws);
    }

    private static async Task<string?> WaitForBindingAsync(
        AutotableConnectionManager manager, string relayGameId, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var rid = manager.GetRuntimeGameIdBoundTo(relayGameId);
            if (rid is not null) return rid;
            await Task.Delay(25);
        }
        return manager.GetRuntimeGameIdBoundTo(relayGameId);
    }

    private static async Task DrainAsync(WsSession session, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try { _ = await session.ReadEnvelopeAsync(timeoutMs: 50); }
            catch { return; }
        }
    }

    private sealed class WsSession : IAsyncDisposable
    {
        private readonly WebSocket _ws;
        public WsSession(WebSocket ws) { _ws = ws; }

        public async Task SendJoinAsync(string gameId)
        {
            var msg = JsonSerializer.Serialize(new { type = "JOIN", gameId });
            var bytes = Encoding.UTF8.GetBytes(msg);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task SendUpdateAsync(object[] entries)
        {
            var msg = JsonSerializer.Serialize(new { type = "UPDATE", entries, full = false });
            var bytes = Encoding.UTF8.GetBytes(msg);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task<JsonElement> ReadEnvelopeAsync(int timeoutMs = 5000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var buffer = new byte[64 * 1024];
            var sb = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buffer, cts.Token);
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);
            return JsonDocument.Parse(sb.ToString()).RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            if (_ws.State == WebSocketState.Open)
            {
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None); }
                catch { }
            }
            _ws.Dispose();
        }
    }
}
