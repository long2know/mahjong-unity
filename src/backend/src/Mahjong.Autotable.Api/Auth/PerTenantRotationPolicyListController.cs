using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 18 — Bishop. Dedicated LIST surface for the
/// per-tenant JWKS rotation policy table, sister-controller of
/// the W16 admin POST/PUT/DELETE/GET surface
/// (<see cref="PerTenantRotationAdminController"/>). The W17 list
/// endpoint at <c>GET /api/admin/jwks-rotation/per-tenant</c>
/// returned every row in tenant-id order; W18 lands a paginated
/// + tenant-prefix-filterable surface so an operator running a
/// cluster with thousands of tenants can search rather than
/// scroll.
///
/// <list type="bullet">
///   <item><c>GET /api/admin/per-tenant-jwks-rotation-policies</c>
///         — list with pagination + filtering.
///         Query parameters:
///         <list type="bullet">
///           <item><c>page</c> — 1-based page number. Default 1,
///                 max <see cref="MaxPage"/>.</item>
///           <item><c>pageSize</c> — Default
///                 <see cref="DefaultPageSize"/>, capped at
///                 <see cref="MaxPageSize"/>.</item>
///           <item><c>tenantPrefix</c> — Optional prefix filter
///                 against <see cref="PerTenantJwksRotationPolicy.TenantId"/>.
///                 Case-sensitive (matches storage semantics).</item>
///         </list>
///   </item>
/// </list>
///
/// <para>The response shape mirrors the W17 pagination envelope:
/// <c>{ items: [...], page, pageSize, totalCount, totalPages,
/// filter: { tenantPrefix } }</c>. Empty result → 200 with
/// <c>items: []</c> + matching counters; never 404 (consistent
/// with the W17 sibling).</para>
///
/// <para>Every LIST request emits a
/// <see cref="ReconnectAuditEntry.KindAuthJwksPerTenantListed"/>
/// audit row so a suspicious enumeration query can be replayed
/// off the audit feed without scraping HTTP logs. Audit detail
/// captures
/// <c>"page={page}|size={pageSize}|prefix={tenantPrefix ?? ""}|count={resultCount}"</c>.</para>
///
/// <para>Why the new route? The W16/W17 controller's existing
/// <c>GET /api/admin/jwks-rotation/per-tenant</c> response
/// shape is wire-stable for the operator dashboard; introducing
/// pagination there would break the dashboard contract. The new
/// route is the long-form pagination surface; both routes
/// continue to coexist. See
/// <c>docs/per-tenant-jwks-rotation.md §3.2 "Pagination"</c>
/// (added W18).</para>
/// </summary>
[ApiController]
[Route("api/admin/per-tenant-jwks-rotation-policies")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class PerTenantRotationPolicyListController : ControllerBase
{
    /// <summary>Default page size when <c>pageSize</c> query is
    /// absent. 50 — the dashboard's typical viewport.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Maximum permitted page size. 250 — the largest
    /// envelope that still fits inside the W14 page-size cap
    /// for tournament-scale queries. Larger requests are clamped
    /// DOWN with no error.</summary>
    public const int MaxPageSize = 250;

    /// <summary>Maximum permitted page number. 10_000 — guards
    /// against a runaway query that requests page 1_000_000 of
    /// an empty result set. Requests above this cap return 400.</summary>
    public const int MaxPage = 10_000;

    /// <summary>Maximum permitted tenant prefix length.
    /// 128 — matches the storage column width.</summary>
    public const int MaxTenantPrefixLength = 128;

    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PerTenantRotationPolicyListController> _logger;
    private readonly PerTenantJwksRotationOptions _options;
    private readonly IPerTenantJwksRotationStore? _store;

    public PerTenantRotationPolicyListController(
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory,
        ILogger<PerTenantRotationPolicyListController> logger,
        PerTenantJwksRotationOptions options,
        IPerTenantJwksRotationStore? store = null)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store;
    }

    private async Task<IActionResult?> GateAsync(CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null)
        {
            return Unauthorized(new { error = "session-required" });
        }
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "admin-required" });
        }
        if (!_options.Enabled || _store is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "per-tenant-disabled",
                detail = "Set JwksRotation:PerTenant:Enabled=true to enable this endpoint.",
            });
        }
        return null;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? tenantPrefix,
        CancellationToken ct)
    {
        var gate = await GateAsync(ct);
        if (gate is not null) return gate;

        var resolvedPage = page ?? 1;
        var resolvedSize = pageSize ?? DefaultPageSize;
        if (resolvedPage < 1)
        {
            return BadRequest(new { error = "page-must-be-positive" });
        }
        if (resolvedPage > MaxPage)
        {
            return BadRequest(new { error = "page-exceeds-maximum", maximum = MaxPage });
        }
        if (resolvedSize < 1)
        {
            return BadRequest(new { error = "pageSize-must-be-positive" });
        }
        var clampedSize = resolvedSize > MaxPageSize ? MaxPageSize : resolvedSize;

        var normalizedPrefix = tenantPrefix?.Trim();
        if (normalizedPrefix is { Length: 0 }) normalizedPrefix = null;
        if (normalizedPrefix is { Length: > MaxTenantPrefixLength })
        {
            return BadRequest(new
            {
                error = "tenantPrefix-exceeds-maximum",
                maximum = MaxTenantPrefixLength,
            });
        }

        var allRows = await _store!.ListAsync(ct);
        var filteredRows = normalizedPrefix is null
            ? allRows
            : allRows.Where(p => p.TenantId.StartsWith(normalizedPrefix, StringComparison.Ordinal)).ToList();

        var totalCount = filteredRows.Count;
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)clampedSize);
        var skip = (resolvedPage - 1) * clampedSize;
        var pageRows = filteredRows
            .Skip(skip)
            .Take(clampedSize)
            .Select(ProjectRow)
            .ToArray();

        await WriteAuditAsync(resolvedPage, clampedSize, normalizedPrefix, pageRows.Length, ct);

        return Ok(new
        {
            items = pageRows,
            page = resolvedPage,
            pageSize = clampedSize,
            totalCount,
            totalPages,
            filter = new { tenantPrefix = normalizedPrefix },
        });
    }

    private static object ProjectRow(PerTenantJwksRotationPolicy p) => new
    {
        tenantId = p.TenantId,
        activeKid = p.ActiveKid,
        previousKid = p.PreviousKid,
        rotationStartUtc = p.RotationStartUtc,
        rotationCompleteUtc = p.RotationCompleteUtc,
        overlapWindowDays = p.OverlapWindowDays,
        createdAt = p.CreatedAt,
        updatedAt = p.UpdatedAt,
    };

    private async Task WriteAuditAsync(
        int page,
        int pageSize,
        string? tenantPrefix,
        int resultCount,
        CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
            {
                Id = Guid.NewGuid(),
                PlayerId = "admin",
                At = DateTime.UtcNow,
                Kind = ReconnectAuditEntry.KindAuthJwksPerTenantListed,
                Detail = $"page={page}|size={pageSize}|prefix={tenantPrefix ?? string.Empty}|count={resultCount}",
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Per-tenant rotation list audit write failed for page={Page}, prefix={Prefix}.",
                page, tenantPrefix);
        }
    }
}
