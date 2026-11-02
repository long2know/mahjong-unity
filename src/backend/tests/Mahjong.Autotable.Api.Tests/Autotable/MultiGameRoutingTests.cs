using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Autotable;

/// <summary>
/// Phase I Wave 3 — multi-game routing surface coverage. Closes the
/// "single-game-per-instance" pin landed in Phase D-backend and verifies
/// Bishop's <c>TryNormalizeGameId</c> validation contract.
///
/// <para><b>Scope</b>: WS query-string and JOIN-message routing under the
/// lifted <c>DefaultGameId</c> coercion. Per-gameId isolation
/// (<c>Update_IsIsolated_PerGameId</c>) lives in
/// <see cref="AutotableWsRelayTests"/> for historical continuity; this
/// file adds the newer surface tests.</para>
///
/// <para><b>Validation contract (Bishop's memo §"Validation rules (settled)")</b>:
/// <list type="bullet">
///   <item>Null / whitespace ⇒ fall back to <see cref="AutotableWsEndpoint.DefaultGameId"/>.</item>
///   <item>After <c>Trim()</c>, length must be ≤ <see cref="AutotableWsEndpoint.MaxGameIdLength"/> (64).</item>
///   <item>After <c>Trim()</c>, must contain no <c>char.IsControl</c> chars.</item>
///   <item>Source priority on JOIN: JOIN.gameId ▶ ?gameId= ▶ DefaultGameId.</item>
///   <item>Validation failure ⇒ WS close with <see cref="WebSocketCloseStatus.PolicyViolation"/>.</item>
/// </list>
/// </para>
/// </summary>
public class MultiGameRoutingTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-multigame-{Guid.NewGuid():N}.db");

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

    // ── #2 — Late join receives only the target game's snapshot ──────────

    [Fact, Trait("Category", "PhaseC-Relay"), Trait("Wave", "Phase-I-3")]
    public async Task LateJoin_ToExistingGameId_ReceivesAccumulatedSnapshot_ForThatGameOnly()
    {
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();

        await using var alice = await OpenAndJoinAsync("MULTI-A", seat: 0);
        await using var bob = await OpenAndJoinAsync("MULTI-B", seat: 1);

        // Alice pushes a "seats" entry into MULTI-A.
        var aliceSeat = JsonSerializer.SerializeToElement(new { seat = 0 });
        await alice.SendUpdateAsync(new[] { new object[] { "seats", alice.PlayerId, aliceSeat } });

        // Bob pushes a distinct "things" entry into MULTI-B (different
        // collection + key so we can tell them apart in Charlie's snapshot).
        //
        // Bishop W25 — the key MUST be outside the tile-id range
        // (AutotableSlotMap.TotalTiles = 108) to avoid colliding with the
        // ChangshaToAutotableTranslator's synthetic face-down wall, which
        // emits keys 0..107 during Seating / RollingDice phases when the
        // runtime state has no real wall yet. Bob's key was 42L before,
        // which collided with the synthetic wall and caused intermittent
        // false positives. We also tag the value with a unique marker so
        // even a coincidental key collision can't pass the assertion.
        const long bobUniqueKey = 999_999_042L;
        const string bobMarker = "bishop-w25-multi-b-marker";
        var bobThing = JsonSerializer.SerializeToElement(new { slotName = "hand.0@0", rotationIndex = 1, marker = bobMarker });
        await bob.SendUpdateAsync(new[] { new object[] { "things", bobUniqueKey, bobThing } });

        // Wait for both UPDATEs to land in the per-game stores before Charlie joins.
        await WaitForAsync(
            () => manager.GetStoredEntryCount("MULTI-A") >= 1 && manager.GetStoredEntryCount("MULTI-B") >= 1,
            timeoutMs: 2000);

        await using var charlie = await OpenAndJoinAsync("MULTI-A", seat: 2);

        var snapshot = charlie.LastSnapshot!.Value;
        var entries = snapshot.GetProperty("entries");

        var sawAliceSeat = false;
        var sawBobThing = false;
        for (var i = 0; i < entries.GetArrayLength(); i++)
        {
            var kind = entries[i][0].GetString();
            if (kind == "seats" && entries[i][1].GetString() == alice.PlayerId)
            {
                sawAliceSeat = true;
            }
            if (kind == "things" && entries[i][1].ValueKind == JsonValueKind.Number
                && entries[i][1].GetInt64() == bobUniqueKey)
            {
                // Defence-in-depth: also require the marker so a key
                // collision (e.g. a future translator change reintroducing
                // wide-range synthetic ids) can't accidentally pass.
                if (entries[i][2].ValueKind == JsonValueKind.Object
                    && entries[i][2].TryGetProperty("marker", out var m)
                    && m.ValueKind == JsonValueKind.String
                    && m.GetString() == bobMarker)
                {
                    sawBobThing = true;
                }
            }
        }

        Assert.True(sawAliceSeat,
            "Charlie joined MULTI-A and must receive Alice's seats[alice] entry in the full snapshot.");
        Assert.False(sawBobThing,
            $"Charlie joined MULTI-A and must NOT see Bob's things[{bobUniqueKey}] (which lives in MULTI-B).");
    }

    // ── #3 — Concurrent NEW under different gameIds doesn't collide ──────

    [Fact, Trait("Category", "PhaseC-Relay"), Trait("Wave", "Phase-I-3")]
    public async Task Concurrent_New_InDifferentGameIds_DoesNotCollide()
    {
        // Open both sockets in parallel; each sends NEW with the query-string
        // gameId honored by Bishop's HandleNewAsync fallback.
        var openA = OpenWithQueryAsync(gameId: "NEW-A", seat: 0, variant: "four_player");
        var openB = OpenWithQueryAsync(gameId: "NEW-B", seat: 0, variant: "four_player");
        await Task.WhenAll(openA, openB);

        await using var sessionA = await openA;
        await using var sessionB = await openB;

        var sendA = sessionA.SendNewAsync();
        var sendB = sessionB.SendNewAsync();
        await Task.WhenAll(sendA, sendB);

        // Each connection's NEW reply is JOINED scoped to its own gameId, then
        // an empty (match-only) initial full snapshot.
        var joinedA = await sessionA.ReadEnvelopeAsync();
        Assert.Equal("JOINED", joinedA.GetProperty("type").GetString());
        Assert.Equal("NEW-A", joinedA.GetProperty("gameId").GetString());
        Assert.True(joinedA.GetProperty("isFirst").GetBoolean());

        var joinedB = await sessionB.ReadEnvelopeAsync();
        Assert.Equal("JOINED", joinedB.GetProperty("type").GetString());
        Assert.Equal("NEW-B", joinedB.GetProperty("gameId").GetString());
        Assert.True(joinedB.GetProperty("isFirst").GetBoolean());

        var snapshotA = await sessionA.ReadEnvelopeAsync();
        Assert.Equal("UPDATE", snapshotA.GetProperty("type").GetString());
        Assert.True(snapshotA.GetProperty("full").GetBoolean());
        sessionA.PlayerId = joinedA.GetProperty("playerId").GetString() ?? throw new InvalidOperationException("no playerId");

        var snapshotB = await sessionB.ReadEnvelopeAsync();
        Assert.Equal("UPDATE", snapshotB.GetProperty("type").GetString());
        Assert.True(snapshotB.GetProperty("full").GetBoolean());
        sessionB.PlayerId = joinedB.GetProperty("playerId").GetString() ?? throw new InvalidOperationException("no playerId");
        Assert.False(snapshotA.TryGetProperty("viewer", out _));
        Assert.False(snapshotB.TryGetProperty("viewer", out _));
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        Assert.Null(manager.GetRuntimeGameIdBoundTo("NEW-A"));
        Assert.Null(manager.GetRuntimeGameIdBoundTo("NEW-B"));

        // Cross-talk probe — A pushes a mouse entry; B must NOT see it.
        var entryValue = JsonSerializer.SerializeToElement(new { x = 9.0, y = 9.0, z = 9.0 });
        await sessionA.SendUpdateAsync(new[] { new object[] { "mouse", sessionA.PlayerId, entryValue } });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await sessionB.ReadEnvelopeAsync(timeoutMs: 500);
        });
    }

    // ── #4 — Validation contract (control chars + length cap) ────────────

    [Theory, Trait("Category", "PhaseC-Relay"), Trait("Wave", "Phase-I-3")]
    [InlineData("ab%00c", "gameId contains control characters")]
    [InlineData("ab%07c", "gameId contains control characters")]
    [InlineData("ab%0Ac", "gameId contains control characters")]
    public async Task GameId_Validation_RejectsControlChars(string encodedGameId, string expectedReason)
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var uri = new Uri(server.BaseAddress, $"autotable/ws?gameId={encodedGameId}&seat=0");

        // The server validates ?gameId= at the top of HandleConnectionAsync;
        // the WS handshake itself succeeds (the upgrade has already completed
        // by the time the manager inspects the query), then the server sends
        // a Close frame with PolicyViolation + the reason string.
        var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var buffer = new byte[1024];
            var result = await ws.ReceiveAsync(buffer, cts.Token);

            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, result.CloseStatus);
            Assert.Equal(expectedReason, result.CloseStatusDescription);
        }
        finally
        {
            ws.Dispose();
        }
    }

    [Fact, Trait("Category", "PhaseC-Relay"), Trait("Wave", "Phase-I-3")]
    public async Task GameId_Validation_RejectsOverLengthIds()
    {
        // 65 chars after URL-decode — one over Bishop's MaxGameIdLength = 64.
        var tooLong = new string('a', AutotableWsEndpoint.MaxGameIdLength + 1);

        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var uri = new Uri(server.BaseAddress, $"autotable/ws?gameId={tooLong}&seat=0");

        var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var buffer = new byte[1024];
            var result = await ws.ReceiveAsync(buffer, cts.Token);

            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, result.CloseStatus);
            Assert.Equal("gameId too long", result.CloseStatusDescription);
        }
        finally
        {
            ws.Dispose();
        }
    }

    [Fact, Trait("Category", "PhaseC-Relay"), Trait("Wave", "Phase-I-3")]
    public async Task GameId_Validation_AcceptsMaxLengthBoundary()
    {
        // Exactly 64 chars — the boundary case; must NOT be rejected.
        var atCap = new string('a', AutotableWsEndpoint.MaxGameIdLength);

        await using var session = await OpenWithQueryAsync(gameId: atCap, seat: 0);
        await session.SendNewAsync();

        var joined = await session.ReadEnvelopeAsync();
        Assert.Equal("JOINED", joined.GetProperty("type").GetString());
        Assert.Equal(atCap, joined.GetProperty("gameId").GetString());
    }

    // ── #5 — Empty / missing gameId falls back to DefaultGameId ──────────

    [Fact, Trait("Category", "PhaseC-Relay"), Trait("Wave", "Phase-I-3")]
    public async Task GameId_EmptyOrMissing_FallsBackToDefault()
    {
        // Two connections — one omits the query entirely, the other passes
        // ?gameId= with an empty value (whitespace-equivalent per TryNormalizeGameId).
        await using var noQueryParam = await OpenAndJoinAsync_NoGameIdQuery(seat: 0);
        await using var emptyQueryParam = await OpenAndJoinAsync_EmptyGameIdQuery(seat: 1);

        Assert.Equal(AutotableWsEndpoint.DefaultGameId, noQueryParam.JoinedGameId);
        Assert.Equal(AutotableWsEndpoint.DefaultGameId, emptyQueryParam.JoinedGameId);

        // Both connections share the same default game — UPDATE from one
        // reaches the other (mirrors legacy single-game-per-instance behaviour).
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var entryValue = JsonSerializer.SerializeToElement(new { x = 1.0, y = 2.0, z = 3.0 });
        await noQueryParam.SendUpdateAsync(new[] { new object[] { "mouse", noQueryParam.PlayerId, entryValue } });

        // The peer receives the relay → confirms both ended up in the same gameId.
        var relayed = await emptyQueryParam.ReadEnvelopeAsync(timeoutMs: 2000);
        Assert.Equal("UPDATE", relayed.GetProperty("type").GetString());
        Assert.False(relayed.GetProperty("full").GetBoolean());

        Assert.True(manager.GetStoredEntryCount(AutotableWsEndpoint.DefaultGameId) >= 1,
            "DefaultGameId game state must have accumulated entries from the fallback connections.");
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private async Task<RelaySession> OpenAndJoinAsync(string gameId, int seat)
    {
        var session = await OpenAsync(seat);
        await session.SendJoinAsync(gameId);

        var joined = await session.ReadEnvelopeAsync();
        Assert.Equal("JOINED", joined.GetProperty("type").GetString());
        Assert.Equal(gameId, joined.GetProperty("gameId").GetString());
        session.PlayerId = joined.GetProperty("playerId").GetString() ?? throw new InvalidOperationException("no playerId");

        var snapshot = await session.ReadEnvelopeAsync();
        Assert.Equal("UPDATE", snapshot.GetProperty("type").GetString());
        Assert.True(snapshot.GetProperty("full").GetBoolean());
        session.LastSnapshot = snapshot;
        return session;
    }

    private Task<RelaySession> OpenAsync(int seat)
        => OpenInternalAsync(query: $"?seat={seat}");

    private Task<RelaySession> OpenWithQueryAsync(string gameId, int seat, string variant = "changsha")
        => OpenInternalAsync(query: $"?gameId={Uri.EscapeDataString(gameId)}&seat={seat}&variant={Uri.EscapeDataString(variant)}");

    private async Task<RelaySession> OpenInternalAsync(string query)
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var uri = new Uri(server.BaseAddress, $"autotable/ws{query}");
        var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);
        return new RelaySession(ws);
    }

    private async Task<RelaySession> OpenAndJoinAsync_NoGameIdQuery(int seat)
    {
        var session = await OpenAsync(seat);
        // JOIN with empty/null body → falls back through the priority chain
        // to DefaultGameId (no ?gameId= was supplied on the handshake either).
        await session.SendJoinAsync(gameId: null);

        var joined = await session.ReadEnvelopeAsync();
        Assert.Equal("JOINED", joined.GetProperty("type").GetString());
        session.JoinedGameId = joined.GetProperty("gameId").GetString();
        session.PlayerId = joined.GetProperty("playerId").GetString() ?? throw new InvalidOperationException("no playerId");

        var snapshot = await session.ReadEnvelopeAsync();
        Assert.Equal("UPDATE", snapshot.GetProperty("type").GetString());
        Assert.True(snapshot.GetProperty("full").GetBoolean());
        session.LastSnapshot = snapshot;
        return session;
    }

    private async Task<RelaySession> OpenAndJoinAsync_EmptyGameIdQuery(int seat)
    {
        // ?gameId= with no value — TryNormalizeGameId returns true + normalized=null,
        // so HandleConnectionAsync proceeds with connection.GameId=null. The JOIN's
        // empty gameId then falls back to DefaultGameId.
        var session = await OpenInternalAsync(query: $"?gameId=&seat={seat}");
        await session.SendJoinAsync(gameId: "");

        var joined = await session.ReadEnvelopeAsync();
        Assert.Equal("JOINED", joined.GetProperty("type").GetString());
        session.JoinedGameId = joined.GetProperty("gameId").GetString();
        session.PlayerId = joined.GetProperty("playerId").GetString() ?? throw new InvalidOperationException("no playerId");

        var snapshot = await session.ReadEnvelopeAsync();
        Assert.Equal("UPDATE", snapshot.GetProperty("type").GetString());
        Assert.True(snapshot.GetProperty("full").GetBoolean());
        session.LastSnapshot = snapshot;
        return session;
    }

    private static async Task WaitForAsync(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
    }

    /// <summary>Mirrors the <see cref="AutotableWsRelayTests"/> session helper
    /// surface — kept local to this file to keep test files self-contained and
    /// avoid coupling a new test suite to an existing one's private types.</summary>
    private sealed class RelaySession : IAsyncDisposable
    {
        private readonly WebSocket _ws;
        public string PlayerId { get; set; } = string.Empty;
        public string? JoinedGameId { get; set; }
        public JsonElement? LastSnapshot { get; set; }

        public RelaySession(WebSocket ws) { _ws = ws; }

        public async Task SendNewAsync()
        {
            var msg = JsonSerializer.Serialize(new { type = "NEW" });
            await SendRawAsync(msg);
        }

        public async Task SendJoinAsync(string? gameId)
        {
            // Serialize {type:"JOIN", gameId:<value>} or {type:"JOIN", gameId:null}
            // explicitly so the server's deserializer hits the same code path as
            // a real client omitting / clearing the field.
            using var ms = new MemoryStream();
            await using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "JOIN");
                writer.WritePropertyName("gameId");
                if (gameId is null) writer.WriteNullValue(); else writer.WriteStringValue(gameId);
                writer.WriteEndObject();
            }
            await SendRawAsync(Encoding.UTF8.GetString(ms.ToArray()));
        }

        public async Task SendUpdateAsync(IEnumerable<object[]> entries)
        {
            var entriesList = entries.ToList();
            using var ms = new MemoryStream();
            await using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "UPDATE");
                writer.WritePropertyName("entries");
                writer.WriteStartArray();
                foreach (var e in entriesList)
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue((string)e[0]);

                    switch (e[1])
                    {
                        case string s: writer.WriteStringValue(s); break;
                        case int i: writer.WriteNumberValue(i); break;
                        case long l: writer.WriteNumberValue(l); break;
                        default: throw new InvalidOperationException("unsupported key type");
                    }

                    if (e[2] is JsonElement je) je.WriteTo(writer);
                    else JsonSerializer.Serialize(writer, e[2]);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteBoolean("full", false);
                writer.WriteEndObject();
            }
            await SendRawAsync(Encoding.UTF8.GetString(ms.ToArray()));
        }

        public async Task SendRawAsync(string payload)
        {
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
