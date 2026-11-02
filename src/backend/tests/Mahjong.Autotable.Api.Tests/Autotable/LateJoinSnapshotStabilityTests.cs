using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// Phase J Wave 10 — late-join snapshot stability supplementary tests
/// (Vasquez). Sibling to Apone's
/// <c>AutotableWsRelayTests.LateJoin_ReceivesAccumulatedSnapshot_OfPriorUpdates_Stability50x</c>
/// 50× in-process loop. Both target the same race (UPDATE → store →
/// JOIN-snapshot ordering) but this suite pins additional invariants:
///
/// <list type="bullet">
///   <item>Late-joiner snapshot for a NEVER-touched seat is empty +
///         <c>full=true</c> (no spurious entries).</item>
///   <item>Three concurrent late joiners observe identical
///         <c>things</c> id sets (broadcast determinism).</item>
///   <item>A re-join (close + reopen same seat) recomputes the
///         snapshot from the current store, not a stale per-connection
///         cache.</item>
///   <item><see cref="AutotableConnectionManager.GetStoredEntryCount(string, string)"/>
///         remains a stable affordance for stability-loop authors.</item>
/// </list>
///
/// <para>Independent test fixture so Apone's flake-hunting trait
/// (<c>Category=Stability</c>) stays a clean signal. This suite carries
/// <c>Category=PhaseJ-Wave10-LateJoin</c>.</para>
/// </summary>
public class LateJoinSnapshotStabilityTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-ljs-{Guid.NewGuid():N}.db");
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

    // ── Plumbing ─────────────────────────────────────────────────────────

    private async Task<WebSocket> OpenWsAsync(int seat)
    {
        Assert.NotNull(_factory);
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var uri = new Uri(server.BaseAddress, $"autotable/ws?variant=four_player&seat={seat}");
        return await wsClient.ConnectAsync(uri, CancellationToken.None);
    }

    private static async Task SendRawAsync(WebSocket ws, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task SendJoinAsync(WebSocket ws, string gameId)
    {
        var msg = JsonSerializer.Serialize(new { type = "JOIN", gameId });
        await SendRawAsync(ws, msg);
    }

    private static async Task SendUpdateAsync(WebSocket ws, params object[][] entries)
    {
        // Matches the upstream [kind, key, value] tuple exactly + the
        // "full: false" delta envelope shape.
        using var ms = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "UPDATE");
            writer.WritePropertyName("entries");
            writer.WriteStartArray();
            foreach (var e in entries)
            {
                writer.WriteStartArray();
                writer.WriteStringValue((string)e[0]);
                switch (e[1])
                {
                    case int i: writer.WriteNumberValue(i); break;
                    case long l: writer.WriteNumberValue(l); break;
                    case string s: writer.WriteStringValue(s); break;
                    default: throw new InvalidOperationException("unsupported key");
                }
                if (e[2] is JsonElement je) je.WriteTo(writer);
                else JsonSerializer.Serialize(writer, e[2]);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteBoolean("full", false);
            writer.WriteEndObject();
        }
        await SendRawAsync(ws, Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static async Task<JsonElement> ReadEnvelopeAsync(WebSocket ws, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, cts.Token);
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(sb.ToString()).RootElement.Clone();
    }

    /// <summary>Open + JOIN + drain the JOINED envelope + return the first
    /// UPDATE snapshot for that join.</summary>
    private async Task<(WebSocket Ws, JsonElement Snapshot)> JoinAsync(string gameId, int seat)
    {
        var ws = await OpenWsAsync(seat);
        await SendJoinAsync(ws, gameId);
        var joined = await ReadEnvelopeAsync(ws);
        Assert.Equal("JOINED", joined.GetProperty("type").GetString());
        var snap = await ReadEnvelopeAsync(ws);
        Assert.Equal("UPDATE", snap.GetProperty("type").GetString());
        return (ws, snap);
    }

    private static async Task CloseAsync(WebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);
        }
        catch { }
        ws.Dispose();
    }

    private static List<long> ExtractThings(JsonElement snap)
    {
        var ids = new List<long>();
        if (!snap.TryGetProperty("entries", out var entries)) return ids;
        if (entries.ValueKind != JsonValueKind.Array) return ids;
        for (var i = 0; i < entries.GetArrayLength(); i++)
        {
            if (entries[i][0].GetString() == "things")
                ids.Add(entries[i][1].GetInt64());
        }
        return ids;
    }

    private static async Task WaitForAsync(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Late joiner of an untouched game sees full=true + no `things`
    //     entries (no carry-over from another game id).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "PhaseJ-Wave10-LateJoin"), Trait("Wave", "Phase-J-10")]
    public async Task LateJoiner_UntouchedGame_NoThingsCarryOver()
    {
        var gameId = $"GAME-LJ-EMPTY-{Guid.NewGuid():N}";
        var (ws, snap) = await JoinAsync(gameId, seat: 0);
        try
        {
            Assert.True(snap.GetProperty("full").GetBoolean());
            var things = ExtractThings(snap);
            Assert.Empty(things);
        }
        finally { await CloseAsync(ws); }
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Three sequential late joiners observe identical `things` sets.
    //     (Sequential to keep the WebSocket TestServer pipe happy — the
    //     race we're pinning is server-side, not connection ordering.)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "PhaseJ-Wave10-LateJoin"), Trait("Wave", "Phase-J-10")]
    public async Task LateJoiners_Multiple_AllSeeIdenticalEntries()
    {
        var gameId = $"GAME-LJ-CONC-{Guid.NewGuid():N}";
        var (alice, _) = await JoinAsync(gameId, seat: 0);
        try
        {
            var v1 = JsonSerializer.SerializeToElement(new { slotName = "hand.0@0", rotationIndex = 1 });
            var v2 = JsonSerializer.SerializeToElement(new { slotName = "hand.1@0", rotationIndex = 1 });
            await SendUpdateAsync(alice,
                new object[] { "things", 100L, v1 },
                new object[] { "things", 101L, v2 });

            var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
            await WaitForAsync(() => manager.GetStoredEntryCount(gameId, "things") >= 2, 5000);
            Assert.True(manager.GetStoredEntryCount(gameId, "things") >= 2,
                "Server never recorded all 2 things entries before late joiners attached.");

            var (bob, bobSnap) = await JoinAsync(gameId, seat: 1);
            var (carol, carolSnap) = await JoinAsync(gameId, seat: 2);
            var (dave, daveSnap) = await JoinAsync(gameId, seat: 3);
            try
            {
                var bobThings = ExtractThings(bobSnap).OrderBy(x => x).ToList();
                var carolThings = ExtractThings(carolSnap).OrderBy(x => x).ToList();
                var daveThings = ExtractThings(daveSnap).OrderBy(x => x).ToList();

                Assert.Contains(100L, bobThings);
                Assert.Contains(101L, bobThings);
                Assert.Equal(bobThings, carolThings);
                Assert.Equal(bobThings, daveThings);
            }
            finally
            {
                await CloseAsync(bob);
                await CloseAsync(carol);
                await CloseAsync(dave);
            }
        }
        finally { await CloseAsync(alice); }
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Re-join (close + reopen same seat) re-emits the current store.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "PhaseJ-Wave10-LateJoin"), Trait("Wave", "Phase-J-10")]
    public async Task ReJoin_AfterStoreMutation_PicksUpLatestEntries()
    {
        var gameId = $"GAME-LJ-REJOIN-{Guid.NewGuid():N}";
        var (alice, _) = await JoinAsync(gameId, seat: 0);
        try
        {
            var v200 = JsonSerializer.SerializeToElement(new { slotName = "hand.0@0", rotationIndex = 1 });
            await SendUpdateAsync(alice, new object[] { "things", 200L, v200 });

            var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
            await WaitForAsync(() => manager.GetStoredEntryCount(gameId, "things") >= 1, 5000);

            var (bob1, firstSnap) = await JoinAsync(gameId, seat: 1);
            await CloseAsync(bob1);

            var v201 = JsonSerializer.SerializeToElement(new { slotName = "hand.1@0", rotationIndex = 1 });
            await SendUpdateAsync(alice, new object[] { "things", 201L, v201 });
            await WaitForAsync(() => manager.GetStoredEntryCount(gameId, "things") >= 2, 5000);

            var (bob2, secondSnap) = await JoinAsync(gameId, seat: 1);
            try
            {
                var laterThings = ExtractThings(secondSnap);
                Assert.Contains(200L, laterThings);
                Assert.Contains(201L, laterThings);
            }
            finally { await CloseAsync(bob2); }

            var earlyThings = ExtractThings(firstSnap);
            Assert.Contains(200L, earlyThings);
            Assert.DoesNotContain(201L, earlyThings);
        }
        finally { await CloseAsync(alice); }
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Manager exposes the per-kind stored-entry counter that the
    //     50× stability loop relies on. (Pin the (string, string)
    //     overload by selecting it via the explicit parameter list.)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "PhaseJ-Wave10-LateJoin"), Trait("Wave", "Phase-J-10")]
    public void Manager_ExposesPerKindStoredEntryCount()
    {
        var managerType = typeof(AutotableConnectionManager);
        var method = managerType.GetMethod(
            "GetStoredEntryCount",
            new[] { typeof(string), typeof(string) });
        Assert.NotNull(method);
        Assert.Equal(typeof(int), method!.ReturnType);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Single-arg overload also still exists (legacy callers).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "PhaseJ-Wave10-LateJoin"), Trait("Wave", "Phase-J-10")]
    public void Manager_LegacyStoredEntryCount_StillExists()
    {
        var managerType = typeof(AutotableConnectionManager);
        var method = managerType.GetMethod(
            "GetStoredEntryCount",
            new[] { typeof(string) });
        Assert.NotNull(method);
        Assert.Equal(typeof(int), method!.ReturnType);
    }
}
