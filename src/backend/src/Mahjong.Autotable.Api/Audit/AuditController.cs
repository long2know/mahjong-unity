using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Audit;

/// <summary>
/// Phase K Wave 8 — Bishop. <c>GET /api/audit/{correlationId}</c>
/// returns every <see cref="ReconnectAuditEntry"/> stamped with the
/// given correlation id, ordered by <see cref="ReconnectAuditEntry.At"/>.
///
/// <para>The endpoint is anonymous-but-rate-limited. A correlation id
/// is a 32-char hex Guid "N" form — invalid shapes return 400 before
/// we touch the database so a fuzz-floods-the-route attempt does not
/// reach the persistence layer.</para>
///
/// <para>The envelope shape mirrors the Wave-6 audit responses so the
/// admin console renders the trail consistently:</para>
/// <code>
/// {
///   "correlationId": "abcd...32hex",
///   "count": 3,
///   "events": [
///     { "id": "...", "at": "...", "kind": "...", "playerId": "...",
///       "detail": "...", "idempotencyKey": "...", "correlationId": "..." },
///     ...
///   ]
/// }
/// </code>
/// </summary>
[ApiController]
[Route("api/audit")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class AuditController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AuditController(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    [HttpGet("{correlationId}")]
    public async Task<IActionResult> GetByCorrelationId(
        [FromRoute] string correlationId,
        CancellationToken ct)
    {
        if (!IsValidCorrelationId(correlationId))
        {
            return BadRequest(new
            {
                error = "invalid-correlation-id",
                detail = "correlationId must be a 32-character hex Guid (\"N\" form).",
            });
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.ReconnectAuditEntries
            .AsNoTracking()
            .Where(r => r.CorrelationId == correlationId)
            .OrderBy(r => r.At)
            .ToListAsync(ct);

        return Ok(new
        {
            correlationId,
            count = rows.Count,
            events = rows.Select(r => new
            {
                id = r.Id,
                at = r.At,
                kind = r.Kind,
                playerId = r.PlayerId,
                detail = r.Detail,
                idempotencyKey = r.IdempotencyKey,
                correlationId = r.CorrelationId,
            }).ToArray(),
        });
    }

    internal static bool IsValidCorrelationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        if (value.Length is not 32 and not 36) return false;
        return Guid.TryParse(value, out _);
    }
}
