using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Ripley L-10 audit fix — the upstream <c>Player.svelte</c> "Leave" action
/// emits <c>["seats", N, { seat: null }]</c>. Previously the autotable WS
/// handler accepted only <c>JsonValueKind.Number</c> for the inner
/// <c>seat</c> property and returned silently on null, so the
/// <c>nicks[N]</c> entry never cleared and the in-lobby seat counter stuck
/// at full capacity.
///
/// <para>This test pins:</para>
/// <list type="number">
///   <item>After a seat is taken and then released, the runtime clears both
///   <see cref="ChangshaSeatState.PlayerId"/> and the per-tab transport
///   binding so the seat is genuinely free for re-seating.</item>
///   <item>The release path is idempotent — a repeat <c>{seat: null}</c>
///   push from a flaky client is a clean no-op.</item>
///   <item>A different player can immediately take the now-free seat.</item>
/// </list>
/// </summary>
public sealed class LeaveSeatViaNullSeatTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"leaveseat-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.BotClaimDelayMs = 1;
                    o.BotPickupDelayMs = 5;
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

    [Fact, Trait("Category", "Acceptance"), Trait("Audit", "Ripley-L10")]
    public async Task LeaveSeatViaNullPayload_ClearsSeatAndAllowsReseat()
    {
        var gameId = $"leave-seat-{Guid.NewGuid():N}";

        await using var session = await OpenAsync(seat: 0, gameId: gameId, dealMode: "manual", botCount: 0);
        await session.SendJoinAsync(gameId);
        _ = await session.ReadEnvelopeAsync(); // JOINED
        _ = await session.ReadEnvelopeAsync(); // initial UPDATE

        var runtime = _factory!.Services.GetRequiredService<IChangshaGameRuntime>();
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();

        // Take seat 0 — this binds the runtime.
        await session.SendUpdateAsync(new object[]
        {
            new object[] { "seats", 0, new { seat = 0 } }
        });

        var runtimeGameId = await WaitForBindingAsync(manager, gameId, timeoutMs: 3000);
        Assert.NotNull(runtimeGameId);

        Assert.True(await WaitForAsync(() =>
        {
            if (!runtime.TryGetSnapshot(runtimeGameId!, out var s) || s is null) return false;
            return !string.IsNullOrEmpty(s.Seats[0].PlayerId);
        }, timeoutMs: 2000), "seat 0 was not taken");

        // Capture the persistent player id assigned at seat-take so we can confirm
        // it later disappears.
        Assert.True(runtime.TryGetSnapshot(runtimeGameId!, out var seated));
        var takerPlayerId = seated!.Seats[0].PlayerId;
        Assert.False(string.IsNullOrEmpty(takerPlayerId));

        await DrainAsync(session, timeoutMs: 200);

        // The L-10 wire shape: { seat: null } from upstream Player.svelte's
        // "Leave" action. Prior to the fix this returned silently.
        await session.SendUpdateAsync(new object[]
        {
            new object[] { "seats", 0, new { seat = (int?)null } }
        });

        Assert.True(await WaitForAsync(() =>
        {
            if (!runtime.TryGetSnapshot(runtimeGameId!, out var s) || s is null) return false;
            return string.IsNullOrEmpty(s.Seats[0].PlayerId) && !s.Seats[0].IsBot;
        }, timeoutMs: 2000),
            "Ripley L-10 regression: seat 0 was not released after {seat:null} push.");

        // Idempotent: a second release push is a clean no-op (no exception, seat
        // stays empty).
        await session.SendUpdateAsync(new object[]
        {
            new object[] { "seats", 0, new { seat = (int?)null } }
        });
        await Task.Delay(150);
        Assert.True(runtime.TryGetSnapshot(runtimeGameId!, out var afterRepeat));
        Assert.True(string.IsNullOrEmpty(afterRepeat!.Seats[0].PlayerId));

        // A different player on a different connection can now take seat 0.
        await using var rival = await OpenAsync(seat: 0, gameId: gameId, dealMode: "manual", botCount: 0);
        await rival.SendJoinAsync(gameId);
        _ = await rival.ReadEnvelopeAsync(); // JOINED
        _ = await rival.ReadEnvelopeAsync(); // initial UPDATE

        await rival.SendUpdateAsync(new object[]
        {
            new object[] { "seats", 0, new { seat = 0 } }
        });

        Assert.True(await WaitForAsync(() =>
        {
            if (!runtime.TryGetSnapshot(runtimeGameId!, out var s) || s is null) return false;
            return !string.IsNullOrEmpty(s.Seats[0].PlayerId)
                && !string.Equals(s.Seats[0].PlayerId, takerPlayerId, StringComparison.Ordinal);
        }, timeoutMs: 2000),
            "Rival player could not take seat 0 after release — the seat is still bound to the previous occupant.");
    }

    [Fact, Trait("Category", "Acceptance"), Trait("Audit", "Ripley-L10")]
    public async Task ReleaseSeatAsync_RuntimeApi_ClearsSeatInSeatingPhase()
    {
        var runtime = _factory!.Services.GetRequiredService<IChangshaGameRuntime>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var gameId = await runtime.CreateGameAsync(seed: 4242,
            botSeatIndexes: System.Array.Empty<int>(),
            hostPlayerId: "host-1", hostConnectionId: "conn-host", cts.Token);

        const string playerId = "player-bishop";
        const string connectionId = "conn-bishop";
        await runtime.TakeSeatAsync(gameId, playerId, connectionId, seatIndex: 2, cts.Token);

        Assert.True(runtime.TryGetSnapshot(gameId, out var seated));
        Assert.Equal(playerId, seated!.Seats[2].PlayerId);

        await runtime.ReleaseSeatAsync(gameId, playerId, connectionId, cts.Token);

        Assert.True(runtime.TryGetSnapshot(gameId, out var freed));
        Assert.True(string.IsNullOrEmpty(freed!.Seats[2].PlayerId),
            "ReleaseSeatAsync must clear the persistent PlayerId on the seat.");
        Assert.False(freed.Seats[2].IsBot,
            "ReleaseSeatAsync must NOT auto-fill the seat with a bot.");

        // Idempotent: a second release is a clean no-op.
        await runtime.ReleaseSeatAsync(gameId, playerId, connectionId, cts.Token);

        // Mid-hand leave is intentionally a no-op (forfeit/disconnect lanes
        // own that flow). Drive the game out of Seating and confirm the
        // guard fires.
        await runtime.TakeSeatAsync(gameId, "p1", "c1", seatIndex: 0, cts.Token);
        await runtime.FillEmptySeatsWithBotsAsync(gameId, cts.Token);
        await runtime.StartGameAsync(gameId, cts.Token);

        Assert.True(runtime.TryGetSnapshot(gameId, out var midHand));
        var midHandPlayerId = midHand!.Seats[0].PlayerId;
        Assert.NotEqual(ChangshaPhase.Seating, midHand.Phase);

        await runtime.ReleaseSeatAsync(gameId, "p1", "c1", cts.Token);
        Assert.True(runtime.TryGetSnapshot(gameId, out var afterMidLeave));
        Assert.Equal(midHandPlayerId, afterMidLeave!.Seats[0].PlayerId);
    }

    // ── Helpers (mirror DealerExtra test pattern) ───────────────────────

    private async Task<WsSession> OpenAsync(int seat, string gameId, string dealMode, int botCount)
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        // bots=false ⇒ no auto bot-fill, so the seat-take does NOT fill all four seats and
        // therefore BE-3's server-start-on-seat-fill does not fire — this suite exercises the
        // seat lifecycle (leave / idempotent re-leave / reseat) in the Seating phase.
        var path = $"autotable/ws?seat={seat}&gameId={Uri.EscapeDataString(gameId)}&dealMode={dealMode}&botCount={botCount}&bots=false";
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
