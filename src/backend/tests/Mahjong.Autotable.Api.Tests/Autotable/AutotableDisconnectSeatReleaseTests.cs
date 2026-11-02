using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// Phase J Wave 2 — autotable disconnect seat-release tests (Vasquez).
///
/// <para>Bishop's Phase J Wave 2 task pins the bug surfaced in the Wave 1
/// hot-seat swap memo: <see cref="AutotableConnectionManager.HandleConnectionAsync"/>
/// terminates a connection via <c>HandleDisconnectAsync</c> which clears the
/// per-game relay store entries but does NOT call
/// <see cref="IChangshaGameRuntime.HandleDisconnectAsync"/>, so the runtime's
/// <c>SeatConnections</c> binding for that connection survives orphaned.
/// Effect: a fresh WS connection cannot retake the seat the disconnected
/// player held, even though the bundle UI presents the seat as "available".</para>
///
/// <para><b>What these tests pin</b> (post-fix):
/// <list type="bullet">
///   <item>An active-seat WS close releases the runtime seat binding so a new
///     connection can take that exact seat.</item>
///   <item>A spectator (<c>?seat=-1</c>) close performs no runtime mutation —
///     no exception, no spurious bot fill, no disturbance to other seat
///     bindings.</item>
///   <item>A disconnect → reconnect on the same seat by a fresh connection
///     ends with the seat bound to the new connection.</item>
/// </list>
/// </para>
///
/// <para><b>Observability strategy.</b> The runtime's <c>SeatConnections</c>
/// dictionary is not exposed on <see cref="IChangshaGameRuntime"/>, so we
/// observe seat release indirectly: after a disconnect, an independent
/// <see cref="IChangshaGameRuntime.TakeSeatAsync"/> call with a fresh
/// connectionId must succeed (current behaviour throws "Seat N is already
/// taken"). The <see cref="ChangshaGameState.Seats"/> binding's
/// <c>PlayerId</c> is observed via <see cref="IChangshaGameRuntime.TryGetSnapshot"/>
/// to confirm the new connection now owns the seat.</para>
/// </summary>
public class AutotableDisconnectSeatReleaseTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-disconnect-{Guid.NewGuid():N}.db");

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
    //  1. Active-seat disconnect releases the runtime binding
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Phase-J-2"), Trait("Wave", "Phase-J-2")]
    public async Task Disconnect_OfActiveSeat_ReleasesRuntimeBinding()
    {
        await using var fixture = new LobbyRepairFixture();
        var alice = await fixture.PlayerAsync();
        var room = await fixture.CreateRoomAsync(alice, 0);
        var runtime = fixture.Runtime;
        var before = await fixture.StateAsync(room);
        Assert.False(before.IsPublic);
        Assert.Equal(ChangshaPhase.Seating, before.Phase);
        Assert.DoesNotContain(before.Seats, seat => seat.IsBot);
        var original = ViewerAuthorityAssertions.GrantedConnection(fixture.Manager, runtime, room.RuntimeId, 0);
        var oldConnectionId = original.Id.ToString("N");
        Assert.Equal(0, runtime.TryGetSeatForConnection(room.RuntimeId, oldConnectionId));
        Assert.Equal(alice.Id, before.Seats[0].PlayerId);
        await room.Creator.DisposeAsync();
        Assert.True(await WaitForAsync(() => runtime.TryGetSeatForConnection(room.RuntimeId, oldConnectionId) is null,
            timeoutMs: 3000), "The disconnected exact transport must lose its runtime binding.");
        Assert.True(runtime.TryGetSnapshot(room.RuntimeId, out var released));
        Assert.Equal(ChangshaPhase.Seating, released!.Phase);
        Assert.Equal(room.RuntimeId, fixture.Manager.GetRuntimeGameIdBoundTo(room.Alias));

        var replacement = await fixture.PlayerAsync();
        Assert.NotEqual(alice.Id, replacement.Id);
        var replacementSocket = await fixture.JoinAsync(replacement, room);
        var granted = ViewerAuthorityAssertions.GrantedConnection(fixture.Manager, runtime, room.RuntimeId, 0);
        Assert.NotEqual(original.Id, granted.Id);
        Assert.Equal(0, runtime.TryGetSeatForConnection(room.RuntimeId, granted.Id.ToString("N")));
        Assert.Null(runtime.TryGetSeatForConnection(room.RuntimeId, oldConnectionId));
        var postState = await fixture.StateAsync(room);
        Assert.Equal(replacement.Id, postState.Seats[0].PlayerId);
        Assert.False(postState.Seats[0].IsBot);
        Assert.Equal(ChangshaPhase.Seating, postState.Phase);
        Assert.DoesNotContain(postState.Seats, seat => seat.IsBot);
        ViewerAuthorityAssertions.Expect(replacementSocket.Frames.First(frame => LobbyRepairFixture.Type(frame) == "JOINED"),
            room.Alias, 0);
        Assert.Equal(room.RuntimeId, fixture.Manager.GetRuntimeGameIdBoundTo(room.Alias));
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Spectator disconnect is a no-op
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Phase-J-2"), Trait("Wave", "Phase-J-2")]
    public async Task Disconnect_OfSpectator_IsNoOp()
    {
        const string gameId = "DISCONNECT-SPECTATOR";
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();

        // Alice claims seat 0 first so we have a known-bound seat to verify
        // is left undisturbed by the spectator's later disconnect.
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

        // Capture the pre-disconnect snapshot of seat 0 to compare later.
        Assert.True(runtime.TryGetSnapshot(runtimeGameId, out var preState));
        var preAlicePlayerId = preState!.Seats[0].PlayerId;
        var preAliceIsBot = preState.Seats[0].IsBot;

        // Watcher joins as spectator (?seat=-1). Spectators never bind a seat
        // and never call TakeSeatAsync; the bundle's spectator path omits the
        // seats UPDATE entirely.
        var watcher = await OpenAndJoinAsync(seat: -1, gameId: gameId);

        // Force-close the spectator's socket. Under Bishop's Wave 2 fix the
        // backend should fast-path the disconnect (no seat-release attempt
        // because no seat was ever bound), throw nothing, and leave the
        // runtime state untouched.
        await watcher.DisposeAsync();

        // Give the disconnect handler enough time to run. We intentionally
        // probe AFTER a delay rather than waiting for a release signal —
        // there shouldn't be one.
        await Task.Delay(250);

        // Alice's seat-0 binding survives the spectator's close, byte-for-byte.
        Assert.True(runtime.TryGetSnapshot(runtimeGameId, out var postState));
        Assert.NotNull(postState);
        Assert.Equal(preAlicePlayerId, postState!.Seats[0].PlayerId);
        Assert.Equal(preAliceIsBot, postState.Seats[0].IsBot);
        Assert.Equal(alice.PlayerId, postState.Seats[0].PlayerId);

        // The spectator's playerId never appears in any seat slot.
        for (var i = 0; i < 4; i++)
            Assert.NotEqual(watcher.PlayerId, postState.Seats[i].PlayerId);

        // No spurious bot fill — the other 3 seats are still in whatever state
        // Alice's seat-take + AutoBotFill left them. We don't assert a specific
        // shape (bot vs empty) because the AutoBotFill behaviour is not the
        // contract under test; we just assert seat-0 is undisturbed.

        // Sanity that Alice's WS is still alive — the spectator's disconnect
        // must not have triggered a cascading teardown.
        Assert.Equal(WebSocketState.Open, alice.SocketState);

        await alice.DisposeAsync();
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Disconnect → reconnect on same seat rebinds
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Phase-J-2"), Trait("Wave", "Phase-J-2")]
    public async Task Disconnect_ThenReconnect_SameSeat_Rebinds()
    {
        await using var fixture = new LobbyRepairFixture();
        var owner = await fixture.PlayerAsync();
        var room = await fixture.CreateRoomAsync(owner, 3, dealMode: "auto");
        var runtime = fixture.Runtime;
        Assert.True(await WaitForAsync(() => runtime.TryGetSnapshot(room.RuntimeId, out var state)
            && state!.Phase == ChangshaPhase.AwaitingDiscard && state.ActiveSeatIndex == 0, timeoutMs: 2000));
        var before = await fixture.StateAsync(room);
        Assert.Equal(14, before.Hands[0].ConcealedTiles.Count);
        var botSeatIndexes = before.Seats.Where(seat => seat.IsBot)
            .Select(seat => seat.SeatIndex).Order().ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, botSeatIndexes);
        var original = ViewerAuthorityAssertions.GrantedConnection(fixture.Manager, runtime, room.RuntimeId, 0);
        var originalId = original.Id.ToString("N");
        await room.Creator.DisposeAsync();
        Assert.True(await WaitForAsync(() => runtime.TryGetSeatForConnection(room.RuntimeId, originalId) is null,
            timeoutMs: 3000));

        var stranger = await fixture.PlayerAsync();
        Assert.NotEqual(owner.Id, stranger.Id);
        var rejectedSocket = await fixture.SocketAsync(stranger, room.Alias, "join=1&seat=0");
        var rejected = await rejectedSocket.WaitAsync(frame => LobbyRepairFixture.Entries(frame)
            .Any(entry => entry[0].GetString() == "actionRejected"));
        var denial = Assert.Single(LobbyRepairFixture.Entries(rejected),
            entry => entry[0].GetString() == "actionRejected");
        Assert.Equal("join", denial[2].GetProperty("action").GetString());
        Assert.Equal("room-not-seating", denial[2].GetProperty("reason").GetString());
        ViewerAuthorityAssertions.Expect(rejected, null, null);
        Assert.Equal(owner.Id, (await fixture.StateAsync(room)).Seats[0].PlayerId);

        var resumed = await fixture.JoinAsync(owner, room, "&seat=3&botCount=0");
        var current = ViewerAuthorityAssertions.GrantedConnection(fixture.Manager, runtime, room.RuntimeId, 0);
        Assert.NotEqual(original.Id, current.Id);
        Assert.Equal(0, runtime.TryGetSeatForConnection(room.RuntimeId, current.Id.ToString("N")));
        Assert.Null(runtime.TryGetSeatForConnection(room.RuntimeId, originalId));
        var joined = resumed.Frames.First(frame => LobbyRepairFixture.Type(frame) == "JOINED");
        Assert.Equal(owner.Id, joined.GetProperty("playerId").GetString());
        ViewerAuthorityAssertions.Expect(joined, room.Alias, 0);
        var full = resumed.Frames.Last(LobbyRepairFixture.IsFull);
        var hand = LobbyRepairFixture.Entries(full).Where(entry => entry[0].GetString() == "things"
            && entry[2].ValueKind == JsonValueKind.Object && entry[2].TryGetProperty("slotName", out var slot)
            && slot.GetString()!.StartsWith("hand.", StringComparison.Ordinal)
            && slot.GetString()!.EndsWith("@0", StringComparison.Ordinal)).ToArray();
        Assert.Equal(before.Hands[0].ConcealedTiles.Order(), hand.Select(entry => entry[1].GetInt32()).Order());
        Assert.All(hand, entry => Assert.Equal(1, entry[2].GetProperty("rotationIndex").GetInt32()));
        Assert.Equal(room.RuntimeId, fixture.Manager.GetRuntimeGameIdBoundTo(room.Alias));
        var tileId = before.Hands[0].ConcealedTiles[0];
        var resumedState = await fixture.StateAsync(room);
        Assert.Equal(botSeatIndexes, resumedState.Seats.Where(seat => seat.IsBot)
            .Select(seat => seat.SeatIndex).Order().ToArray());
        await resumed.UpdateAsync(["discard", 0, new { tileId }]);
        Assert.True(await WaitForAsync(() => runtime.TryGetSnapshot(room.RuntimeId, out var state)
            && state!.StateVersion > resumedState.StateVersion
            && state.DiscardPile.Any(discard => discard.SeatIndex == 0 && discard.TileId == tileId), timeoutMs: 3000));
    }

    // ────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────

    private async Task<WsSession> OpenAndJoinAsync(int seat, string gameId)
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var path = $"autotable/ws?seat={seat}&gameId={Uri.EscapeDataString(gameId)}";
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
