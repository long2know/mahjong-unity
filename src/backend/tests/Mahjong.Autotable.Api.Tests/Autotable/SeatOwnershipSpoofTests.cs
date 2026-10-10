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
/// Blocker D (Bishop rev2) — seat-ownership spoof hardening. The raw WebSocket
/// <c>?seat=N</c> query param used to seed <see cref="AutotableConnection.ViewerSeat"/>
/// directly, so a connection could pass <c>?seat=2</c> (never taking the seat) and receive
/// seat 2's REAL concealed hand — a confirmed leak of the foreign seat's real tile ids on the
/// pre-fix live build (13 real ids for seat 2). These REAL ENDPOINT tests (raw WS against an
/// in-memory <see cref="WebApplicationFactory{TEntryPoint}"/>) pin the hardened contract:
/// <list type="bullet">
///   <item>an UNOWNED <c>?seat=N</c> requester receives ZERO numeric (real-id) keys for any
///     foreign concealed hand — it stays a spectator/opaque viewer;</item>
///   <item>a viewer that legitimately owns its seat via <c>TakeSeat</c> still sees its OWN hand
///     as usable real ids (privacy must not over-strip);</item>
///   <item>a reconnecting owner (same persistent player id, no <c>?seat=</c>) re-binds its own
///     projection through runtime-confirmed ownership (TryGetSeatForPlayer).</item>
/// </list>
/// </summary>
public sealed class SeatOwnershipSpoofTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"seat-spoof-{Guid.NewGuid():N}.db");
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
        if (_tempDb is not null && File.Exists(_tempDb))
        {
            try { File.Delete(_tempDb); } catch { /* best effort */ }
        }
        return Task.CompletedTask;
    }

    // ── 1. UNOWNED `?seat=N` requester: zero foreign real-id keys ────────────────────
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "seat-spoof")]
    public async Task UnownedQuerySeat_DoesNotProjectForeignConcealedHand()
    {
        const string relayGameId = "SEATSPOOF-D1";
        await CreateBindAndDealAsync(relayGameId, seed: 21);

        // Attacker connects with ?seat=2 but NEVER takes the seat — pure query hint.
        await using var spoof = await OpenAsync("?seat=2&bots=false", relayGameId);
        var update = await spoof.JoinAndReadAsync(relayGameId);
        var things = ExtractThings(update);
        Assert.NotEmpty(things);

        // The pre-fix build leaked seat 2's concealed hand as numeric real ids. Post-fix
        // EVERY foreign concealed hand tile (@1/@2/@3) and ALL walls must be opaque handles.
        var leaked = things
            .Where(t => IsConcealedFromSeat(t.Slot, unownedSpectator: true) && t.KeyIsNumeric)
            .Select(t => $"{t.Slot}=#{t.NumericKey}")
            .ToList();
        Assert.True(leaked.Count == 0,
            "Unowned ?seat=2 requester received NUMERIC real-tileId keys for concealed slots "
            + "(seat spoof): " + string.Join(", ", leaked.Take(10)));

        // Specifically the requested seat-2 hand must carry ZERO real ids.
        var seat2HandNumeric = things.Count(t =>
            t.Slot.StartsWith("hand.", StringComparison.Ordinal)
            && t.Slot.EndsWith("@2", StringComparison.Ordinal)
            && t.KeyIsNumeric);
        Assert.Equal(0, seat2HandNumeric);
    }

    // ── 2. legitimate ownership via TakeSeat: own hand stays numeric/usable ───────────
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "seat-spoof")]
    public async Task TakeSeat_BindsOwnHandProjection_ForeignStaysOpaque()
    {
        const string relayGameId = "SEATSPOOF-D2";
        await CreateBindAndDealAsync(relayGameId, seed: 22);

        // No ?seat= at all; establish ownership the legitimate way.
        await using var player = await OpenAsync("?bots=false", relayGameId);
        await player.JoinAndReadAsync(relayGameId);
        await player.TakeSeatAsync(0);
        var update = await player.ReadLatestFullUpdateAsync();
        var things = ExtractThings(update);
        Assert.NotEmpty(things);

        var ownHandNumeric = things.Count(t =>
            t.Slot.StartsWith("hand.", StringComparison.Ordinal)
            && t.Slot.EndsWith("@0", StringComparison.Ordinal)
            && t.KeyIsNumeric);
        Assert.True(ownHandNumeric > 0,
            "Owner (seat 0 via TakeSeat) must see its OWN hand as real ids, but saw none.");

        var foreignLeak = things
            .Where(t => IsConcealedFromSeat(t.Slot, viewerSeat: 0) && t.KeyIsNumeric)
            .Select(t => $"{t.Slot}=#{t.NumericKey}")
            .ToList();
        Assert.True(foreignLeak.Count == 0,
            "Seat-0 owner received FOREIGN numeric keys: " + string.Join(", ", foreignLeak.Take(10)));
    }

    // ── 3. reconnect: owner re-binds projection via runtime-confirmed ownership ───────
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "seat-spoof")]
    public async Task Reconnect_OwnerRebindsOwnHand_ViaRuntimeOwnership_NoQuerySeat()
    {
        const string relayGameId = "SEATSPOOF-D3";
        await CreateBindAndDealAsync(relayGameId, seed: 23);
        var ownerPid = "owner-pid-" + Guid.NewGuid().ToString("N");

        // First connection (cookie-identified) takes seat 0.
        await using (var first = await OpenAsync("?bots=false", relayGameId, cookiePlayerId: ownerPid))
        {
            await first.JoinAndReadAsync(relayGameId);
            await first.TakeSeatAsync(0);
            _ = await first.ReadLatestFullUpdateAsync();
        }

        // Reconnect: brand-new WS, SAME persistent id, and crucially NO ?seat= — the owner
        // must be re-projected face-up purely from TryGetSeatForPlayer ownership.
        await using var second = await OpenAsync("?bots=false", relayGameId, cookiePlayerId: ownerPid);
        var update = await second.JoinAndReadLatestAsync(relayGameId);
        var things = ExtractThings(update);
        Assert.NotEmpty(things);

        var ownHandNumeric = things.Count(t =>
            t.Slot.StartsWith("hand.", StringComparison.Ordinal)
            && t.Slot.EndsWith("@0", StringComparison.Ordinal)
            && t.KeyIsNumeric);
        Assert.True(ownHandNumeric > 0,
            "Reconnecting owner must re-bind its OWN hand via ownership (TryGetSeatForPlayer), "
            + "but saw no real ids.");
    }

    // ── 4. spectator sentinel control: zero foreign real ids ─────────────────────────
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "seat-spoof")]
    public async Task SpectatorSentinel_SeesNoForeignConcealedRealIds()
    {
        const string relayGameId = "SEATSPOOF-D4";
        await CreateBindAndDealAsync(relayGameId, seed: 24);

        await using var spectator = await OpenAsync("?seat=-1&bots=false", relayGameId);
        var update = await spectator.JoinAndReadAsync(relayGameId);
        var things = ExtractThings(update);
        Assert.NotEmpty(things);

        var leaked = things.Count(t => IsConcealedFromSeat(t.Slot, unownedSpectator: true) && t.KeyIsNumeric);
        Assert.Equal(0, leaked);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────
    private async Task CreateBindAndDealAsync(string relayGameId, int seed)
    {
        var runtime = _factory!.Services.GetRequiredService<IChangshaGameRuntime>();
        var runtimeGameId = await runtime.CreateGameAsync(
            seed: seed, botSeatIndexes: new[] { 1, 2, 3 }, hostPlayerId: null, hostConnectionId: null);
        await runtime.StartGameAsync(runtimeGameId);

        var manager = _factory.Services.GetRequiredService<AutotableConnectionManager>();
        await WaitForAsync(() => runtime.TryGetSnapshot(runtimeGameId, out var s) && s is not null
            && s.Phase != ChangshaPhase.Seating, timeoutMs: 3000);
        manager.BindRuntimeGameForTest(relayGameId, runtimeGameId);
    }

    private static bool IsConcealedFromSeat(string slot, int viewerSeat = -1, bool unownedSpectator = false)
    {
        if (slot.StartsWith("wall.", StringComparison.Ordinal)) return true;
        if (slot.StartsWith("hand.", StringComparison.Ordinal))
            return unownedSpectator || !slot.EndsWith("@" + viewerSeat, StringComparison.Ordinal);
        return false;
    }

    private readonly record struct ThingEntry(string Slot, bool KeyIsNumeric, long NumericKey);

    private static IReadOnlyList<ThingEntry> ExtractThings(JsonElement update)
    {
        var result = new List<ThingEntry>();
        var entries = update.GetProperty("entries");
        for (var i = 0; i < entries.GetArrayLength(); i++)
        {
            var entry = entries[i];
            if (entry[0].GetString() != "things") continue;
            if (entry[2].ValueKind != JsonValueKind.Object) continue;
            if (!entry[2].TryGetProperty("slotName", out var slotEl) || slotEl.ValueKind != JsonValueKind.String) continue;
            var slot = slotEl.GetString() ?? string.Empty;

            var keyEl = entry[1];
            var isNumeric = keyEl.ValueKind == JsonValueKind.Number && keyEl.TryGetInt64(out _);
            long numeric = isNumeric ? keyEl.GetInt64() : -1;
            if (!isNumeric && keyEl.ValueKind == JsonValueKind.String
                && long.TryParse(keyEl.GetString(), out var parsed) && parsed is >= 0 and <= 107)
            {
                isNumeric = true; numeric = parsed;
            }
            result.Add(new ThingEntry(slot, isNumeric && numeric is >= 0 and <= 107, numeric));
        }
        return result;
    }

    private async Task<WsSession> OpenAsync(string queryString, string gameId, string? cookiePlayerId = null)
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        if (cookiePlayerId is not null)
        {
            // Burke — the mahjong_pid cookie carries a SIGNED credential; a raw player id is
            // rejected and rotated onto a fresh identity. Sign through the host's own service so
            // this connection really is `cookiePlayerId`.
            var signed = _factory!.Services.GetRequiredService<PlayerIdentityService>()
                .Protect(cookiePlayerId);
            wsClient.ConfigureRequest = req => req.Headers["Cookie"] = $"mahjong_pid={signed}";
        }
        var sep = queryString.Contains('?') ? "&" : "?";
        var uri = new Uri(server.BaseAddress, $"autotable/ws{queryString}{sep}gameId={Uri.EscapeDataString(gameId)}");
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
        return predicate();
    }

    private sealed class WsSession : IAsyncDisposable
    {
        private readonly WebSocket _ws;
        public WsSession(WebSocket ws) { _ws = ws; }

        public async Task<JsonElement> JoinAndReadAsync(string gameId)
        {
            await SendRawAsync(JsonSerializer.Serialize(new { type = "JOIN", gameId }));
            _ = await ReadEnvelopeAsync(); // JOINED
            return await ReadFullUpdateAsync();
        }

        /// <summary>JOIN, then return the LAST full snapshot within a quiet window (covers the
        /// reconnect owner-inference re-projection that follows the initial snapshot).</summary>
        public async Task<JsonElement> JoinAndReadLatestAsync(string gameId)
        {
            await SendRawAsync(JsonSerializer.Serialize(new { type = "JOIN", gameId }));
            return await ReadLatestFullUpdateAsync();
        }

        public async Task TakeSeatAsync(int seatIndex) => await SendRawAsync(JsonSerializer.Serialize(
            new { type = "UPDATE", entries = new object[] { new object[] { "seats", "me", new { seat = seatIndex } } }, full = false }));

        public async Task<JsonElement> ReadFullUpdateAsync(int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var env = await ReadEnvelopeAsync(timeoutMs);
                if (env.TryGetProperty("type", out var t) && t.GetString() == "UPDATE"
                    && env.TryGetProperty("full", out var f) && f.ValueKind == JsonValueKind.True)
                    return env;
            }
            throw new TimeoutException("No full UPDATE snapshot received.");
        }

        public async Task<JsonElement> ReadLatestFullUpdateAsync(int quietMs = 400, int hardTimeoutMs = 5000)
        {
            JsonElement? last = null;
            var hardDeadline = DateTime.UtcNow.AddMilliseconds(hardTimeoutMs);
            while (DateTime.UtcNow < hardDeadline)
            {
                JsonElement env;
                try { env = await ReadEnvelopeAsync(quietMs); }
                catch (OperationCanceledException) { break; }
                if (env.TryGetProperty("type", out var t) && t.GetString() == "UPDATE"
                    && env.TryGetProperty("full", out var f) && f.ValueKind == JsonValueKind.True)
                    last = env;
            }
            if (last is null) throw new TimeoutException("No full UPDATE snapshot received.");
            return last.Value;
        }

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
