using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// UAT backend endpoint contracts (Bishop lane, Ripley §9-12):
///   BE-3 server-driven start on seat-fill (auto deals once, no legacy #deal),
///   BE-2/G18 drop inbound runtime-owned kinds (no store/relay/broadcast) + corrective
///   snapshot to the offender, and BE-5 mutable ViewerSeat bound on TakeSeat.
/// Real in-memory WS against <see cref="Program"/>. RED @200cad4, GREEN after.
/// </summary>
public sealed class BishopUatEndpointContractsTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"uat-endpoint-{Guid.NewGuid():N}.db");

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
                    o.BotPickupDelayMs = 1;
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

    // ── BE-3 — server-driven start on seat-fill (auto deals once; no #deal) ──

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "BE-3")]
    public async Task BE3_AutoMode_SeatFill_ServerAutoDeals_WithoutClientMatch()
    {
        var (session, runtime, rid) = await ConnectAndTakeSeatAsync("seat=0&dealMode=auto&botCount=3");
        await using var _ = session;

        // No client match / Deal is sent. The server must auto-deal on seat-fill.
        var dealt = await WaitForAsync(() =>
            runtime.TryGetSnapshot(rid, out var s) && s is not null
            && s.Phase == ChangshaPhase.AwaitingDiscard
            && s.Hands.Sum(h => h.ConcealedTiles.Count) == 14 + 13 + 13 + 13,
            timeoutMs: 4000);
        Assert.True(dealt, "Auto mode must server-deal on seat-fill without a client #deal / match push.");
    }

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "BE-3")]
    public async Task BE3_AutoMode_SeatFill_IsIdempotent_UnderDuplicateSeatTakes()
    {
        var (session, runtime, rid) = await ConnectAndTakeSeatAsync("seat=0&dealMode=auto&botCount=3");
        await using var _ = session;

        Assert.True(await WaitForAsync(() =>
            runtime.TryGetSnapshot(rid, out var s) && s is not null && s.Phase == ChangshaPhase.AwaitingDiscard,
            timeoutMs: 4000), "precondition: auto-dealt");

        Assert.True(runtime.TryGetSnapshot(rid, out var before) && before is not null);
        var handBefore = before!.Hands.Single(h => h.SeatIndex == 0).ConcealedTiles.ToArray();

        // Duplicate seat-take must NOT re-deal / re-shuffle.
        await session.SendUpdateAsync(new object[] { new object[] { "seats", 0, new { seat = 0 } } });
        await Task.Delay(300);

        Assert.True(runtime.TryGetSnapshot(rid, out var after) && after is not null);
        Assert.Equal(before.Phase, after!.Phase);
        Assert.Equal(handBefore, after.Hands.Single(h => h.SeatIndex == 0).ConcealedTiles.ToArray());
    }

    // ── BE-5 — mutable ViewerSeat bound on TakeSeat (own hand face-up, no reload) ──

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "BE-5")]
    public async Task BE5_UnseatedConnect_ThenTakeSeat_ProjectsOwnHandFaceUp()
    {
        // Connect WITHOUT ?seat= (ViewerSeat=null ⇒ the projection treats every hand as
        // foreign and forces rotationIndex=2 face-down). After Take-seat the server must
        // BIND ViewerSeat to the taken seat and re-project so the owner's own hand renders
        // FACE-UP (rotationIndex != 2) — with no client-side override and no page reload.
        var gameId = $"uat-{Guid.NewGuid():N}";
        var s = await OpenSessionAsync("dealMode=auto&botCount=3", gameId); // no seat= ⇒ unseated
        await using var _sess = s;
        await s.SendJoinAsync(gameId);
        _ = await s.ReadEnvelopeAsync(); // JOINED
        _ = await s.ReadEnvelopeAsync(); // initial UPDATE
        await s.SendUpdateAsync(new object[] { new object[] { "seats", 0, new { seat = 0 } } });

        var ownHandFaceUp = await AnyFrameMatchesAsync(s, 4000, e =>
            e.TryGetProperty("full", out var full) && full.ValueKind == JsonValueKind.True
            && e.TryGetProperty("entries", out var entries)
            && entries.EnumerateArray().Any(t =>
                t.GetArrayLength() >= 3 && t[0].GetString() == "things"
                && t[2].ValueKind == JsonValueKind.Object
                && t[2].TryGetProperty("slotName", out var sn) && sn.ValueKind == JsonValueKind.String
                && sn.GetString()!.StartsWith("hand.", StringComparison.Ordinal)
                && sn.GetString()!.EndsWith("@0", StringComparison.Ordinal)
                && t[2].TryGetProperty("rotationIndex", out var rot)
                && rot.ValueKind == JsonValueKind.Number && rot.GetInt32() != 2));
        Assert.True(ownHandFaceUp, "own hand must be server-projected FACE-UP after take-seat (BE-5).");
    }

    // ── BE-2 / G18 — drop inbound runtime-owned kinds; peer non-observation + correction ──

    [Theory, Trait("Category", "UatBackend"), Trait("Contract", "BE-2")]
    [InlineData("things")]
    [InlineData("match")]
    [InlineData("dice")]
    public async Task BE2_InboundRuntimeOwnedKind_IsDropped_NoPeerBroadcast(string kind)
    {
        var gameId = $"uat-{Guid.NewGuid():N}";
        // Offender: a seated human (auto-deals on seat-fill).
        var a = await OpenSessionAsync("seat=0&dealMode=auto&botCount=3", gameId);
        await using var _a = a;
        await a.SendJoinAsync(gameId);
        _ = await a.ReadEnvelopeAsync(); _ = await a.ReadEnvelopeAsync();
        await a.SendUpdateAsync(new object[] { new object[] { "seats", 0, new { seat = 0 } } });

        // Peer: a spectator observing the same game.
        var b = await OpenSessionAsync("seat=-1", gameId);
        await using var _b = b;
        await b.SendJoinAsync(gameId);
        _ = await b.ReadEnvelopeAsync(); _ = await b.ReadEnvelopeAsync();
        await DrainAsync(a, 400); await DrainAsync(b, 400);

        // Offender pushes a runtime-owned scene entry carrying a SENTINEL the runtime
        // never emits (things keys are tile ids 0..107; match key is 0; dice key is 0).
        const long sentinelKey = 987654321L;
        await a.SendUpdateAsync(new object[]
        {
            new object[] { kind, sentinelKey, new { sentinel = "bishop-be2", slotName = "wall.0.0@0" } }
        });

        // Peer must NEVER observe the client-originated frame (BE-2/G18 peer non-observation).
        var peerSaw = await AnyFrameMatchesAsync(b, 700,
            e => e.TryGetProperty("entries", out var entries)
                 && entries.EnumerateArray().Any(t =>
                        t.GetArrayLength() >= 2
                        && t[0].GetString() == kind
                        && t[1].ValueKind == JsonValueKind.Number
                        && t[1].GetInt64() == sentinelKey));
        Assert.False(peerSaw, $"peer must not receive a client-originated '{kind}' frame (sentinel {sentinelKey}).");
    }

    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "BE-2")]
    public async Task BE2_InboundThingsPush_TriggersCorrectiveFullSnapshot_ToOffender()
    {
        var (session, _, _) = await ConnectAndTakeSeatAsync("seat=0&dealMode=auto&botCount=3");
        await using var _ = session;
        await DrainAsync(session, 500);

        await session.SendUpdateAsync(new object[]
        {
            new object[] { "things", 987654321L, new { sentinel = "bishop-be2", slotName = "wall.0.0@0" } }
        });

        // The offender must receive a corrective FULL authoritative snapshot that overwrites
        // its local scatter (Full=true), and it must NOT echo the sentinel back.
        var gotFullCorrection = await AnyFrameMatchesAsync(session, 1500, e =>
            e.TryGetProperty("full", out var full) && full.ValueKind == JsonValueKind.True
            && e.TryGetProperty("entries", out var entries)
            && entries.EnumerateArray().All(t =>
                   !(t.GetArrayLength() >= 2 && t[0].GetString() == "things"
                     && t[1].ValueKind == JsonValueKind.Number && t[1].GetInt64() == 987654321L)));
        Assert.True(gotFullCorrection, "offender must receive a corrective full snapshot with no sentinel echo.");
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private async Task<WsSession> OpenSessionAsync(string query, string gameId)
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var path = $"autotable/ws?{query}&gameId={Uri.EscapeDataString(gameId)}";
        var ws = await wsClient.ConnectAsync(new Uri(server.BaseAddress, path), CancellationToken.None);
        return new WsSession(ws);
    }

    private static async Task DrainAsync(WsSession session, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try { _ = await session.ReadEnvelopeAsync(60); }
            catch { return; }
        }
    }

    private static async Task<bool> AnyFrameMatchesAsync(WsSession session, int timeoutMs, Func<JsonElement, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            JsonElement env;
            try { env = await session.ReadEnvelopeAsync(120); }
            catch { continue; }
            if (env.TryGetProperty("type", out var t) && t.GetString() == "UPDATE" && predicate(env))
                return true;
        }
        return false;
    }

    private async Task<(WsSession Session, IChangshaGameRuntime Runtime, string RuntimeGameId)>
        ConnectAndTakeSeatAsync(string query)
    {
        var gameId = $"uat-{Guid.NewGuid():N}";
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var path = $"autotable/ws?{query}&gameId={Uri.EscapeDataString(gameId)}";
        var ws = await wsClient.ConnectAsync(new Uri(server.BaseAddress, path), CancellationToken.None);
        var session = new WsSession(ws);

        await session.SendJoinAsync(gameId);
        _ = await session.ReadEnvelopeAsync(); // JOINED
        _ = await session.ReadEnvelopeAsync(); // initial UPDATE

        await session.SendUpdateAsync(new object[] { new object[] { "seats", 0, new { seat = 0 } } });

        var manager = _factory.Services.GetRequiredService<AutotableConnectionManager>();
        var runtime = _factory.Services.GetRequiredService<IChangshaGameRuntime>();
        var rid = await WaitForBindingAsync(manager, gameId, 3000);
        Assert.NotNull(rid);
        return (session, runtime, rid!);
    }

    private static async Task<string?> WaitForBindingAsync(AutotableConnectionManager manager, string relayGameId, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var rid = manager.GetRuntimeGameIdBoundTo(relayGameId);
            if (rid is not null) return rid;
            await Task.Delay(20);
        }
        return manager.GetRuntimeGameIdBoundTo(relayGameId);
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(20);
        }
        return predicate();
    }

    internal sealed class WsSession : IAsyncDisposable
    {
        private readonly WebSocket _ws;
        public WebSocketState SocketState => _ws.State;
        public WsSession(WebSocket ws) { _ws = ws; }

        public async Task SendJoinAsync(string gameId)
        {
            var msg = JsonSerializer.Serialize(new { type = "JOIN", gameId });
            await _ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task SendUpdateAsync(object[] entries)
        {
            var msg = JsonSerializer.Serialize(new { type = "UPDATE", entries, full = false });
            await _ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, CancellationToken.None);
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
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
                catch { }
            }
            _ws.Dispose();
        }
    }
}
