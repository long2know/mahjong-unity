using System.Collections.Concurrent;
using Mahjong.Autotable.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 12 — Bishop. Persisted single bracket pairing
/// row. The W6-W10 bracket-generator surface kept pairings as
/// in-memory tuples; W12 lands the durable row so brackets
/// survive process restarts + multi-replica deployments.
///
/// <para>The <c>(TournamentId, RoundNumber, MatchSlot)</c>
/// unique constraint guarantees one row per pairing — replaying
/// a <c>game-complete</c> event through
/// <see cref="EfBracketStore.RecordResultAsync"/> updates the
/// existing row rather than inserting a duplicate.</para>
/// </summary>
public sealed class BracketRecord
{
    /// <summary>Surrogate id. The natural key
    /// <c>(TournamentId, RoundNumber, MatchSlot)</c> is the
    /// unique constraint enforced by EF.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning tournament.</summary>
    public Guid TournamentId { get; set; }

    /// <summary>Bracket round, 1-indexed. Round-robin formats
    /// emit one row per round per pairing.</summary>
    public int RoundNumber { get; set; }

    /// <summary>Slot within the round, 0-indexed. Tie-breaks the
    /// natural key — same (TournamentId, Round) can carry many
    /// pairings; the slot pins ordering.</summary>
    public int MatchSlot { get; set; }

    /// <summary>Seed (player id, "__bye__" sentinel, or seed
    /// label) on the A side of the pairing.</summary>
    public string SeedA { get; set; } = string.Empty;

    /// <summary>Seed (player id, "__bye__" sentinel, or seed
    /// label) on the B side of the pairing.</summary>
    public string SeedB { get; set; } = string.Empty;

    /// <summary>Winner seed (one of <see cref="SeedA"/> or
    /// <see cref="SeedB"/>) — null while the match is pending.
    /// Bye pairings stamp the bye-recipient as the winner at
    /// insert time.</summary>
    public string? WinnerSeed { get; set; }

    /// <summary>Pairing status:
    /// <c>"pending"</c>, <c>"active"</c>, <c>"completed"</c>,
    /// <c>"forfeit"</c>, <c>"bye"</c>. Surfaced to the
    /// listing endpoint.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>UTC timestamp when the result was recorded.
    /// Null while pending; set by
    /// <see cref="EfBracketStore.RecordResultAsync"/>.</summary>
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Phase K Wave 12 — Bishop. Persistence seam for the durable
/// bracket store. The interface is intentionally narrow:
/// insert-or-update a pairing, fetch by natural key, list
/// per-tournament, record completion. Listing by round is the
/// dominant read path; the (TournamentId, RoundNumber,
/// MatchSlot) unique index keeps it O(log n).
///
/// <para>Toggle: <c>Tournament:BracketStoreImpl</c>
/// (<c>"InMemory"</c> default for tests / <c>"Ef"</c> for
/// production). See <c>docs/bracket-shape.md §3</c>.</para>
/// </summary>
public interface IBracketStore
{
    /// <summary>Upsert a pairing keyed on
    /// <c>(TournamentId, RoundNumber, MatchSlot)</c>. Returns
    /// the stored record (with id resolved). Re-applying the
    /// same key updates the existing row — the call is
    /// idempotent w.r.t. replay-game-complete events.</summary>
    Task<BracketRecord> UpsertAsync(BracketRecord record, CancellationToken ct = default);

    /// <summary>Fetch a single pairing by natural key. Returns
    /// <c>null</c> when no row exists.</summary>
    Task<BracketRecord?> GetAsync(Guid tournamentId, int roundNumber, int matchSlot, CancellationToken ct = default);

    /// <summary>List every pairing for the supplied tournament,
    /// ordered by <c>(RoundNumber, MatchSlot)</c>.</summary>
    Task<IReadOnlyList<BracketRecord>> ListAsync(Guid tournamentId, CancellationToken ct = default);

    /// <summary>Idempotent completion path — stamps
    /// <see cref="BracketRecord.WinnerSeed"/> +
    /// <see cref="BracketRecord.Status"/> +
    /// <see cref="BracketRecord.CompletedAt"/>. Replaying the
    /// same event is a no-op.</summary>
    Task<BracketRecord?> RecordResultAsync(
        Guid tournamentId,
        int roundNumber,
        int matchSlot,
        string winnerSeed,
        string status,
        DateTime completedAtUtc,
        CancellationToken ct = default);

    /// <summary>Total row count — surfaced for tests.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}

/// <summary>
/// Phase K Wave 12 — Bishop. In-memory <see cref="IBracketStore"/>
/// for tests + single-replica dev. The shape mirrors the EF
/// implementation so the contract test suite passes against both
/// bindings.
/// </summary>
public sealed class InMemoryBracketStore : IBracketStore
{
    private readonly ConcurrentDictionary<(Guid, int, int), BracketRecord> _rows =
        new();

    public Task<BracketRecord> UpsertAsync(BracketRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var key = (record.TournamentId, record.RoundNumber, record.MatchSlot);
        _rows.AddOrUpdate(key, record, (_, existing) =>
        {
            existing.SeedA = record.SeedA;
            existing.SeedB = record.SeedB;
            existing.WinnerSeed = record.WinnerSeed;
            existing.Status = record.Status;
            existing.CompletedAt = record.CompletedAt;
            return existing;
        });
        return Task.FromResult(_rows[key]);
    }

    public Task<BracketRecord?> GetAsync(Guid tournamentId, int roundNumber, int matchSlot, CancellationToken ct = default) =>
        Task.FromResult(_rows.TryGetValue((tournamentId, roundNumber, matchSlot), out var r) ? r : null);

    public Task<IReadOnlyList<BracketRecord>> ListAsync(Guid tournamentId, CancellationToken ct = default)
    {
        IReadOnlyList<BracketRecord> rows = _rows.Values
            .Where(r => r.TournamentId == tournamentId)
            .OrderBy(r => r.RoundNumber)
            .ThenBy(r => r.MatchSlot)
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<BracketRecord?> RecordResultAsync(
        Guid tournamentId,
        int roundNumber,
        int matchSlot,
        string winnerSeed,
        string status,
        DateTime completedAtUtc,
        CancellationToken ct = default)
    {
        var key = (tournamentId, roundNumber, matchSlot);
        if (!_rows.TryGetValue(key, out var row)) return Task.FromResult<BracketRecord?>(null);
        // Idempotent: re-applying the same winner is a no-op.
        row.WinnerSeed = winnerSeed;
        row.Status = status;
        row.CompletedAt = completedAtUtc;
        return Task.FromResult<BracketRecord?>(row);
    }

    public Task<int> CountAsync(CancellationToken ct = default) =>
        Task.FromResult(_rows.Count);
}

/// <summary>
/// Phase K Wave 12 — Bishop. EF-backed durable
/// <see cref="IBracketStore"/>. Persists rows to the
/// <see cref="AppDbContext.BracketRecords"/> table; reads use
/// the <c>(TournamentId, RoundNumber, MatchSlot)</c> unique
/// index for O(log n) point lookups.
/// </summary>
public sealed class EfBracketStore : IBracketStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EfBracketStore> _logger;

    public EfBracketStore(IServiceScopeFactory scopeFactory, ILogger<EfBracketStore> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BracketRecord> UpsertAsync(BracketRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.BracketRecords
            .FirstOrDefaultAsync(
                r => r.TournamentId == record.TournamentId
                  && r.RoundNumber == record.RoundNumber
                  && r.MatchSlot == record.MatchSlot,
                ct);
        if (existing is null)
        {
            if (record.Id == Guid.Empty) record.Id = Guid.NewGuid();
            db.BracketRecords.Add(record);
            await db.SaveChangesAsync(ct);
            return record;
        }
        existing.SeedA = record.SeedA;
        existing.SeedB = record.SeedB;
        existing.WinnerSeed = record.WinnerSeed;
        existing.Status = record.Status;
        existing.CompletedAt = record.CompletedAt;
        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<BracketRecord?> GetAsync(Guid tournamentId, int roundNumber, int matchSlot, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BracketRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.TournamentId == tournamentId
                  && r.RoundNumber == roundNumber
                  && r.MatchSlot == matchSlot,
                ct);
    }

    public async Task<IReadOnlyList<BracketRecord>> ListAsync(Guid tournamentId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BracketRecords
            .AsNoTracking()
            .Where(r => r.TournamentId == tournamentId)
            .OrderBy(r => r.RoundNumber)
            .ThenBy(r => r.MatchSlot)
            .ToListAsync(ct);
    }

    public async Task<BracketRecord?> RecordResultAsync(
        Guid tournamentId,
        int roundNumber,
        int matchSlot,
        string winnerSeed,
        string status,
        DateTime completedAtUtc,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.BracketRecords
            .FirstOrDefaultAsync(
                r => r.TournamentId == tournamentId
                  && r.RoundNumber == roundNumber
                  && r.MatchSlot == matchSlot,
                ct);
        if (row is null) return null;
        row.WinnerSeed = winnerSeed;
        row.Status = string.IsNullOrEmpty(status) ? "completed" : status;
        row.CompletedAt = completedAtUtc;
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.BracketRecords.CountAsync(ct);
    }
}

/// <summary>
/// Phase K Wave 12 — Bishop. Configuration for the bracket
/// persistence layer. Bound from the <c>Tournament</c>
/// configuration section.
/// </summary>
public sealed class BracketStorageOptions
{
    /// <summary>Implementation selector — case-insensitive.
    /// <c>"InMemory"</c> uses <see cref="InMemoryBracketStore"/>
    /// (default for tests + dev); <c>"Ef"</c> uses
    /// <see cref="EfBracketStore"/> (default for prod).</summary>
    public string BracketStoreImpl { get; set; } = "InMemory";
}

/// <summary>
/// Phase K Wave 14 — Bishop. Configuration knob for the
/// admin-facing bracket query endpoint
/// (<c>GET /api/tournaments/{id}/brackets</c>). Bound from the
/// <c>Tournament</c> section so the page-size sits alongside the
/// W12 bracket storage toggle. See
/// <c>docs/bracket-shape.md §5</c>.
/// </summary>
public sealed class BracketQueryOptions
{
    /// <summary>Default page size when the caller omits the
    /// <c>limit</c> query parameter.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Hard upper bound — larger <c>limit</c> values
    /// are silently clamped.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Server-side default page size. 0 / negative
    /// → use <see cref="DefaultPageSize"/>.</summary>
    public int PageSize { get; set; } = DefaultPageSize;
}
