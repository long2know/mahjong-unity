using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 22 — Bishop. Admin-gated endpoint that
/// finalizes a tournament: locks every round, computes the
/// final standings, persists one
/// <see cref="TournamentStanding"/> row per player, marks the
/// tournament status as <c>complete</c>, and emits a
/// <see cref="ReconnectAuditEntry.KindTournamentCompleted"/>
/// event row. Surface:
/// <c>POST /api/admin/tournaments/{id}/finalize</c>.
///
/// <para>The endpoint is idempotent — a second call returns
/// 200 with the already-recorded standings (no rows
/// re-stamped). Tournaments with any <c>pending</c> or
/// <c>in-progress</c> matches are refused with 409 +
/// <see cref="ErrorIncompleteRounds"/> so the operator surface
/// fails loudly rather than silently locking an unfinished
/// event.</para>
///
/// <para>Auth: 401 / 403 / 400 / 404 / 409 / 200. Mandatory
/// <c>X-Admin-Reason</c> header — the reason is stamped on
/// the audit row so the trail captures WHY the operator
/// finalized the tournament.</para>
/// </summary>
[ApiController]
[Route("api/admin/tournaments/{id:guid}/finalize")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class TournamentFinalizationController : ControllerBase
{
    public const string AdminReasonHeader = "X-Admin-Reason";
    public const int MaxAdminReasonLength = 512;

    public const string ErrorIncompleteRounds = "incomplete-rounds";
    public const string ErrorTournamentNotFound = "tournament-not-found";
    public const string ErrorTournamentNotStarted = "tournament-not-started";

    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;

    public TournamentFinalizationController(
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    [HttpPost]
    public async Task<IActionResult> Finalize(
        [FromRoute] Guid id,
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

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tournament = await db.Tournaments.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tournament is null)
        {
            return NotFound(new { error = ErrorTournamentNotFound, tournamentId = id });
        }

        // Idempotent: already-finalized tournaments return the
        // recorded standings without re-stamping rows. The
        // schema-level (TournamentId, PlayerId) unique index
        // is the safety net.
        if (string.Equals(tournament.Status, "complete", StringComparison.OrdinalIgnoreCase))
        {
            var existing = await db.TournamentStandings
                .AsNoTracking()
                .Where(s => s.TournamentId == id)
                .OrderBy(s => s.Rank)
                .ToListAsync(ct);
            return Ok(new
            {
                tournamentId = id,
                status = tournament.Status,
                idempotent = true,
                completedAt = tournament.CompletedAt,
                standings = existing.Select(s => new
                {
                    playerId = s.PlayerId,
                    rank = s.Rank,
                    points = s.Points,
                    gamesPlayed = s.GamesPlayed,
                }).ToArray(),
                adminReason,
            });
        }

        if (string.Equals(tournament.Status, "draft", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = ErrorTournamentNotStarted, status = tournament.Status });
        }

        var matches = await db.TournamentMatches
            .Where(m => m.TournamentId == id)
            .ToListAsync(ct);

        var incomplete = matches
            .Where(m => !string.Equals(m.Status, "complete", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (incomplete.Count > 0)
        {
            return Conflict(new
            {
                error = ErrorIncompleteRounds,
                tournamentId = id,
                incompleteMatches = incomplete.Count,
                rounds = incomplete.Select(m => m.Round).Distinct().OrderBy(r => r).ToArray(),
            });
        }

        // Compute per-player (wins, gamesPlayed) from the
        // completed matches. Multi-seat (round-robin) matches
        // contribute one game for every seated player.
        var playerWins = new Dictionary<string, int>(StringComparer.Ordinal);
        var playerGames = new Dictionary<string, int>(StringComparer.Ordinal);
        var registrations = await db.TournamentRegistrations
            .AsNoTracking()
            .Where(r => r.TournamentId == id)
            .ToListAsync(ct);
        foreach (var reg in registrations)
        {
            playerWins.TryAdd(reg.PlayerId, 0);
            playerGames.TryAdd(reg.PlayerId, 0);
        }
        foreach (var m in matches)
        {
            foreach (var seated in EnumerateSeats(m))
            {
                playerGames[seated] = playerGames.GetValueOrDefault(seated, 0) + 1;
            }
            if (!string.IsNullOrWhiteSpace(m.WinnerPlayerId))
            {
                playerWins[m.WinnerPlayerId] = playerWins.GetValueOrDefault(m.WinnerPlayerId, 0) + 1;
            }
        }

        // Competition ranking — same points → same rank; next
        // tie-resolved player skips ahead by the tie cohort
        // size.
        var ordered = playerGames.Keys
            .OrderByDescending(p => playerWins.GetValueOrDefault(p, 0))
            .ThenBy(p => p, StringComparer.Ordinal)
            .ToList();
        var finalizedAt = DateTime.UtcNow;
        int currentRank = 0;
        int seenCount = 0;
        int? lastPoints = null;
        var standings = new List<TournamentStanding>(ordered.Count);
        foreach (var pid in ordered)
        {
            seenCount++;
            var pts = playerWins.GetValueOrDefault(pid, 0);
            if (lastPoints is null || pts != lastPoints.Value)
            {
                currentRank = seenCount;
                lastPoints = pts;
            }
            standings.Add(new TournamentStanding
            {
                Id = Guid.NewGuid(),
                TournamentId = id,
                PlayerId = pid,
                Rank = currentRank,
                Points = pts,
                GamesPlayed = playerGames.GetValueOrDefault(pid, 0),
                FinalizedAtUtc = finalizedAt,
            });
        }
        db.TournamentStandings.AddRange(standings);

        tournament.Status = "complete";
        tournament.CompletedAt = finalizedAt;

        var winnerPlayerId = standings.FirstOrDefault(s => s.Rank == 1)?.PlayerId ?? string.Empty;

        db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
        {
            Id = Guid.NewGuid(),
            PlayerId = "admin",
            At = finalizedAt,
            Kind = ReconnectAuditEntry.KindTournamentFinalized,
            Detail = $"reason={adminReason}|tournamentId={id:N}|standings={standings.Count}",
        });
        db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
        {
            Id = Guid.NewGuid(),
            PlayerId = "admin",
            At = finalizedAt,
            Kind = ReconnectAuditEntry.KindTournamentCompleted,
            Detail = $"tournamentId={id:N}|winnerPlayerId={winnerPlayerId}|playerCount={standings.Count}",
        });

        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            tournamentId = id,
            status = tournament.Status,
            idempotent = false,
            completedAt = tournament.CompletedAt,
            standings = standings.Select(s => new
            {
                playerId = s.PlayerId,
                rank = s.Rank,
                points = s.Points,
                gamesPlayed = s.GamesPlayed,
            }).ToArray(),
            adminReason,
        });
    }

    /// <summary>
    /// Enumerates every non-empty seat on a match — supports
    /// 2-player formats (single-elim/Swiss) which leave
    /// Player3Id/Player4Id null AND 4-player round-robin
    /// matches which populate all four.
    /// </summary>
    internal static IEnumerable<string> EnumerateSeats(TournamentMatch m)
    {
        if (!string.IsNullOrWhiteSpace(m.Player1Id)) yield return m.Player1Id;
        if (!string.IsNullOrWhiteSpace(m.Player2Id)) yield return m.Player2Id;
        if (!string.IsNullOrWhiteSpace(m.Player3Id)) yield return m.Player3Id!;
        if (!string.IsNullOrWhiteSpace(m.Player4Id)) yield return m.Player4Id!;
    }
}
