using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Replays;

/// <summary>
/// Phase K Wave 21 — Bishop. Append-only audit row capturing a
/// single replay-store restoration attempt. The replay-by-id
/// surface is the durable home for completed-game playback;
/// operators chasing a "did this replay restore correctly?"
/// question need a per-replay attempt trail beyond the
/// integrity-audit checksum projection that W19 landed.
///
/// <para>One row is written per restoration attempt — both
/// successful reads and failures. The
/// <c>GET /api/admin/replays/{id}/restoration-audit</c>
/// endpoint returns the most recent 10 rows for a replay so the
/// operator gets a short trail without paging through the full
/// table.</para>
///
/// <para>Indexes: <c>(ReplayId, AttemptedAtUtc DESC)</c> is the
/// dominant read path; secondary index on
/// <see cref="AttemptedAtUtc"/> for the global
/// trail-by-time admin view.</para>
/// </summary>
public class ReplayRestorationAttempt
{
    public const int MaxOutcomeLength = 32;
    public const int MaxDetailLength = 512;

    /// <summary>Outcome wire-name: read succeeded.</summary>
    public const string OutcomeRead = "read";

    /// <summary>Outcome wire-name: restoration write succeeded.</summary>
    public const string OutcomeRestored = "restored";

    /// <summary>Outcome wire-name: read failed (replay missing).</summary>
    public const string OutcomeNotFound = "not-found";

    /// <summary>Outcome wire-name: integrity check failed —
    /// the replay's stored checksum does not match the
    /// computed checksum of the payload.</summary>
    public const string OutcomeIntegrityFailure = "integrity-failure";

    /// <summary>Outcome wire-name: caller was unauthorised.</summary>
    public const string OutcomeUnauthorised = "unauthorised";

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning replay id — joins to
    /// <see cref="ReplayRecord.ReplayId"/>.</summary>
    public string ReplayId { get; set; } = string.Empty;

    /// <summary>Caller id — admin name or "system" for
    /// background-service attempts.</summary>
    public string OperatorId { get; set; } = string.Empty;

    /// <summary>Outcome wire-name. One of the
    /// <see cref="OutcomeRead"/> / <see cref="OutcomeRestored"/> /
    /// <see cref="OutcomeNotFound"/> / <see cref="OutcomeIntegrityFailure"/> /
    /// <see cref="OutcomeUnauthorised"/> constants.</summary>
    public string Outcome { get; set; } = OutcomeRead;

    /// <summary>Free-form detail string — error message,
    /// computed hash, etc.</summary>
    public string DetailMessage { get; set; } = string.Empty;

    public DateTime AttemptedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Phase K Wave 21 — Bishop. Admin-gated endpoint that returns
/// the last 10 restoration-attempt rows for a replay.
/// Surface:
/// <c>GET /api/admin/replays/{id}/restoration-audit</c>.
///
/// <para>Auth: 401 (no session) → 403 (non-admin) → 404 (replay
/// missing) → 200 (success). Returns at most 10 rows ordered
/// most-recent-first.</para>
/// </summary>
[ApiController]
[Route("api/admin/replays/{replayId}/restoration-audit")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class ReplayRestorationAuditController : ControllerBase
{
    public const int MaxResults = 10;

    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;

    public ReplayRestorationAuditController(
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    [HttpGet]
    public async Task<IActionResult> Audit(
        [FromRoute] string replayId,
        CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "session-required" });
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "admin-required" });
        }
        if (string.IsNullOrWhiteSpace(replayId))
        {
            return BadRequest(new { error = "replay-id-required" });
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var replay = await db.Replays
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.ReplayId == replayId, ct);
        if (replay is null)
        {
            return NotFound(new { error = "replay-not-found", replayId });
        }

        var attempts = await db.ReplayRestorationAttempts
            .AsNoTracking()
            .Where(a => a.ReplayId == replayId)
            .OrderByDescending(a => a.AttemptedAtUtc)
            .Take(MaxResults)
            .ToListAsync(ct);

        // Stamp a fresh "read" attempt so the trail self-records
        // operator audits.
        db.ReplayRestorationAttempts.Add(new ReplayRestorationAttempt
        {
            ReplayId = replayId,
            OperatorId = session.PlayerId,
            Outcome = ReplayRestorationAttempt.OutcomeRead,
            DetailMessage = "audit-read",
            AttemptedAtUtc = DateTime.UtcNow,
        });
        db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
        {
            Id = Guid.NewGuid(),
            PlayerId = session.PlayerId,
            At = DateTime.UtcNow,
            Kind = ReconnectAuditEntry.KindReplayRestorationAttempt,
            Detail = $"replayId={replayId}|outcome={ReplayRestorationAttempt.OutcomeRead}|operator={session.PlayerId}",
        });
        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            replayId,
            attempts = attempts.Select(a => new
            {
                id = a.Id,
                operatorId = a.OperatorId,
                outcome = a.Outcome,
                detailMessage = a.DetailMessage,
                attemptedAtUtc = a.AttemptedAtUtc,
            }),
            count = attempts.Count,
        });
    }
}
