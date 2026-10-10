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

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// Phase J Wave 1 — hot-seat swap WS surface tests (Vasquez).
///
/// <para>Hicks's J-1 UI (commit <c>781798e</c>) adds a "Move" button to the
/// sidebar HUD that lets a connected player swap seats — or jump to/from
/// spectator — via a soft reconnect cycle: rewrite <c>?seat=</c> on the URL,
/// disconnect, the existing auto-reconnect picks up the new seat off
/// <c>buildWsUrl()</c> after <c>RECONNECT_DELAY</c>. There is no new backend
/// surface in this wave (Hicks's diff is frontend-only — confirmed in
/// <c>.squad/decisions/inbox/hicks-phase-j-wave-1.md</c>); the swap leans on
/// the existing Phase I Wave 4 spectator widening (<c>?seat=-1</c>) and the
/// Phase F seat-take/runtime-binding pipe.</para>
///
/// <para><b>What these tests pin:</b> the backend's runtime binding survives
/// across the disconnect/reconnect cycle the bundle uses, so the second
/// connection lands on the SAME <see cref="ChangshaGameState"/> and can take
/// a different seat without losing match progress. We pin three swap
/// transitions:
/// <list type="bullet">
///   <item>player → player (different seat): runtime binding survives, new
///     connection's seat-take succeeds for any unheld seat.</item>
///   <item>player → spectator: new spectator connection joins the same
///     gameId, snapshot reflects the still-bound runtime state, and the
///     spectator does NOT take any seat (its <c>ViewerSeat</c> is null).</item>
///   <item>spectator → player: new player connection joins the same gameId
///     (which already has a runtime binding from the spectator's snapshot
///     fetch — or creates one fresh — but in any case the bundle's
///     subsequent seats-take routes to a free seat without error).</item>
/// </list>
/// </para>
///
/// <para><b>Phase J Wave 2 update re: "frees seat" semantics.</b> The autotable WS
/// path's <c>HandleDisconnectAsync</c> now calls
/// <c>ChangshaGameRuntime.HandleDisconnectAsync</c> (mirror of the SignalR Hub
/// path), so a WS-disconnected player's <c>SeatConnections</c> entry is
/// promptly released and other connections can claim the seat — including the
/// post-take <c>FillEmptySeatsWithBotsAsync</c> flow which drops a bot into
/// the freed slot. Note that <c>seat.PlayerId</c> is preserved by the runtime
/// release so the Wave 1 reconnect-by-seat-index flow still works, but a
/// subsequent seat-take + auto-bot-fill cycle will overwrite that PlayerId
/// with a bot id. The "PlayerToSpectator" fact below remains unchanged
/// (the spectator does not trigger auto-bot-fill, so the released seat stays
/// pinned to Alice's PlayerId for the duration of the spectator session).</para>
/// </summary>
public class HotSeatSwapTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-hotseat-{Guid.NewGuid():N}.db");

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

    // ────────────────────────────────────────────────────────────────────
    //  1. Player → Player: a fresh connection lands on the same runtime
    //     game and can claim a different seat.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Phase-J-1"), Trait("Wave", "Phase-J-1")]
    public async Task HotSeatSwap_PlayerToPlayer_PreservesGameState()
    {
        const string gameId = "HOTSEAT-PP";
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();

        // ws#1 joins as seat 0 and explicitly takes seat 0 via the bundle's
        // seats UPDATE (this is what Hicks's Player.svelte does on click).
        var alice = await OpenAndJoinAsync(seat: 0, gameId: gameId, botCount: 0);
        await alice.TakeSeatAsync(0);

        // Wait for the runtime binding + state.Seats[0] to reflect Alice.
        var aliceSeated = await WaitForAsync(() =>
        {
            var rid = manager.GetRuntimeGameIdBoundTo(gameId);
            if (string.IsNullOrEmpty(rid)) return false;
            if (!runtime.TryGetSnapshot(rid, out var s) || s is null) return false;
            return string.Equals(s.Seats[0].PlayerId, alice.PlayerId, StringComparison.Ordinal)
                && !s.Seats[0].IsBot;
        }, timeoutMs: 2000);
        Assert.True(aliceSeated,
            "Alice's seat-take should bind state.Seats[0].PlayerId to her connectionId.");

        var runtimeGameId = manager.GetRuntimeGameIdBoundTo(gameId)!;
        Assert.True(runtime.TryGetSnapshot(runtimeGameId, out var before));
        Assert.Equal(ChangshaPhase.Seating, before!.Phase);
        Assert.DoesNotContain(before.Seats, seat => seat.IsBot);
        var originalSeed = before.Seed;
        var originalScores = before.CumulativeScores.OrderBy(pair => pair.Key).ToArray();
        var originalConnection = Mahjong.Autotable.Api.Tests.TestInfrastructure.ViewerAuthorityAssertions
            .GrantedConnection(manager, runtime, runtimeGameId, 0);
        var originalConnectionId = originalConnection.Id.ToString("N");

        // Alice disconnects (the bundle's soft-reconnect closes the old WS).
        await alice.DisposeAsync();
        Assert.True(await WaitForAsync(
            () => runtime.TryGetSeatForConnection(runtimeGameId, originalConnectionId) is null,
            timeoutMs: 2000), "The old transport must release its actual seat grant.");

        // ws#2 joins the SAME gameId with seat=1 (Hicks's picker disables the
        // current seat, so the new connection never tries to take seat 0).
        await using var bob = await OpenAndJoinAsync(seat: 1, gameId: gameId, botCount: 3);
        await bob.TakeSeatAsync(1);

        // The runtime binding must be the SAME instance — Bob's snapshot is
        // backed by Alice's game state, not a freshly-created one.
        var sameRuntime = string.Equals(
            manager.GetRuntimeGameIdBoundTo(gameId), runtimeGameId, StringComparison.Ordinal);
        Assert.True(sameRuntime,
            "Hot-seat swap must preserve the runtime binding for the relay gameId; " +
            $"expected {runtimeGameId} but saw {manager.GetRuntimeGameIdBoundTo(gameId)}.");

        // A late joiner's bot preference cannot change this zero-bot room.
        var bobSeated = await WaitForAsync(() =>
        {
            if (!runtime.TryGetSnapshot(runtimeGameId, out var s) || s is null) return false;
            return string.Equals(s.Seats[1].PlayerId, bob.PlayerId, StringComparison.Ordinal)
                && !s.Seats[1].IsBot;
        }, timeoutMs: 2000);
        Assert.True(bobSeated,
            "Bob must own seat 1 in the original Seating room without filling its other seats.");

        Assert.True(runtime.TryGetSnapshot(runtimeGameId, out var finalState));
        Assert.NotNull(finalState);
        Assert.Equal(ChangshaPhase.Seating, finalState!.Phase);
        Assert.DoesNotContain(finalState.Seats, seat => seat.IsBot);
        Assert.Equal(originalSeed, finalState.Seed);
        Assert.Equal(originalScores, finalState.CumulativeScores.OrderBy(pair => pair.Key).ToArray());
        Assert.Null(runtime.TryGetSeatForConnection(runtimeGameId, originalConnectionId));
        var replacement = Mahjong.Autotable.Api.Tests.TestInfrastructure.ViewerAuthorityAssertions
            .GrantedConnection(manager, runtime, runtimeGameId, 1);
        Assert.NotEqual(originalConnection.Id, replacement.Id);
        Assert.Equal(1, runtime.TryGetSeatForConnection(runtimeGameId, replacement.Id.ToString("N")));
        Assert.False(runtime.AreAllSeatsOccupied(runtimeGameId));
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Player → Spectator: spectator joins the same gameId, sees the
    //     prior runtime state, and does NOT take any seat.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Phase-J-1"), Trait("Wave", "Phase-J-1")]
    public async Task HotSeatSwap_PlayerToSpectator_DoesNotClaimSeat()
    {
        const string gameId = "HOTSEAT-PS";
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();

        // ws#1 takes seat 0 as a player.
        var alice = await OpenAndJoinAsync(seat: 0, gameId: gameId);
        await alice.TakeSeatAsync(0);

        var aliceSeated = await WaitForAsync(() =>
        {
            var rid = manager.GetRuntimeGameIdBoundTo(gameId);
            return !string.IsNullOrEmpty(rid)
                && runtime.TryGetSnapshot(rid!, out var s) && s is not null
                && string.Equals(s.Seats[0].PlayerId, alice.PlayerId, StringComparison.Ordinal);
        }, timeoutMs: 2000);
        Assert.True(aliceSeated, "Alice's seat-0 take should be observable in runtime state.");

        var runtimeGameId = manager.GetRuntimeGameIdBoundTo(gameId)!;
        await alice.DisposeAsync();

        // ws#2 joins as spectator (seat=-1). The bundle's spectator path does
        // NOT send a seats UPDATE — spectators never take seats.
        await using var watcher = await OpenAndJoinAsync(seat: -1, gameId: gameId);

        // Spectator's first UPDATE snapshot must surface the prior game state
        // (runtime is still bound to the same runtimeGameId — autotable WS
        // doesn't tear down runtime bindings on disconnect).
        Assert.Equal(runtimeGameId, manager.GetRuntimeGameIdBoundTo(gameId));

        // Crucially: the spectator connection has no ViewerSeat, so any seat
        // entry the snapshot carries references the prior player (Alice) —
        // never the spectator. Walk the snapshot's seats and verify the
        // spectator's playerId never appears as a seat occupant.
        var snapshot = watcher.LastSnapshot!.Value;
        var entries = snapshot.GetProperty("entries");
        for (var i = 0; i < entries.GetArrayLength(); i++)
        {
            var entry = entries[i];
            if (entry[0].GetString() != "seats") continue;
            if (entry[2].ValueKind == JsonValueKind.Object &&
                entry[2].TryGetProperty("playerId", out var pid) &&
                pid.ValueKind == JsonValueKind.String)
            {
                Assert.NotEqual(watcher.PlayerId, pid.GetString());
            }
        }

        // And the runtime snapshot must have no SeatConnections entry whose
        // playerId is the spectator's. (Alice's seat-0 binding survives —
        // documented in the class-level docstring as the current contract.)
        Assert.True(runtime.TryGetSnapshot(runtimeGameId, out var state));
        Assert.NotNull(state);
        for (var seatIndex = 0; seatIndex < 4; seatIndex++)
        {
            Assert.NotEqual(watcher.PlayerId, state!.Seats[seatIndex].PlayerId);
        }
        Assert.Equal(alice.PlayerId, state!.Seats[0].PlayerId);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Spectator → Player: a previously-spectator user reconnects with
    //     a real seat and the seat-take succeeds.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Phase-J-1"), Trait("Wave", "Phase-J-1")]
    public async Task HotSeatSwap_SpectatorToPlayer_BindsSeat()
    {
        const string gameId = "HOTSEAT-SP";
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();

        // ws#1 joins as spectator (no seat-take).
        var watcher = await OpenAndJoinAsync(seat: -1, gameId: gameId);

        // A bare spectator JOIN does NOT auto-bind a runtime game (the
        // translator just ships the match[0] override fallback). The
        // EnsureRuntimeBoundAsync path only fires on seat-take, claim, etc.
        // So we expect no runtime binding here yet — record whether one
        // exists so we can confirm the eventual binding is consistent.
        var preBindingExisted = !string.IsNullOrEmpty(manager.GetRuntimeGameIdBoundTo(gameId));

        await watcher.DisposeAsync();

        // ws#2 joins the same gameId as seat=2 and takes seat 2.
        await using var bob = await OpenAndJoinAsync(seat: 2, gameId: gameId);
        await bob.TakeSeatAsync(2);

        var seatedAt2 = await WaitForAsync(() =>
        {
            var rid = manager.GetRuntimeGameIdBoundTo(gameId);
            if (string.IsNullOrEmpty(rid)) return false;
            if (!runtime.TryGetSnapshot(rid!, out var s) || s is null) return false;
            return string.Equals(s.Seats[2].PlayerId, bob.PlayerId, StringComparison.Ordinal)
                && !s.Seats[2].IsBot;
        }, timeoutMs: 2000);
        Assert.True(seatedAt2,
            "Spectator → seat 2 swap should bind state.Seats[2] to Bob's connectionId.");

        // If a runtime binding existed during Watcher's spectator session,
        // Bob's seat-take should reuse it (same gameId → same runtime). If
        // none existed, Bob's seat-take freshly created one. Either way the
        // current binding is the only runtime game associated with this
        // relay gameId, and Bob is correctly seated in it.
        var runtimeGameId = manager.GetRuntimeGameIdBoundTo(gameId);
        Assert.False(string.IsNullOrEmpty(runtimeGameId));
        Assert.True(runtime.TryGetSnapshot(runtimeGameId!, out var state));
        Assert.NotNull(state);

        // Seat 2 holds Bob. No other seat carries the spectator's playerId.
        Assert.Equal(bob.PlayerId, state!.Seats[2].PlayerId);
        for (var seatIndex = 0; seatIndex < 4; seatIndex++)
        {
            Assert.NotEqual(watcher.PlayerId, state.Seats[seatIndex].PlayerId);
        }

        // Diagnostic preservation of the pre-swap binding fact for traceability.
        // (No assertion: bundle behaviour is "spectator binds nothing", but the
        // backend might in future eagerly bind on JOIN — both are consistent
        // with the seat-2 outcome.)
        _ = preBindingExisted;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────

    private async Task<WsSession> OpenAndJoinAsync(int seat, string gameId, int? botCount = null)
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var path = $"autotable/ws?seat={seat}&gameId={Uri.EscapeDataString(gameId)}";
        if (botCount is not null) path += $"&botCount={botCount.Value}";
        var uri = new Uri(server.BaseAddress, path);
        var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);
        var session = new WsSession(ws);
        await session.SendJoinAsync(gameId);
        var joined = await session.ReadEnvelopeAsync();
        Assert.Equal("JOINED", joined.GetProperty("type").GetString());
        session.PlayerId = joined.GetProperty("playerId").GetString()
            ?? throw new InvalidOperationException("JOINED envelope missing playerId.");
        var snapshot = await session.ReadEnvelopeAsync();
        Assert.Equal("UPDATE", snapshot.GetProperty("type").GetString());
        session.LastSnapshot = snapshot;
        return session;
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }
        return false;
    }

    private sealed class WsSession : IAsyncDisposable
    {
        private readonly WebSocket _ws;
        public string PlayerId { get; set; } = string.Empty;
        public JsonElement? LastSnapshot { get; set; }
        public WebSocketState SocketState => _ws.State;

        public WsSession(WebSocket ws) { _ws = ws; }

        public async Task SendJoinAsync(string gameId)
        {
            var msg = JsonSerializer.Serialize(new { type = "JOIN", gameId });
            var bytes = Encoding.UTF8.GetBytes(msg);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task TakeSeatAsync(int seatIndex)
        {
            // Wire shape: { type: "UPDATE", entries: [["seats", <key>, {seat: N}]] }
            // Key mirrors upstream's per-player keyed seat entry (we use the
            // player's connection id as a string key to match Player.svelte).
            using var ms = new MemoryStream();
            await using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "UPDATE");
                writer.WritePropertyName("entries");
                writer.WriteStartArray();
                writer.WriteStartArray();
                writer.WriteStringValue("seats");
                writer.WriteStringValue(PlayerId);
                writer.WriteStartObject();
                writer.WriteNumber("seat", seatIndex);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndArray();
                writer.WriteBoolean("full", false);
                writer.WriteEndObject();
            }
            var payload = Encoding.UTF8.GetString(ms.ToArray());
            var bytes = Encoding.UTF8.GetBytes(payload);
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
