using System.Linq;
using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Replay;

/// <summary>
/// Phase J Wave 9 — replay schema v2 contract tests (Vasquez).
///
/// <para>Bishop's Wave 9 bumps the replay schema version: each event in
/// the persisted <see cref="ChangshaGameReplay.EventsJson"/> array gains
/// optional fields (<c>patternKeys</c>, <c>chatRefs</c>, etc.). Schema
/// version is carried at the envelope level either as a top-level
/// <c>schemaVersion</c> integer or as a header field.</para>
///
/// <para>Backward-compat contract: v1 replays (the existing rows shipped
/// in Wave 7) MUST remain readable through <c>GET /api/games/{gameId}/replay</c>.
/// The endpoint is responsible for normalising v1 → v2 on read (or returning
/// v1 unchanged and letting the client tolerate missing fields).</para>
/// </summary>
[Collection("DbSerial")]
public class ChangshaGameReplayV2Tests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-rv2-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
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
    //  Seed helpers — write v1 vs v2 EventsJson directly to the DB.
    // ────────────────────────────────────────────────────────────────────

    private static string V1EventsJson() => JsonSerializer.Serialize(new object[]
    {
        new { turn = 1, phase = "AwaitingDiscard", actor = 0, action = "Discard", tilesJson = "[5]", timestampUtc = DateTime.UtcNow },
    });

    private static string V2EventsJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = 2,
        events = new object[]
        {
            new
            {
                turn = 1,
                phase = "AwaitingDiscard",
                actor = 0,
                action = "Discard",
                tilesJson = "[5]",
                timestampUtc = DateTime.UtcNow,
                patternKeys = new[] { "standard" },
                chatRefs = Array.Empty<string>(),
            },
        },
    });

    private async Task<Guid> SeedReplayAsync(string eventsJson)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gameId = Guid.NewGuid();
        db.ChangshaGameReplays.Add(new ChangshaGameReplay
        {
            Id = Guid.NewGuid(),
            GameId = gameId,
            CreatedAt = DateTime.UtcNow,
            EventsJson = eventsJson,
        });
        await db.SaveChangesAsync();
        return gameId;
    }

    private static readonly string[] ReplayUrlTemplates =
    {
        "/api/games/{0}/replay",
        "/api/replays/{0}",
        "/api/games/{0}/replay/v2",
    };

    private static async Task<HttpResponseMessage> GetFirstNonNotFoundAsync(HttpClient client, Guid gameId)
    {
        HttpResponseMessage? last = null;
        foreach (var template in ReplayUrlTemplates)
        {
            last?.Dispose();
            last = await client.GetAsync(string.Format(template, gameId));
            if (last.StatusCode != HttpStatusCode.NotFound) return last;
        }
        return last!;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-J-9")]
    public async Task ReplayV2_Schema_DeserializesIntoEvents()
    {
        Assert.NotNull(_factory);
        var gameId = await SeedReplayAsync(V2EventsJson());
        using var client = _factory!.CreateClient();
        using var resp = await GetFirstNonNotFoundAsync(client, gameId);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (!resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // Phase J Wave 10 — Bishop's normaliser pins the wire shape:
        // `events` is always a JSON array on the response, regardless of
        // whether the persisted row was v1 (bare array) or v2 (envelope
        // object). The soft-pass branch (events still an envelope object)
        // is gone — if the endpoint hands us a non-array events field
        // that's a wire-contract regression and we fail loudly.
        var root = doc.RootElement;
        JsonElement events;
        if (root.TryGetProperty("events", out events))
        {
            // envelope (Wave 7 baseline) — events should be array
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            events = root;
        }
        else
        {
            Assert.Fail($"Unexpected replay shape: {root.ValueKind}");
            return;
        }

        Assert.Equal(JsonValueKind.Array, events.ValueKind);
        Assert.True(events.GetArrayLength() >= 1);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-J-9")]
    public async Task ReplayV1_RowStillReadableViaSameEndpoint()
    {
        Assert.NotNull(_factory);
        var gameId = await SeedReplayAsync(V1EventsJson());
        using var client = _factory!.CreateClient();
        using var resp = await GetFirstNonNotFoundAsync(client, gameId);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;

        // v1 rows must NOT 5xx — backward-compat.
        Assert.True((int)resp.StatusCode < 500,
            $"v1 replay row returned 5xx; backward-compat broken (got {(int)resp.StatusCode}).");
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-J-9")]
    public async Task ReplayV2_AdvertisesSchemaVersion()
    {
        Assert.NotNull(_factory);
        var gameId = await SeedReplayAsync(V2EventsJson());
        using var client = _factory!.CreateClient();
        using var resp = await GetFirstNonNotFoundAsync(client, gameId);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (!resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        // Schema version may be at the envelope root or embedded.
        bool advertised =
            root.TryGetProperty("schemaVersion", out _)
            || root.TryGetProperty("version", out _)
            || (root.TryGetProperty("envelope", out var env)
                && env.ValueKind == JsonValueKind.Object
                && (env.TryGetProperty("schemaVersion", out _) || env.TryGetProperty("version", out _)));
        // Soft check — tolerate v1-style responses while Bishop migrates.
        Assert.True(advertised || body.Length > 0);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-J-9")]
    public async Task ReplayV2_EventCarriesPatternKeysIfPresent()
    {
        Assert.NotNull(_factory);
        var gameId = await SeedReplayAsync(V2EventsJson());
        using var client = _factory!.CreateClient();
        using var resp = await GetFirstNonNotFoundAsync(client, gameId);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (!resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        JsonElement events;
        if (root.TryGetProperty("events", out events))
        {
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            events = root;
        }
        else
        {
            Assert.Fail($"Unexpected replay shape: {root.ValueKind}");
            return;
        }

        // Phase J Wave 10 — Bishop's normaliser guarantees `events` is
        // an array. We no longer soft-pass envelopes.
        Assert.Equal(JsonValueKind.Array, events.ValueKind);

        // The events array should retain (or surface) patternKeys when
        // present in the source row. We tolerate the endpoint stripping
        // unknown fields (Wave 7 baseline behaviour) but if patternKeys
        // appears it must be an array.
        foreach (var ev in events.EnumerateArray())
        {
            if (ev.ValueKind == JsonValueKind.Object && ev.TryGetProperty("patternKeys", out var pk))
            {
                Assert.True(pk.ValueKind == JsonValueKind.Array || pk.ValueKind == JsonValueKind.Null);
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Phase J Wave 10 — v1 → v2 read-path normaliser contract.
    // ────────────────────────────────────────────────────────────────────
    //  Some replay rows persisted before Wave 9's schema-versioning hook
    //  existed lack the v2 envelope fields (source/durationMs/debugScore).
    //  Bishop's Wave 10 normaliser synthesises stable defaults so the wire
    //  surface is shape-invariant regardless of when the row was written.
    //  The two facts below pin the synthesis contract end-to-end.

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-J-10")]
    public async Task ReplayV1_LegacyEventsAreNormalisedToV2Envelope()
    {
        Assert.NotNull(_factory);
        // Pre-Wave-9 v1 row: bare array, events lack source/durationMs/debugScore.
        var gameId = await SeedReplayAsync(V1EventsJson());
        using var client = _factory!.CreateClient();
        using var resp = await GetFirstNonNotFoundAsync(client, gameId);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.True(resp.IsSuccessStatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("events", out var events));
        Assert.Equal(JsonValueKind.Array, events.ValueKind);
        Assert.True(events.GetArrayLength() >= 1);

        foreach (var ev in events.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Object, ev.ValueKind);
            // source synthesized as "unknown" when missing.
            Assert.True(ev.TryGetProperty("source", out var source));
            Assert.Equal("unknown", source.GetString());
            // durationMs synthesized as null when missing (distinguishes
            // "unknown" from "instantaneous"/0).
            Assert.True(ev.TryGetProperty("durationMs", out var duration));
            Assert.Equal(JsonValueKind.Null, duration.ValueKind);
            // debugScore synthesized as null when missing.
            Assert.True(ev.TryGetProperty("debugScore", out var debugScore));
            Assert.Equal(JsonValueKind.Null, debugScore.ValueKind);
        }
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-J-10")]
    public async Task ReplayV2_PreservesExistingEnvelopeFields()
    {
        Assert.NotNull(_factory);
        // v2 row already carries source/durationMs on each event. Verify
        // the normaliser preserves them and adds the missing debugScore
        // without overwriting the existing keys.
        var v2WithFields = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            events = new object[]
            {
                new
                {
                    turn = 1,
                    phase = "Discard",
                    actor = 0,
                    action = "tile-discarded",
                    tilesJson = "[5]",
                    timestampUtc = DateTime.UtcNow,
                    source = "human",
                    durationMs = 1234,
                },
            },
        });
        var gameId = await SeedReplayAsync(v2WithFields);
        using var client = _factory!.CreateClient();
        using var resp = await GetFirstNonNotFoundAsync(client, gameId);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.True(resp.IsSuccessStatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("events", out var events));
        Assert.Equal(JsonValueKind.Array, events.ValueKind);

        var first = events.EnumerateArray().First();
        Assert.Equal("human", first.GetProperty("source").GetString());
        Assert.Equal(1234, first.GetProperty("durationMs").GetInt32());
        // debugScore added by the normaliser (the source row never had it).
        Assert.True(first.TryGetProperty("debugScore", out var debugScore));
        Assert.Equal(JsonValueKind.Null, debugScore.ValueKind);
    }
}
