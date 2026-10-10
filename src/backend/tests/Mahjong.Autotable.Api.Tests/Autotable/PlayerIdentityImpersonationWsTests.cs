using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// Burke — end-to-end replay of the identity-impersonation blocker over the real
/// <c>/autotable/ws</c> endpoint.
///
/// <para><b>The exploit (as live-confirmed by Frost).</b> A durable <c>playerId</c> is PUBLIC —
/// it is broadcast in the <c>seats</c>/<c>nicks</c> wire keys. The attacker read a victim's id,
/// set it as their own <c>mahjong_pid</c> cookie, connected, and was bound to the victim's
/// durable identity. The reconnect owner-inference path
/// (<c>TryGetSeatForPlayer(gameId, connection.PlayerId)</c>) then projected the victim's REAL
/// concealed hand to the attacker and let the attacker act on the victim's seat. Zero
/// rejections.</para>
///
/// <para>These facts pin the fix at the identity boundary — the attacker's forged cookie no
/// longer resolves to the victim's identity, so the (unchanged, correct) seat authorization
/// never sees an attacker connection wearing the victim's id. The assertions deliberately read
/// AUTHORITATIVE runtime state rather than any particular rejection message, so they coexist
/// with the endpoint-authorization diff without depending on its wire shape.</para>
/// </summary>
public sealed class PlayerIdentityImpersonationWsTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    private IChangshaGameRuntime Runtime => _factory!.Services.GetRequiredService<IChangshaGameRuntime>();
    private AutotableConnectionManager Manager => _factory!.Services.GetRequiredService<AutotableConnectionManager>();
    private PlayerIdentityService Identity => _factory!.Services.GetRequiredService<PlayerIdentityService>();

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"identity-ws-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s => s.Configure<ChangshaRuntimeOptions>(o =>
            {
                o.BotTurnDelayMs = 1;
                o.BotClaimDelayMs = 1;
                o.BotPickupDelayMs = 1;
                o.ClaimWindowTimeoutMs = 50;
                o.DealBatchDelayMs = 0;
                o.PersistSnapshots = false;
            }));
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

    // ── 1. forged RAW cookie: no identity, no hand, no seat authority ───────────

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-impersonation")]
    public async Task ForgedRawPlayerIdCookie_CannotInheritIdentity_HandOrSeatAuthority()
    {
        const string relayGameId = "IDENTITY-IMPERSONATE-1";
        var (runtimeGameId, victimPublicId) = await SeatVictimAndDealAsync(relayGameId, seed: 41);
        var beforeAttack = await SnapshotAsync(runtimeGameId);

        // Attacker replays the victim's PUBLIC id verbatim as a raw cookie.
        await using var attacker = await OpenAsync(relayGameId, rawCookieValue: victimPublicId);
        var attackerJoined = await attacker.JoinAndReadAsync(relayGameId);
        var attackerPlayerId = attackerJoined.GetProperty("playerId").GetString();

        // (a) identity — the forged cookie bought a brand-new identity, not the victim's.
        Assert.NotEqual(victimPublicId, attackerPlayerId);
        Assert.False(string.IsNullOrEmpty(attackerPlayerId));
        Assert.Null(Runtime.TryGetSeatForPlayer(runtimeGameId, attackerPlayerId!));
        Assert.Equal(0, Runtime.TryGetSeatForPlayer(runtimeGameId, victimPublicId));

        // (b) privacy — the victim's concealed hand stays opaque to the attacker.
        var attackerView = await attacker.SettleAsync();
        var leakedTiles = LeakedRealIds(attackerView, "@0");
        Assert.True(leakedTiles.Count == 0,
            "Impersonating connection received REAL tile ids for the victim's concealed hand: "
            + string.Join(", ", leakedTiles.Take(10)));

        // (c) authority — a seat-0 action from the attacker does not move authoritative state.
        await attacker.SendClaimAsync(seatIndex: 0, action: "pass");
        await attacker.SendClaimAsync(seatIndex: 0, action: "claim");
        await Task.Delay(600);

        var afterAttack = await SnapshotAsync(runtimeGameId);
        Assert.Equal(beforeAttack.StateVersion, afterAttack.StateVersion);
        Assert.Equal(victimPublicId, afterAttack.Seats[0].PlayerId);
        Assert.Equal(0, Runtime.TryGetSeatForPlayer(runtimeGameId, victimPublicId));
    }

    // ── 2. tampered SIGNED cookie is no better than a raw one ───────────────────

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-impersonation")]
    public async Task TamperedCredential_OverWebSocket_YieldsAFreshIdentity_NotTheVictims()
    {
        const string relayGameId = "IDENTITY-IMPERSONATE-2";
        var (runtimeGameId, victimPublicId) = await SeatVictimAndDealAsync(relayGameId, seed: 42);

        // The attacker gets hold of the token SHAPE and flips one MAC character.
        var parts = Identity.Protect(victimPublicId).Split('.');
        var mac = parts[3].ToCharArray();
        mac[^1] = mac[^1] == 'A' ? 'B' : 'A';
        var tampered = string.Join('.', parts[0], parts[1], parts[2], new string(mac));

        await using var attacker = await OpenAsync(relayGameId, rawCookieValue: tampered);
        var joined = await attacker.JoinAndReadAsync(relayGameId);

        Assert.NotEqual(victimPublicId, joined.GetProperty("playerId").GetString());
        Assert.Equal(0, Runtime.TryGetSeatForPlayer(runtimeGameId, victimPublicId));
        Assert.Empty(LeakedRealIds(await attacker.SettleAsync(), "@0"));
    }

    // ── 3. positive control: the real owner still reconnects cleanly ────────────

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-reconnect")]
    public async Task LegitimateOwner_ReconnectsWithTheSameCredential_AndRegainsItsOwnHand()
    {
        const string relayGameId = "IDENTITY-RECONNECT-1";
        var (runtimeGameId, ownerPublicId) = await SeatVictimAndDealAsync(relayGameId, seed: 43);

        // Brand-new socket, same signed credential, NO ?seat= hint.
        await using var second = await OpenAsync(relayGameId, signedPlayerId: ownerPublicId);
        var joined = await second.JoinAndReadAsync(relayGameId);
        Assert.Equal(ownerPublicId, joined.GetProperty("playerId").GetString());

        var view = await second.SettleAsync();
        Assert.True(CountRealIds(view, "@0") > 0,
            "The reconnecting owner must re-bind its own hand through runtime-confirmed ownership.");
        Assert.Equal(0, Runtime.TryGetSeatForPlayer(runtimeGameId, ownerPublicId));
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-reconnect")]
    public async Task CookielessConnection_GetsAFreshSignedCredential_NotAGuessableOne()
    {
        const string relayGameId = "IDENTITY-FRESH-1";

        await using var first = await OpenAsync(relayGameId);
        await using var second = await OpenAsync(relayGameId);

        var a = (await first.JoinAndReadAsync(relayGameId)).GetProperty("playerId").GetString();
        var b = (await second.JoinAndReadAsync(relayGameId)).GetProperty("playerId").GetString();

        Assert.False(string.IsNullOrEmpty(a));
        Assert.NotEqual(a, b);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a dealt table (bots at 1/2/3, seat 0 open), then seats the victim through the real
    /// WS endpoint with a SIGNED credential and drops the transport. Returns the runtime game id
    /// and the victim's PUBLIC player id — the value an attacker can read off the wire.
    /// </summary>
    private async Task<(string RuntimeGameId, string VictimPublicId)> SeatVictimAndDealAsync(
        string relayGameId, int seed)
    {
        var runtimeGameId = await Runtime.CreateGameAsync(
            seed: seed, botSeatIndexes: new[] { 1, 2, 3 }, hostPlayerId: null, hostConnectionId: null);
        await Runtime.StartGameAsync(runtimeGameId);
        Assert.True(await WaitForAsync(() => Runtime.TryGetSnapshot(runtimeGameId, out var s) && s is not null
            && s.Phase != ChangshaPhase.Seating, 10000), "The table must reach a dealt phase.");
        Manager.BindRuntimeGameForTest(relayGameId, runtimeGameId);

        var victimPublicId = "victim-" + Guid.NewGuid().ToString("N");
        await using (var victim = await OpenAsync(relayGameId, signedPlayerId: victimPublicId))
        {
            var joined = await victim.JoinAndReadAsync(relayGameId);
            Assert.Equal(victimPublicId, joined.GetProperty("playerId").GetString());
            await victim.TakeSeatAsync(0);

            var ownView = await victim.SettleAsync();
            Assert.True(CountRealIds(ownView, "@0") > 0,
                "The legitimate owner must see its own concealed hand as real tile ids.");
        }

        Assert.True(await WaitForAsync(
            () => Runtime.TryGetSeatForPlayer(runtimeGameId, victimPublicId) == 0, 8000),
            "The seat stays bound to the durable player id across a transport drop.");
        return (runtimeGameId, victimPublicId);
    }

    private async Task<ChangshaGameState> SnapshotAsync(string runtimeGameId)
    {
        await Task.Yield();
        Assert.True(Runtime.TryGetSnapshot(runtimeGameId, out var state) && state is not null);
        return state!;
    }

    /// <summary>Real tile ids (numeric keys in [0,107]) leaking for a concealed-hand slot suffix.</summary>
    private static IReadOnlyList<string> LeakedRealIds(JsonElement update, string seatSuffix) =>
        RealIdEntries(update, seatSuffix).ToList();

    private static int CountRealIds(JsonElement update, string seatSuffix) =>
        RealIdEntries(update, seatSuffix).Count();

    private static IEnumerable<string> RealIdEntries(JsonElement update, string seatSuffix)
    {
        var entries = update.GetProperty("entries");
        for (var i = 0; i < entries.GetArrayLength(); i++)
        {
            var entry = entries[i];
            if (entry[0].GetString() != "things") continue;
            if (entry[2].ValueKind != JsonValueKind.Object) continue;
            if (!entry[2].TryGetProperty("slotName", out var slotEl) || slotEl.ValueKind != JsonValueKind.String) continue;
            var slot = slotEl.GetString() ?? string.Empty;
            if (!slot.StartsWith("hand.", StringComparison.Ordinal)) continue;
            if (!slot.EndsWith(seatSuffix, StringComparison.Ordinal)) continue;

            var keyEl = entry[1];
            long numeric = -1;
            if (keyEl.ValueKind == JsonValueKind.Number && keyEl.TryGetInt64(out var n)) numeric = n;
            else if (keyEl.ValueKind == JsonValueKind.String
                     && long.TryParse(keyEl.GetString(), out var parsed)) numeric = parsed;
            if (numeric is >= 0 and <= 107) yield return $"{slot}=#{numeric}";
        }
    }

    private async Task<WsSession> OpenAsync(
        string gameId,
        string? signedPlayerId = null,
        string? rawCookieValue = null)
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var cookieValue = signedPlayerId is not null ? Identity.Protect(signedPlayerId) : rawCookieValue;
        if (cookieValue is not null)
        {
            wsClient.ConfigureRequest = req =>
                req.Headers["Cookie"] = $"{PlayerIdentityService.CookieName}={cookieValue}";
        }
        var uri = new Uri(server.BaseAddress,
            $"autotable/ws?bots=false&gameId={Uri.EscapeDataString(gameId)}");
        return new WsSession(await wsClient.ConnectAsync(uri, CancellationToken.None));
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

    private sealed class WsSession : IAsyncDisposable
    {
        private readonly WebSocket _ws;
        public WsSession(WebSocket ws) { _ws = ws; }

        private JsonElement? _lastFull;

        /// <summary>
        /// Sends JOIN, returns the JOINED envelope (which carries the resolved playerId), and
        /// settles on the initial full snapshot so <see cref="SettleAsync"/> can hand it back.
        /// </summary>
        public async Task<JsonElement> JoinAndReadAsync(string gameId)
        {
            await SendRawAsync(JsonSerializer.Serialize(new { type = "JOIN", gameId }));
            var joined = await ReadEnvelopeAsync();
            await SettleAsync();
            return joined;
        }

        /// <summary>
        /// Drains until the socket is quiet and returns the LAST full snapshot seen on this
        /// session (covers the reconnect re-projection that follows the initial push). A passive
        /// viewer that receives no further snapshot keeps the one it already has.
        /// </summary>
        public async Task<JsonElement> SettleAsync(int quietMs = 800, int hardTimeoutMs = 15000)
        {
            var hardDeadline = DateTime.UtcNow.AddMilliseconds(hardTimeoutMs);
            while (DateTime.UtcNow < hardDeadline)
            {
                // Until a full snapshot has been seen, wait patiently (a loaded CI box can take
                // seconds); once one is in hand, only drain what arrives inside a quiet window.
                var budget = _lastFull is null
                    ? (int)Math.Max(quietMs, (hardDeadline - DateTime.UtcNow).TotalMilliseconds)
                    : quietMs;
                try { _ = await ReadEnvelopeAsync(budget); }
                catch (OperationCanceledException) { break; }
            }
            if (_lastFull is null) throw new TimeoutException("No full UPDATE snapshot received.");
            return _lastFull.Value;
        }

        public Task TakeSeatAsync(int seatIndex) =>
            SendUpdateAsync(new object[] { "seats", "me", new { seat = seatIndex } });

        public Task SendClaimAsync(int seatIndex, string action) =>
            SendUpdateAsync(new object[] { "claim", seatIndex, new { action, type = (string?)null } });

        /// <summary>Each argument is ONE collection entry (<c>[kind, key, value]</c>).</summary>
        public Task SendUpdateAsync(params object[][] entries) =>
            SendRawAsync(JsonSerializer.Serialize(new { type = "UPDATE", entries, full = false }));

        private async Task SendRawAsync(string json) =>
            await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

        private async Task<JsonElement> ReadEnvelopeAsync(int timeoutMs = 5000)
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

            var env = JsonDocument.Parse(sb.ToString()).RootElement.Clone();
            if (env.TryGetProperty("type", out var t) && t.GetString() == "UPDATE"
                && env.TryGetProperty("full", out var f) && f.ValueKind == JsonValueKind.True)
            {
                _lastFull = env;
            }
            return env;
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
