using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 21 — Bishop. Live Swiss pairing apply service.
/// W20 landed the "preview" path: <c>SwissPairingService.PairNextRoundAsync</c>
/// writes <see cref="SwissPairingAuditEntry"/> rows + returns the
/// proposed pairings, but never materialises <see cref="TournamentMatch"/>
/// rows. W21 closes the loop with the apply path: given a
/// <c>(tournamentId, round)</c> pair-id, walk the W20 audit rows
/// for that round and insert one <c>TournamentMatch</c> per
/// board.
///
/// <para><b>Idempotency</b> — the natural key is
/// <c>(TournamentId, Round, Board)</c>. A re-call with the same
/// pair-id finds matches already present and short-circuits
/// without writing duplicates. The wire response indicates
/// whether the call created the rows or returned an existing
/// snapshot (<c>created</c> boolean).</para>
///
/// <para><b>Wire-stable error codes</b>:
/// <list type="bullet">
///   <item><c>tournament-not-found</c> — no <see cref="Tournament"/>
///         row for the supplied id.</item>
///   <item><c>not-swiss-format</c> — the tournament's
///         <see cref="Tournament.Format"/> isn't
///         <c>"swiss"</c>.</item>
///   <item><c>round-not-paired</c> — no audit rows for the
///         supplied <c>(tournamentId, round)</c> — the W20 preview
///         was never run.</item>
///   <item><c>round-out-of-range</c> — the supplied round is
///         &lt; 1.</item>
/// </list></para>
///
/// <para>Audit Kind: <see cref="ReconnectAuditEntry.KindTournamentSwissRoundApplied"/>.</para>
///
/// <para>Documented in <c>docs/swiss-pairing.md §9 "Apply round"</c>
/// (added W21).</para>
/// </summary>
public sealed class SwissApplyRoundService
{
    public const string ErrorTournamentNotFound = "tournament-not-found";
    public const string ErrorNotSwissFormat = "not-swiss-format";
    public const string ErrorRoundNotPaired = "round-not-paired";
    public const string ErrorRoundOutOfRange = "round-out-of-range";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SwissApplyRoundService> _logger;

    public SwissApplyRoundService(
        IServiceScopeFactory scopeFactory,
        ILogger<SwissApplyRoundService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Materialise <see cref="TournamentMatch"/> rows from the
    /// W20 <see cref="SwissPairingAuditEntry"/> rows for
    /// <paramref name="tournamentId"/> + <paramref name="round"/>.
    /// Idempotent — a second call returns the existing matches
    /// with <c>Created=false</c>.
    /// </summary>
    public async Task<SwissApplyRoundResult> ApplyRoundAsync(
        Guid tournamentId,
        int round,
        CancellationToken ct)
    {
        if (round < 1)
        {
            return SwissApplyRoundResult.Failure(
                ErrorRoundOutOfRange, $"round must be >= 1 (was {round})");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tournament = await db.Tournaments
            .FirstOrDefaultAsync(t => t.Id == tournamentId, ct);
        if (tournament is null)
        {
            return SwissApplyRoundResult.Failure(
                ErrorTournamentNotFound, "tournament-not-found");
        }
        if (!string.Equals(tournament.Format, "swiss", StringComparison.OrdinalIgnoreCase))
        {
            return SwissApplyRoundResult.Failure(
                ErrorNotSwissFormat,
                $"tournament format is '{tournament.Format}', not 'swiss'");
        }

        // Idempotency check — if matches already exist for the
        // supplied (TournamentId, Round) we short-circuit without
        // inserting duplicates.
        var existing = await db.TournamentMatches
            .Where(m => m.TournamentId == tournamentId && m.Round == round)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
        if (existing.Count > 0)
        {
            return SwissApplyRoundResult.Success(
                tournamentId, round,
                created: false,
                boards: existing
                    .Select((m, idx) => new SwissApplyRoundResult.Board(
                        idx + 1, m.Id, m.Player1Id, m.Player2Id))
                    .ToList());
        }

        var auditRows = await db.SwissPairingAuditEntries
            .Where(a => a.TournamentId == tournamentId && a.Round == round)
            .OrderBy(a => a.Board)
            .ToListAsync(ct);
        if (auditRows.Count == 0)
        {
            return SwissApplyRoundResult.Failure(
                ErrorRoundNotPaired,
                $"no W20 pairing audit rows for tournament {tournamentId:N} round {round}");
        }

        var boards = new List<SwissApplyRoundResult.Board>(auditRows.Count);
        foreach (var entry in auditRows)
        {
            var match = new TournamentMatch
            {
                Id = Guid.NewGuid(),
                TournamentId = tournamentId,
                Round = round,
                Player1Id = entry.White,
                Player2Id = entry.Black,
                Status = "pending",
                CreatedAt = DateTime.UtcNow,
            };
            db.TournamentMatches.Add(match);
            boards.Add(new SwissApplyRoundResult.Board(
                entry.Board, match.Id, match.Player1Id, match.Player2Id));
        }

        db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
        {
            Id = Guid.NewGuid(),
            PlayerId = "admin",
            At = DateTime.UtcNow,
            Kind = ReconnectAuditEntry.KindTournamentSwissRoundApplied,
            Detail = $"tournamentId={tournamentId:N}|round={round}|boards={auditRows.Count}",
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException dux)
        {
            _logger.LogWarning(dux,
                "Swiss apply-round collided with existing match rows (tournament={Tournament}, round={Round}).",
                tournamentId, round);
            // Re-read the existing rows — a concurrent caller
            // beat us; surface their snapshot rather than fail.
            var snapshot = await db.TournamentMatches
                .Where(m => m.TournamentId == tournamentId && m.Round == round)
                .OrderBy(m => m.CreatedAt)
                .ToListAsync(ct);
            return SwissApplyRoundResult.Success(
                tournamentId, round,
                created: false,
                boards: snapshot
                    .Select((m, idx) => new SwissApplyRoundResult.Board(
                        idx + 1, m.Id, m.Player1Id, m.Player2Id))
                    .ToList());
        }

        return SwissApplyRoundResult.Success(tournamentId, round, created: true, boards);
    }
}

/// <summary>
/// Phase K Wave 21 — Bishop. Result envelope for
/// <see cref="SwissApplyRoundService.ApplyRoundAsync"/>.
/// </summary>
public sealed class SwissApplyRoundResult
{
    public bool Succeeded { get; }
    public string ErrorCode { get; }
    public string ErrorMessage { get; }
    public Guid TournamentId { get; }
    public int Round { get; }
    public bool Created { get; }
    public IReadOnlyList<Board> Boards { get; }

    private SwissApplyRoundResult(
        bool succeeded,
        string errorCode,
        string errorMessage,
        Guid tournamentId,
        int round,
        bool created,
        IReadOnlyList<Board> boards)
    {
        Succeeded = succeeded;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        TournamentId = tournamentId;
        Round = round;
        Created = created;
        Boards = boards;
    }

    public static SwissApplyRoundResult Success(
        Guid tournamentId, int round, bool created, IReadOnlyList<Board> boards) =>
        new(true, string.Empty, string.Empty, tournamentId, round, created, boards);

    public static SwissApplyRoundResult Failure(string code, string message) =>
        new(false, code, message, Guid.Empty, 0, false, Array.Empty<Board>());

    /// <summary>Per-board wire envelope.</summary>
    public sealed record Board(int BoardNumber, Guid MatchId, string Player1Id, string Player2Id);
}

/// <summary>
/// Phase K Wave 21 — Bishop. Admin-gated endpoint that materialises
/// the W20-proposed pairings into <see cref="TournamentMatch"/>
/// rows. Surface:
/// <c>POST /api/admin/tournaments/{id}/swiss-apply-round</c>.
///
/// <para>Auth: 401 (no session) → 403 (non-admin) → 400 (input
/// validation) → 404 (tournament missing) → 409 (apply collided
/// — rare; surfaced via the idempotency snapshot path) → 422
/// (round not paired) → 200 (success).</para>
///
/// <para>Mandatory headers: <c>X-Admin-Reason</c> (mirrors the
/// W20 controllers).</para>
/// </summary>
[ApiController]
[Route("api/admin/tournaments/{id:guid}/swiss-apply-round")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class SwissApplyRoundController : ControllerBase
{
    public const string AdminReasonHeader = "X-Admin-Reason";
    public const int MaxAdminReasonLength = 512;

    private readonly AuthCookieService _cookies;
    private readonly SwissApplyRoundService _service;

    public SwissApplyRoundController(
        AuthCookieService cookies,
        SwissApplyRoundService service)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public sealed class ApplyRequest
    {
        /// <summary>Round number to apply. Maps 1:1 to the
        /// W20 <see cref="SwissPairingAuditEntry.Round"/>.</summary>
        public int Round { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> ApplyRound(
        [FromRoute] Guid id,
        [FromBody] ApplyRequest? request,
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
        var reason = reasonValues.ToString();
        if (string.IsNullOrWhiteSpace(reason))
        {
            return BadRequest(new { error = "admin-reason-required", header = AdminReasonHeader });
        }
        if (reason.Length > MaxAdminReasonLength)
        {
            return BadRequest(new
            {
                error = "admin-reason-too-long",
                maximum = MaxAdminReasonLength,
                actual = reason.Length,
            });
        }

        if (request is null)
        {
            return BadRequest(new { error = "body-required" });
        }

        var result = await _service.ApplyRoundAsync(id, request.Round, ct);
        if (!result.Succeeded)
        {
            return result.ErrorCode switch
            {
                SwissApplyRoundService.ErrorTournamentNotFound =>
                    NotFound(new { error = result.ErrorCode, detail = result.ErrorMessage }),
                SwissApplyRoundService.ErrorRoundNotPaired =>
                    StatusCode(StatusCodes.Status422UnprocessableEntity,
                        new { error = result.ErrorCode, detail = result.ErrorMessage }),
                _ => BadRequest(new { error = result.ErrorCode, detail = result.ErrorMessage }),
            };
        }

        return Ok(new
        {
            tournamentId = result.TournamentId,
            round = result.Round,
            created = result.Created,
            boardCount = result.Boards.Count,
            boards = result.Boards.Select(b => new
            {
                board = b.BoardNumber,
                matchId = b.MatchId,
                player1Id = b.Player1Id,
                player2Id = b.Player2Id,
            }),
            reason,
        });
    }
}
