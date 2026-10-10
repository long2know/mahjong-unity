using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations.Schema;
using Mahjong.Autotable.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 15 — Bishop. Per-tenant JWKS rotation policy row.
/// The W14 staged rotation policy
/// (<see cref="JwtStagedRotationPolicy"/>) carries one global
/// rotation window — sufficient for single-tenant deployments
/// but coarse for multi-tenant clusters where one customer can
/// be mid-rotation while a sibling tenant is steady state.
///
/// <para>W15 lands the durable per-tenant row keyed by a stable
/// tenant identifier. Both rotation timestamps use
/// <see cref="DateTimeOffset"/> so a tenant whose ops team
/// schedules rotations in their local timezone keeps the offset
/// intact across persistence (the W14 <see cref="DateTime"/> path
/// stripped the offset on serialisation, surfacing as midnight-UTC
/// to operators outside UTC+0).</para>
///
/// <para>Toggle: <c>JwksRotation:PerTenant:Enabled</c> (default
/// false). When disabled, the table exists but no lookup path
/// consults it — the global <see cref="JwtStagedRotationPolicy"/>
/// remains authoritative. When enabled, a multi-tenant validator
/// surface resolves the per-tenant policy first and falls back to
/// the global window when no row matches. See
/// <c>docs/per-tenant-jwks.md</c>.</para>
/// </summary>
public sealed class PerTenantJwksRotationPolicy
{
    /// <summary>Stable tenant identifier. Maps to the
    /// <c>tenant</c> claim on multi-tenant JWTs (the same field
    /// future surfaces will resolve through the auth pipeline).
    /// PK + index — the dominant read path is single-row lookup
    /// by tenant id.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Instant the staged rotation opened for this
    /// tenant. <see cref="DateTimeOffset"/> retains the operator
    /// timezone across persistence — see the class-level remark
    /// on the W14 → W15 type widening.</summary>
    public DateTimeOffset RotationStartUtc { get; set; }

    /// <summary>Instant the staged rotation will complete (the
    /// overlap window's outer edge). Tokens minted before this
    /// instant under the previous active key continue to
    /// validate; after this instant the previous key is dropped
    /// and rollbacks fail with
    /// <see cref="JwtValidationService.ErrorRollbackRejected"/>.</summary>
    public DateTimeOffset RotationCompleteUtc { get; set; }

    /// <summary>Active key id (the kid the issuer is currently
    /// minting tokens under). MUST match an entry in the
    /// tenant's JWKS document.</summary>
    public string ActiveKid { get; set; } = string.Empty;

    /// <summary>Previous active key id (the kid the issuer was
    /// minting tokens under before <see cref="RotationStartUtc"/>).
    /// Empty when no rotation is in progress or the previous key
    /// has been retired.</summary>
    public string PreviousKid { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the row was first written.
    /// Surfaced for audit / dashboard rendering.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when the row was last mutated.
    /// Bumped on every upsert through <see cref="IPerTenantJwksRotationStore"/>.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Phase K Wave 17 — Bishop. <see cref="DateTimeOffset"/>
    /// projection of <see cref="CreatedAt"/>. The persisted
    /// column is still <c>DateTime</c> (Utc) — the projection
    /// stamps <see cref="TimeSpan.Zero"/> offset on read so
    /// downstream surfaces that prefer
    /// <see cref="DateTimeOffset"/> (e.g. the W16/W17 admin
    /// controller wire shape) don't round-trip through
    /// <c>DateTime</c>. The setter writes back through the
    /// underlying <see cref="CreatedAt"/> column so EF persists
    /// it transparently. <see cref="NotMappedAttribute"/> keeps
    /// EF from creating a duplicate column.
    /// </summary>
    [NotMapped]
    public DateTimeOffset CreatedAtOffset
    {
        get => new(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc), TimeSpan.Zero);
        set => CreatedAt = value.UtcDateTime;
    }

    /// <summary>Phase K Wave 17 — Bishop. <see cref="DateTimeOffset"/>
    /// projection of <see cref="UpdatedAt"/>. See
    /// <see cref="CreatedAtOffset"/> for the rationale.</summary>
    [NotMapped]
    public DateTimeOffset UpdatedAtOffset
    {
        get => new(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc), TimeSpan.Zero);
        set => UpdatedAt = value.UtcDateTime;
    }

    /// <summary>
    /// Phase K Wave 16 — Bishop. Per-row overlap-window grace
    /// period (days) AFTER <see cref="RotationCompleteUtc"/>.
    /// During the window, tokens signed under this policy still
    /// validate; past the window the policy is treated as STALE
    /// by <see cref="PerTenantJwksRotationValidator"/> and
    /// signing is gated. Zero / negative = fall back to the
    /// configuration-level
    /// <see cref="PerTenantJwksRotationOptions.DefaultOverlapDays"/>.
    /// Surfaced as a per-tenant override so a high-traffic
    /// tenant can buy a longer grace window than the default
    /// without forcing the global default to widen.
    /// </summary>
    public int OverlapWindowDays { get; set; } = 0;

    /// <summary>True when <paramref name="utcNow"/> falls inside
    /// the per-tenant rotation overlap window. The check uses
    /// <see cref="DateTimeOffset"/> comparisons so a non-UTC
    /// RotationStart still resolves correctly relative to the
    /// passed-in clock.</summary>
    public bool IsWithinOverlapWindow(DateTimeOffset utcNow) =>
        utcNow >= RotationStartUtc && utcNow <= RotationCompleteUtc;
}

/// <summary>
/// Phase K Wave 15 — Bishop. Persistence seam for the per-tenant
/// JWKS rotation policy table. Interface is intentionally narrow:
/// upsert a policy, lookup by tenant, list (audit). The toggle
/// gates registration — when <c>JwksRotation:PerTenant:Enabled</c>
/// is false the store is not wired and the validator surface
/// falls through to the global <see cref="JwtStagedRotationPolicy"/>.
/// </summary>
public interface IPerTenantJwksRotationStore
{
    /// <summary>Upsert a per-tenant rotation row keyed by
    /// <see cref="PerTenantJwksRotationPolicy.TenantId"/>. Bumps
    /// <see cref="PerTenantJwksRotationPolicy.UpdatedAt"/> on
    /// write.</summary>
    Task<PerTenantJwksRotationPolicy> UpsertAsync(
        PerTenantJwksRotationPolicy policy,
        CancellationToken ct = default);

    /// <summary>Single-row lookup by tenant id. Returns
    /// <c>null</c> when no policy exists for the tenant — the
    /// caller falls back to the global rotation policy.</summary>
    Task<PerTenantJwksRotationPolicy?> GetAsync(
        string tenantId, CancellationToken ct = default);

    /// <summary>List every policy row, ordered by tenant id.
    /// Surfaced for audit / ops dashboard rendering — paginate
    /// at the call site if the tenant count grows large.</summary>
    Task<IReadOnlyList<PerTenantJwksRotationPolicy>> ListAsync(CancellationToken ct = default);

    /// <summary>Total row count — surfaced for tests.</summary>
    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>
    /// Phase K Wave 17 — Bishop. Hard-delete a per-tenant
    /// rotation row by tenant id. Returns the number of rows
    /// deleted (0 when no row matched, 1 on success). The W16
    /// admin controller used an upsert-of-sentinel-row workaround
    /// because the W15 contract didn't expose this method; W17
    /// retires the workaround. Subsequent
    /// <see cref="GetAsync"/> calls return null and the validator
    /// treats the tenant as NoPolicy (falls back to the global
    /// rotation window).
    /// </summary>
    Task<int> DeleteAsync(string tenantId, CancellationToken ct = default);
}

/// <summary>
/// Phase K Wave 15 — Bishop. In-memory
/// <see cref="IPerTenantJwksRotationStore"/>. Wired when
/// <c>JwksRotation:PerTenant:Enabled=true</c> AND
/// <c>JwksRotation:PerTenant:StorageImpl="InMemory"</c> (default).
/// </summary>
public sealed class InMemoryPerTenantJwksRotationStore : IPerTenantJwksRotationStore
{
    private readonly ConcurrentDictionary<string, PerTenantJwksRotationPolicy> _rows =
        new(StringComparer.Ordinal);

    public Task<PerTenantJwksRotationPolicy> UpsertAsync(
        PerTenantJwksRotationPolicy policy,
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
            existing.RotationStartUtc = policy.RotationStartUtc;
            existing.RotationCompleteUtc = policy.RotationCompleteUtc;
            existing.ActiveKid = policy.ActiveKid;
            existing.PreviousKid = policy.PreviousKid;
            existing.OverlapWindowDays = policy.OverlapWindowDays;
            existing.UpdatedAt = policy.UpdatedAt;
            return existing;
        });
        return Task.FromResult(_rows[policy.TenantId]);
    }

    public Task<PerTenantJwksRotationPolicy?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId)) return Task.FromResult<PerTenantJwksRotationPolicy?>(null);
        return Task.FromResult(_rows.TryGetValue(tenantId, out var v) ? v : null);
    }

    public Task<IReadOnlyList<PerTenantJwksRotationPolicy>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PerTenantJwksRotationPolicy> rows = _rows.Values
            .OrderBy(r => r.TenantId, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<int> CountAsync(CancellationToken ct = default) =>
        Task.FromResult(_rows.Count);

    public Task<int> DeleteAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId)) return Task.FromResult(0);
        return Task.FromResult(_rows.TryRemove(tenantId, out _) ? 1 : 0);
    }
}

/// <summary>
/// Phase K Wave 15 — Bishop. EF-backed
/// <see cref="IPerTenantJwksRotationStore"/>. Wired when
/// <c>JwksRotation:PerTenant:Enabled=true</c> AND
/// <c>JwksRotation:PerTenant:StorageImpl="Ef"</c>. Persists rows
/// to the <c>PerTenantJwksRotationPolicies</c> table with
/// <see cref="PerTenantJwksRotationPolicy.TenantId"/> as the
/// primary key (single-row lookup is O(log n)).
/// </summary>
public sealed class EfPerTenantJwksRotationStore : IPerTenantJwksRotationStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EfPerTenantJwksRotationStore> _logger;

    public EfPerTenantJwksRotationStore(
        IServiceScopeFactory scopeFactory,
        ILogger<EfPerTenantJwksRotationStore> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PerTenantJwksRotationPolicy> UpsertAsync(
        PerTenantJwksRotationPolicy policy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.TenantId))
        {
            throw new ArgumentException("TenantId required.", nameof(policy));
        }
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.PerTenantJwksRotationPolicies
            .FirstOrDefaultAsync(p => p.TenantId == policy.TenantId, ct);
        if (existing is null)
        {
            policy.CreatedAt = DateTime.UtcNow;
            policy.UpdatedAt = policy.CreatedAt;
            db.PerTenantJwksRotationPolicies.Add(policy);
        }
        else
        {
            existing.RotationStartUtc = policy.RotationStartUtc;
            existing.RotationCompleteUtc = policy.RotationCompleteUtc;
            existing.ActiveKid = policy.ActiveKid;
            existing.PreviousKid = policy.PreviousKid;
            existing.OverlapWindowDays = policy.OverlapWindowDays;
            existing.UpdatedAt = DateTime.UtcNow;
            policy = existing;
        }
        await db.SaveChangesAsync(ct);
        return policy;
    }

    public async Task<PerTenantJwksRotationPolicy?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId)) return null;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PerTenantJwksRotationPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, ct);
    }

    public async Task<IReadOnlyList<PerTenantJwksRotationPolicy>> ListAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PerTenantJwksRotationPolicies
            .AsNoTracking()
            .OrderBy(p => p.TenantId)
            .ToListAsync(ct);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PerTenantJwksRotationPolicies.CountAsync(ct);
    }

    public async Task<int> DeleteAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId)) return 0;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PerTenantJwksRotationPolicies
            .Where(p => p.TenantId == tenantId)
            .ExecuteDeleteAsync(ct);
    }
}

/// <summary>
/// Phase K Wave 15 — Bishop. Configuration for the per-tenant
/// JWKS rotation surface. Bound from the
/// <c>JwksRotation:PerTenant</c> section.
/// </summary>
public sealed class PerTenantJwksRotationOptions
{
    /// <summary>
    /// Master toggle. Default false — single-tenant deployments
    /// fall through to the global
    /// <see cref="JwtStagedRotationPolicy"/>. Multi-tenant
    /// deployments flip this to true and populate the
    /// <c>PerTenantJwksRotationPolicies</c> table.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Implementation selector — case-insensitive.
    /// <c>"InMemory"</c> (default) uses the in-memory store;
    /// <c>"Ef"</c> persists to the database.
    /// </summary>
    public string StorageImpl { get; set; } = "InMemory";

    /// <summary>
    /// Phase K Wave 16 — Bishop. Default overlap-window grace
    /// period (days) applied AFTER each policy row's
    /// <see cref="PerTenantJwksRotationPolicy.RotationCompleteUtc"/>
    /// when the row does not pin its own
    /// <see cref="PerTenantJwksRotationPolicy.OverlapWindowDays"/>.
    /// During this window tokens still sign; past the window
    /// the validator gates signing for the affected tenant.
    /// Zero / negative falls back to
    /// <see cref="PerTenantJwksRotationValidator.DefaultOverlapDays"/>.
    /// </summary>
    public int DefaultOverlapDays { get; set; } = PerTenantJwksRotationValidator.DefaultOverlapDays;
}
