using System.Security.Cryptography;
using System.Text;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Replays;

/// <summary>
/// Phase K Wave 19 — Bishop. Admin-gated read surface for the
/// replay-store integrity audit. The replay table is the durable
/// home for completed-game playback; operators chasing a
/// suspected mutation (replay tampering, retention-sweep bug,
/// per-tenant data leak) need a deterministic checksum projection
/// scoped by time-range + tenant so they can diff snapshots
/// between two runs and prove the store has not silently mutated
/// under them.
///
/// <list type="bullet">
///   <item><c>GET /api/admin/replays/integrity-audit?from=&lt;iso&gt;&amp;to=&lt;iso&gt;[&amp;tenant=&lt;t&gt;]</c>
///         — returns the per-tenant checksum + row count for
///         every <see cref="ReplayRecord"/> whose
///         <see cref="ReplayRecord.IngestedAt"/> falls inside
///         the supplied window.</item>
/// </list>
///
/// <para>Response shape:
/// <code>
/// {
///   "from": "2026-05-01T00:00:00Z",
///   "to": "2026-05-31T23:59:59Z",
///   "tenantFilter": "tenant-abc",   // null = no filter
///   "tenants": [
///     {
///       "tenantId": "tenant-abc",
///       "rowCount": 1234,
///       "checksum": "&lt;sha-256 hex&gt;"
///     },
///     ...
///   ],
///   "totalRowCount": 1234,
///   "globalChecksum": "&lt;sha-256 hex&gt;"
/// }
/// </code></para>
///
/// <para>Checksum: SHA-256 over the canonical, sorted projection
/// <c>"&lt;ReplayId&gt;|&lt;GameId&gt;|&lt;CompletedAtUtcTicks&gt;|&lt;TurnCount&gt;|&lt;IngestedAtUtcTicks&gt;"</c>
/// — joined with newlines. The sort order is by <c>ReplayId</c>
/// (the synthetic primary key) so the checksum is deterministic
/// across replicas. The global checksum hashes every per-tenant
/// row across the entire window in tenant-id order so two callers
/// that ran the same query at the same instant get identical
/// global digests.</para>
///
/// <para>Auth: 401 (no session) → 403 (non-admin) → 400 (window
/// invalid / missing) → 200 (success). The endpoint never reads
/// the gzip-compressed payload column — it operates on metadata
/// only so a tenant with millions of rows can still be audited
/// without a multi-GB network round-trip.</para>
///
/// <para>Audit: every successful call emits a
/// <see cref="ReconnectAuditEntry.KindReplayIntegrityAudit"/>
/// row with <c>Detail = "from=&lt;iso&gt;|to=&lt;iso&gt;|tenants=&lt;n&gt;|rows=&lt;n&gt;"</c>.</para>
///
/// <para>See <c>docs/replay-by-id.md §6 "Integrity audit"</c>
/// (added W19) for the operator runbook.</para>
/// </summary>
[ApiController]
[Route("api/admin/replays/integrity-audit")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class ReplayStoreIntegrityAuditController : ControllerBase
{
    /// <summary>Maximum permitted window span. 90 days — the
    /// default replay retention window. Larger windows return
    /// HTTP 400 so a runaway caller cannot scan the entire
    /// table.</summary>
    public const int MaxWindowDays = 90;

    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReplayStoreIntegrityAuditController> _logger;

    public ReplayStoreIntegrityAuditController(
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory,
        ILogger<ReplayStoreIntegrityAuditController> logger)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet]
    public async Task<IActionResult> Audit(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? tenant,
        CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "session-required" });
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "admin-required" });
        }

        if (from is null) return BadRequest(new { error = "from-required" });
        if (to is null) return BadRequest(new { error = "to-required" });
        var fromUtc = DateTime.SpecifyKind(from.Value, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to.Value, DateTimeKind.Utc);
        if (toUtc <= fromUtc) return BadRequest(new { error = "to-must-follow-from" });
        if ((toUtc - fromUtc).TotalDays > MaxWindowDays)
        {
            return BadRequest(new
            {
                error = "window-exceeds-maximum",
                maximumDays = MaxWindowDays,
            });
        }

        var tenantFilter = string.IsNullOrWhiteSpace(tenant) ? null : tenant.Trim();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        IQueryable<ReplayRecord> query = db.Replays.AsNoTracking()
            .Where(r => r.IngestedAt >= fromUtc && r.IngestedAt <= toUtc);
        if (tenantFilter is not null)
        {
            query = query.Where(r => r.TenantId == tenantFilter);
        }

        // Metadata-only projection — drop the gzip-compressed
        // payload column so the audit walks a slim row.
        var rows = await query
            .OrderBy(r => r.TenantId)
            .ThenBy(r => r.ReplayId)
            .Select(r => new
            {
                r.ReplayId,
                r.GameId,
                r.CompletedAt,
                r.TurnCount,
                r.IngestedAt,
                r.TenantId,
            })
            .ToListAsync(ct);

        // Per-tenant checksum + the global digest.
        var perTenant = new List<object>();
        var globalSha = SHA256.Create();
        var byTenant = rows
            .GroupBy(r => r.TenantId ?? string.Empty, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in byTenant)
        {
            var tenantSha = SHA256.Create();
            foreach (var r in group.OrderBy(x => x.ReplayId, StringComparer.Ordinal))
            {
                var projection = ProjectionFor(r.ReplayId, r.GameId, r.CompletedAt, r.TurnCount, r.IngestedAt);
                var bytes = Encoding.UTF8.GetBytes(projection + "\n");
                tenantSha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                globalSha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }
            tenantSha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            perTenant.Add(new
            {
                tenantId = string.IsNullOrEmpty(group.Key) ? null : group.Key,
                rowCount = group.Count(),
                checksum = Convert.ToHexString(tenantSha.Hash!).ToLowerInvariant(),
            });
            tenantSha.Dispose();
        }
        globalSha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var globalChecksum = Convert.ToHexString(globalSha.Hash!).ToLowerInvariant();
        globalSha.Dispose();

        await WriteAuditAsync(fromUtc, toUtc, perTenant.Count, rows.Count, ct);

        return Ok(new
        {
            from = fromUtc,
            to = toUtc,
            tenantFilter,
            tenants = perTenant,
            totalRowCount = rows.Count,
            globalChecksum,
        });
    }

    internal static string ProjectionFor(string replayId, Guid gameId, DateTime completedAt, int turnCount, DateTime ingestedAt)
    {
        // Use universal-sortable ticks so DST / timezone shifts
        // don't perturb the checksum.
        return string.Concat(
            replayId, "|",
            gameId.ToString("N"), "|",
            completedAt.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), "|",
            turnCount.ToString(System.Globalization.CultureInfo.InvariantCulture), "|",
            ingestedAt.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task WriteAuditAsync(DateTime fromUtc, DateTime toUtc, int tenantCount, int rowCount, CancellationToken ct)
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
                Kind = ReconnectAuditEntry.KindReplayIntegrityAudit,
                Detail = $"from={fromUtc:O}|to={toUtc:O}|tenants={tenantCount}|rows={rowCount}",
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Replay integrity audit write failed (from={From}, to={To}).",
                fromUtc, toUtc);
        }
    }
}
