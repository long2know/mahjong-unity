using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// Phase J Wave 7 — <c>GET /api/games/{gameId}/replay</c> contract tests
/// (Vasquez backstop).
///
/// <para>Bishop's Wave-7 replay endpoint surfaces the persisted
/// <see cref="ChangshaGameReplay"/> row for a completed game. The runtime
/// writes the row in <c>EmitGameCompletedAsync</c>; this controller suite
/// validates the read-path wire contract independently by pre-seeding
/// the DB directly. The end-to-end "row is written on completion" flow
/// is exercised by <see cref="ChangshaReplayPersistenceTests"/>.</para>
///
/// <para>Three contracts are pinned:</para>
/// <list type="number">
///   <item><b>200 with materialised events array</b> — the response body
///         exposes <c>events</c> as a structured JSON array (not a
///         re-encoded string), each entry carrying the wire fields
///         <c>turn / phase / actor / action / tilesJson / timestampUtc</c>.</item>
///   <item><b>404 for unknown id</b> — no replay row → 404 with a
///         structured error body.</item>
///   <item><b>400 for malformed id</b> — non-GUID route value → 400.</item>
/// </list>
/// </summary>
[Collection("DbSerial")]
public class ChangshaReplayEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-replay-{Guid.NewGuid():N}.db");

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
    //  1. 200 + structured events array when a replay row exists
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-7")]
    public async Task ReplayEndpoint_ReturnsPersistedSnapshot_WhenRowExists()
    {
        // Pre-seed a parent ChangshaGames row (the replay FK references it)
        // + a ChangshaGameReplays row carrying a minimal canonical wire
        // payload. The controller must echo the events back as a
        // structured JSON array — a regression where the controller
        // re-serialises EventsJson would manifest here as ValueKind.String.
        var gameGuid = Guid.NewGuid();
        var sampleEvents = new[]
        {
            new
            {
                turn = 1,
                phase = "Setup",
                actor = -1,
                action = "game-created",
                tilesJson = "[]",
                timestampUtc = DateTime.UtcNow,
            },
            new
            {
                turn = 4,
                phase = "Discard",
                actor = 2,
                action = "tile-discarded",
                tilesJson = "[47]",
                timestampUtc = DateTime.UtcNow,
            },
        };
        var eventsJson = JsonSerializer.Serialize(sampleEvents);

        Assert.NotNull(_factory);
        using (var scope = _factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChangshaGames.Add(new ChangshaGame
            {
                Id = gameGuid,
                Seed = 42,
                StateJson = "{}",
                CurrentHandNumber = 4,
                CurrentRoundNumber = 1,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            db.ChangshaGameReplays.Add(new ChangshaGameReplay
            {
                Id = Guid.NewGuid(),
                GameId = gameGuid,
                CreatedAt = DateTime.UtcNow,
                EventsJson = eventsJson,
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        using var response = await client.GetAsync($"/api/games/{gameGuid}/replay");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("gameId", out var gameIdEl),
            "Replay response missing 'gameId'.");
        Assert.Equal(gameGuid, gameIdEl.GetGuid());

        Assert.True(root.TryGetProperty("createdAt", out _),
            "Replay response missing 'createdAt'.");

        Assert.True(root.TryGetProperty("events", out var eventsEl),
            "Replay response missing 'events' — Wave 7 wire contract regression.");
        // Critical assertion: events MUST be a structured array, not a
        // re-encoded JSON string. If a future refactor accidentally
        // double-encodes EventsJson (e.g. returns the raw string field)
        // the consumer's JSON.parse would fail silently.
        Assert.Equal(JsonValueKind.Array, eventsEl.ValueKind);
        Assert.Equal(2, eventsEl.GetArrayLength());

        // Field shape on each event entry.
        foreach (var evt in eventsEl.EnumerateArray())
        {
            Assert.True(evt.TryGetProperty("turn", out _),
                "Replay event entry missing 'turn'.");
            Assert.True(evt.TryGetProperty("phase", out _),
                "Replay event entry missing 'phase'.");
            Assert.True(evt.TryGetProperty("actor", out _),
                "Replay event entry missing 'actor'.");
            Assert.True(evt.TryGetProperty("action", out _),
                "Replay event entry missing 'action'.");
            Assert.True(evt.TryGetProperty("tilesJson", out _),
                "Replay event entry missing 'tilesJson'.");
            Assert.True(evt.TryGetProperty("timestampUtc", out _),
                "Replay event entry missing 'timestampUtc'.");
        }

        // The second seeded entry's specific fields — pin the round-trip.
        var second = eventsEl.EnumerateArray().Skip(1).First();
        Assert.Equal("Discard", second.GetProperty("phase").GetString());
        Assert.Equal("tile-discarded", second.GetProperty("action").GetString());
        Assert.Equal(2, second.GetProperty("actor").GetInt32());
        Assert.Equal("[47]", second.GetProperty("tilesJson").GetString());
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. 404 for unknown game id
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-7")]
    public async Task ReplayEndpoint_Returns404_ForUnknownGameId()
    {
        // No replay row in the DB → 404. The controller MUST NOT auto-create
        // / synthesise an empty replay for unknown ids — the canonical
        // signal "this game never completed (or never existed)" is the
        // missing row.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();

        var unknownGuid = Guid.NewGuid();
        using var response = await client.GetAsync($"/api/games/{unknownGuid}/replay");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. 400 for malformed (non-GUID) game id
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-7")]
    public async Task ReplayEndpoint_Returns400_ForMalformedGameId()
    {
        // ASP.NET route binding accepts any string; the controller is
        // responsible for the GUID validity check. A 400 carries the
        // structured `{ error }` body — Hicks's replay viewer surfaces
        // this directly.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync("/api/games/not-a-guid/replay");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. ReplayPhaseBucket maps every documented runtime EventType
    // ────────────────────────────────────────────────────────────────────

    [Theory, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-7")]
    [InlineData("game-created", "Setup")]
    [InlineData("game-started", "Setup")]
    [InlineData("banker-rotated", "Setup")]
    [InlineData("dice-rolled", "Deal")]
    [InlineData("tiles-dealt", "Deal")]
    [InlineData("tile-drawn", "Deal")]
    [InlineData("tiles-picked-up", "Deal")]
    [InlineData("kong-replacement-drawn", "Deal")]
    [InlineData("wall-exhausted", "Deal")]
    [InlineData("tile-discarded", "Discard")]
    [InlineData("claim-window-open", "Claim")]
    [InlineData("claim-resolved", "Claim")]
    [InlineData("claim-passed", "Claim")]
    [InlineData("concealed-kong", "Claim")]
    [InlineData("added-kong-declared", "Claim")]
    [InlineData("added-kong", "Claim")]
    [InlineData("win-declared", "Hu")]
    [InlineData("scoring-complete", "Hu")]
    [InlineData("draw-hand", "Hu")]
    [InlineData("false-hu-penalty", "Hu")]
    [InlineData("unknown-future-event", "Other")]
    public void ReplayPhaseBucket_MapsKnownEventTypes(string eventType, string expectedBucket)
    {
        // The bucket mapping is the canonical taxonomy the Hicks-facing
        // replay viewer uses to group events. A regression where the
        // runtime adds a new event type that drops through to a wrong
        // bucket would corrupt the viewer's segmentation; pinning all
        // documented types here keeps the surface honest.
        Assert.Equal(expectedBucket, ChangshaGameRuntime.ReplayPhaseBucket(eventType));
    }
}
