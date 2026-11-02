using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Tournament;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Bishop;

/// <summary>
/// Phase K Wave 19 — Bishop. Contract tests for the new
/// <see cref="SwissPairingAuditEntry"/> entity + its EF wiring.
/// Covers: round-trip via sqlite, composite unique key
/// constraint on <c>(TournamentId,Round,Board)</c>, CreatedAtUtc
/// auto-stamp, and bye-sentinel storage matching
/// <see cref="FideC04SwissPairingService.ByeOpponent"/>.
/// </summary>
[Collection("DbSerial")]
public sealed class SwissPairingAuditEntityTests : IDisposable
{
    private static readonly string _scratchDir =
        Path.Combine(AppContext.BaseDirectory, "bishop-w19-swiss-audit-sqlite");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    public SwissPairingAuditEntityTests()
    {
        Directory.CreateDirectory(_scratchDir);
        _dbPath = Path.Combine(_scratchDir, $"bishop-w19-swiss-{Guid.NewGuid():N}.sqlite");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private AppDbContext NewDb() => _sp.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task RoundTrip_PersistsAllFields()
    {
        var id = Guid.NewGuid();
        var tid = Guid.NewGuid();
        var entry = new SwissPairingAuditEntry
        {
            Id = id,
            TournamentId = tid,
            Round = 1,
            Board = 1,
            White = "player-w",
            Black = "player-b",
            Tiebreaker = "buchholz",
        };
        await using (var db = NewDb())
        {
            db.SwissPairingAuditEntries.Add(entry);
            await db.SaveChangesAsync();
        }
        await using (var db = NewDb())
        {
            var read = await db.SwissPairingAuditEntries.SingleAsync(e => e.Id == id);
            Assert.Equal(tid, read.TournamentId);
            Assert.Equal(1, read.Round);
            Assert.Equal(1, read.Board);
            Assert.Equal("player-w", read.White);
            Assert.Equal("player-b", read.Black);
            Assert.Equal("buchholz", read.Tiebreaker);
            Assert.True(read.CreatedAtUtc <= DateTime.UtcNow);
        }
    }

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task UniqueIndex_RejectsDuplicateTournamentRoundBoard()
    {
        var tid = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.SwissPairingAuditEntries.Add(new SwissPairingAuditEntry
            {
                TournamentId = tid, Round = 2, Board = 3, White = "a", Black = "b",
            });
            await db.SaveChangesAsync();
        }
        await using (var db = NewDb())
        {
            db.SwissPairingAuditEntries.Add(new SwissPairingAuditEntry
            {
                TournamentId = tid, Round = 2, Board = 3, White = "x", Black = "y",
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task UniqueIndex_AllowsDistinctBoardWithSameTournamentAndRound()
    {
        var tid = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.SwissPairingAuditEntries.AddRange(
                new SwissPairingAuditEntry { TournamentId = tid, Round = 1, Board = 1, White = "a", Black = "b" },
                new SwissPairingAuditEntry { TournamentId = tid, Round = 1, Board = 2, White = "c", Black = "d" });
            await db.SaveChangesAsync();
        }
        await using (var db = NewDb())
        {
            Assert.Equal(2, await db.SwissPairingAuditEntries.CountAsync());
        }
    }

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task UniqueIndex_AllowsDistinctRoundWithSameTournamentAndBoard()
    {
        var tid = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.SwissPairingAuditEntries.AddRange(
                new SwissPairingAuditEntry { TournamentId = tid, Round = 1, Board = 1, White = "a", Black = "b" },
                new SwissPairingAuditEntry { TournamentId = tid, Round = 2, Board = 1, White = "c", Black = "d" });
            await db.SaveChangesAsync();
        }
        await using (var db = NewDb())
        {
            Assert.Equal(2, await db.SwissPairingAuditEntries.CountAsync());
        }
    }

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task UniqueIndex_AllowsSameRoundBoardForDifferentTournaments()
    {
        await using (var db = NewDb())
        {
            db.SwissPairingAuditEntries.AddRange(
                new SwissPairingAuditEntry { TournamentId = Guid.NewGuid(), Round = 1, Board = 1, White = "a", Black = "b" },
                new SwissPairingAuditEntry { TournamentId = Guid.NewGuid(), Round = 1, Board = 1, White = "c", Black = "d" });
            await db.SaveChangesAsync();
        }
        await using (var db = NewDb())
        {
            Assert.Equal(2, await db.SwissPairingAuditEntries.CountAsync());
        }
    }

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task StoresByeSentinel_MatchingFideC04Const()
    {
        var entry = new SwissPairingAuditEntry
        {
            TournamentId = Guid.NewGuid(),
            Round = 5,
            Board = 7,
            White = "player-w",
            Black = FideC04SwissPairingService.ByeOpponent,
            Tiebreaker = "bye",
        };
        await using (var db = NewDb())
        {
            db.SwissPairingAuditEntries.Add(entry);
            await db.SaveChangesAsync();
        }
        await using (var db = NewDb())
        {
            var read = await db.SwissPairingAuditEntries
                .SingleAsync(e => e.TournamentId == entry.TournamentId);
            Assert.Equal(FideC04SwissPairingService.ByeOpponent, read.Black);
        }
    }

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task CreatedAtUtc_AutoStampsOnConstruction()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var e = new SwissPairingAuditEntry { TournamentId = Guid.NewGuid(), Round = 1, Board = 1, White = "a", Black = "b" };
        await using (var db = NewDb())
        {
            db.SwissPairingAuditEntries.Add(e);
            await db.SaveChangesAsync();
        }
        var after = DateTime.UtcNow.AddSeconds(1);
        Assert.InRange(e.CreatedAtUtc, before, after);
    }

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task OrderingByRoundThenBoard_IsDeterministic()
    {
        var tid = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.SwissPairingAuditEntries.AddRange(
                new SwissPairingAuditEntry { TournamentId = tid, Round = 2, Board = 1, White = "a", Black = "b" },
                new SwissPairingAuditEntry { TournamentId = tid, Round = 1, Board = 2, White = "c", Black = "d" },
                new SwissPairingAuditEntry { TournamentId = tid, Round = 1, Board = 1, White = "e", Black = "f" });
            await db.SaveChangesAsync();
        }
        await using (var db = NewDb())
        {
            var ordered = await db.SwissPairingAuditEntries
                .Where(e => e.TournamentId == tid)
                .OrderBy(e => e.Round).ThenBy(e => e.Board)
                .ToListAsync();
            Assert.Equal(new[] { (1, 1), (1, 2), (2, 1) },
                ordered.Select(e => (e.Round, e.Board)).ToArray());
        }
    }

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void EntityHasMaxTiebreakerLengthConstant()
    {
        Assert.Equal(64, SwissPairingAuditEntry.MaxTiebreakerLength);
    }

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public async Task DbSet_IsExposedOnAppDbContext()
    {
        await using var db = NewDb();
        Assert.NotNull(db.SwissPairingAuditEntries);
    }
}
