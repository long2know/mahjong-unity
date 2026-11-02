using System.Collections.Concurrent;
using Mahjong.Autotable.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Spectator;

/// <summary>
/// Phase K Wave 13 — Bishop. Persisted audit row for every
/// spectator-handoff token mint. The W12 surface
/// (<see cref="SpectatorHandoffController.Handoff"/>) returned a
/// short-lived JWT without a trail; W13 lands the durable row so
/// security review can answer "who minted this token, when, from
/// which IP / UA?" without parsing the application log stream.
///
/// <para>The natural key is the JWT JTI claim — guaranteed unique
/// by the issuer. The audit row is written BEFORE the token is
/// returned so a server crash mid-flow cannot strand an issued
/// token with no audit trail; the worst case is an orphan audit
/// row whose token never reaches the client (operationally
/// indistinguishable from a token issued + immediately dropped).
/// </para>
///
/// <para>Retention: <c>Spectator:Audit:RetentionDays</c> (default
/// 30). See <c>docs/spectator-handoff.md §3 "Audit"</c>.</para>
/// </summary>
public sealed class SpectatorHandoffAuditRecord
{
    /// <summary>Surrogate id. <see cref="TokenJti"/> is the
    /// natural unique key but we keep the Guid PK for join
    /// stability with the rest of the schema.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Caller's resolved player id (the session subject
    /// at mint time).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Game scope the token was issued against. Embedded
    /// in the JWT scope claim as <c>spectator:{gameId}</c>.</summary>
    public Guid GameId { get; set; }

    /// <summary>JWT id (<c>jti</c>) claim — RFC 7519 unique
    /// identifier. Persisted so a revocation flow can match
    /// against the stamped row.</summary>
    public string TokenJti { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the token was issued. Drives
    /// the retention sweep window.</summary>
    public DateTime IssuedAt { get; set; }

    /// <summary>Resolved scope literal (e.g.
    /// <c>"spectator:&lt;gameId&gt;"</c>). Surfaced verbatim so
    /// the audit consumer doesn't have to recompose it from
    /// <see cref="GameId"/>.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>Best-effort client IP address as observed by
    /// the API. Empty when the transport can't surface it
    /// (in-memory test clients).</summary>
    public string ClientIp { get; set; } = string.Empty;

    /// <summary>Raw <c>User-Agent</c> header (truncated to 256
    /// chars). Empty when not supplied.</summary>
    public string UserAgent { get; set; } = string.Empty;
}

/// <summary>
/// Phase K Wave 13 — Bishop. Persistence seam for the spectator
/// handoff audit trail. The contract is intentionally minimal:
/// insert a row, list per user / game, sweep expired rows.
///
/// <para>Toggle: <c>Spectator:Audit:StorageImpl</c>
/// (<c>"InMemory"</c> default for tests / <c>"Ef"</c> for
/// production). The store + the rendered controller flow are
/// covered by <c>Phase_K_W13/Bishop/SpectatorHandoffAuditFacts.cs</c>.</para>
/// </summary>
public interface ISpectatorHandoffAuditStore
{
    /// <summary>Insert a single audit row. The store stamps
    /// <see cref="SpectatorHandoffAuditRecord.IssuedAt"/> from
    /// the caller — production paths use <c>DateTime.UtcNow</c>;
    /// tests pin a deterministic clock.</summary>
    Task<SpectatorHandoffAuditRecord> InsertAsync(
        SpectatorHandoffAuditRecord record,
        CancellationToken ct = default);

    /// <summary>List every audit row for the supplied
    /// <paramref name="gameId"/>, ordered by
    /// <see cref="SpectatorHandoffAuditRecord.IssuedAt"/>
    /// descending (most-recent first).</summary>
    Task<IReadOnlyList<SpectatorHandoffAuditRecord>> ListByGameAsync(
        Guid gameId, CancellationToken ct = default);

    /// <summary>Sweep audit rows older than the cutoff. Returns
    /// the count evicted.</summary>
    Task<int> SweepExpiredAsync(DateTime cutoffUtc, CancellationToken ct = default);

    /// <summary>Total row count — surfaced for tests.</summary>
    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// Phase K Wave 14 — Bishop. Paginated query backing
    /// <c>GET /api/spectator/handoff/audit</c>. Filters by
    /// <paramref name="gameId"/> when supplied (else returns rows
    /// across every game) plus an optional <paramref name="fromUtc"/>
    /// / <paramref name="toUtc"/> window applied against
    /// <see cref="SpectatorHandoffAuditRecord.IssuedAt"/>. Results
    /// are ordered <c>IssuedAt</c> descending (most-recent first);
    /// <paramref name="skip"/> + <paramref name="take"/> pin the
    /// page. See <c>docs/spectator-handoff.md §4</c>.
    /// </summary>
    Task<IReadOnlyList<SpectatorHandoffAuditRecord>> QueryAsync(
        Guid? gameId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int skip,
        int take,
        CancellationToken ct = default);
}

/// <summary>
/// Phase K Wave 13 — Bishop. In-memory
/// <see cref="ISpectatorHandoffAuditStore"/> for tests + dev
/// fixtures.
/// </summary>
public sealed class InMemorySpectatorHandoffAuditStore : ISpectatorHandoffAuditStore
{
    private readonly ConcurrentDictionary<Guid, SpectatorHandoffAuditRecord> _rows = new();

    public Task<SpectatorHandoffAuditRecord> InsertAsync(
        SpectatorHandoffAuditRecord record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Id == Guid.Empty) record.Id = Guid.NewGuid();
        _rows[record.Id] = record;
        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<SpectatorHandoffAuditRecord>> ListByGameAsync(
        Guid gameId, CancellationToken ct = default)
    {
        IReadOnlyList<SpectatorHandoffAuditRecord> rows = _rows.Values
            .Where(r => r.GameId == gameId)
            .OrderByDescending(r => r.IssuedAt)
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<int> SweepExpiredAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        var victims = _rows
            .Where(kv => kv.Value.IssuedAt < cutoffUtc)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in victims) _rows.TryRemove(k, out _);
        return Task.FromResult(victims.Count);
    }

    public Task<int> CountAsync(CancellationToken ct = default) =>
        Task.FromResult(_rows.Count);

    public Task<IReadOnlyList<SpectatorHandoffAuditRecord>> QueryAsync(
        Guid? gameId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        IEnumerable<SpectatorHandoffAuditRecord> q = _rows.Values;
        if (gameId is { } g) q = q.Where(r => r.GameId == g);
        if (fromUtc is { } f) q = q.Where(r => r.IssuedAt >= f);
        if (toUtc is { } t) q = q.Where(r => r.IssuedAt <= t);
        IReadOnlyList<SpectatorHandoffAuditRecord> rows = q
            .OrderByDescending(r => r.IssuedAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Max(0, take))
            .ToList();
        return Task.FromResult(rows);
    }
}

/// <summary>
/// Phase K Wave 13 — Bishop. EF-backed durable
/// <see cref="ISpectatorHandoffAuditStore"/>. Persists rows to
/// <see cref="AppDbContext.SpectatorHandoffAuditRecords"/>; reads
/// the per-game listing off the
/// <c>(GameId, IssuedAt)</c> composite index for O(log n) lookups.
/// </summary>
public sealed class EfSpectatorHandoffAuditStore : ISpectatorHandoffAuditStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EfSpectatorHandoffAuditStore> _logger;

    public EfSpectatorHandoffAuditStore(
        IServiceScopeFactory scopeFactory,
        ILogger<EfSpectatorHandoffAuditStore> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SpectatorHandoffAuditRecord> InsertAsync(
        SpectatorHandoffAuditRecord record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Id == Guid.Empty) record.Id = Guid.NewGuid();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SpectatorHandoffAuditRecords.Add(record);
        await db.SaveChangesAsync(ct);
        return record;
    }

    public async Task<IReadOnlyList<SpectatorHandoffAuditRecord>> ListByGameAsync(
        Guid gameId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SpectatorHandoffAuditRecords
            .AsNoTracking()
            .Where(r => r.GameId == gameId)
            .OrderByDescending(r => r.IssuedAt)
            .ToListAsync(ct);
    }

    public async Task<int> SweepExpiredAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            return await db.SpectatorHandoffAuditRecords
                .Where(r => r.IssuedAt < cutoffUtc)
                .ExecuteDeleteAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SpectatorHandoffAudit bulk-delete failed; falling back to per-row delete.");
            var rows = await db.SpectatorHandoffAuditRecords
                .Where(r => r.IssuedAt < cutoffUtc)
                .ToListAsync(ct);
            db.SpectatorHandoffAuditRecords.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            return rows.Count;
        }
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SpectatorHandoffAuditRecords.CountAsync(ct);
    }

    public async Task<IReadOnlyList<SpectatorHandoffAuditRecord>> QueryAsync(
        Guid? gameId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IQueryable<SpectatorHandoffAuditRecord> q = db.SpectatorHandoffAuditRecords.AsNoTracking();
        if (gameId is { } g) q = q.Where(r => r.GameId == g);
        if (fromUtc is { } f) q = q.Where(r => r.IssuedAt >= f);
        if (toUtc is { } t) q = q.Where(r => r.IssuedAt <= t);
        return await q
            .OrderByDescending(r => r.IssuedAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Max(0, take))
            .ToListAsync(ct);
    }
}

/// <summary>
/// Phase K Wave 13 — Bishop. Configuration for the spectator
/// handoff audit trail. Bound from the <c>Spectator:Audit</c>
/// section.
/// </summary>
public sealed class SpectatorHandoffAuditOptions
{
    /// <summary>Default retention window in days.</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>Implementation selector — case-insensitive.
    /// <c>"InMemory"</c> uses
    /// <see cref="InMemorySpectatorHandoffAuditStore"/>;
    /// <c>"Ef"</c> uses
    /// <see cref="EfSpectatorHandoffAuditStore"/>.</summary>
    public string StorageImpl { get; set; } = "InMemory";

    /// <summary>Retention window in days. Rows older than this
    /// are dropped by the sweeper. 0 = use the default
    /// (<see cref="DefaultRetentionDays"/>).</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>
    /// Phase K Wave 14 — Bishop. Default page size for the admin
    /// audit query endpoint
    /// (<c>GET /api/spectator/handoff/audit</c>).
    /// </summary>
    public const int DefaultPageSize = 50;

    /// <summary>
    /// Phase K Wave 14 — Bishop. Maximum page size clients can
    /// request via the <c>limit</c> query parameter. Larger
    /// values are silently clamped.
    /// </summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// Phase K Wave 14 — Bishop. Default page size for the audit
    /// query endpoint. Bound from <c>Spectator:Audit:PageSize</c>.
    /// Values ≤ 0 fall back to <see cref="DefaultPageSize"/>; values
    /// above <see cref="MaxPageSize"/> are clamped down. See
    /// <c>docs/spectator-handoff.md §4</c>.
    /// </summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>
    /// Phase K Wave 15 — Bishop. Retention sweep cadence (minutes)
    /// for <see cref="SpectatorHandoffAuditRetentionSweep"/>.
    /// Default 5 minutes — short enough that a leaked token's
    /// audit row is gone within a 30-minute incident window once
    /// retention is dialled down. Values &lt; 1 fall back to 1
    /// minute (tests pin a short cadence).
    /// </summary>
    public int SweepIntervalMinutes { get; set; } = 5;
}

/// <summary>
/// Phase K Wave 15 — Bishop. Background sweep that deletes
/// <see cref="SpectatorHandoffAuditRecord"/> rows older than
/// the configured retention window
/// (<see cref="SpectatorHandoffAuditOptions.RetentionDays"/>).
/// Runs every <see cref="SpectatorHandoffAuditOptions.SweepIntervalMinutes"/>
/// (default 5 minutes) so leaked audit rows are short-lived even
/// when retention is dialled down mid-incident.
///
/// <para>Registered as a hosted service only when
/// <c>Spectator:Audit:StorageImpl="Ef"</c> — the in-memory store
/// has no on-disk footprint to sweep across restarts. See
/// <c>docs/spectator-handoff.md §5 "Retention sweep"</c>.</para>
/// </summary>
public sealed class SpectatorHandoffAuditRetentionSweep : Microsoft.Extensions.Hosting.BackgroundService
{
    public const int DefaultSweepIntervalMinutes = 5;

    private readonly ISpectatorHandoffAuditStore _store;
    private readonly SpectatorHandoffAuditOptions _options;
    private readonly ILogger<SpectatorHandoffAuditRetentionSweep> _logger;

    public SpectatorHandoffAuditRetentionSweep(
        ISpectatorHandoffAuditStore store,
        SpectatorHandoffAuditOptions options,
        ILogger<SpectatorHandoffAuditRetentionSweep> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = _options.SweepIntervalMinutes > 0
            ? _options.SweepIntervalMinutes
            : DefaultSweepIntervalMinutes;
        var interval = TimeSpan.FromMinutes(Math.Max(1, minutes));
        _logger.LogInformation(
            "SpectatorHandoffAuditRetentionSweep started (interval={Minutes}m, retention={Days}d).",
            interval.TotalMinutes,
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
                    "SpectatorHandoffAuditRetentionSweep failed (non-fatal); next tick in {Minutes}m.",
                    interval.TotalMinutes);
            }
        }

        _logger.LogInformation("SpectatorHandoffAuditRetentionSweep stopped.");
    }

    /// <summary>Single-sweep entry-point exposed so tests can
    /// drive deletions deterministically.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var retention = _options.RetentionDays > 0
            ? _options.RetentionDays
            : SpectatorHandoffAuditOptions.DefaultRetentionDays;
        var cutoff = DateTime.UtcNow.AddDays(-retention);
        var removed = await _store.SweepExpiredAsync(cutoff, ct);
        if (removed > 0)
        {
            _logger.LogInformation(
                "SpectatorHandoffAuditRetentionSweep removed {Count} row(s) older than {Days}d (cutoff={Cutoff:O}).",
                removed, retention, cutoff);
        }
        return removed;
    }
}
