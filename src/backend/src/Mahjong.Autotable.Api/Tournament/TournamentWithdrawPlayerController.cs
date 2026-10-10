using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 21 — Bishop. Admin-gated endpoint that
/// withdraws a player from an active tournament mid-event.
/// Surface:
/// <c>POST /api/admin/tournaments/{id}/withdraw-player</c>.
///
/// <para>Withdrawal sets the player's registration
/// <see cref="TournamentRegistration.Seed"/> to a negative
/// sentinel so downstream pairing services
/// (<see cref="SwissPairingService"/>) exclude them from
/// future rounds. Existing in-progress matches are dropped to
/// <c>pending</c> so the operator can re-pair upcoming rounds
/// via the W20/W21 Swiss surfaces. Completed matches are
/// untouched — the historical record is preserved.</para>
///
/// <para>The withdrawal audit row is stamped via
/// <see cref="ReconnectAuditEntry.KindTournamentPlayerWithdrawn"/>.</para>
///
/// <para>Auth: 401 / 403 / 400 / 404 / 200. Mandatory
/// <c>X-Admin-Reason</c> header.</para>
/// </summary>
[ApiController]
[Route("api/admin/tournaments/{id:guid}/withdraw-player")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class TournamentWithdrawPlayerController : ControllerBase
{
    public const string AdminReasonHeader = "X-Admin-Reason";
    public const int MaxAdminReasonLength = 512;
    public const int MaxReasonBodyLength = 1024;

    /// <summary>Sentinel <see cref="TournamentRegistration.Seed"/>
    /// value applied when a player is withdrawn. The pairing
    /// services filter <c>Seed &gt;= 0</c> so any negative seed
    /// keeps the player off the pairing roster while preserving
    /// the registration row for audit.</summary>
    public const int WithdrawnSeedSentinel = -1;

    public const string ErrorTournamentNotFound = "tournament-not-found";
    public const string ErrorPlayerNotRegistered = "player-not-registered";
    public const string ErrorAlreadyWithdrawn = "already-withdrawn";

    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;

    public TournamentWithdrawPlayerController(
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public sealed class WithdrawRequest
    {
        public string? PlayerId { get; set; }
        public string? Reason { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> WithdrawPlayer(
        [FromRoute] Guid id,
        [FromBody] WithdrawRequest? request,
        CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "session-required" });
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "admin-required" });
        }
        if (!HttpContext.Request.Headers.TryGetValue(AdminReasonHeader, out var reasonValues))
        {
            return BadRequest(new { error = "admin-reason-required", header = AdminReasonHeader });
        }
        var adminReason = reasonValues.ToString();
        if (string.IsNullOrWhiteSpace(adminReason))
        {
            return BadRequest(new { error = "admin-reason-required", header = AdminReasonHeader });
        }
        if (adminReason.Length > MaxAdminReasonLength)
        {
            return BadRequest(new { error = "admin-reason-too-long", maximum = MaxAdminReasonLength });
        }
        if (request is null)
        {
            return BadRequest(new { error = "body-required" });
        }
        if (string.IsNullOrWhiteSpace(request.PlayerId))
        {
            return BadRequest(new { error = "player-id-required" });
        }
        if (request.Reason is not null && request.Reason.Length > MaxReasonBodyLength)
        {
            return BadRequest(new { error = "reason-too-long", maximum = MaxReasonBodyLength });
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tournament = await db.Tournaments.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tournament is null)
        {
            return NotFound(new { error = ErrorTournamentNotFound, tournamentId = id });
        }

        var registration = await db.TournamentRegistrations
            .FirstOrDefaultAsync(r => r.TournamentId == id && r.PlayerId == request.PlayerId, ct);
        if (registration is null)
        {
            return NotFound(new { error = ErrorPlayerNotRegistered, playerId = request.PlayerId });
        }
        if (registration.Seed < 0)
        {
            return Conflict(new { error = ErrorAlreadyWithdrawn, playerId = request.PlayerId });
        }

        var originalSeed = registration.Seed;
        registration.Seed = WithdrawnSeedSentinel;

        // Drop pending / in-progress matches involving the
        // player so the next pairing round can pick them up.
        // Completed matches stay untouched — historical record.
        var inFlight = await db.TournamentMatches
            .Where(m => m.TournamentId == id
                && (m.Player1Id == request.PlayerId || m.Player2Id == request.PlayerId
                    || m.Player3Id == request.PlayerId || m.Player4Id == request.PlayerId)
                && m.Status != "complete")
            .ToListAsync(ct);
        int droppedRound = 0;
        foreach (var m in inFlight)
        {
            droppedRound = Math.Max(droppedRound, m.Round);
            db.TournamentMatches.Remove(m);
        }

        db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
        {
            Id = Guid.NewGuid(),
            PlayerId = "admin",
            At = DateTime.UtcNow,
            Kind = ReconnectAuditEntry.KindTournamentPlayerWithdrawn,
            Detail = $"reason={adminReason}|tournamentId={id:N}|playerId={request.PlayerId}|withdrawnFromRound={droppedRound}|originalSeed={originalSeed}",
        });

        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            tournamentId = id,
            playerId = request.PlayerId,
            originalSeed,
            droppedMatches = inFlight.Count,
            withdrawnFromRound = droppedRound,
            adminReason,
            note = request.Reason ?? string.Empty,
        });
    }
}
