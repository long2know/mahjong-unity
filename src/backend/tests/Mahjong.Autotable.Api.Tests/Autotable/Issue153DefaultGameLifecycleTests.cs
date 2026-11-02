using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// #153 — backend default-game lifecycle contract (real WebSocket, no state injection / test hooks).
///
/// <para>Hudson's independent live diagnosis (issue #153, comment 5096546486) proved the Changsha
/// engine and bot scheduler are healthy: a fresh game deals 14/13/13/13, bots take their own pickups
/// and turns, and hands roll over. The reproducible backend defect is <b>persistence/attach UX</b>:
/// the persistent <see cref="AutotableWsEndpoint.DefaultGameId"/> ("changsha-default") — the fallback
/// binding for a bare <c>?variant=changsha</c> connection that carries no explicit gameId — is reused
/// forever. A fresh browser opening the bare URL after a previous match was dealt / stalled / left
/// silently JOINs that leftover game (seat 0 still owned by the departed player, hand frozen
/// mid-ceremony) → the user's "restart → Take Seat → nothing progresses" report.</para>
///
/// <para>The fix makes stale/default attach <b>explicit and safe</b>: a seat-seeking newcomer whose
/// persistent identity does not already own a seat, arriving at an <em>abandoned</em> default game
/// that has advanced past <see cref="ChangshaPhase.Seating"/>, is given a fresh table instead of the
/// stale one. Two invariants are preserved verbatim and pinned below: a <b>deliberate reconnect</b>
/// (same cookie-derived playerId returning to its seat) reattaches, and every <b>explicit
/// <c>?gameId=</c></b> game keeps first-creator-wins + multi-human join. A live default game with
/// another player still connected is never torn out from under them.</para>
/// </summary>
public sealed class Issue153DefaultGameLifecycleTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    private const string DefaultGameId = AutotableWsEndpoint.DefaultGameId;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"issue153-lifecycle-{Guid.NewGuid():N}.db");
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

    private IChangshaGameRuntime Runtime => _factory!.Services.GetRequiredService<IChangshaGameRuntime>();
    private AutotableConnectionManager Manager => _factory!.Services.GetRequiredService<AutotableConnectionManager>();

    // ── PRIMARY: fresh browser must NOT inherit a stale, already-dealt default game ──────────────

    [Fact]
    public async Task FreshBrowser_AfterDefaultGameDealt_GetsFreshTable_NotStaleState()
    {
        // Browser A: bare default URL, auto deal, seat 0, plays into a dealt hand, then leaves.
        string ridA;
        await using (var a = await OpenAsync("?variant=changsha&dealMode=auto"))
        {
            await a.JoinAndReadAsync(DefaultGameId);
            await a.TakeSeatAsync(0);
            var bound = await WaitForBindingAsync();
            Assert.NotNull(bound);
            ridA = bound!;
            await a.SendDealAsync();
            var dealt = await WaitForAsync(() => Runtime.TryGetSnapshot(ridA, out var s) && s is not null
                && s.Phase >= ChangshaPhase.AwaitingDiscard);
            Assert.True(dealt, "precondition: browser A's default game must reach a dealt hand.");
        }

        // A has left: wait until the server has fully processed the disconnect (abandoned game).
        Assert.True(await WaitForAsync(() => Manager.ConnectionCount == 0),
            "browser A's disconnect must be processed before browser B connects.");

        // Browser B: a DIFFERENT fresh identity (no cookie) opens the same bare default URL.
        await using var b = await OpenAsync("?variant=changsha&dealMode=auto");
        var joinedB = await b.JoinAndReadAsync(DefaultGameId);
        var pidB = joinedB.GetProperty("playerId").GetString();
        await b.TakeSeatAsync(0);
        var ridB = await WaitForBindingChangeAsync(ridA);

        Assert.NotNull(ridB);
        Assert.NotEqual(ridA, ridB);

        // B sits at a FRESH table (a different runtime game), seat 0 owned by B, not
        // inheriting A's abandoned hand. BE-3 — an auto game with bot-fill server-starts
        // on seat-fill, so B's fresh table deals its OWN hand; the anti-stale guarantee
        // is that it is a DIFFERENT game (ridB != ridA, asserted above) owned by B.
        Assert.True(Runtime.TryGetSnapshot(ridB!, out var snapB) && snapB is not null);
        Assert.Equal(pidB, snapB!.Seats[0].PlayerId);
        Assert.False(snapB.Seats[0].IsBot, "seat 0 of B's fresh table is the human B, not a bot.");

        // The stale game A left behind was retired, not handed to B.
        Assert.False(Runtime.TryGetSnapshot(ridA, out var staleSnap) && staleSnap is not null
            && !staleSnap.IsGameComplete,
            "the stale default game must be retired (removed / terminal), never reattached to a newcomer.");
    }

    // ── PRESERVE: a deliberate reconnect (same playerId) reattaches to its own game ──────────────

    [Fact]
    public async Task DeliberateReconnect_SamePlayer_ReattachesToStartedGame_NotReset()
    {
        var pid = $"reconnect-{Guid.NewGuid():N}";
        string ridA;
        await using (var a = await OpenAsync("?variant=changsha&dealMode=auto", cookiePlayerId: pid))
        {
            await a.JoinAndReadAsync(DefaultGameId);
            await a.TakeSeatAsync(0);
            ridA = (await WaitForBindingAsync())!;
            await a.SendDealAsync();
            Assert.True(await WaitForAsync(() => Runtime.TryGetSnapshot(ridA, out var s) && s is not null
                && s.Phase >= ChangshaPhase.AwaitingDiscard), "precondition: reconnect game must be dealt.");
        }

        Assert.True(await WaitForAsync(() => Manager.ConnectionCount == 0),
            "the first connection's disconnect must be processed before the reconnect.");

        // SAME persistent identity returns (cookie carried) and re-takes its seat.
        await using var a2 = await OpenAsync("?variant=changsha&dealMode=auto", cookiePlayerId: pid);
        await a2.JoinAndReadAsync(DefaultGameId);
        await a2.TakeSeatAsync(0);
        await Task.Delay(150);
        var ridA2 = Manager.GetRuntimeGameIdBoundTo(DefaultGameId);

        Assert.Equal(ridA, ridA2); // reattached, NOT reset
        Assert.True(Runtime.TryGetSnapshot(ridA2!, out var snap) && snap is not null);
        Assert.Equal(pid, snap!.Seats[0].PlayerId);
        Assert.True(snap.Phase >= ChangshaPhase.AwaitingDiscard,
            "a deliberate reconnect must preserve the in-progress hand, not restart it.");
    }

    // ── PRESERVE: explicit ?gameId= games keep first-creator-wins + join (never reset) ──────────

    [Fact]
    public async Task ExplicitGameId_NewPlayer_JoinsExistingGame_NotReset()
    {
        var gameId = $"shared-153-{Guid.NewGuid():N}";
        await using var a = await OpenAsync($"?variant=changsha&dealMode=auto&gameId={gameId}");
        await a.JoinAndReadAsync(gameId);
        await a.TakeSeatAsync(0);
        var ridA = await WaitForBindingAsync(gameId);
        Assert.NotNull(ridA);
        await a.SendDealAsync();
        Assert.True(await WaitForAsync(() => Runtime.TryGetSnapshot(ridA!, out var s) && s is not null
            && s.Phase >= ChangshaPhase.AwaitingDiscard), "precondition: explicit game must be dealt.");

        // A DIFFERENT player joins the SAME explicit gameId while A is still playing.
        await using var b = await OpenAsync($"?variant=changsha&gameId={gameId}");
        await b.JoinAndReadAsync(gameId);
        await b.TakeSeatAsync(1);
        await Task.Delay(150);

        // Explicit games are never subject to the default-game reset: the binding is unchanged.
        Assert.Equal(ridA, Manager.GetRuntimeGameIdBoundTo(gameId));
        Assert.True(Runtime.TryGetSnapshot(ridA!, out var snap) && snap is not null);
        Assert.True(snap!.Phase >= ChangshaPhase.AwaitingDiscard,
            "first-creator-wins: the joiner attaches to the creator's in-progress game unchanged.");
    }

    // ── SAFETY: a live default game with another player connected is not torn out ────────────────

    [Fact]
    public async Task LiveDefaultGame_WithConnectedPlayer_NotRetiredByConcurrentNewcomer()
    {
        await using var a = await OpenAsync("?variant=changsha&dealMode=auto");
        await a.JoinAndReadAsync(DefaultGameId);
        await a.TakeSeatAsync(0);
        var ridA = await WaitForBindingAsync();
        Assert.NotNull(ridA);
        await a.SendDealAsync();
        Assert.True(await WaitForAsync(() => Runtime.TryGetSnapshot(ridA!, out var s) && s is not null
            && s.Phase >= ChangshaPhase.AwaitingDiscard), "precondition: live default game must be dealt.");

        // A stays connected. A newcomer arrives at the default game and tries to sit.
        await using var b = await OpenAsync("?variant=changsha&dealMode=auto");
        await b.JoinAndReadAsync(DefaultGameId);
        await b.TakeSeatAsync(1);
        await Task.Delay(200);

        // A's live game must survive — the newcomer must not retire a game with active co-players.
        Assert.Equal(ridA, Manager.GetRuntimeGameIdBoundTo(DefaultGameId));
        Assert.True(Runtime.TryGetSnapshot(ridA!, out var snap) && snap is not null
            && !snap.IsGameComplete,
            "a default game with another connected player must not be retired by a newcomer.");
    }

    // ── REGRESSION (#153 provider-timing flake): JOIN handshake is order-independent ─────────────
    //
    // The endpoint sends JOINED then a full UPDATE, but a newcomer joining a game whose bots are
    // already playing races the connection send-lock against the runtime StateChanged broadcast
    // drainer, so the initial UPDATE can arrive BEFORE the JOINED ack. Under SqlServer-cell timing
    // (db-providers run 31594744445) the UPDATE won and the old helper threw KeyNotFoundException
    // reading playerId off it. These pin BOTH orders deterministically at the frame-classification
    // layer (no live socket / provider needed) so the flake cannot regress.

    [Fact]
    public async Task JoinHandshake_AckBeforeUpdate_ReturnsJoinedWithPlayerId()
    {
        var source = FrameSource(
            Frame("""{"type":"JOINED","gameId":"g","playerId":"ack-first","isFirst":false}"""),
            Frame("""{"type":"UPDATE","entries":[],"full":true}"""));

        var (joined, initialUpdate) = await WsSession.ReadJoinHandshakeAsync(source);

        Assert.Equal("JOINED", joined.GetProperty("type").GetString());
        Assert.Equal("ack-first", joined.GetProperty("playerId").GetString());
        Assert.Equal("UPDATE", initialUpdate.GetProperty("type").GetString());
    }

    [Fact]
    public async Task JoinHandshake_UpdateBeforeAck_PreservesUpdate_AndReturnsJoined()
    {
        // The exact failing interleaving: a bot-driven full UPDATE wins the send lock first.
        var source = FrameSource(
            Frame("""{"type":"UPDATE","entries":[["seats",0,{"seat":0}]],"full":true}"""),
            Frame("""{"type":"JOINED","gameId":"g","playerId":"ack-second","isFirst":false}"""));

        var (joined, initialUpdate) = await WsSession.ReadJoinHandshakeAsync(source);

        Assert.Equal("ack-second", joined.GetProperty("playerId").GetString());
        // The UPDATE that beat the ack is preserved, not discarded.
        Assert.Equal("UPDATE", initialUpdate.GetProperty("type").GetString());
        Assert.True(initialUpdate.GetProperty("full").GetBoolean());
    }

    [Fact]
    public async Task JoinHandshake_MultipleBotUpdatesBeforeAck_KeepsFirstUpdate()
    {
        var source = FrameSource(
            Frame("""{"type":"UPDATE","entries":[["turn","current",{"activeSeat":1}]],"full":true}"""),
            Frame("""{"type":"UPDATE","entries":[["turn","current",{"activeSeat":2}]],"full":true}"""),
            Frame("""{"type":"JOINED","gameId":"g","playerId":"p","isFirst":false}"""));

        var (joined, initialUpdate) = await WsSession.ReadJoinHandshakeAsync(source);

        Assert.Equal("p", joined.GetProperty("playerId").GetString());
        // The FIRST UPDATE is the one preserved.
        Assert.Equal(1, initialUpdate.GetProperty("entries")[0][2].GetProperty("activeSeat").GetInt32());
    }

    [Fact]
    public async Task JoinHandshake_JoinedWithoutPlayerId_FailsExplicitly()
    {
        var source = FrameSource(
            Frame("""{"type":"JOINED","gameId":"g","isFirst":false}"""),
            Frame("""{"type":"UPDATE","entries":[],"full":true}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WsSession.ReadJoinHandshakeAsync(source));
        Assert.Contains("playerId", ex.Message);
    }

    [Fact]
    public async Task JoinHandshake_UnexpectedFrameType_FailsExplicitly()
    {
        var source = FrameSource(
            Frame("""{"type":"ERROR","reason":"boom"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WsSession.ReadJoinHandshakeAsync(source));
        Assert.Contains("Unexpected frame", ex.Message);
    }

    private static JsonElement Frame(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Func<Task<JsonElement>> FrameSource(params JsonElement[] frames)
    {
        var queue = new Queue<JsonElement>(frames);
        return () => queue.Count > 0
            ? Task.FromResult(queue.Dequeue())
            : throw new InvalidOperationException("frame source exhausted (handshake over-read).");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task<string?> WaitForBindingAsync(string relayGameId = DefaultGameId)
    {
        await WaitForAsync(() => Manager.GetRuntimeGameIdBoundTo(relayGameId) is not null);
        return Manager.GetRuntimeGameIdBoundTo(relayGameId);
    }

    private async Task<string?> WaitForBindingChangeAsync(string previous, string relayGameId = DefaultGameId)
    {
        await WaitForAsync(() =>
        {
            var rid = Manager.GetRuntimeGameIdBoundTo(relayGameId);
            return rid is not null && !string.Equals(rid, previous, StringComparison.Ordinal);
        });
        return Manager.GetRuntimeGameIdBoundTo(relayGameId);
    }

    private async Task<bool> WaitForAsync(Func<bool> predicate, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(20);
        }
        return predicate();
    }

    private async Task<WsSession> OpenAsync(string queryString, string? cookiePlayerId = null)
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        if (cookiePlayerId is not null)
        {
            // Burke — mahjong_pid carries a SIGNED credential; sign through the host's own
            // service so the reconnect really presents the same durable identity.
            var signed = _factory!.Services
                .GetRequiredService<Mahjong.Autotable.Api.Players.PlayerIdentityService>()
                .Protect(cookiePlayerId);
            wsClient.ConfigureRequest = req =>
                req.Headers["Cookie"] = $"mahjong_pid={signed}";
        }
        var uri = new Uri(server.BaseAddress, $"autotable/ws{queryString}");
        var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);
        return new WsSession(ws);
    }

    private sealed class WsSession : IAsyncDisposable
    {
        // A newcomer that JOINs a game whose bots are already playing can receive a handful of
        // bot-driven full UPDATE broadcasts before its own JOINED ack wins the connection send
        // lock. Bound the handshake-classify loop so a genuine protocol breach fails explicitly
        // instead of blocking forever. The per-frame timeout in ReadEnvelopeAsync guards a
        // STALLED stream; this guards an unbounded non-ack stream.
        private const int MaxJoinHandshakeFrames = 64;

        private readonly WebSocket _ws;
        public string PlayerId { get; private set; } = string.Empty;
        public WsSession(WebSocket ws) { _ws = ws; }

        public async Task<JsonElement> JoinAndReadAsync(string gameId)
        {
            await SendRawAsync(JsonSerializer.Serialize(new { type = "JOIN", gameId }));
            var (joined, _) = await ReadJoinHandshakeAsync(() => ReadEnvelopeAsync());
            PlayerId = joined.GetProperty("playerId").GetString() ?? string.Empty;
            return joined;
        }

        /// <summary>
        /// Reads the post-JOIN handshake frames and returns the authoritative <c>JOINED</c> ack
        /// plus the initial full <c>UPDATE</c>, <b>independent of the order in which they arrive</b>.
        ///
        /// <para>#153 provider-timing flake — <see cref="AutotableWsEndpoint.HandleJoinAsync"/>
        /// sends <c>JOINED</c> (carrying <c>playerId</c>) and then a full <c>UPDATE</c> snapshot in
        /// that program order, but those two writes race the connection's send-lock
        /// (<c>SemaphoreSlim</c>) against the runtime's <c>StateChanged</c> broadcast drainer. When
        /// this newcomer joins a game whose bots are already playing (the explicit-<c>gameId</c>
        /// join pinned by <see cref="ExplicitGameId_NewPlayer_JoinsExistingGame_NotReset"/>), a
        /// bot-driven full <c>UPDATE</c> can win the send lock and reach this socket <b>before</b>
        /// the <c>JOINED</c> ack. The previous helper assumed frame position (frame 0 = ack) and
        /// threw <see cref="KeyNotFoundException"/> reading <c>playerId</c> off that UPDATE — the
        /// SqlServer-cell failure this fixes. Classify by the protocol discriminator (<c>type</c>)
        /// and keep the first UPDATE rather than discarding whichever arrives first.</para>
        /// </summary>
        internal static async Task<(JsonElement Joined, JsonElement InitialUpdate)> ReadJoinHandshakeAsync(
            Func<Task<JsonElement>> readEnvelope)
        {
            JsonElement? joined = null;
            JsonElement? initialUpdate = null;

            for (var frame = 0; frame < MaxJoinHandshakeFrames; frame++)
            {
                var envelope = await readEnvelope();
                var type = envelope.TryGetProperty("type", out var typeEl)
                           && typeEl.ValueKind == JsonValueKind.String
                    ? typeEl.GetString()
                    : null;

                switch (type)
                {
                    case "JOINED":
                        // The join-ack is identified by its discriminator AND its required shape:
                        // a JOINED without a string playerId is a protocol breach, not a retry.
                        if (!envelope.TryGetProperty("playerId", out var pid)
                            || pid.ValueKind != JsonValueKind.String)
                        {
                            throw new InvalidOperationException(
                                "JOINED ack missing required string 'playerId'.");
                        }
                        joined = envelope;
                        break;

                    case "UPDATE":
                        // Preserve the FIRST authoritative snapshot even if it beat the ack.
                        initialUpdate ??= envelope;
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unexpected frame during JOIN handshake (type='{type ?? "<missing>"}').");
                }

                if (joined is not null && initialUpdate is not null)
                    return (joined.Value, initialUpdate.Value);
            }

            throw new InvalidOperationException(
                "JOIN handshake did not deliver both a JOINED ack and an initial UPDATE within "
                + $"{MaxJoinHandshakeFrames} frames (joined={joined is not null}, "
                + $"initialUpdate={initialUpdate is not null}).");
        }

        public async Task TakeSeatAsync(int seatIndex) => await SendUpdateAsync(new object[]
        {
            new object[] { "seats", seatIndex, new { seat = seatIndex } }
        });

        public async Task SendDealAsync() => await SendUpdateAsync(new object[]
        {
            new object[] { "match", 0, new { dealCommand = "start" } }
        });

        public async Task SendUpdateAsync(object[] entries) =>
            await SendRawAsync(JsonSerializer.Serialize(new { type = "UPDATE", entries, full = false }));

        private async Task SendRawAsync(string payload) => await _ws.SendAsync(
            Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task<JsonElement> ReadEnvelopeAsync(int timeoutMs = 5000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var buffer = new byte[64 * 1024];
            var sb = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Explicit failure on an unexpected terminal frame — no silent empty-string
                    // parse (which would surface as an opaque JsonException instead).
                    throw new InvalidOperationException(
                        $"WebSocket closed during read (status={result.CloseStatus}, "
                        + $"description='{result.CloseStatusDescription}').");
                }
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
