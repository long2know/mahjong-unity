using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations.Schema;
using Mahjong.Autotable.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Replays;

/// <summary>
/// Phase K Wave 16 — Bishop. Per-tenant replay retention policy
/// row. The W12 + W15
/// <see cref="ReplayOptions.RetentionDays"/> knob is a single
/// global window — operators running a multi-tenant cluster
/// need per-tenant overrides (a free-tier tenant may keep 7 days
/// while an enterprise tenant pays for 365). W16 lands the
/// durable per-tenant row keyed by a stable tenant identifier
/// + wires the value into the
/// <see cref="ReplayStoreRetentionSweep"/> hosted service so a
/// runtime upsert takes effect on the next tick.
///
/// <para>The W15 hourly sweep walks <c>CompletedAt &lt; now -
/// RetentionDays</c>; W16 widens that to consult the per-tenant
/// row first and fall back to <see cref="ReplayOptions.RetentionDays"/>
/// when no row matches. The store seam
/// (<see cref="IReplayRetentionPolicyStore"/>) is intentionally
/// narrow: upsert, lookup, list, delete. Operator UX lives
/// upstream — this row is consumed by the sweep, not directly
/// by an admin endpoint (W16 leaves the admin surface to a
/// future wave to keep the diff scoped).</para>
///
/// <para>See <c>docs/replay-by-id.md §4.1 "Per-tenant retention"</c>.</para>
/// </summary>
public sealed class ReplayRetentionPolicy
{
    /// <summary>Stable tenant identifier. Maps to the
    /// <c>tenant</c> claim on multi-tenant JWTs (mirrors
    /// <see cref="Mahjong.Autotable.Api.Auth.PerTenantJwksRotationPolicy.TenantId"/>).
    /// PK + index — the dominant read path is single-row lookup
    /// by tenant id from inside the sweep.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Per-tenant retention window in days. Must be
    /// strictly positive — zero / negative is treated as
    /// "policy absent" by the sweep so the global default
    /// applies.</summary>
    public int RetentionDays { get; set; }

    /// <summary>UTC timestamp when the row was first written.
    /// Surfaced for audit / dashboard rendering.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when the row was last mutated.
    /// Bumped on every upsert through the store.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Phase K Wave 17 — Bishop. <see cref="DateTimeOffset"/>
    /// projection of <see cref="CreatedAt"/>. Mirrors the W17
    /// widening on <see cref="Mahjong.Autotable.Api.Auth.PerTenantJwksRotationPolicy.CreatedAtOffset"/>.
    /// </summary>
    [NotMapped]
    public DateTimeOffset CreatedAtOffset
    {
        get => new(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc), TimeSpan.Zero);
        set => CreatedAt = value.UtcDateTime;
    }

    /// <summary>Phase K Wave 17 — Bishop. <see cref="DateTimeOffset"/>
    /// projection of <see cref="UpdatedAt"/>.</summary>
    [NotMapped]
    public DateTimeOffset UpdatedAtOffset
    {
        get => new(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc), TimeSpan.Zero);
        set => UpdatedAt = value.UtcDateTime;
    }
}

/// <summary>
/// Phase K Wave 16 — Bishop. Persistence seam for the
/// per-tenant replay retention policy table. Upsert + lookup +
/// list + delete; the sweep consumes <see cref="GetAsync"/>
/// once per tick to decide each tenant's window.
/// </summary>
public interface IReplayRetentionPolicyStore
{
    /// <summary>Upsert a per-tenant retention row keyed by
    /// <see cref="ReplayRetentionPolicy.TenantId"/>.</summary>
    Task<ReplayRetentionPolicy> UpsertAsync(
        ReplayRetentionPolicy policy,
        CancellationToken ct = default);

    /// <summary>Single-row lookup by tenant id. Returns
    /// <c>null</c> when no policy exists — the sweep falls
    /// back to the global default.</summary>
    Task<ReplayRetentionPolicy?> GetAsync(
        string tenantId,
        CancellationToken ct = default);

    /// <summary>List every policy row, ordered by tenant id.
    /// Surfaced for audit / ops dashboard rendering.</summary>
    Task<IReadOnlyList<ReplayRetentionPolicy>> ListAsync(
        CancellationToken ct = default);

    /// <summary>Delete a policy row. Returns the number of
    /// rows deleted (0 or 1).</summary>
    Task<int> DeleteAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Total row count — surfaced for tests.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}

/// <summary>
/// Phase K Wave 16 — Bishop. In-memory implementation. Mirrors
/// the EF impl shape so the same contract test suite passes
/// against both bindings.
/// </summary>
public sealed class InMemoryReplayRetentionPolicyStore : IReplayRetentionPolicyStore
{
    private readonly ConcurrentDictionary<string, ReplayRetentionPolicy> _rows =
        new(StringComparer.Ordinal);

    public Task<ReplayRetentionPolicy> UpsertAsync(
        ReplayRetentionPolicy policy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.TenantId))
        {
            throw new ArgumentException("TenantId required.", nameof(policy));
        }
        policy.UpdatedAt = DateTime.UtcNow;
        _rows.AddOrUpdate(policy.TenantId, policy, (_, existing) =>
        {
            existing.RetentionDays = policy.RetentionDays;
            existing.UpdatedAt = policy.UpdatedAt;
            return existing;
        });
        return Task.FromResult(_rows[policy.TenantId]);
    }

    public Task<ReplayRetentionPolicy?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId)) return Task.FromResult<ReplayRetentionPolicy?>(null);
        return Task.FromResult(_rows.TryGetValue(tenantId, out var v) ? v : null);
    }

    public Task<IReadOnlyList<ReplayRetentionPolicy>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ReplayRetentionPolicy> rows = _rows.Values
            .OrderBy(r => r.TenantId, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<int> DeleteAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId)) return Task.FromResult(0);
        return Task.FromResult(_rows.TryRemove(tenantId, out _) ? 1 : 0);
    }

    public Task<int> CountAsync(CancellationToken ct = default) =>
        Task.FromResult(_rows.Count);
}

/// <summary>
/// Phase K Wave 16 — Bishop. EF-backed implementation. Persists
/// to <c>ReplayRetentionPolicies</c> keyed by tenant id.
/// </summary>
public sealed class EfReplayRetentionPolicyStore : IReplayRetentionPolicyStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EfReplayRetentionPolicyStore> _logger;

    public EfReplayRetentionPolicyStore(
        IServiceScopeFactory scopeFactory,
        ILogger<EfReplayRetentionPolicyStore> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ReplayRetentionPolicy> UpsertAsync(
        ReplayRetentionPolicy policy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.TenantId))
        {
            throw new ArgumentException("TenantId required.", nameof(policy));
        }
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.ReplayRetentionPolicies
            .FirstOrDefaultAsync(p => p.TenantId == policy.TenantId, ct);
        if (existing is null)
        {
            policy.CreatedAt = DateTime.UtcNow;
            policy.UpdatedAt = policy.CreatedAt;
            db.ReplayRetentionPolicies.Add(policy);
        }
        else
        {
            existing.RetentionDays = policy.RetentionDays;
            existing.UpdatedAt = DateTime.UtcNow;
            policy = existing;
        }
        await db.SaveChangesAsync(ct);
        return policy;
    }

    public async Task<ReplayRetentionPolicy?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId)) return null;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ReplayRetentionPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
    }

    public async Task<IReadOnlyList<ReplayRetentionPolicy>> ListAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ReplayRetentionPolicies
            .AsNoTracking()
            .OrderBy(p => p.TenantId)
            .ToListAsync(ct);
    }

    public async Task<int> DeleteAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId)) return 0;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ReplayRetentionPolicies
            .Where(p => p.TenantId == tenantId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ReplayRetentionPolicies.CountAsync(ct);
    }
}
