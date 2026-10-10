using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Audit;

/// <summary>
/// Phase K Wave 22 — Bishop. Admin-gated paginated query
/// surface over the <see cref="ReconnectAuditEntry"/> audit log.
/// Surface:
/// <c>GET /api/admin/audit-log?kind=...&amp;actor=...&amp;from=...&amp;to=...&amp;page=...&amp;pageSize=...</c>.
///
/// <para>Filters:
/// <list type="bullet">
///   <item><c>kind</c> — exact match on
///         <see cref="ReconnectAuditEntry.Kind"/>.</item>
///   <item><c>actor</c> — exact match on
///         <see cref="ReconnectAuditEntry.PlayerId"/>.</item>
///   <item><c>from</c> / <c>to</c> — ISO 8601 UTC range on
///         <see cref="ReconnectAuditEntry.At"/>.</item>
/// </list>
/// Paging:
/// <list type="bullet">
///   <item><c>page</c> — 1-based page index (default 1).</item>
///   <item><c>pageSize</c> — default
///         <see cref="DefaultPageSize"/>, max
///         <see cref="MaxPageSize"/>.</item>
/// </list></para>
///
/// <para>Auth: 401 / 403 / 400 / 200. Reads emit a
/// <see cref="ReconnectAuditEntry.KindAuditLogQueried"/> meta
/// audit row so the trail captures who looked at the trail.</para>
/// </summary>
[ApiController]
[Route("api/admin/audit-log")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class AuditLogQueryController : ControllerBase
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;

    public AuditLogQueryController(
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] string? kind,
        [FromQuery] string? actor,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "session-required" });
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "admin-required" });
        }

        DateTime? fromUtc = ParseUtc(from);
        if (!string.IsNullOrWhiteSpace(from) && fromUtc is null)
        {
            return BadRequest(new { error = "from-must-be-iso8601" });
        }
        DateTime? toUtc = ParseUtc(to);
        if (!string.IsNullOrWhiteSpace(to) && toUtc is null)
        {
            return BadRequest(new { error = "to-must-be-iso8601" });
        }
        if (fromUtc is not null && toUtc is not null && fromUtc > toUtc)
        {
            return BadRequest(new { error = "from-after-to" });
        }
        int pageIndex = page ?? 1;
        if (pageIndex < 1)
        {
            return BadRequest(new { error = "page-must-be-positive", page = pageIndex });
        }
        int requestedPageSize = pageSize ?? DefaultPageSize;
        if (requestedPageSize < 1)
        {
            return BadRequest(new { error = "page-size-must-be-positive", pageSize = requestedPageSize });
        }
        int effectivePageSize = Math.Min(requestedPageSize, MaxPageSize);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        IQueryable<ReconnectAuditEntry> q = db.ReconnectAuditEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(kind))
        {
            var k = kind.Trim();
            q = q.Where(r => r.Kind == k);
        }
        if (!string.IsNullOrWhiteSpace(actor))
        {
            var a = actor.Trim();
            q = q.Where(r => r.PlayerId == a);
        }
        if (fromUtc is not null)
        {
            var fv = fromUtc.Value;
            q = q.Where(r => r.At >= fv);
        }
        if (toUtc is not null)
        {
            var tv = toUtc.Value;
            q = q.Where(r => r.At <= tv);
        }

        var totalCount = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(r => r.At)
            .ThenBy(r => r.Id)
            .Skip((pageIndex - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToListAsync(ct);

        // Meta audit — the trail captures the query parameters
        // so an investigator can see who searched for what.
        db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
        {
            Id = Guid.NewGuid(),
            PlayerId = string.IsNullOrEmpty(session.PlayerId) ? "admin" : session.PlayerId,
            At = DateTime.UtcNow,
            Kind = ReconnectAuditEntry.KindAuditLogQueried,
            Detail = $"kind={kind ?? string.Empty}|actor={actor ?? string.Empty}|page={pageIndex}|pageSize={effectivePageSize}|rows={rows.Count}",
        });
        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            count = rows.Count,
            totalCount,
            page = pageIndex,
            pageSize = effectivePageSize,
            requestedPageSize,
            pageSizeCapped = requestedPageSize > MaxPageSize,
            totalPages = effectivePageSize == 0 ? 0 : (totalCount + effectivePageSize - 1) / effectivePageSize,
            filters = new
            {
                kind,
                actor,
                from = fromUtc,
                to = toUtc,
            },
            events = rows.Select(r => new
            {
                id = r.Id,
                at = r.At,
                kind = r.Kind,
                actor = r.PlayerId,
                detail = r.Detail,
                correlationId = r.CorrelationId,
                idempotencyKey = r.IdempotencyKey,
            }).ToArray(),
        });
    }

    private static DateTime? ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTime.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return null;
        }
        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }
}
