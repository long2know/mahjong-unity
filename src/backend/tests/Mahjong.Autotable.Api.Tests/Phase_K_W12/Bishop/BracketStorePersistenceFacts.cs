using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Tournament;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W12.Bishop;

/// <summary>
/// Phase K Wave 12 — Bishop. Hard-asserted contract for the
/// persisted tournament-bracket store
/// (<see cref="IBracketStore"/>).
///
/// <list type="number">
///   <item><see cref="InMemoryBracketStore"/> implements
///         <see cref="IBracketStore"/>.</item>
///   <item><see cref="EfBracketStore"/> implements
///         <see cref="IBracketStore"/>.</item>
///   <item><see cref="BracketStorageOptions"/> default
///         BracketStoreImpl = "InMemory".</item>
///   <item>Upsert+Get round-trips a row (in-memory).</item>
///   <item>Upsert+Get round-trips a row (EF).</item>
///   <item>Upsert is idempotent on natural key (in-memory).</item>
///   <item>Upsert is idempotent on natural key (EF).</item>
///   <item>RecordResultAsync stamps winner+completion
///         (in-memory).</item>
///   <item>RecordResultAsync stamps winner+completion (EF).</item>
///   <item>RecordResultAsync replayed twice is idempotent
///         (in-memory).</item>
///   <item>ListAsync orders rows by (round, slot).</item>
/// </list>
/// </summary>
[Collection("DbSerial")]
public sealed class BracketStorePersistenceFacts : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w12-brk-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.PersistSnapshots = false;
                    o.BotTurnDelayMs = 1;
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

    private EfBracketStore NewEfStore() =>
        new(_factory!.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EfBracketStore>.Instance);

    private static BracketRecord MakeRecord(
        Guid tournamentId,
        int round = 1,
        int slot = 0,
        string seedA = "playerA",
        string seedB = "playerB") =>
        new()
        {
            TournamentId = tournamentId,
            RoundNumber = round,
            MatchSlot = slot,
            SeedA = seedA,
            SeedB = seedB,
            Status = "pending",
        };

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-12")]
    public void InMemoryStore_ImplementsInterface()
    {
        Assert.True(typeof(IBracketStore).IsAssignableFrom(typeof(InMemoryBracketStore)));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-12")]
    public void EfStore_ImplementsInterface()
    {
        Assert.True(typeof(IBracketStore).IsAssignableFrom(typeof(EfBracketStore)));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-12")]
    public void Options_DefaultBracketStoreImplIsInMemory()
    {
        var opts = new BracketStorageOptions();
        Assert.Equal("InMemory", opts.BracketStoreImpl);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-12")]
    public async Task InMemory_UpsertGetRoundTrip()
    {
        var store = new InMemoryBracketStore();
        var tournamentId = Guid.NewGuid();
        await store.UpsertAsync(MakeRecord(tournamentId));
        var got = await store.GetAsync(tournamentId, 1, 0);
        Assert.NotNull(got);
        Assert.Equal("playerA", got!.SeedA);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-12")]
    public async Task Ef_UpsertGetRoundTrip()
    {
        var store = NewEfStore();
        var tournamentId = Guid.NewGuid();
        await store.UpsertAsync(MakeRecord(tournamentId));
        var got = await store.GetAsync(tournamentId, 1, 0);
        Assert.NotNull(got);
        Assert.Equal("playerA", got!.SeedA);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-12")]
    public async Task InMemory_UpsertIsIdempotent()
    {
        var store = new InMemoryBracketStore();
        var tournamentId = Guid.NewGuid();
        await store.UpsertAsync(MakeRecord(tournamentId));
        await store.UpsertAsync(MakeRecord(tournamentId));
        Assert.Equal(1, await store.CountAsync());
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-12")]
    public async Task Ef_UpsertIsIdempotent()
    {
        var store = NewEfStore();
        var tournamentId = Guid.NewGuid();
        await store.UpsertAsync(MakeRecord(tournamentId));
        await store.UpsertAsync(MakeRecord(tournamentId));
        var rows = await store.ListAsync(tournamentId);
        Assert.Single(rows);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-12")]
    public async Task InMemory_RecordResultStampsWinner()
    {
        var store = new InMemoryBracketStore();
        var tournamentId = Guid.NewGuid();
        await store.UpsertAsync(MakeRecord(tournamentId));
        var when = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var updated = await store.RecordResultAsync(tournamentId, 1, 0, "playerA", "completed", when);
        Assert.NotNull(updated);
        Assert.Equal("playerA", updated!.WinnerSeed);
        Assert.Equal("completed", updated.Status);
        Assert.Equal(when, updated.CompletedAt);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-12")]
    public async Task Ef_RecordResultStampsWinner()
    {
        var store = NewEfStore();
        var tournamentId = Guid.NewGuid();
        await store.UpsertAsync(MakeRecord(tournamentId));
        var when = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var updated = await store.RecordResultAsync(tournamentId, 1, 0, "playerA", "completed", when);
        Assert.NotNull(updated);
        Assert.Equal("playerA", updated!.WinnerSeed);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-12")]
    public async Task InMemory_RecordResultReplayIsIdempotent()
    {
        var store = new InMemoryBracketStore();
        var tournamentId = Guid.NewGuid();
        await store.UpsertAsync(MakeRecord(tournamentId));
        var when = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        await store.RecordResultAsync(tournamentId, 1, 0, "playerA", "completed", when);
        await store.RecordResultAsync(tournamentId, 1, 0, "playerA", "completed", when);
        Assert.Equal(1, await store.CountAsync());
        var got = await store.GetAsync(tournamentId, 1, 0);
        Assert.Equal("playerA", got!.WinnerSeed);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-12")]
    public async Task InMemory_ListOrdersRowsByRoundThenSlot()
    {
        var store = new InMemoryBracketStore();
        var tournamentId = Guid.NewGuid();
        await store.UpsertAsync(MakeRecord(tournamentId, round: 2, slot: 0));
        await store.UpsertAsync(MakeRecord(tournamentId, round: 1, slot: 1));
        await store.UpsertAsync(MakeRecord(tournamentId, round: 1, slot: 0));
        var rows = await store.ListAsync(tournamentId);
        Assert.Equal(3, rows.Count);
        Assert.Equal(1, rows[0].RoundNumber);
        Assert.Equal(0, rows[0].MatchSlot);
        Assert.Equal(1, rows[1].RoundNumber);
        Assert.Equal(1, rows[1].MatchSlot);
        Assert.Equal(2, rows[2].RoundNumber);
    }
}
