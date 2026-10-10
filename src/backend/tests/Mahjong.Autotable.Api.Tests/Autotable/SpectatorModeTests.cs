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
/// Phase I Wave 4 — spectator-mode WS surface tests (Vasquez).
///
/// <para>Bishop's Phase I Wave 4 backend lift widened the <c>?seat=</c> validation
/// from <c>0..3</c> to <c>-1..3</c>, with <c>seat=-1</c> as the spectator sentinel.
/// Spectator connections receive snapshots and broadcasts but are never routed
/// into a seat slot; their <c>ViewerSeat</c> stays <c>null</c> so the per-viewer
/// privacy filter strips every foreign-seat face. Spectator+<c>botCount=4</c>
/// triggers the all-bots-watch auto-deal flow.</para>
///
/// <para>Each test opens a raw WebSocket against an in-memory
/// <see cref="WebApplicationFactory{TEntryPoint}"/>, sends a JOIN, and inspects
/// the JOINED + UPDATE envelopes. Where the test needs to observe runtime state
/// (auto-deal landing, no-auto-deal staying in Seating), it resolves the bound
/// runtime gameId via <see cref="AutotableConnectionManager.GetRuntimeGameIdBoundTo"/>
/// and reads the snapshot through <see cref="IChangshaGameRuntime.TryGetSnapshot"/>.</para>
/// </summary>
public class SpectatorModeTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-spectator-{Guid.NewGuid():N}.db");

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
    //  1. Spectator connects without taking a seat
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Phase-I-4"), Trait("Wave", "Phase-I-4")]
    public async Task Spectator_ConnectsWithoutSeat()
    {
        await using var session = await OpenAsync(seat: -1, gameId: "SPEC-CONNECT");
        await session.SendJoinAsync("SPEC-CONNECT");

        var joined = await session.ReadEnvelopeAsync();
        Assert.Equal("JOINED", joined.GetProperty("type").GetString());
        Assert.Equal("SPEC-CONNECT", joined.GetProperty("gameId").GetString());
        var spectatorPlayerId = joined.GetProperty("playerId").GetString();
        Assert.False(string.IsNullOrEmpty(spectatorPlayerId));

        var update = await session.ReadEnvelopeAsync();
        Assert.Equal("UPDATE", update.GetProperty("type").GetString());
        Assert.True(update.GetProperty("full").GetBoolean());

        // No runtime is bound for a bare-spectator JOIN (no botCount=4 trigger,
        // no seat-take), so the snapshot ships only the translator's match[0]
        // override — there are no `seats` entries to carry our player id.
        // Walk every entry; assert no `seats` entry references the spectator id.
        var entries = update.GetProperty("entries");
        for (var i = 0; i < entries.GetArrayLength(); i++)
        {
            var entry = entries[i];
            if (entry[0].GetString() != "seats") continue;
            if (entry[2].ValueKind == JsonValueKind.Object &&
                entry[2].TryGetProperty("playerId", out var pid) &&
                pid.ValueKind == JsonValueKind.String)
            {
                Assert.NotEqual(spectatorPlayerId, pid.GetString());
            }
        }

        // Reinforce: the connection manager must not have bound this connection
        // to any seat slot. We can't easily reach the manager's per-seat state,
        // but we can verify the upstream contract that the connection survived
        // JOIN without an early close.
        Assert.Equal(WebSocketState.Open, session.SocketState);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Spectator receives the same full snapshot a seated player would
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Phase-I-4"), Trait("Wave", "Phase-I-4")]
    public async Task Spectator_ReceivesFullSnapshot()
    {
        // Pre-create a runtime game and bind it to a relay gameId so the
        // spectator's snapshot is populated with things/seats/match by the
        // translator (rather than the match-only empty fallback).
        var runtime = _factory!.Services.GetRequiredService<IChangshaGameRuntime>();
        var runtimeGameId = await runtime.CreateGameAsync(
            seed: 31, botSeatIndexes: new[] { 1, 2, 3 }, hostPlayerId: null, hostConnectionId: null);
        await runtime.StartGameAsync(runtimeGameId);
        await Task.Delay(50); // let any deal-batch fanout settle

        var manager = _factory.Services.GetRequiredService<AutotableConnectionManager>();
        manager.BindRuntimeGameForTest("SPEC-SNAP", runtimeGameId);

        await using var session = await OpenAsync(seat: -1, gameId: "SPEC-SNAP");
        await session.SendJoinAsync("SPEC-SNAP");

        var joined = await session.ReadEnvelopeAsync();
        Assert.Equal("JOINED", joined.GetProperty("type").GetString());

        var update = await session.ReadEnvelopeAsync();
        Assert.Equal("UPDATE", update.GetProperty("type").GetString());
        Assert.True(update.GetProperty("full").GetBoolean());

        var entries = update.GetProperty("entries");
        var things = 0; var seats = 0; var matches = 0;
        for (var i = 0; i < entries.GetArrayLength(); i++)
        {
            var kind = entries[i][0].GetString();
            if (kind == "things") things++;
            else if (kind == "seats") seats++;
            else if (kind == "match") matches++;
        }
        Assert.Equal(108, things);
        Assert.Equal(4, seats);
        Assert.Equal(1, matches);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Spectator does not see seat-scoped face data (turn-prompt analog)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Phase-I-4"), Trait("Wave", "Phase-I-4")]
    public async Task Spectator_DoesNotReceiveTurnPrompts()
    {
        // The autotable per-seat "turn prompt" surface is the seat's face-up
        // hand tiles. Bishop's per-viewer privacy filter (line 805) strips
        // `face` from every `things` entry whose slot @seat suffix does not
        // match the viewer seat. A spectator has no viewer seat (null), so
        // EVERY hand-slot tile must come back face-stripped.
        var runtime = _factory!.Services.GetRequiredService<IChangshaGameRuntime>();
        var runtimeGameId = await runtime.CreateGameAsync(
            seed: 47, botSeatIndexes: new[] { 1, 2, 3 }, hostPlayerId: null, hostConnectionId: null);
        await runtime.StartGameAsync(runtimeGameId);
        await Task.Delay(50);

        var manager = _factory.Services.GetRequiredService<AutotableConnectionManager>();
        manager.BindRuntimeGameForTest("SPEC-PRIV", runtimeGameId);

        await using var session = await OpenAsync(seat: -1, gameId: "SPEC-PRIV");
        await session.SendJoinAsync("SPEC-PRIV");

        _ = await session.ReadEnvelopeAsync(); // JOINED
        var update = await session.ReadEnvelopeAsync();
        Assert.Equal("UPDATE", update.GetProperty("type").GetString());

        var entries = update.GetProperty("entries");
        var handThingsSeen = 0;
        var handThingsExposingFace = 0;
        for (var i = 0; i < entries.GetArrayLength(); i++)
        {
            var entry = entries[i];
            if (entry[0].GetString() != "things") continue;
            if (entry[2].ValueKind != JsonValueKind.Object) continue;
            if (!entry[2].TryGetProperty("slotName", out var slotEl) ||
                slotEl.ValueKind != JsonValueKind.String) continue;
            var slot = slotEl.GetString() ?? string.Empty;
            if (!slot.StartsWith("hand.", StringComparison.Ordinal)) continue;

            handThingsSeen++;
            // Privacy filter writes face=null explicitly on stripped entries.
            // If the spectator sees ANY hand slot with a non-null face, the
            // filter regressed for the seat=-1 path.
            if (entry[2].TryGetProperty("face", out var faceEl) &&
                faceEl.ValueKind != JsonValueKind.Null)
            {
                handThingsExposingFace++;
            }
        }
        Assert.True(handThingsSeen > 0,
            "Expected at least one hand-slot `things` entry in the snapshot.");
        Assert.Equal(0, handThingsExposingFace);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Spectator + botCount=4 auto-deals (all-bots-watch mode)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Phase-I-4"), Trait("Wave", "Phase-I-4")]
    public async Task Spectator_With4Bots_AutoDeals()
    {
        await using var session = await OpenAsync(seat: -1, gameId: "AUTODEAL-1", botCount: 4);
        await session.SendJoinAsync("AUTODEAL-1");

        // Consume JOINED + first UPDATE — the WS handler runs the auto-deal
        // *after* the snapshot is sent, so we read those out of the way.
        _ = await session.ReadEnvelopeAsync();
        _ = await session.ReadEnvelopeAsync();

        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();

        // Bounded poll: the spectator auto-deal path binds a runtime game,
        // fills bots, and calls StartGameAsync. The runtime auto-deal path
        // (default DealMode = Auto) lands the game in AwaitingDiscard.
        var bound = await WaitForAsync(() =>
        {
            var rid = manager.GetRuntimeGameIdBoundTo("AUTODEAL-1");
            if (string.IsNullOrEmpty(rid)) return false;
            if (!runtime.TryGetSnapshot(rid, out var s) || s is null) return false;
            return s.Phase != ChangshaPhase.Seating;
        }, timeoutMs: 3000);

        Assert.True(bound,
            "Spectator (?seat=-1&botCount=4) did not trigger the auto-deal flow " +
            "within 3000ms. Bishop's TryAutoDealForSpectatorAsync hook should " +
            "bind the runtime, FillEmptySeatsWithBots, and StartGameAsync.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Spectator + botCount<4 does NOT auto-deal
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Phase-I-4"), Trait("Wave", "Phase-I-4")]
    public async Task Spectator_With3Bots_DoesNotAutoDeal()
    {
        await using var session = await OpenAsync(seat: -1, gameId: "NOAUTO-1", botCount: 3);
        await session.SendJoinAsync("NOAUTO-1");

        _ = await session.ReadEnvelopeAsync(); // JOINED
        _ = await session.ReadEnvelopeAsync(); // UPDATE

        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();

        // Wait a deterministic settle window — Bishop's auto-deal hook runs
        // synchronously inside HandleJoinAsync, so 500ms is more than enough.
        await Task.Delay(500);

        // Either the runtime never got bound (auto-deal short-circuited on the
        // botCount != 4 guard) OR a runtime was bound but its phase is still
        // Seating. Accept either outcome — both satisfy "did not auto-deal".
        var rid = manager.GetRuntimeGameIdBoundTo("NOAUTO-1");
        if (string.IsNullOrEmpty(rid))
        {
            // No runtime binding — the strongest "no auto-deal" signal.
            return;
        }

        Assert.True(runtime.TryGetSnapshot(rid, out var state));
        Assert.NotNull(state);
        Assert.Equal(ChangshaPhase.Seating, state!.Phase);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. seat=0 with botCount=4 — Bishop chose clamp (player cap stays at 3)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Phase-I-4"), Trait("Wave", "Phase-I-4")]
    public async Task Seat0_BotCount_StillCapsAt3()
    {
        // Bishop's diff at AutotableWsEndpoint.cs:208 sets botCountCap = isSpectator ? 4 : 3.
        // A player connection (seat=0) with botCount=4 fails the parsedBotCount<=botCountCap
        // predicate and the field stays at the default 3 — the WS connection itself is NOT
        // closed. The defensive form below accepts either Bishop's clamp-to-3 choice OR a
        // PolicyViolation close: whichever it is, the all-bots-watch auto-deal path must
        // remain spectator-only.
        WebSocketState finalState;
        bool joinSucceeded;
        try
        {
            await using var session = await OpenAsync(seat: 0, gameId: "CAP-CLAMP", botCount: 4);
            await session.SendJoinAsync("CAP-CLAMP");

            try
            {
                var joined = await session.ReadEnvelopeAsync(timeoutMs: 2000);
                joinSucceeded = string.Equals(
                    joined.GetProperty("type").GetString(),
                    "JOINED",
                    StringComparison.Ordinal);
                _ = await session.ReadEnvelopeAsync(timeoutMs: 2000); // UPDATE if any
            }
            catch (WebSocketException)
            {
                // Bishop chose WS close — the read after JOIN throws. Acceptable.
                joinSucceeded = false;
            }
            catch (OperationCanceledException)
            {
                joinSucceeded = false;
            }

            // Give the connection's read-loop a beat in case the close is asynchronous.
            await Task.Delay(100);
            finalState = session.SocketState;
        }
        catch (WebSocketException)
        {
            finalState = WebSocketState.Closed;
            joinSucceeded = false;
        }

        // Either accepted (joinSucceeded == true, socket open / closed cleanly)
        // OR rejected (joinSucceeded == false, socket closed by server).
        // The auto-deal must NOT have fired for a player connection — verified by:
        // either no runtime binding (rejection path) OR the binding's phase still
        // Seating (clamp path: seat 0 is human → no seat-take → no auto-deal).
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();
        var rid = manager.GetRuntimeGameIdBoundTo("CAP-CLAMP");
        if (!string.IsNullOrEmpty(rid))
        {
            Assert.True(runtime.TryGetSnapshot(rid, out var state));
            Assert.NotNull(state);
            Assert.Equal(ChangshaPhase.Seating, state!.Phase);
        }

        // Defensive: surface what we observed for diagnostic purposes.
        Assert.True(
            joinSucceeded || finalState != WebSocketState.Open,
            "Seat 0 with botCount=4 produced an unexpected state: " +
            $"joinSucceeded={joinSucceeded}, finalSocketState={finalState}.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. Phase K (bot-autoplay) — botCount=4 alone (no seat=) auto-promotes
    //     to spectator so the all-bots-watch URL self-plays without needing
    //     the user to remember to add `seat=-1`.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Phase-K-BotAutoplay"), Trait("Wave", "Phase-K-BotAutoplay")]
    public async Task BotCount4_NoSeatParam_PromotesToSpectatorAndAutoDeals()
    {
        // No `seat=` query parameter; just `botCount=4`. The Phase K promote
        // rule in AutotableWsEndpoint.cs should flip the connection to
        // spectator and trigger TryAutoDealForSpectatorAsync exactly like
        // an explicit `?seat=-1&botCount=4` connection.
        await using var session = await OpenWithoutSeatAsync(gameId: "AUTODEAL-PROMOTE", botCount: 4);
        await session.SendJoinAsync("AUTODEAL-PROMOTE");

        _ = await session.ReadEnvelopeAsync(); // JOINED
        _ = await session.ReadEnvelopeAsync(); // UPDATE (full snapshot)

        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();

        var bound = await WaitForAsync(() =>
        {
            var rid = manager.GetRuntimeGameIdBoundTo("AUTODEAL-PROMOTE");
            if (string.IsNullOrEmpty(rid)) return false;
            if (!runtime.TryGetSnapshot(rid, out var s) || s is null) return false;
            return s.Phase != ChangshaPhase.Seating;
        }, timeoutMs: 3000);

        Assert.True(bound,
            "?botCount=4 (no seat=) should auto-promote to spectator and trigger " +
            "the auto-deal flow. Bishop's Phase K promote rule in " +
            "AutotableWsEndpoint.cs:202+ is missing or broken.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────

    private async Task<WsSession> OpenAsync(int seat, string gameId, int? botCount = null)
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var path = $"autotable/ws?seat={seat}&gameId={Uri.EscapeDataString(gameId)}";
        if (botCount.HasValue) path += $"&botCount={botCount.Value}";
        var uri = new Uri(server.BaseAddress, path);
        var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);
        return new WsSession(ws);
    }

    private async Task<WsSession> OpenWithoutSeatAsync(string gameId, int botCount)
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var path = $"autotable/ws?gameId={Uri.EscapeDataString(gameId)}&botCount={botCount}";
        var uri = new Uri(server.BaseAddress, path);
        var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);
        return new WsSession(ws);
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
        public WebSocketState SocketState => _ws.State;
        public WsSession(WebSocket ws) { _ws = ws; }

        public async Task SendJoinAsync(string gameId)
        {
            var msg = JsonSerializer.Serialize(new { type = "JOIN", gameId });
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
