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
/// CAT-PHASE-C-RELAY: end-to-end relay tests for the bidirectional autotable
/// WS pipe. Each test opens 1+ in-memory <see cref="WebSocket"/> client(s)
/// against the real ASP.NET pipeline (via <see cref="WebApplicationFactory{TEntryPoint}"/>),
/// sends bundle-shaped JSON messages, and verifies the per-game state store +
/// broadcast behaviour described in
/// <c>.squad/decisions.md "Architectural Pivot Plan (Ripley — 2026-05-13)" §2
/// Phase C-relay scope</c>.
/// </summary>
public class AutotableWsRelayTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = System.IO.Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = System.IO.Path.Combine(dataDir, $"mahjong-relay-{Guid.NewGuid():N}.db");

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

    // ── 1. Two connections, same gameId — sender's UPDATE reaches the other ──

    [Fact, Trait("Category", "PhaseC-Relay")]
    public async Task Update_FromOneConnection_IsRelayed_ToPeerInSameGame()
    {
        await using var alice = await OpenAndJoinAsync("GAME-A", seat: 0);
        await using var bob = await OpenAndJoinAsync("GAME-A", seat: 1);

        // Use a "mouse" entry (cosmetic, non-runtime-routed in Phase D-backend)
        // so the relay path is exercised in isolation from the Changsha routing.
        var entryValue = JsonSerializer.SerializeToElement(new { x = 1.0, y = 2.0, z = 3.0 });
        await alice.SendUpdateAsync(new[] { new object[] { "mouse", alice.PlayerId, entryValue } });

        var relayed = await bob.ReadEnvelopeAsync(timeoutMs: 2000);
        Assert.Equal("UPDATE", relayed.GetProperty("type").GetString());
        Assert.False(relayed.GetProperty("full").GetBoolean());

        var entries = relayed.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal("mouse", entries[0][0].GetString());
        Assert.Equal(alice.PlayerId, entries[0][1].GetString());
        Assert.Equal(1.0, entries[0][2].GetProperty("x").GetDouble());
    }

    // ── 2. Sender does NOT receive its own UPDATE back ────────────────

    [Fact, Trait("Category", "PhaseC-Relay")]
    public async Task Update_SenderDoesNotReceiveOwnMessageBack()
    {
        await using var alice = await OpenAndJoinAsync("GAME-B", seat: 0);
        await using var bob = await OpenAndJoinAsync("GAME-B", seat: 1);

        var entryValue = JsonSerializer.SerializeToElement(new { x = 1.0, y = 2.0, z = 3.0 });
        await alice.SendUpdateAsync(new[] { new object[] { "mouse", alice.PlayerId, entryValue } });

        // Bob must receive it.
        _ = await bob.ReadEnvelopeAsync(timeoutMs: 2000);

        // Alice must NOT — give the relay plenty of time, then assert the
        // read times out.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await alice.ReadEnvelopeAsync(timeoutMs: 500);
        });
    }

    [Theory, Trait("Category", "PhaseC-Relay")]
    [InlineData("four_player")]
    [InlineData("four-player")]
    [InlineData("three_player")]
    [InlineData("three-player")]
    [InlineData("bamboo")]
    [InlineData("minefield")]
    public async Task RelaySeatTake_ConfirmsOriginAndPeers_AndReplaysCurrentOwnership(string variant)
    {
        var gameId = $"relay-seat-{Guid.NewGuid():N}";
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        await using var origin = await OpenAndJoinAsync(gameId, seat: 0, variant);
        AssertSeatSnapshot(origin.LastSnapshot!.Value);

        // A connected Collection does not apply its own set(): even the only
        // player needs the server's UPDATE before client.seat becomes owned.
        var take = new object[] { "seats", origin.PlayerId, new { seat = 0 } };
        await origin.SendUpdateAsync(new[] { take });
        AssertRelayDelta(await origin.ReadEnvelopeAsync(timeoutMs: 2000), take);

        await using var peer = await OpenAndJoinAsync(gameId, seat: 1, variant);
        AssertSeatSnapshot(peer.LastSnapshot!.Value, take);

        foreach (var value in new object?[] { new { seat = 1 }, new { seat = (int?)null }, null })
        {
            var update = new object[] { "seats", origin.PlayerId, value! };
            await origin.SendUpdateAsync(new[] { update });
            AssertRelayDelta(await origin.ReadEnvelopeAsync(timeoutMs: 2000), update);
            AssertRelayDelta(await peer.ReadEnvelopeAsync(timeoutMs: 2000), update);
        }
        Assert.Equal(0, manager.GetStoredEntryCount(gameId, "seats"));

        var peerTake = new object[] { "seats", peer.PlayerId, new { seat = 0 } };
        await peer.SendUpdateAsync(new[] { peerTake });
        AssertRelayDelta(await peer.ReadEnvelopeAsync(timeoutMs: 2000), peerTake);
        AssertRelayDelta(await origin.ReadEnvelopeAsync(timeoutMs: 2000), peerTake);

        await using var lateJoiner = await OpenAndJoinAsync(gameId, seat: 2, variant);
        AssertSeatSnapshot(lateJoiner.LastSnapshot!.Value, peerTake);
        Assert.Null(manager.GetRuntimeGameIdBoundTo(gameId));
    }

    [Theory, Trait("Category", "PhaseC-Relay")]
    [InlineData("four_player")]
    [InlineData("four-player")]
    [InlineData("three_player")]
    [InlineData("three-player")]
    [InlineData("bamboo")]
    [InlineData("minefield")]
    public async Task RelaySetupTransaction_ConfirmsUnmodifiedEntriesToOrigin(string variant)
    {
        var gameId = $"relay-setup-{Guid.NewGuid():N}";
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        await using var origin = await OpenAndJoinAsync(gameId, seat: 0, variant);
        var entries = new[]
        {
            new object[] { "unique", "seats", "seat" },
            new object[] { "perPlayer", "seats", true },
            new object[] { "seats", origin.PlayerId, new { seat = 0 } },
            new object[] { "match", 0, new
            {
                conditions = new { gameType = variant.Replace('-', '_').ToUpperInvariant() },
                dealer = 0,
                honba = 0
            } },
            new object[] { "things", 7, new { slotName = "hand.0@0", rotationIndex = 1 } },
            new object[] { "nicks", origin.PlayerId, "Relay origin" }
        };

        await origin.SendUpdateAsync(entries);
        AssertRelayDelta(await origin.ReadEnvelopeAsync(timeoutMs: 2000), entries);
        Assert.Equal(entries.Length, manager.GetStoredEntryCount(gameId));
        Assert.Null(manager.GetRuntimeGameIdBoundTo(gameId));
    }

    // ── C-1 anti-spoof: a client-pushed gameComplete is dropped, never relayed ──

    [Fact, Trait("Category", "PhaseC-Relay")]
    public async Task ClientPushedGameComplete_IsDropped_NotRelayedToPeers()
    {
        // #120/#122 (rejected-gate fix) — `gameComplete` is server-emitted only (inbound-only,
        // locked C-1). A changsha-mode client that pushes a fake
        // ["gameComplete","current",{isComplete:true}] MUST be dropped by the endpoint's explicit
        // `case ChangshaCollectionKinds.GameComplete: break;` — never relayed to peers — or a client
        // could spoof an end-of-game modal on other players. We send it alongside a benign cosmetic
        // `mouse` entry: the mouse MUST relay (proves the path is alive); the gameComplete MUST NOT.
        await using var alice = await OpenAndJoinAsync("GAME-GC-SPOOF", seat: 0);
        await using var bob = await OpenAndJoinAsync("GAME-GC-SPOOF", seat: 1);

        var spoof = JsonSerializer.SerializeToElement(new { isComplete = true, maxHands = 4 });
        var mouse = JsonSerializer.SerializeToElement(new { x = 9.0, y = 9.0, z = 9.0 });
        await alice.SendUpdateAsync(new[]
        {
            new object[] { "gameComplete", "current", spoof },
            new object[] { "mouse", alice.PlayerId, mouse },
        });

        var relayed = await bob.ReadEnvelopeAsync(timeoutMs: 2000);
        Assert.Equal("UPDATE", relayed.GetProperty("type").GetString());

        var kinds = new List<string>();
        foreach (var e in relayed.GetProperty("entries").EnumerateArray())
            kinds.Add(e[0].GetString() ?? string.Empty);

        Assert.Contains("mouse", kinds);              // benign cosmetic relayed → relay path alive
        Assert.DoesNotContain("gameComplete", kinds); // spoofed server-emitted kind was dropped
    }

    // ── 3. Late JOIN replays accumulated snapshot ─────────────────────

    [Fact, Trait("Category", "PhaseC-Relay")]
    public async Task LateJoin_ReceivesAccumulatedSnapshot_OfPriorUpdates()
    {
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();

        await using var alice = await OpenAndJoinAsync("GAME-C", seat: 0, "four_player");

        // Alice uploads three things entries.
        var thingValue1 = JsonSerializer.SerializeToElement(new { slotName = "hand.0@0", rotationIndex = 1 });
        var thingValue2 = JsonSerializer.SerializeToElement(new { slotName = "hand.1@0", rotationIndex = 1 });
        var thingValue3 = JsonSerializer.SerializeToElement(new { slotName = "hand.2@0", rotationIndex = 1 });
        await alice.SendUpdateAsync(new[]
        {
            new object[] { "things", 10L, thingValue1 },
            new object[] { "things", 11L, thingValue2 },
            new object[] { "things", 12L, thingValue3 }
        });

        // Wait until the server has applied Alice's UPDATE (defeat the race
        // between the WS-send completion and the server-side read loop). We
        // count "things" specifically — the aggregate count is non-deterministic
        // because translator-emitted match/seat entries may already push it
        // ≥3 before the UPDATE lands, causing the wait to fire prematurely.
        await WaitForAsync(
            () => manager.GetStoredEntryCount("GAME-C", "things") >= 3,
            timeoutMs: 5000,
            reason: "server never recorded all 3 things entries for GAME-C");

        // Now Bob joins late.
        await using var bob = await OpenAndJoinAsync("GAME-C", seat: 1, "four_player");

        // Bob's full snapshot must contain all three things entries that Alice uploaded.
        var snapshot = bob.LastSnapshot!.Value;
        Assert.Equal("UPDATE", snapshot.GetProperty("type").GetString());
        Assert.True(snapshot.GetProperty("full").GetBoolean());

        var entries = snapshot.GetProperty("entries");
        var thingEntries = new List<long>();
        for (var i = 0; i < entries.GetArrayLength(); i++)
        {
            if (entries[i][0].GetString() == "things")
            {
                thingEntries.Add(entries[i][1].GetInt64());
            }
        }
        Assert.Contains(10L, thingEntries);
        Assert.Contains(11L, thingEntries);
        Assert.Contains(12L, thingEntries);
    }

    // ── 3b. Late JOIN stability — re-run the scenario 50× ─────────────
    //
    // Phase J Wave 10 regression gate for the
    // <c>LateJoin_ReceivesAccumulatedSnapshot_OfPriorUpdates</c> flake. Loops
    // the same accumulated-snapshot path 50 times in-process to flush out the
    // server-side UPDATE → store → JOIN-snapshot race that intermittently let
    // a late joiner observe an empty/partial snapshot under CI load.
    // Trait("Category", "Stability") so the suite can be filtered for
    // dedicated stability runs.
    [Fact, Trait("Category", "PhaseC-Relay"), Trait("Category", "Stability"), Trait("Wave", "Phase-J-10")]
    public async Task LateJoin_ReceivesAccumulatedSnapshot_OfPriorUpdates_Stability50x()
    {
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();

        for (var iteration = 0; iteration < 50; iteration++)
        {
            var gameId = $"GAME-C-STAB-{iteration}";

            await using var alice = await OpenAndJoinAsync(gameId, seat: 0, "four_player");

            var thingValue1 = JsonSerializer.SerializeToElement(new { slotName = "hand.0@0", rotationIndex = 1 });
            var thingValue2 = JsonSerializer.SerializeToElement(new { slotName = "hand.1@0", rotationIndex = 1 });
            var thingValue3 = JsonSerializer.SerializeToElement(new { slotName = "hand.2@0", rotationIndex = 1 });
            await alice.SendUpdateAsync(new[]
            {
                new object[] { "things", 10L, thingValue1 },
                new object[] { "things", 11L, thingValue2 },
                new object[] { "things", 12L, thingValue3 }
            });

            await WaitForAsync(
                () => manager.GetStoredEntryCount(gameId, "things") >= 3,
                timeoutMs: 5000,
                reason: $"iteration {iteration}: server never recorded all 3 things entries for {gameId}");

            await using var bob = await OpenAndJoinAsync(gameId, seat: 1, "four_player");

            var snapshot = bob.LastSnapshot!.Value;
            Assert.Equal("UPDATE", snapshot.GetProperty("type").GetString());
            Assert.True(snapshot.GetProperty("full").GetBoolean(),
                $"iteration {iteration}: snapshot.full must be true");

            var entries = snapshot.GetProperty("entries");
            var thingEntries = new List<long>();
            for (var i = 0; i < entries.GetArrayLength(); i++)
            {
                if (entries[i][0].GetString() == "things")
                {
                    thingEntries.Add(entries[i][1].GetInt64());
                }
            }
            Assert.True(thingEntries.Contains(10L),
                $"iteration {iteration}: bob's snapshot missing things[10] (saw [{string.Join(",", thingEntries)}])");
            Assert.True(thingEntries.Contains(11L),
                $"iteration {iteration}: bob's snapshot missing things[11] (saw [{string.Join(",", thingEntries)}])");
            Assert.True(thingEntries.Contains(12L),
                $"iteration {iteration}: bob's snapshot missing things[12] (saw [{string.Join(",", thingEntries)}])");
        }
    }

    // ── 4. NEW gameId starts empty ────────────────────────────────────

    [Fact, Trait("Category", "PhaseC-Relay")]
    public async Task New_AllocatesGameId_WithEmptyState()
    {
        await using var session = await OpenAsync(seat: 0, variant: "four_player");
        await session.SendNewAsync();

        var joined = await session.ReadEnvelopeAsync();
        Assert.Equal("JOINED", joined.GetProperty("type").GetString());
        // Phase D-backend single-game: NEW resolves to the default gameId
        // (rather than a freshly allocated random id). Phase E will widen.
        Assert.Equal(AutotableWsEndpoint.DefaultGameId, joined.GetProperty("gameId").GetString());
        // NEW is by definition the first joiner of a fresh game — bundle's
        // sendOnConnect path relies on this.
        Assert.True(joined.GetProperty("isFirst").GetBoolean());
        Assert.False(joined.TryGetProperty("viewer", out _));

        var update = await session.ReadEnvelopeAsync();
        Assert.Equal("UPDATE", update.GetProperty("type").GetString());
        Assert.True(update.GetProperty("full").GetBoolean());
        Assert.False(update.TryGetProperty("viewer", out _));

        // A brand-new game with no Changsha runtime backing it ships only the
        // translator's match[0] override (forces fives='000').
        var entries = update.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal("match", entries[0][0].GetString());
        Assert.Null(_factory!.Services.GetRequiredService<AutotableConnectionManager>()
            .GetRuntimeGameIdBoundTo(joined.GetProperty("gameId").GetString()!));
        Assert.Equal(0, _factory.Services.GetRequiredService<IChangshaGameRuntime>().GameCount);
    }

    // ── 5. Per-game isolation — UPDATE in game A doesn't leak to game B ─

    // Phase I Wave 3 — unskipped after Bishop lifted DefaultGameId coercion
    // in HandleNewAsync/HandleJoinAsync. _games is keyed per-gameId so the
    // isolation check is now exercised end-to-end.
    [Fact, Trait("Category", "PhaseC-Relay"), Trait("Wave", "Phase-I-3")]
    public async Task Update_IsIsolated_PerGameId()
    {
        await using var aliceGameA = await OpenAndJoinAsync("ISO-A", seat: 0);
        await using var bobGameB = await OpenAndJoinAsync("ISO-B", seat: 0);

        var entryValue = JsonSerializer.SerializeToElement(new { seat = 0 });
        await aliceGameA.SendUpdateAsync(new[] { new object[] { "seats", aliceGameA.PlayerId, entryValue } });

        // Bob in the other game must not see it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await bobGameB.ReadEnvelopeAsync(timeoutMs: 500);
        });
    }

    // ── 6. Last-player disconnect cleans up the game state ────────────

    [Fact, Trait("Category", "PhaseC-Relay")]
    public async Task Disconnect_OfLastPlayer_ClearsGameState()
    {
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();
        var initialGameCount = manager.GameCount;

        var alice = await OpenAndJoinAsync("DROP-A", seat: 0, "four_player");
        var thingValue = JsonSerializer.SerializeToElement(new { slotName = "hand.0@0", rotationIndex = 1 });
        await alice.SendUpdateAsync(new[] { new object[] { "things", 5L, thingValue } });
        await WaitForAsync(() => manager.GetStoredEntryCount("DROP-A") >= 1, timeoutMs: 2000);

        // Sanity — game exists.
        Assert.True(manager.GameCount >= initialGameCount + 1);

        await alice.DisposeAsync();
        await WaitForAsync(() => manager.GameCount <= initialGameCount, timeoutMs: 2000);
        Assert.True(manager.GameCount <= initialGameCount,
            $"expected game state to be cleared on last disconnect (gameCount={manager.GameCount})");

        // A fresh joiner to the same gameId should now see no things entries
        // (state was cleared).
        await using var revisit = await OpenAndJoinAsync("DROP-A", seat: 0, "four_player");
        var snapshot = revisit.LastSnapshot!.Value;
        var entries = snapshot.GetProperty("entries");
        for (var i = 0; i < entries.GetArrayLength(); i++)
        {
            if (entries[i][0].GetString() == "things")
            {
                Assert.Fail($"expected no things entries after game cleanup, but found tile id {entries[i][1]}");
            }
        }
    }

    // ── 7. Non-last-player disconnect keeps the game alive ────────────

    [Fact, Trait("Category", "PhaseC-Relay")]
    public async Task Disconnect_OfNonLastPlayer_KeepsGameStateAlive()
    {
        var manager = _factory!.Services.GetRequiredService<AutotableConnectionManager>();

        var alice = await OpenAndJoinAsync("KEEP-A", seat: 0, "four_player");
        await using var bob = await OpenAndJoinAsync("KEEP-A", seat: 1, "four_player");

        var thingValue = JsonSerializer.SerializeToElement(new { slotName = "hand.0@0", rotationIndex = 1 });
        await alice.SendUpdateAsync(new[] { new object[] { "things", 7L, thingValue } });

        // Bob receives the relay (one update).
        _ = await bob.ReadEnvelopeAsync(timeoutMs: 2000);
        // And the store has applied it.
        await WaitForAsync(() => manager.GetStoredEntryCount("KEEP-A") >= 1, timeoutMs: 2000);

        // Alice leaves; Bob stays.
        await alice.DisposeAsync();
        // Give the disconnect a moment to be observed by the server-side cleanup.
        await Task.Delay(200);

        // A new joiner to the same game must still receive the prior thing.
        await using var carol = await OpenAndJoinAsync("KEEP-A", seat: 2, "four_player");
        var snapshot = carol.LastSnapshot!.Value;
        var entries = snapshot.GetProperty("entries");
        var seenTileId = false;
        for (var i = 0; i < entries.GetArrayLength(); i++)
        {
            if (entries[i][0].GetString() == "things" && entries[i][1].GetInt64() == 7L)
            {
                seenTileId = true;
                break;
            }
        }
        Assert.True(seenTileId, "expected things[7] to survive Alice's disconnect because Bob is still in the game");
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static void AssertRelayDelta(JsonElement envelope, params object[][] expectedEntries)
    {
        Assert.Equal("UPDATE", envelope.GetProperty("type").GetString());
        Assert.False(envelope.GetProperty("full").GetBoolean());
        Assert.Equal(JsonSerializer.Serialize(expectedEntries), envelope.GetProperty("entries").GetRawText());
    }

    private static void AssertSeatSnapshot(JsonElement envelope, params object[][] expectedEntries)
    {
        Assert.Equal("UPDATE", envelope.GetProperty("type").GetString());
        Assert.True(envelope.GetProperty("full").GetBoolean());
        var seats = envelope.GetProperty("entries").EnumerateArray()
            .Where(entry => entry[0].GetString() == "seats").ToArray();
        Assert.Equal(JsonSerializer.Serialize(expectedEntries), JsonSerializer.Serialize(seats));
    }

    private async Task<RelaySession> OpenAsync(int seat, string variant = "changsha")
    {
        var server = _factory!.Server;
        var wsClient = server.CreateWebSocketClient();
        var uri = new Uri(server.BaseAddress, $"autotable/ws?variant={variant}&seat={seat}");
        var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);
        return new RelaySession(ws);
    }

    private async Task<RelaySession> OpenAndJoinAsync(string gameId, int seat, string variant = "changsha")
    {
        var session = await OpenAsync(seat, variant);
        await session.SendJoinAsync(gameId);

        // JOIN reply is JOINED + the initial full UPDATE snapshot.
        var joined = await session.ReadEnvelopeAsync();
        Assert.Equal("JOINED", joined.GetProperty("type").GetString());
        session.PlayerId = joined.GetProperty("playerId").GetString() ?? throw new InvalidOperationException("no playerId");

        var snapshot = await session.ReadEnvelopeAsync();
        Assert.Equal("UPDATE", snapshot.GetProperty("type").GetString());
        Assert.True(snapshot.GetProperty("full").GetBoolean());
        session.LastSnapshot = snapshot;
        return session;
    }

    private static async Task WaitForAsync(Func<bool> predicate, int timeoutMs, string? reason = null)
    {
        // Phase J Wave 10 — hard-assert on timeout. Previously this helper
        // returned silently when the deadline elapsed, which let the
        // `LateJoin_ReceivesAccumulatedSnapshot_OfPriorUpdates` flake (and any
        // future race-style test) limp forward and fail downstream with a
        // misleading assertion. By raising on timeout the failing test now
        // points at the actual stuck precondition instead.
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
        if (!predicate())
        {
            throw new Xunit.Sdk.XunitException(
                $"WaitForAsync timed out after {timeoutMs}ms" +
                (string.IsNullOrEmpty(reason) ? "" : $": {reason}"));
        }
    }

    private sealed class RelaySession : IAsyncDisposable
    {
        private readonly WebSocket _ws;
        public string PlayerId { get; set; } = string.Empty;
        public JsonElement? LastSnapshot { get; set; }

        public RelaySession(WebSocket ws) { _ws = ws; }

        public async Task SendNewAsync()
        {
            var msg = JsonSerializer.Serialize(new { type = "NEW" });
            await SendRawAsync(msg);
        }

        public async Task SendJoinAsync(string gameId)
        {
            var msg = JsonSerializer.Serialize(new { type = "JOIN", gameId });
            await SendRawAsync(msg);
        }

        public async Task SendUpdateAsync(IEnumerable<object[]> entries)
        {
            var entriesList = entries.ToList();
            // Write the entries as raw arrays so the on-the-wire shape matches
            // upstream's [kind, key, value] tuple exactly.
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

                    if (e[2] is JsonElement je)
                    {
                        je.WriteTo(writer);
                    }
                    else
                    {
                        JsonSerializer.Serialize(writer, e[2]);
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteBoolean("full", false);
                writer.WriteEndObject();
            }
            var payload = Encoding.UTF8.GetString(ms.ToArray());
            await SendRawAsync(payload);
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
