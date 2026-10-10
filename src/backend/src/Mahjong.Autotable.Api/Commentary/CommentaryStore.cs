using System.Collections.Concurrent;
using System.Text.Json;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Mahjong.Autotable.Api.Commentary;

/// <summary>
/// Phase K Wave 11 — Bishop. Persistence seam for the
/// <see cref="CommentaryRecord"/> list. The W7-W9 surface kept
/// records in memory inside the
/// <see cref="ICommentaryGenerator"/> implementation; that worked
/// for single-replica dev but lost records on a process restart
/// and didn't survive the multi-replica deployment shape. W11
/// introduces this seam so production can persist records to the
/// <see cref="CommentaryRecordRow"/> table with a configurable
/// retention window.
///
/// <para>The interface is intentionally narrow — append + read.
/// The generator owns its own caching for the
/// <see cref="ICommentaryGenerator.GetAsync"/> envelope (per-game
/// single-summary), and the store owns the durable
/// <see cref="GetRecordsAsync"/> list. Two layers, two responsibilities,
/// no shared mutable state.</para>
///
/// <para>Toggle: <c>Commentary:StorageImpl</c>
/// (<c>"InMemory"</c> default for tests / <c>"Ef"</c> default for
/// production). See <c>Program.cs</c>.</para>
/// </summary>
public interface ICommentaryStore
{
    /// <summary>Append a single record to the store. Returns the
    /// stored entry (identical to the input — the contract is a
    /// fluent append rather than a row-id surface).</summary>
    Task<CommentaryRecord> AppendAsync(Guid gameId, CommentaryRecord record, CancellationToken ct = default);

    /// <summary>Bulk-append. The store may persist all entries in
    /// a single batch when the implementation supports it.</summary>
    Task AppendRangeAsync(Guid gameId, IReadOnlyList<CommentaryRecord> records, CancellationToken ct = default);

    /// <summary>
    /// Read up to <paramref name="limit"/> records for
    /// <paramref name="gameId"/> with
    /// <see cref="CommentaryRecord.GeneratedAt"/> strictly greater
    /// than <paramref name="afterUtc"/>. Results are ordered by
    /// <see cref="CommentaryRecord.GeneratedAt"/> ascending so the
    /// paginated reader can use the last entry's timestamp as the
    /// next-page cursor.
    /// </summary>
    Task<IReadOnlyList<CommentaryRecord>> ReadAsync(
        Guid gameId,
        DateTimeOffset? afterUtc,
        int limit,
        CancellationToken ct = default);

    /// <summary>Total record count for the supplied game. Tests
    /// use this to confirm the append path landed; the controller
    /// does not need it.</summary>
    Task<int> CountAsync(Guid gameId, CancellationToken ct = default);

    /// <summary>Delete every record older than
    /// <paramref name="olderThanUtc"/>. Returns the count of
    /// records evicted. The store decides the deletion strategy
    /// (the EF impl does a single bulk-delete query; the in-memory
    /// impl loops over the dictionary).</summary>
    Task<int> SweepExpiredAsync(DateTime olderThanUtc, CancellationToken ct = default);
}

/// <summary>
/// Phase K Wave 11 — Bishop. In-memory record store used by tests
/// + single-replica development. The shape mirrors the EF impl so
/// the contract test suite passes against both bindings.
/// </summary>
public sealed class InMemoryCommentaryStore : ICommentaryStore
{
    private readonly ConcurrentDictionary<Guid, ConcurrentBag<CommentaryRecord>> _records =
        new();

    public Task<CommentaryRecord> AppendAsync(Guid gameId, CommentaryRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var bag = _records.GetOrAdd(gameId, _ => new ConcurrentBag<CommentaryRecord>());
        bag.Add(record);
        return Task.FromResult(record);
    }

    public Task AppendRangeAsync(Guid gameId, IReadOnlyList<CommentaryRecord> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var bag = _records.GetOrAdd(gameId, _ => new ConcurrentBag<CommentaryRecord>());
        foreach (var r in records) bag.Add(r);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CommentaryRecord>> ReadAsync(
        Guid gameId,
        DateTimeOffset? afterUtc,
        int limit,
        CancellationToken ct = default)
    {
        if (!_records.TryGetValue(gameId, out var bag))
            return Task.FromResult<IReadOnlyList<CommentaryRecord>>(Array.Empty<CommentaryRecord>());

        IEnumerable<CommentaryRecord> q = bag;
        if (afterUtc is { } cutoff) q = q.Where(r => r.GeneratedAt > cutoff);
        var ordered = q.OrderBy(r => r.GeneratedAt)
            .Take(Math.Max(0, limit))
            .ToList();
        return Task.FromResult<IReadOnlyList<CommentaryRecord>>(ordered);
    }

    public Task<int> CountAsync(Guid gameId, CancellationToken ct = default)
    {
        return Task.FromResult(_records.TryGetValue(gameId, out var bag) ? bag.Count : 0);
    }

    public Task<int> SweepExpiredAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        var removed = 0;
        foreach (var pair in _records)
        {
            var survivors = pair.Value
                .Where(r => r.GeneratedAt.UtcDateTime >= olderThanUtc)
                .ToList();
            removed += pair.Value.Count - survivors.Count;
            if (survivors.Count == 0)
            {
                _records.TryRemove(pair.Key, out _);
            }
            else
            {
                var fresh = new ConcurrentBag<CommentaryRecord>(survivors);
                _records[pair.Key] = fresh;
            }
        }
        return Task.FromResult(removed);
    }
}

/// <summary>
/// Phase K Wave 11 — Bishop. EF-backed durable record store.
/// Writes one <see cref="CommentaryRecordRow"/> per
/// <see cref="CommentaryRecord"/> append and serves the
/// paginated read path off the
/// <c>(GameId, GeneratedAtUtc)</c> index. The retention window
/// is driven by the
/// <see cref="CommentaryStorageOptions.RetentionDays"/> setting;
/// the <see cref="CommentaryRetentionSweepService"/> background
/// service walks the <c>ExpiresAtUtc</c> index on a nightly
/// cadence and deletes expired rows.
/// </summary>
public sealed class EfCommentaryStore : ICommentaryStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CommentaryStorageOptions _options;
    private readonly ILogger<EfCommentaryStore> _logger;

    public EfCommentaryStore(
        IServiceScopeFactory scopeFactory,
        CommentaryStorageOptions options,
        ILogger<EfCommentaryStore> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CommentaryRecord> AppendAsync(Guid gameId, CommentaryRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CommentaryRecords.Add(ToRow(gameId, record, _options.RetentionDays));
        await db.SaveChangesAsync(ct);
        return record;
    }

    public async Task AppendRangeAsync(Guid gameId, IReadOnlyList<CommentaryRecord> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) return;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var r in records)
        {
            db.CommentaryRecords.Add(ToRow(gameId, r, _options.RetentionDays));
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<CommentaryRecord>> ReadAsync(
        Guid gameId,
        DateTimeOffset? afterUtc,
        int limit,
        CancellationToken ct = default)
    {
        if (limit <= 0) return Array.Empty<CommentaryRecord>();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var query = db.CommentaryRecords
            .AsNoTracking()
            .Where(r => r.GameId == gameId);
        if (afterUtc is { } cutoff)
        {
            var cutoffUtc = cutoff.UtcDateTime;
            query = query.Where(r => r.GeneratedAtUtc > cutoffUtc);
        }
        var rows = await query
            .OrderBy(r => r.GeneratedAtUtc)
            .Take(limit)
            .ToListAsync(ct);
        return rows.Select(FromRow).ToList();
    }

    public async Task<int> CountAsync(Guid gameId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.CommentaryRecords.CountAsync(r => r.GameId == gameId, ct);
    }

    public async Task<int> SweepExpiredAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // ExecuteDeleteAsync is the EF 7+ bulk-delete; falls back
        // to a per-row delete on providers that don't support it.
        try
        {
            return await db.CommentaryRecords
                .Where(r => r.ExpiresAtUtc < olderThanUtc)
                .ExecuteDeleteAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CommentaryRecord bulk-delete failed; falling back to per-row delete.");
            var rows = await db.CommentaryRecords
                .Where(r => r.ExpiresAtUtc < olderThanUtc)
                .ToListAsync(ct);
            db.CommentaryRecords.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            return rows.Count;
        }
    }

    private static CommentaryRecordRow ToRow(Guid gameId, CommentaryRecord record, int retentionDays)
    {
        var generatedUtc = record.GeneratedAt.UtcDateTime;
        var retention = retentionDays > 0 ? retentionDays : CommentaryStorageOptions.DefaultRetentionDays;
        return new CommentaryRecordRow
        {
            Id = Guid.NewGuid(),
            GameId = gameId,
            TurnNumber = record.TurnNumber,
            Phase = record.Phase,
            Speaker = record.Speaker,
            Text = record.Text,
            EmotionIntensity = record.EmotionIntensity,
            TileReferencesJson = SerialiseTileReferences(record.TileReferences),
            GeneratedAtUtc = generatedUtc,
            ExpiresAtUtc = generatedUtc.AddDays(retention),
        };
    }

    private static CommentaryRecord FromRow(CommentaryRecordRow row)
    {
        var tiles = DeserialiseTileReferences(row.TileReferencesJson);
        return new CommentaryRecord(
            GameId: row.GameId.ToString("N"),
            TurnNumber: row.TurnNumber,
            Phase: row.Phase,
            Speaker: row.Speaker,
            Text: row.Text,
            EmotionIntensity: row.EmotionIntensity,
            TileReferences: tiles,
            GeneratedAt: new DateTimeOffset(DateTime.SpecifyKind(row.GeneratedAtUtc, DateTimeKind.Utc)));
    }

    private static string SerialiseTileReferences(IReadOnlyList<TileReference>? tiles)
    {
        if (tiles is null || tiles.Count == 0) return "[]";
        return JsonSerializer.Serialize(tiles.Select(t => new
        {
            tileId = t.TileId,
            suit = t.Suit,
            rank = t.Rank,
        }));
    }

    private static IReadOnlyList<TileReference> DeserialiseTileReferences(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return Array.Empty<TileReference>();
        try
        {
            var entries = JsonSerializer.Deserialize<List<TileReferenceDto>>(json);
            if (entries is null) return Array.Empty<TileReference>();
            return entries
                .Select(e => new TileReference(e.tileId ?? string.Empty, e.suit ?? "unknown", e.rank))
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<TileReference>();
        }
    }

    private sealed record TileReferenceDto(string? tileId, string? suit, int rank);
}

/// <summary>
/// Phase K Wave 11 — Bishop. Storage configuration for the
/// commentary record persistence layer. Bound from the
/// <c>Commentary</c> configuration section (alongside
/// <see cref="CommentaryOptions"/>) so an operator can tune the
/// retention window + flip the storage implementation without
/// touching code.
/// </summary>
public sealed class CommentaryStorageOptions
{
    /// <summary>Default retention window (days). Records older
    /// than this are deleted by the nightly sweeper.</summary>
    public const int DefaultRetentionDays = 7;

    /// <summary>Implementation selector — case-insensitive.
    /// <c>"InMemory"</c> uses <see cref="InMemoryCommentaryStore"/>
    /// (default for tests + dev); <c>"Ef"</c> uses
    /// <see cref="EfCommentaryStore"/> (default for production).</summary>
    public string StorageImpl { get; set; } = "InMemory";

    /// <summary>Retention window in days. 0 = use the default
    /// (<see cref="DefaultRetentionDays"/>).</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>Background sweep cadence in hours. Default 24
    /// (nightly). Lowered values are tolerated for test
    /// fixtures.</summary>
    public int SweepIntervalHours { get; set; } = 24;
}

/// <summary>
/// Phase K Wave 11 — Bishop. Nightly background sweep that
/// deletes <see cref="CommentaryRecordRow"/> rows past their
/// configured retention window. Registered as a hosted service
/// only when <c>Commentary:StorageImpl="Ef"</c> — the in-memory
/// store has no on-disk footprint to sweep.
/// </summary>
public sealed class CommentaryRetentionSweepService : BackgroundService
{
    private readonly ICommentaryStore _store;
    private readonly CommentaryStorageOptions _options;
    private readonly ILogger<CommentaryRetentionSweepService> _logger;

    public CommentaryRetentionSweepService(
        ICommentaryStore store,
        CommentaryStorageOptions options,
        ILogger<CommentaryRetentionSweepService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, _options.SweepIntervalHours));
        _logger.LogInformation(
            "CommentaryRetentionSweepService started (interval={Hours}h, retention={Days}d).",
            interval.TotalHours,
            _options.RetentionDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CommentaryRetentionSweep failed (non-fatal); next tick in {Hours}h.",
                    interval.TotalHours);
            }
        }

        _logger.LogInformation("CommentaryRetentionSweepService stopped.");
    }

    /// <summary>Single-sweep entry-point exposed so tests can
    /// drive deletions deterministically.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow;
        var removed = await _store.SweepExpiredAsync(cutoff, ct);
        if (removed > 0)
        {
            _logger.LogInformation(
                "CommentaryRetentionSweep removed {Count} expired record(s) older than {Cutoff:O}.",
                removed, cutoff);
        }
        return removed;
    }
}
