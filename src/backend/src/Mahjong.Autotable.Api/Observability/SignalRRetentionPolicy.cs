using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations.Schema;
using Mahjong.Autotable.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Observability;

/// <summary>
/// Phase K Wave 17 — Bishop. Per-tenant SignalR sequence
/// retention policy row. The W12 + W13 + W14 surfaces shipped
/// a single GLOBAL retention window
/// (<see cref="SignalRSequenceStoreOptions.RetentionMinutes"/>);
/// W17 lands the per-tenant override so operators running a
/// multi-tenant cluster can give a paying customer a longer
/// replay window than the free-tier default without touching
/// the global knob.
///
/// <para>The W13 <see cref="SignalRSequenceRetentionSweep"/>
/// already walks <c>ExpiresAt &lt; now</c>; the W17 sweep widens
/// to consult <see cref="ISignalRRetentionPolicyStore.GetAsync"/>
/// for every distinct tenant before the global default applies.
/// The store seam is intentionally narrow: upsert, lookup, list,
/// delete. Operator UX (admin CRUD controller) is wired in
/// <c>SignalRRetentionAdminController</c>.</para>
///
/// <para>The default per-tenant TTL is <c>24 hours</c> (the
/// 1440-minute spec called out by W17). Zero / negative is
/// treated as "policy absent" by the sweep — the global
/// default applies.</para>
///
/// <para>See <c>docs/realtime-resilience.md §7</c>.</para>
/// </summary>
public sealed class SignalRRetentionPolicy
{
    /// <summary>Default per-tenant retention window in minutes
    /// (24 hours). Operators tune this per-tenant; the global
    /// default
    /// <see cref="SignalRSequenceStoreOptions.RetentionMinutes"/>
    /// remains in force when no per-tenant row matches.</summary>
    public const int DefaultRetentionMinutes = 24 * 60;

    /// <summary>Upper bound on per-tenant retention. Sixty days
    /// of sequence rows is well beyond the longest reconnect
    /// window the platform has seen; pinning a max keeps a
    /// runaway upsert from costing the operator the database.</summary>
    public const int MaxRetentionMinutes = 60 * 24 * 60;

    /// <summary>Stable tenant identifier. Maps to the
    /// <c>tenant</c> claim on multi-tenant JWTs (mirrors
    /// <see cref="Mahjong.Autotable.Api.Auth.PerTenantJwksRotationPolicy.TenantId"/>).
    /// PK + index — the dominant read path is single-row lookup
    /// by tenant id from inside the sweep.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Per-tenant retention window in minutes. Default
    /// <see cref="DefaultRetentionMinutes"/>. Must be strictly
    /// positive — zero / negative is treated as "policy absent"
    /// by the sweep so the global default applies.</summary>
    public int RetentionMinutes { get; set; } = DefaultRetentionMinutes;

    /// <summary>UTC timestamp when the row was first written.
    /// Surfaced for audit / dashboard rendering.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when the row was last mutated.
    /// Bumped on every upsert through the store.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary><see cref="DateTimeOffset"/> projection of
    /// <see cref="CreatedAt"/>.</summary>
    [NotMapped]
    public DateTimeOffset CreatedAtOffset
    {
        get => new(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc), TimeSpan.Zero);
        set => CreatedAt = value.UtcDateTime;
    }

    /// <summary><see cref="DateTimeOffset"/> projection of
    /// <see cref="UpdatedAt"/>.</summary>
    [NotMapped]
    public DateTimeOffset UpdatedAtOffset
    {
        get => new(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc), TimeSpan.Zero);
        set => UpdatedAt = value.UtcDateTime;
    }
}

/// <summary>
/// Phase K Wave 17 — Bishop. Persistence seam for the
/// per-tenant SignalR retention policy table. Upsert + lookup +
/// list + delete; the sweep consumes <see cref="GetAsync"/>
/// once per distinct tenant per tick to decide each tenant's
/// window.
/// </summary>
public interface ISignalRRetentionPolicyStore
{
    /// <summary>Upsert a per-tenant retention row keyed by
    /// <see cref="SignalRRetentionPolicy.TenantId"/>.</summary>
    Task<SignalRRetentionPolicy> UpsertAsync(
        SignalRRetentionPolicy policy,
        CancellationToken ct = default);

    /// <summary>Single-row lookup by tenant id. Returns
    /// <c>null</c> when no policy exists — the sweep falls
    /// back to the global default.</summary>
    Task<SignalRRetentionPolicy?> GetAsync(
        string tenantId,
        CancellationToken ct = default);

    /// <summary>List every policy row, ordered by tenant id.
    /// Surfaced for audit / ops dashboard rendering.</summary>
    Task<IReadOnlyList<SignalRRetentionPolicy>> ListAsync(
        CancellationToken ct = default);

    /// <summary>Delete a policy row. Returns the number of
    /// rows deleted (0 or 1).</summary>
    Task<int> DeleteAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Total row count — surfaced for tests.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}

/// <summary>
/// Phase K Wave 17 — Bishop. In-memory implementation. Mirrors
/// the EF impl shape so the same contract test suite passes
/// against both bindings.
/// </summary>
public sealed class InMemorySignalRRetentionPolicyStore : ISignalRRetentionPolicyStore
{
    private readonly ConcurrentDictionary<string, SignalRRetentionPolicy> _rows =
        new(StringComparer.Ordinal);

    public Task<SignalRRetentionPolicy> UpsertAsync(
        SignalRRetentionPolicy policy,
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
            existing.RetentionMinutes = policy.RetentionMinutes;
            existing.UpdatedAt = policy.UpdatedAt;
            return existing;
        });
        return Task.FromResult(_rows[policy.TenantId]);
    }

    public Task<SignalRRetentionPolicy?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId)) return Task.FromResult<SignalRRetentionPolicy?>(null);
        return Task.FromResult(_rows.TryGetValue(tenantId, out var v) ? v : null);
    }

    public Task<IReadOnlyList<SignalRRetentionPolicy>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<SignalRRetentionPolicy> rows = _rows.Values
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
/// Phase K Wave 17 — Bishop. EF-backed implementation. Persists
/// to <c>SignalRRetentionPolicies</c> keyed by tenant id.
/// </summary>
public sealed class EfSignalRRetentionPolicyStore : ISignalRRetentionPolicyStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EfSignalRRetentionPolicyStore> _logger;

    public EfSignalRRetentionPolicyStore(
        IServiceScopeFactory scopeFactory,
        ILogger<EfSignalRRetentionPolicyStore> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SignalRRetentionPolicy> UpsertAsync(
        SignalRRetentionPolicy policy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.TenantId))
        {
            throw new ArgumentException("TenantId required.", nameof(policy));
        }
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.SignalRRetentionPolicies
            .FirstOrDefaultAsync(p => p.TenantId == policy.TenantId, ct);
        if (existing is null)
        {
            policy.CreatedAt = DateTime.UtcNow;
            policy.UpdatedAt = policy.CreatedAt;
            db.SignalRRetentionPolicies.Add(policy);
        }
        else
        {
            existing.RetentionMinutes = policy.RetentionMinutes;
            existing.UpdatedAt = DateTime.UtcNow;
            policy = existing;
        }
        await db.SaveChangesAsync(ct);
        return policy;
    }

    public async Task<SignalRRetentionPolicy?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId)) return null;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SignalRRetentionPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
    }

    public async Task<IReadOnlyList<SignalRRetentionPolicy>> ListAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SignalRRetentionPolicies
            .AsNoTracking()
            .OrderBy(p => p.TenantId)
            .ToListAsync(ct);
    }

    public async Task<int> DeleteAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId)) return 0;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SignalRRetentionPolicies
            .Where(p => p.TenantId == tenantId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SignalRRetentionPolicies.CountAsync(ct);
    }
}
