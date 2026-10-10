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
/// SC-2 endpoint privacy — the SHARED-STORE cross-viewer leak that rejected :18084.
///
/// <para>These are REAL ENDPOINT tests (raw WebSocket against an in-memory
/// <see cref="WebApplicationFactory{TEntryPoint}"/>), NOT projector unit tests. The
/// existing <c>BishopUatPrivacyContractsTests</c> P-1..P-5 only exercise
/// <see cref="ChangshaToAutotableTranslator.Translate"/> in isolation, where each
/// per-viewer projection is already correct — so they pass even on the leaking build.
/// The defect lives one layer up: <see cref="AutotableConnectionManager"/> persisted
/// every viewer's per-viewer <c>things</c> projection into the SHARED
/// <see cref="AutotableGameState"/>. Because the seated owner keys its own concealed
/// hand by NUMERIC real tileId while a spectator/foreign viewer keys the same slot by
/// an opaque <c>h_</c> handle, the shared store accumulated the UNION of both, and a
/// later viewer's snapshot (built from the store, only FACE-stripped by
/// <c>FilterEntriesForViewer</c>, which preserves the KEY) shipped the owner's real
/// tileId KEYS — and the key alone reconstructs identity (typeIndex = key/4).</para>
///
/// <para>Reproduction: seat 0 owner connects first (poisons the store on the leaking
/// build), then a second viewer (spectator, or a second seated viewer) reads its own
/// full snapshot. The invariant a correct build must satisfy: a viewer NEVER receives a
/// numeric 0..107 <c>things</c> KEY for a slot concealed from it, and no physical slot
/// carries two entries (a numeric + opaque duplicate). Both FAIL on :18084 and PASS on
/// the shared-store fix.</para>
/// </summary>
public class Sc2SharedStoreEndpointLeakTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-sc2leak-{Guid.NewGuid():N}.db");

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

    // ── 1. cookie-less spectator: zero numeric keys on ANY concealed slot ──────────
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19-endpoint")]
    public async Task Spectator_AfterOwnerPoisonsStore_ReceivesNoNumericConcealedKeys()
    {
        const string relayGameId = "SC2LEAK-SPEC";
        await CreateBindAndDealAsync(relayGameId, seed: 7);

        // Seat-0 OWNER connects first: ViewerSeat=0 ⇒ its projection keys its own hand
        // by NUMERIC real tileId. On the leaking build this is persisted to the shared
        // AutotableGameState, contaminating every later viewer's snapshot.
        await using var owner = await OpenAsync(seat: 0, relayGameId);
        await owner.SendJoinAsync(relayGameId);
        await owner.ReadFullUpdateAsync(); // JOINED + UPDATE handled inside
        // Blocker D (Bishop rev2) — the owner LEGITIMATELY takes seat 0 (ViewerSeat is no
        // longer granted by the `?seat=` hint) so its projection keys its own hand by
        // NUMERIC real tileId — the store-poisoning attempt this test guards against.
        await owner.SendTakeSeatAsync(0);
        await owner.ReadLatestFullUpdateAsync();

        // Cookie-less SPECTATOR (ViewerSeat=null) reads its own full snapshot.
        await using var spectator = await OpenAsync(seat: -1, relayGameId);
        await spectator.SendJoinAsync(relayGameId);
        var specUpdate = await spectator.ReadFullUpdateAsync();

        var things = ExtractThings(specUpdate);
        Assert.NotEmpty(things);

        // (a) A spectator conceals EVERY hand + wall tile — each MUST be an opaque
        //     h_ handle, never a numeric real id (numeric key = leaked identity).
        var leaked = things
            .Where(t => IsConcealedFromSpectator(t.Slot) && t.KeyIsNumeric)
            .Select(t => $"{t.Slot}=#{t.NumericKey}")
            .ToList();
        Assert.True(leaked.Count == 0,
            "Spectator received NUMERIC real-tileId keys for concealed slots (SC-2 shared-store leak): "
            + string.Join(", ", leaked.Take(10)));

        // (b) No physical slot carries two entries (numeric + opaque duplicate).
        AssertNoDuplicateSlots(things);
    }

    // ── 2. second SEATED viewer: no FOREIGN numeric keys; own hand stays numeric ───
    [Fact, Trait("Category", "UatBackend"), Trait("Contract", "G19-endpoint")]
    public async Task SecondSeatedViewer_AfterOwnerPoisonsStore_SeesNoForeignNumericKeys()
    {
        const string relayGameId = "SC2LEAK-SEAT1";
        await CreateBindAndDealAsync(relayGameId, seed: 11);

        // Seat-0 owner poisons the shared store with its own numeric hand keys.
        await using var owner = await OpenAsync(seat: 0, relayGameId);
        await owner.SendJoinAsync(relayGameId);
        await owner.ReadFullUpdateAsync();
        await owner.SendTakeSeatAsync(0);              // Blocker D — legitimate ownership
        await owner.ReadLatestFullUpdateAsync();

        // A DIFFERENT seated viewer (seat 1) reads its own snapshot.
        await using var viewer1 = await OpenAsync(seat: 1, relayGameId);
        await viewer1.SendJoinAsync(relayGameId);
        await viewer1.ReadFullUpdateAsync();
        await viewer1.SendTakeSeatAsync(1);            // Blocker D — legitimate ownership of seat 1
        var update = await viewer1.ReadLatestFullUpdateAsync();

        var things = ExtractThings(update);
        Assert.NotEmpty(things);

        // Foreign hands (@0/@2/@3) and ALL walls must be opaque for seat 1; a numeric
        // key there is the owner's real id leaking through the shared store.
        var leaked = things
            .Where(t => IsConcealedFromSeat(t.Slot, viewerSeat: 1) && t.KeyIsNumeric)
            .Select(t => $"{t.Slot}=#{t.NumericKey}")
            .ToList();
        Assert.True(leaked.Count == 0,
            "Seat-1 viewer received foreign NUMERIC real-tileId keys (SC-2 shared-store leak): "
            + string.Join(", ", leaked.Take(10)));

        // Sanity: seat 1 still gets its OWN hand as resolvable real ids (privacy must
        // not over-strip — own tiles stay usable).
        var ownHandNumeric = things.Count(t =>
            t.Slot.StartsWith("hand.", StringComparison.Ordinal)
            && t.Slot.EndsWith("@1", StringComparison.Ordinal)
            && t.KeyIsNumeric);
        Assert.True(ownHandNumeric > 0,
            "Seat-1 viewer must still see its OWN hand tiles as real ids (usable), but saw none.");

        AssertNoDuplicateSlots(things);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────

    private async Task CreateBindAndDealAsync(string relayGameId, int seed)
    {
        var runtime = _factory!.Services.GetRequiredService<IChangshaGameRuntime>();
        // Seats 1,2,3 = bots; seat 0 is the human owner slot (dealt, unoccupied until
        // the owner WS connects). StartGameAsync deals all 108 tiles.
        var runtimeGameId = await runtime.CreateGameAsync(
            seed: seed, botSeatIndexes: new[] { 1, 2, 3 }, hostPlayerId: null, hostConnectionId: null);
        await runtime.StartGameAsync(runtimeGameId);

        var manager = _factory.Services.GetRequiredService<AutotableConnectionManager>();
        // Wait until the deal has fanned out (108 concealed tiles exist in the runtime).
        await WaitForAsync(() => runtime.TryGetSnapshot(runtimeGameId, out var s) && s is not null
            && s.Phase != ChangshaPhase.Seating, timeoutMs: 3000);
        manager.BindRuntimeGameForTest(relayGameId, runtimeGameId);
    }

    private static bool IsConcealedFromSpectator(string slot) =>
        slot.StartsWith("hand.", StringComparison.Ordinal)
        || slot.StartsWith("wall.", StringComparison.Ordinal);

    private static bool IsConcealedFromSeat(string slot, int viewerSeat)
    {
        if (slot.StartsWith("wall.", StringComparison.Ordinal)) return true;
        if (slot.StartsWith("hand.", StringComparison.Ordinal))
            return !slot.EndsWith("@" + viewerSeat, StringComparison.Ordinal);
        return false;
    }

    private static void AssertNoDuplicateSlots(IReadOnlyList<ThingEntry> things)
    {
        var dups = things.GroupBy(t => t.Slot)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}×{g.Count()}")
            .ToList();
        Assert.True(dups.Count == 0,
            "A physical slot carried multiple `things` entries (numeric+opaque duplicate from the "
            + "shared-store union): " + string.Join(", ", dups.Take(10)));
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
            // A numeric-STRING key ("42") would also reconstruct identity; treat it as numeric.
            if (!isNumeric && keyEl.ValueKind == JsonValueKind.String
                && long.TryParse(keyEl.GetString(), out var parsed) && parsed is >= 0 and <= 107)
            {
                isNumeric = true; numeric = parsed;
            }
            result.Add(new ThingEntry(slot, isNumeric && numeric is >= 0 and <= 107, numeric));
        }
        return result;
    }

    private async Task<WsSession> OpenAsync(int seat, string gameId)
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        // Blocker D (Bishop rev2) — `?seat=` is now a non-authoritative hint (it no longer
        // grants ViewerSeat/projection), and bots=false keeps the post-deal seat-take from
        // triggering bot-fill / server-start side effects in these leak tests. A seated
        // viewer establishes ViewerSeat the legitimate way, via TakeSeat (see TakeSeatAsync).
        var path = $"autotable/ws?seat={seat}&bots=false&gameId={Uri.EscapeDataString(gameId)}";
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
        return predicate();
    }

    private sealed class WsSession : IAsyncDisposable
    {
        private readonly WebSocket _ws;
        public WsSession(WebSocket ws) { _ws = ws; }

        public async Task SendJoinAsync(string gameId)
        {
            var msg = JsonSerializer.Serialize(new { type = "JOIN", gameId });
            await _ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        /// <summary>
        /// Blocker D (Bishop rev2) — legitimately take <paramref name="seat"/> via the real
        /// `seats` UPDATE path (the ONLY way to bind ViewerSeat now that `?seat=` is a
        /// non-authoritative hint). Entry is the upstream [kind, key, value] array.
        /// </summary>
        public async Task SendTakeSeatAsync(int seat)
        {
            var msg = JsonSerializer.Serialize(new object[]
            {
                new { type = "UPDATE", entries = new object[] { new object[] { "seats", "me", new { seat } } }, full = false },
            }[0]);
            await _ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        /// <summary>Reads envelopes and returns the LAST full UPDATE seen within
        /// <paramref name="quietMs"/> of silence — used after a seat-take that triggers a
        /// re-projection so the seated (ViewerSeat-bound) snapshot is the one asserted.</summary>
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
                {
                    last = env;
                }
            }
            if (last is null) throw new TimeoutException("No full UPDATE snapshot received.");
            return last.Value;
        }

        /// <summary>Reads envelopes until the first full UPDATE (skips JOINED / non-full frames).</summary>
        public async Task<JsonElement> ReadFullUpdateAsync(int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var env = await ReadEnvelopeAsync(timeoutMs);
                if (env.TryGetProperty("type", out var t) && t.GetString() == "UPDATE"
                    && env.TryGetProperty("full", out var f) && f.ValueKind == JsonValueKind.True)
                {
                    return env;
                }
            }
            throw new TimeoutException("No full UPDATE snapshot received.");
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
