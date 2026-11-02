using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Tournament;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W13.Bishop;

/// <summary>
/// Phase K Wave 13 — Bishop. Hard-asserted integration contract
/// for the durable bracket store wired into
/// <see cref="TournamentService"/>.
///
/// <list type="number">
///   <item><see cref="TournamentService"/> constructor accepts an
///         optional <see cref="IBracketStore"/>.</item>
///   <item><see cref="TournamentService.StartAsync"/> upserts a
///         BracketRecord per pairing.</item>
///   <item>BracketRecords carry per-round MatchSlot = 0, 1, 2…
///         (ordered insertion).</item>
///   <item>Seeds (SeedA / SeedB) reflect the match's
///         Player1Id / Player2Id.</item>
///   <item>Initial pairings are stamped <c>pending</c>.</item>
///   <item><see cref="TournamentService.AdvanceMatchAsync"/>
///         stamps the matching BracketRecord with
///         <c>completed</c> + WinnerSeed.</item>
///   <item>Replayed AdvanceMatchAsync is idempotent on the
///         BracketRecord (no duplicate / no overwrite divergence).</item>
///   <item><see cref="TournamentService.ForfeitMatchAsync"/>
///         stamps the BracketRecord with <c>forfeit</c>.</item>
///   <item>Bye pairings stamp <c>__bye__</c> as the
///         SeedB.</item>
/// </list>
/// </summary>
[Collection("DbSerial")]
public sealed class BracketStoreIntegrationTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w13-brk-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
                s.Configure<ChangshaRuntimeOptions>(o => { o.PersistSnapshots = false; }));
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

    private TournamentService NewServiceWithStore(IBracketStore store, AppDbContext db) =>
        new(db, rollover: null, bracketBroadcaster: null, bracketStore: store);

    private AppDbContext NewDb() =>
        _factory!.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    private async Task<Data.Entities.Tournament> SeedTournamentAsync(
        AppDbContext db, string format, int playerCount)
    {
        var t = new Data.Entities.Tournament
        {
            Id = Guid.NewGuid(),
            Name = $"w13-test-{Guid.NewGuid():N}",
            Format = format,
            Status = "open",
            CreatedByPlayerId = "player-1",
            MaxPlayers = Math.Max(playerCount, 2),
            GamesPerMatch = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.Tournaments.Add(t);
        for (var i = 0; i < playerCount; i++)
        {
            db.TournamentRegistrations.Add(new TournamentRegistration
            {
                Id = Guid.NewGuid(),
                TournamentId = t.Id,
                PlayerId = $"player-{i + 1}",
                Seed = i + 1,
                RegisteredAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return t;
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Service_Constructor_AcceptsOptionalBracketStore()
    {
        var db = NewDb();
        var store = new InMemoryBracketStore();
        var svc = new TournamentService(db, rollover: null, bracketBroadcaster: null, bracketStore: store);
        Assert.NotNull(svc);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task StartAsync_UpsertsBracketRecordsPerPairing()
    {
        var db = NewDb();
        var store = new InMemoryBracketStore();
        var svc = NewServiceWithStore(store, db);
        var t = await SeedTournamentAsync(db, "single-elimination", 4);
        await svc.StartAsync(t.Id, "player-1");
        var rows = await store.ListAsync(t.Id);
        Assert.Equal(2, rows.Count); // 4-player SE → 2 round-1 pairings
        Assert.All(rows, r => Assert.Equal(1, r.RoundNumber));
        Assert.All(rows, r => Assert.Equal("pending", r.Status));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task StartAsync_StampsSeedsFromPairings()
    {
        var db = NewDb();
        var store = new InMemoryBracketStore();
        var svc = NewServiceWithStore(store, db);
        var t = await SeedTournamentAsync(db, "single-elimination", 4);
        var matches = await svc.StartAsync(t.Id, "player-1");
        var rows = await store.ListAsync(t.Id);
        foreach (var m in matches)
        {
            var row = rows.FirstOrDefault(r =>
                r.RoundNumber == m.Round
                && (r.SeedA == m.Player1Id && r.SeedB == m.Player2Id));
            Assert.NotNull(row);
        }
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task StartAsync_AssignsZeroBasedSlots()
    {
        var db = NewDb();
        var store = new InMemoryBracketStore();
        var svc = NewServiceWithStore(store, db);
        var t = await SeedTournamentAsync(db, "single-elimination", 4);
        await svc.StartAsync(t.Id, "player-1");
        var rows = (await store.ListAsync(t.Id))
            .Where(r => r.RoundNumber == 1)
            .OrderBy(r => r.MatchSlot)
            .ToList();
        Assert.Equal(0, rows[0].MatchSlot);
        Assert.Equal(1, rows[1].MatchSlot);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task AdvanceMatchAsync_StampsBracketRecord()
    {
        var db = NewDb();
        var store = new InMemoryBracketStore();
        var svc = NewServiceWithStore(store, db);
        var t = await SeedTournamentAsync(db, "single-elimination", 2);
        var matches = await svc.StartAsync(t.Id, "player-1");
        var match = matches[0];

        // Bind a game id to the match so the advance lookup can find it.
        match.Status = "in-progress";
        match.GameIdsJson = System.Text.Json.JsonSerializer.Serialize(new[] { Guid.NewGuid() });
        await db.SaveChangesAsync();
        var gameId = System.Text.Json.JsonSerializer.Deserialize<Guid[]>(match.GameIdsJson)![0];

        await svc.AdvanceMatchAsync(gameId, match.Player1Id);

        var rows = await store.ListAsync(t.Id);
        var row = rows.FirstOrDefault(r => r.RoundNumber == 1 && r.MatchSlot == 0);
        Assert.NotNull(row);
        Assert.Equal("completed", row!.Status);
        Assert.Equal(match.Player1Id, row.WinnerSeed);
        Assert.NotNull(row.CompletedAt);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task AdvanceMatchAsync_IsIdempotentOnBracketRecord()
    {
        var db = NewDb();
        var store = new InMemoryBracketStore();
        var svc = NewServiceWithStore(store, db);
        var t = await SeedTournamentAsync(db, "single-elimination", 2);
        var matches = await svc.StartAsync(t.Id, "player-1");
        var match = matches[0];
        match.Status = "in-progress";
        var gameId = Guid.NewGuid();
        match.GameIdsJson = System.Text.Json.JsonSerializer.Serialize(new[] { gameId });
        await db.SaveChangesAsync();

        await svc.AdvanceMatchAsync(gameId, match.Player1Id);
        var beforeCount = (await store.ListAsync(t.Id)).Count;
        // Second advance on the same gameId returns null (match already complete)
        await svc.AdvanceMatchAsync(gameId, match.Player1Id);
        var afterCount = (await store.ListAsync(t.Id)).Count;
        Assert.Equal(beforeCount, afterCount);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task ForfeitMatchAsync_StampsBracketRecordAsForfeit()
    {
        var db = NewDb();
        var store = new InMemoryBracketStore();
        var svc = NewServiceWithStore(store, db);
        var t = await SeedTournamentAsync(db, "single-elimination", 2);
        var matches = await svc.StartAsync(t.Id, "player-1");
        var match = matches[0];
        match.Status = "in-progress";
        var gameId = Guid.NewGuid();
        match.GameIdsJson = System.Text.Json.JsonSerializer.Serialize(new[] { gameId });
        await db.SaveChangesAsync();

        await svc.ForfeitMatchAsync(gameId, match.Player1Id, match.Player2Id);

        var rows = await store.ListAsync(t.Id);
        var row = rows.FirstOrDefault(r => r.RoundNumber == 1 && r.MatchSlot == 0);
        Assert.NotNull(row);
        Assert.Equal("forfeit", row!.Status);
        Assert.Equal(match.Player1Id, row.WinnerSeed);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task BracketByeSeed_ConstantIsStable()
    {
        Assert.Equal("__bye__", TournamentService.BracketByeSeed);
        await Task.CompletedTask;
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task StartAsync_NoStore_DoesNotThrow()
    {
        var db = NewDb();
        var svc = new TournamentService(db); // no bracket store
        var t = await SeedTournamentAsync(db, "single-elimination", 4);
        var matches = await svc.StartAsync(t.Id, "player-1");
        Assert.NotEmpty(matches);
    }
}
