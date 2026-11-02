using System.Collections.Concurrent;
using System.Security.Cryptography;
using Mahjong.Autotable.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Mahjong.Autotable.Api.Replays;

/// <summary>
/// Phase K Wave 12 — Bishop. Persistence seam for the durable
/// replay-by-id surface (<c>GET /api/replays/{replayId}</c> +
/// <c>POST /api/replays</c>). The store is intentionally narrow:
/// insert with a server-minted id, fetch by id, sweep expired
/// rows. Listing-by-game / paging-by-time live forward of this
/// surface in W13.
///
/// <para>Toggle: <c>Replays:StorageImpl</c>
/// (<c>"InMemory"</c> default for tests / <c>"Ef"</c> for
/// production). See <c>docs/replay-by-id.md</c>.</para>
/// </summary>
public interface IReplayStore
{
    /// <summary>Insert a replay row. The implementation mints
    /// the <see cref="ReplayRecord.ReplayId"/> when the caller
    /// passes an empty string; otherwise the supplied id wins.
    /// Returns the stored record (with id + timestamps
    /// resolved).</summary>
    Task<ReplayRecord> InsertAsync(ReplayRecord record, CancellationToken ct = default);

    /// <summary>Fetch a single replay row by id. Returns
    /// <c>null</c> when no row exists.</summary>
    Task<ReplayRecord?> GetAsync(string replayId, CancellationToken ct = default);

    /// <summary>Delete every replay row with
    /// <see cref="ReplayRecord.ExpiresAt"/> strictly older than
    /// <paramref name="utcNow"/>. Returns the count of records
    /// evicted.</summary>
    Task<int> SweepExpiredAsync(DateTime utcNow, CancellationToken ct = default);

    /// <summary>
    /// Phase K Wave 15 — Bishop. Sweep rows whose
    /// <see cref="ReplayRecord.CompletedAt"/> is strictly older
    /// than <paramref name="utcNow"/> minus <paramref name="retentionDays"/>.
    /// The W12 sweep used <see cref="ReplayRecord.ExpiresAt"/>
    /// (computed once at insert time) — a runtime change to
    /// <c>Replays:RetentionDays</c> would not retro-apply to
    /// rows already in the store. The W15 sweep evaluates the
    /// retention window against the current configured retention
    /// at each tick, so the operator can dial retention down
    /// (or up) and the next sweep honours the new window.
    ///
    /// <para>The two sweeps are intentionally orthogonal — both
    /// can run alongside each other without double-counting (the
    /// second pass over a row already deleted by the first is a
    /// no-op). The <see cref="ReplayStoreRetentionSweep"/> hosted
    /// service drives this path; see
    /// <c>docs/replay-by-id.md §4 "Retention sweep"</c>.</para>
    /// </summary>
    Task<int> SweepByCompletedAtAsync(int retentionDays, DateTime utcNow, CancellationToken ct = default);

    /// <summary>
    /// Phase K Wave 16 — Bishop. Per-tenant aware sweep. For
    /// every row in the store:
    /// <list type="bullet">
    ///   <item>When <see cref="ReplayRecord.TenantId"/> is set
    ///         AND <paramref name="policyStore"/> has a matching
    ///         policy with a strictly-positive
    ///         <see cref="ReplayRetentionPolicy.RetentionDays"/>,
    ///         the row is deleted when
    ///         <c>CompletedAt &lt; utcNow - policy.RetentionDays</c>.</item>
    ///   <item>Otherwise the row is deleted when
    ///         <c>CompletedAt &lt; utcNow - fallbackDays</c>
    ///         (the global default).</item>
    /// </list>
    /// Returns the total number of rows evicted across all
    /// tenants. A policy upsert / delete takes effect on the
    /// NEXT tick — the sweep re-reads the policy table at
    /// every invocation so operators can dial retention mid-
    /// flight without restarting the host. See
    /// <c>docs/replay-by-id.md §4.1</c>.
    /// </summary>
    Task<int> SweepWithPerTenantPolicyAsync(
        IReplayRetentionPolicyStore policyStore,
        int fallbackDays,
        DateTime utcNow,
        CancellationToken ct = default);

    /// <summary>
    /// Phase K Wave 20 — Bishop. Per-tenant aware sweep that
    /// ALSO returns the per-tenant breakdown of the evicted
    /// row count. Mirrors the W16
    /// <see cref="SweepWithPerTenantPolicyAsync"/> semantics
    /// — same policy lookup + same fallback retention — but
    /// surfaces the count-by-tenant map so the W20
    /// <c>ReplayStoreExpiryHandler</c> can emit the
    /// <c>replay_expired_total{tenant=…}</c> counter without
    /// re-querying the store. Rows whose
    /// <see cref="ReplayRecord.TenantId"/> is null / empty
    /// land under the literal key <c>""</c> (rendered as
    /// <c>_unknown</c> in Prometheus by the metric
    /// collector — same bucket convention as the W19
    /// integrity-audit surface).
    ///
    /// <para>Default interface implementation: round-trips
    /// through the W16 sweep, returns a single-bucket map
    /// keyed by <c>""</c> with the total count. This keeps
    /// the contract additive — every existing implementation
    /// compiles without modification — and the canonical
    /// <see cref="InMemoryReplayStore"/> /
    /// <see cref="EfReplayStore"/> overrides supply the real
    /// per-tenant breakdown.</para>
    /// </summary>
    async Task<IReadOnlyDictionary<string, int>> SweepWithPerTenantBreakdownAsync(
        IReplayRetentionPolicyStore policyStore,
        int fallbackDays,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        var total = await SweepWithPerTenantPolicyAsync(policyStore, fallbackDays, utcNow, ct);
        return new Dictionary<string, int> { [string.Empty] = total };
    }

    /// <summary>Total row count — surfaced so tests can assert
    /// the insert path landed.</summary>
    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// Phase K Wave 14 — Bishop. Paginated metadata-only listing
    /// backing <c>GET /api/replays</c>. Filters by completed-at
    /// range (<paramref name="fromUtc"/> / <paramref name="toUtc"/>
    /// applied against <see cref="ReplayRecord.CompletedAt"/>) and
    /// optional <paramref name="variant"/>. Returns
    /// <see cref="ReplayRecord"/> shells with the compressed
    /// payload column zeroed — the wire surface drops the heavy
    /// payload (clients pull it via
    /// <c>GET /api/replays/{replayId}</c>). Results are ordered
    /// <c>CompletedAt</c> descending (most-recent first).
    /// See <c>docs/replay-by-id.md §3</c>.
    /// </summary>
    Task<IReadOnlyList<ReplayRecord>> ListAsync(
        DateTime? fromUtc,
        DateTime? toUtc,
        string? variant,
        int skip,
        int take,
        CancellationToken ct = default);
}

/// <summary>
/// Phase K Wave 12 — Bishop. In-memory <see cref="IReplayStore"/>.
/// Used by tests + single-replica development. The shape mirrors
/// the EF impl so the contract test suite passes against both
/// bindings.
/// </summary>
public sealed class InMemoryReplayStore : IReplayStore
{
    private readonly ConcurrentDictionary<string, ReplayRecord> _rows =
        new(StringComparer.Ordinal);

    public Task<ReplayRecord> InsertAsync(ReplayRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.ReplayId))
        {
            record.ReplayId = ReplayIdGenerator.Mint();
        }
        if (record.IngestedAt == default) record.IngestedAt = DateTime.UtcNow;
        if (record.ExpiresAt == default)
        {
            record.ExpiresAt = (record.CompletedAt == default ? record.IngestedAt : record.CompletedAt)
                .AddDays(ReplayOptions.DefaultRetentionDays);
        }
        _rows[record.ReplayId] = record;
        return Task.FromResult(record);
    }

    public Task<ReplayRecord?> GetAsync(string replayId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(replayId)) return Task.FromResult<ReplayRecord?>(null);
        return Task.FromResult(_rows.TryGetValue(replayId, out var r) ? r : null);
    }

    public Task<int> SweepExpiredAsync(DateTime utcNow, CancellationToken ct = default)
    {
        var removed = 0;
        foreach (var pair in _rows)
        {
            if (pair.Value.ExpiresAt < utcNow && _rows.TryRemove(pair.Key, out _))
            {
                removed++;
            }
        }
        return Task.FromResult(removed);
    }

    public Task<int> SweepByCompletedAtAsync(int retentionDays, DateTime utcNow, CancellationToken ct = default)
    {
        if (retentionDays <= 0) return Task.FromResult(0);
        var cutoff = utcNow.AddDays(-retentionDays);
        var removed = 0;
        foreach (var pair in _rows)
        {
            if (pair.Value.CompletedAt < cutoff && _rows.TryRemove(pair.Key, out _))
            {
                removed++;
            }
        }
        return Task.FromResult(removed);
    }

    public async Task<int> SweepWithPerTenantPolicyAsync(
        IReplayRetentionPolicyStore policyStore,
        int fallbackDays,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policyStore);
        var policies = await policyStore.ListAsync(ct).ConfigureAwait(false);
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in policies)
        {
            if (!string.IsNullOrWhiteSpace(p.TenantId) && p.RetentionDays > 0)
            {
                map[p.TenantId] = p.RetentionDays;
            }
        }
        var fallbackCutoff = fallbackDays > 0
            ? utcNow.AddDays(-fallbackDays)
            : (DateTime?)null;
        var removed = 0;
        foreach (var pair in _rows)
        {
            var row = pair.Value;
            DateTime? cutoff;
            if (!string.IsNullOrEmpty(row.TenantId) && map.TryGetValue(row.TenantId, out var days))
            {
                cutoff = utcNow.AddDays(-days);
            }
            else
            {
                cutoff = fallbackCutoff;
            }
            if (cutoff is { } c && row.CompletedAt < c && _rows.TryRemove(pair.Key, out _))
            {
                removed++;
            }
        }
        return removed;
    }

    /// <summary>Phase K Wave 20 — Bishop. In-memory override of
    /// <see cref="IReplayStore.SweepWithPerTenantBreakdownAsync"/>
    /// that supplies the real per-tenant breakdown rather than
    /// the single-bucket fallback baked into the interface
    /// default.</summary>
    public async Task<IReadOnlyDictionary<string, int>> SweepWithPerTenantBreakdownAsync(
        IReplayRetentionPolicyStore policyStore,
        int fallbackDays,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policyStore);
        var policies = await policyStore.ListAsync(ct).ConfigureAwait(false);
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in policies)
        {
            if (!string.IsNullOrWhiteSpace(p.TenantId) && p.RetentionDays > 0)
            {
                map[p.TenantId] = p.RetentionDays;
            }
        }
        var fallbackCutoff = fallbackDays > 0
            ? utcNow.AddDays(-fallbackDays)
            : (DateTime?)null;
        var breakdown = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in _rows)
        {
            var row = pair.Value;
            DateTime? cutoff;
            if (!string.IsNullOrEmpty(row.TenantId) && map.TryGetValue(row.TenantId, out var days))
            {
                cutoff = utcNow.AddDays(-days);
            }
            else
            {
                cutoff = fallbackCutoff;
            }
            if (cutoff is { } c && row.CompletedAt < c && _rows.TryRemove(pair.Key, out _))
            {
                var bucket = string.IsNullOrEmpty(row.TenantId) ? string.Empty : row.TenantId!;
                breakdown[bucket] = breakdown.TryGetValue(bucket, out var existing) ? existing + 1 : 1;
            }
        }
        return breakdown;
    }

    public Task<int> CountAsync(CancellationToken ct = default) =>
        Task.FromResult(_rows.Count);

    public Task<IReadOnlyList<ReplayRecord>> ListAsync(
        DateTime? fromUtc,
        DateTime? toUtc,
        string? variant,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        IEnumerable<ReplayRecord> q = _rows.Values;
        if (fromUtc is { } f) q = q.Where(r => r.CompletedAt >= f);
        if (toUtc is { } t) q = q.Where(r => r.CompletedAt <= t);
        if (!string.IsNullOrWhiteSpace(variant))
        {
            var v = variant.Trim();
            q = q.Where(r => string.Equals(r.Variant, v, StringComparison.OrdinalIgnoreCase));
        }
        IReadOnlyList<ReplayRecord> rows = q
            .OrderByDescending(r => r.CompletedAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Max(0, take))
            .Select(r => new ReplayRecord
            {
                ReplayId = r.ReplayId,
                GameId = r.GameId,
                CompletedAt = r.CompletedAt,
                Variant = r.Variant,
                TurnCount = r.TurnCount,
                IngestedAt = r.IngestedAt,
                ExpiresAt = r.ExpiresAt,
                TenantId = r.TenantId,
                // Metadata-only — clients fetch the payload via
                // GET /api/replays/{replayId}.
                CompressedPayload = Array.Empty<byte>(),
            })
            .ToList();
        return Task.FromResult(rows);
    }
}

/// <summary>
/// Phase K Wave 12 — Bishop. EF-backed durable replay store.
/// Persists rows to the <see cref="AppDbContext.Replays"/>
/// table. Reads are O(log n) on the
/// <see cref="ReplayRecord.ReplayId"/> primary key; the
/// retention sweep walks the <c>ExpiresAt</c> index.
/// </summary>
public sealed class EfReplayStore : IReplayStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ReplayOptions _options;
    private readonly ILogger<EfReplayStore> _logger;

    public EfReplayStore(
        IServiceScopeFactory scopeFactory,
        ReplayOptions options,
        ILogger<EfReplayStore> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ReplayRecord> InsertAsync(ReplayRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.ReplayId))
        {
            record.ReplayId = ReplayIdGenerator.Mint();
        }
        if (record.IngestedAt == default) record.IngestedAt = DateTime.UtcNow;
        if (record.ExpiresAt == default)
        {
            var anchor = record.CompletedAt == default ? record.IngestedAt : record.CompletedAt;
            var retentionDays = _options.RetentionDays > 0 ? _options.RetentionDays : ReplayOptions.DefaultRetentionDays;
            record.ExpiresAt = anchor.AddDays(retentionDays);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Replays.Add(record);
        await db.SaveChangesAsync(ct);
        return record;
    }

    public async Task<ReplayRecord?> GetAsync(string replayId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(replayId)) return null;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Replays
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.ReplayId == replayId, ct);
    }

    public async Task<int> SweepExpiredAsync(DateTime utcNow, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            return await db.Replays
                .Where(r => r.ExpiresAt < utcNow)
                .ExecuteDeleteAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Replay bulk-delete failed; falling back to per-row delete.");
            var rows = await db.Replays
                .Where(r => r.ExpiresAt < utcNow)
                .ToListAsync(ct);
            db.Replays.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            return rows.Count;
        }
    }

    public async Task<int> SweepByCompletedAtAsync(int retentionDays, DateTime utcNow, CancellationToken ct = default)
    {
        if (retentionDays <= 0) return 0;
        var cutoff = utcNow.AddDays(-retentionDays);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            return await db.Replays
                .Where(r => r.CompletedAt < cutoff)
                .ExecuteDeleteAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Replay completed-at bulk-delete failed; falling back to per-row delete.");
            var rows = await db.Replays
                .Where(r => r.CompletedAt < cutoff)
                .ToListAsync(ct);
            db.Replays.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            return rows.Count;
        }
    }

    public async Task<int> SweepWithPerTenantPolicyAsync(
        IReplayRetentionPolicyStore policyStore,
        int fallbackDays,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policyStore);
        var policies = await policyStore.ListAsync(ct).ConfigureAwait(false);
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in policies)
        {
            if (!string.IsNullOrWhiteSpace(p.TenantId) && p.RetentionDays > 0)
            {
                map[p.TenantId] = p.RetentionDays;
            }
        }
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var totalRemoved = 0;
        // Per-tenant pass. We compute the cutoff in-process and
        // issue one bulk-delete per tenant — the per-tenant
        // tenant list is short (single-digit to low-hundreds in
        // realistic deployments) so the loop's network round-
        // trips stay bounded.
        foreach (var (tenant, days) in map)
        {
            var cutoff = utcNow.AddDays(-days);
            try
            {
                var t = tenant;
                var c = cutoff;
                totalRemoved += await db.Replays
                    .Where(r => r.TenantId == t && r.CompletedAt < c)
                    .ExecuteDeleteAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Per-tenant bulk-delete failed for tenant={Tenant}; falling back to per-row delete.",
                    tenant);
                var t = tenant;
                var c = cutoff;
                var rows = await db.Replays
                    .Where(r => r.TenantId == t && r.CompletedAt < c)
                    .ToListAsync(ct);
                db.Replays.RemoveRange(rows);
                await db.SaveChangesAsync(ct);
                totalRemoved += rows.Count;
            }
        }
        // Fallback pass — every row whose TenantId is null OR
        // not in the policy map. Single bulk delete with a NOT
        // IN clause against the policy map keys.
        if (fallbackDays > 0)
        {
            var cutoff = utcNow.AddDays(-fallbackDays);
            var tenantKeys = map.Keys.ToList();
            try
            {
                totalRemoved += await db.Replays
                    .Where(r => r.CompletedAt < cutoff)
                    .Where(r => r.TenantId == null || !tenantKeys.Contains(r.TenantId))
                    .ExecuteDeleteAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Fallback bulk-delete failed; per-row pass.");
                var rows = await db.Replays
                    .Where(r => r.CompletedAt < cutoff)
                    .Where(r => r.TenantId == null || !tenantKeys.Contains(r.TenantId))
                    .ToListAsync(ct);
                db.Replays.RemoveRange(rows);
                await db.SaveChangesAsync(ct);
                totalRemoved += rows.Count;
            }
        }
        return totalRemoved;
    }

    /// <summary>Phase K Wave 20 — Bishop. EF override of
    /// <see cref="IReplayStore.SweepWithPerTenantBreakdownAsync"/>.
    /// Mirrors the W16 EF sweep semantics — per-tenant bulk
    /// delete + fallback NOT-IN delete — but counts rows by
    /// tenant before deletion so the W20 expiry handler can
    /// emit the per-tenant counter.</summary>
    public async Task<IReadOnlyDictionary<string, int>> SweepWithPerTenantBreakdownAsync(
        IReplayRetentionPolicyStore policyStore,
        int fallbackDays,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policyStore);
        var policies = await policyStore.ListAsync(ct).ConfigureAwait(false);
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in policies)
        {
            if (!string.IsNullOrWhiteSpace(p.TenantId) && p.RetentionDays > 0)
            {
                map[p.TenantId] = p.RetentionDays;
            }
        }
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var breakdown = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (tenant, days) in map)
        {
            var cutoff = utcNow.AddDays(-days);
            var t = tenant;
            var c = cutoff;
            var count = await db.Replays.CountAsync(r => r.TenantId == t && r.CompletedAt < c, ct);
            if (count == 0) continue;
            try
            {
                var deleted = await db.Replays
                    .Where(r => r.TenantId == t && r.CompletedAt < c)
                    .ExecuteDeleteAsync(ct);
                breakdown[t] = (breakdown.TryGetValue(t, out var existing) ? existing : 0) + deleted;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Per-tenant bulk-delete (breakdown) failed for tenant={Tenant}; falling back to per-row delete.",
                    tenant);
                var rows = await db.Replays
                    .Where(r => r.TenantId == t && r.CompletedAt < c)
                    .ToListAsync(ct);
                db.Replays.RemoveRange(rows);
                await db.SaveChangesAsync(ct);
                breakdown[t] = (breakdown.TryGetValue(t, out var existing) ? existing : 0) + rows.Count;
            }
        }
        if (fallbackDays > 0)
        {
            var cutoff = utcNow.AddDays(-fallbackDays);
            var tenantKeys = map.Keys.ToList();
            try
            {
                // Use a single query to fetch the tenant ids of the rows
                // about to be evicted so the per-tenant breakdown picks
                // them up under their real bucket; the null bucket lands
                // under "" (rendered as "_unknown" by the metric).
                var doomed = await db.Replays
                    .Where(r => r.CompletedAt < cutoff)
                    .Where(r => r.TenantId == null || !tenantKeys.Contains(r.TenantId))
                    .Select(r => r.TenantId)
                    .ToListAsync(ct);
                if (doomed.Count > 0)
                {
                    await db.Replays
                        .Where(r => r.CompletedAt < cutoff)
                        .Where(r => r.TenantId == null || !tenantKeys.Contains(r.TenantId))
                        .ExecuteDeleteAsync(ct);
                    foreach (var raw in doomed)
                    {
                        var bucket = string.IsNullOrEmpty(raw) ? string.Empty : raw!;
                        breakdown[bucket] = (breakdown.TryGetValue(bucket, out var existing) ? existing : 0) + 1;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Fallback bulk-delete (breakdown) failed; per-row pass.");
                var rows = await db.Replays
                    .Where(r => r.CompletedAt < cutoff)
                    .Where(r => r.TenantId == null || !tenantKeys.Contains(r.TenantId))
                    .ToListAsync(ct);
                db.Replays.RemoveRange(rows);
                await db.SaveChangesAsync(ct);
                foreach (var row in rows)
                {
                    var bucket = string.IsNullOrEmpty(row.TenantId) ? string.Empty : row.TenantId!;
                    breakdown[bucket] = (breakdown.TryGetValue(bucket, out var existing) ? existing : 0) + 1;
                }
            }
        }
        return breakdown;
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Replays.CountAsync(ct);
    }

    public async Task<IReadOnlyList<ReplayRecord>> ListAsync(
        DateTime? fromUtc,
        DateTime? toUtc,
        string? variant,
        int skip,
        int take,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IQueryable<ReplayRecord> q = db.Replays.AsNoTracking();
        if (fromUtc is { } f) q = q.Where(r => r.CompletedAt >= f);
        if (toUtc is { } t) q = q.Where(r => r.CompletedAt <= t);
        if (!string.IsNullOrWhiteSpace(variant))
        {
            var v = variant.Trim();
            q = q.Where(r => r.Variant == v);
        }
        // Metadata-only projection — the heavy CompressedPayload
        // column is intentionally dropped from the wire so the
        // listing endpoint stays cheap even when the result set
        // spans tens of thousands of rows.
        return await q
            .OrderByDescending(r => r.CompletedAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Max(0, take))
            .Select(r => new ReplayRecord
            {
                ReplayId = r.ReplayId,
                GameId = r.GameId,
                CompletedAt = r.CompletedAt,
                Variant = r.Variant,
                TurnCount = r.TurnCount,
                IngestedAt = r.IngestedAt,
                ExpiresAt = r.ExpiresAt,
                TenantId = r.TenantId,
                CompressedPayload = Array.Empty<byte>(),
            })
            .ToListAsync(ct);
    }
}

/// <summary>
/// Phase K Wave 12 — Bishop. URL-safe synthetic replay id minter.
/// Format: <c>r-{8 url-safe base64 chars}</c>. 6 bytes of
/// randomness = 48 bits ≈ 2.8 × 10¹⁴ values; collision-free at any
/// realistic ingest rate.
/// </summary>
public static class ReplayIdGenerator
{
    private const string Prefix = "r-";

    public static string Mint()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        // url-safe base64 without padding — 8 chars for 6 bytes.
        return Prefix + Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}

/// <summary>
/// Phase K Wave 12 — Bishop. Configuration for the replay
/// persistence surface. Bound from the <c>Replays</c>
/// configuration section.
/// </summary>
public sealed class ReplayOptions
{
    /// <summary>Default retention window (days). Replay rows
    /// older than this are deleted by the nightly sweeper.</summary>
    public const int DefaultRetentionDays = 90;

    /// <summary>Implementation selector — case-insensitive.
    /// <c>"InMemory"</c> uses <see cref="InMemoryReplayStore"/>
    /// (default for tests + dev); <c>"Ef"</c> uses
    /// <see cref="EfReplayStore"/> (default for production).</summary>
    public string StorageImpl { get; set; } = "InMemory";

    /// <summary>Retention window in days. 0 = use the default
    /// (<see cref="DefaultRetentionDays"/>).</summary>
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>Background sweep cadence in hours. Default 24
    /// (nightly). Lowered values are tolerated for test
    /// fixtures.</summary>
    public int SweepIntervalHours { get; set; } = 24;

    /// <summary>
    /// Phase K Wave 20 — Bishop. Cadence of the W20
    /// auto-expiry CronJob handler in minutes. Default
    /// 60 (hourly). The handler walks the W16 per-tenant
    /// breakdown sweep on every tick and emits one
    /// <c>replay_expired_total{tenant=…}</c> observation per
    /// evicted row. Lowered values are tolerated for test
    /// fixtures; 0 falls back to the default 60.
    /// </summary>
    public int AutoExpiryTickIntervalMinutes { get; set; } = 60;

    /// <summary>Maximum compressed-payload size accepted on
    /// POST. Defaults to 8 MB — large enough for the longest
    /// 16-hand replays we've observed (~600 KB after gzip),
    /// small enough that a hostile client can't pin a worker
    /// thread on a multi-MB upload.</summary>
    public int MaxCompressedBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Phase K Wave 13 — Bishop. When <c>true</c> (default),
    /// <c>POST /api/replays</c> requires an authenticated session
    /// with the <c>admin</c> role. Anonymous callers get HTTP 401,
    /// non-admin sessions get HTTP 403. Development fixtures can
    /// flip this to <c>false</c> to keep the W12 open-POST behaviour
    /// for fast iteration. See <c>docs/replay-by-id.md</c> §POST
    /// admin gating.
    /// </summary>
    public bool RequireAdminForPost { get; set; } = true;

    /// <summary>
    /// Phase K Wave 14 — Bishop. Default page size for the
    /// metadata-only listing endpoint
    /// (<c>GET /api/replays</c>). Bound from
    /// <c>Replays:PageSize</c>. See
    /// <c>docs/replay-by-id.md §3</c>.
    /// </summary>
    public const int DefaultPageSize = 25;

    /// <summary>
    /// Phase K Wave 14 — Bishop. Hard upper bound on the listing
    /// page size — larger client-supplied <c>limit</c> values are
    /// silently clamped.
    /// </summary>
    public const int MaxPageSize = 100;

    /// <summary>
    /// Phase K Wave 14 — Bishop. Server-side default page size.
    /// 0 / negative → use <see cref="DefaultPageSize"/>.
    /// </summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>
    /// Phase K Wave 15 — Bishop. Hourly retention sweep cadence
    /// (minutes) for <see cref="ReplayStoreRetentionSweep"/>. The
    /// W12 sweep ran daily against <c>ExpiresAt</c>; the W15
    /// sweep runs at this cadence against
    /// <c>CompletedAt &lt; now - RetentionDays</c> so changes to
    /// <see cref="RetentionDays"/> retro-apply on the next tick.
    /// Default 60 minutes. Values &lt; 1 fall back to 1 minute
    /// (tests pin a short cadence).
    /// </summary>
    public int StoreSweepIntervalMinutes { get; set; } = 60;
}

/// <summary>
/// Phase K Wave 12 — Bishop. Nightly background sweep that
/// deletes <see cref="ReplayRecord"/> rows past their configured
/// retention window. Registered as a hosted service only when
/// <c>Replays:StorageImpl="Ef"</c> — the in-memory store has no
/// on-disk footprint to sweep.
/// </summary>
public sealed class ReplayRetentionSweepService : BackgroundService
{
    private readonly IReplayStore _store;
    private readonly ReplayOptions _options;
    private readonly ILogger<ReplayRetentionSweepService> _logger;

    public ReplayRetentionSweepService(
        IReplayStore store,
        ReplayOptions options,
        ILogger<ReplayRetentionSweepService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, _options.SweepIntervalHours));
        _logger.LogInformation(
            "ReplayRetentionSweepService started (interval={Hours}h, retention={Days}d).",
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
                    "ReplayRetentionSweep failed (non-fatal); next tick in {Hours}h.",
                    interval.TotalHours);
            }
        }

        _logger.LogInformation("ReplayRetentionSweepService stopped.");
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
                "ReplayRetentionSweep removed {Count} expired record(s) older than {Cutoff:O}.",
                removed, cutoff);
        }
        return removed;
    }
}

/// <summary>
/// Phase K Wave 15 — Bishop. Hourly retention sweep that
/// evaluates the current configured <c>Replays:RetentionDays</c>
/// against each row's <see cref="ReplayRecord.CompletedAt"/>.
/// Complements the W12 <see cref="ReplayRetentionSweepService"/>
/// (daily, <see cref="ReplayRecord.ExpiresAt"/>-based) by giving
/// the operator a live knob — a runtime change to
/// <c>Replays:RetentionDays</c> takes effect on the next hourly
/// tick instead of only on rows ingested after the change.
///
/// <para>The two sweeps coexist: both delete the same row at
/// most once. The W15 sweep is registered only when
/// <c>Replays:StorageImpl="Ef"</c> — the in-memory store has no
/// on-disk footprint and the W12 service is sufficient.</para>
///
/// <para>Toggle: <see cref="ReplayOptions.StoreSweepIntervalMinutes"/>
/// (default 60). See <c>docs/replay-by-id.md §4</c>.</para>
/// </summary>
public sealed class ReplayStoreRetentionSweep : BackgroundService
{
    public const int DefaultSweepIntervalMinutes = 60;

    private readonly IReplayStore _store;
    private readonly ReplayOptions _options;
    private readonly ILogger<ReplayStoreRetentionSweep> _logger;
    private readonly IReplayRetentionPolicyStore? _policyStore;

    public ReplayStoreRetentionSweep(
        IReplayStore store,
        ReplayOptions options,
        ILogger<ReplayStoreRetentionSweep> logger,
        IReplayRetentionPolicyStore? policyStore = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _policyStore = policyStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = _options.StoreSweepIntervalMinutes > 0
            ? _options.StoreSweepIntervalMinutes
            : DefaultSweepIntervalMinutes;
        var interval = TimeSpan.FromMinutes(Math.Max(1, minutes));
        _logger.LogInformation(
            "ReplayStoreRetentionSweep started (interval={Minutes}m, retention={Days}d, perTenant={PerTenant}).",
            interval.TotalMinutes,
            _options.RetentionDays,
            _policyStore is not null);

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
                    "ReplayStoreRetentionSweep failed (non-fatal); next tick in {Minutes}m.",
                    interval.TotalMinutes);
            }
        }

        _logger.LogInformation("ReplayStoreRetentionSweep stopped.");
    }

    /// <summary>Single-sweep entry-point exposed so tests can
    /// drive deletions deterministically. When a per-tenant
    /// policy store is wired (W16), the sweep consults it
    /// before falling back to the global retention; otherwise
    /// the W15 global-only path runs unchanged.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var retention = _options.RetentionDays > 0
            ? _options.RetentionDays
            : ReplayOptions.DefaultRetentionDays;
        var utcNow = DateTime.UtcNow;
        int removed;
        if (_policyStore is not null)
        {
            removed = await _store.SweepWithPerTenantPolicyAsync(
                _policyStore, retention, utcNow, ct);
        }
        else
        {
            removed = await _store.SweepByCompletedAtAsync(retention, utcNow, ct);
        }
        if (removed > 0)
        {
            _logger.LogInformation(
                "ReplayStoreRetentionSweep removed {Count} record(s) (fallbackRetention={Days}d, cutoff={Cutoff:O}).",
                removed, retention, utcNow.AddDays(-retention));
        }
        return removed;
    }
}
