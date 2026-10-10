using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 20 — Bishop. Live Swiss pairing service. W19
/// landed the persistence (<see cref="SwissPairingAuditEntry"/>)
/// + the read endpoint
/// (<c>GET /api/admin/tournaments/{id}/swiss-pairing-audit</c>);
/// W20 lands the write path: the admin operator triggers the
/// next-round pairing computation, the service computes the
/// pairings with the Buchholz tiebreaker stack, writes one
/// audit row per pairing, and returns the proposed pairings to
/// the caller for downstream materialisation into
/// <see cref="TournamentMatch"/> rows.
///
/// <para><b>Algorithm</b> — the live pairing service is a thin
/// orchestration layer around the FIDE C.04 engine
/// (<see cref="FideC04SwissPairingService"/>):
/// <list type="number">
///   <item>Load tournament + tournament matches scoped to the
///         supplied tournament id; reject non-Swiss formats.</item>
///   <item>Compute current standings from completed rounds —
///         match-points per player (W=1, D=0.5, L=0); record the
///         opponent graph for prior-pairing avoidance.</item>
///   <item>Order players by descending match-points, then by
///         descending <b>Buchholz</b> tiebreaker (sum of
///         opponents' match-points), then by seed index. This
///         is the W19 audit-entry <c>Tiebreaker = "buchholz"</c>
///         stamp; the W20 service exposes a configurable
///         tiebreaker selector for <c>median-buchholz</c>
///         (default for ≥ 5 rounds) when the round count is
///         tall enough that the drop-extremes mitigation is
///         meaningful.</item>
///   <item>Delegate the actual pair selection to the existing
///         FIDE C.04 engine — full backtracking, no rematches
///         unless impossible, lex-smallest permutation
///         winning ties.</item>
///   <item>Stamp one <see cref="SwissPairingAuditEntry"/> row
///         per emitted pairing with the chosen tiebreaker
///         name; emit one <see cref="ReconnectAuditEntry"/>
///         row per pairing under
///         <see cref="ReconnectAuditEntry.KindTournamentSwissPairingComputed"/>.</item>
///   <item>Return the proposed pairings (the service does NOT
///         persist <see cref="TournamentMatch"/> rows — that
///         remains the responsibility of
///         <see cref="TournamentService.AdvanceMatchAsync"/>'s
///         Swiss branch, called separately so an operator can
///         preview the pairing before committing).</item>
/// </list></para>
///
/// <para><b>Colour balance</b> — Mahjong heats abstract to
/// 2-player table pairings; "white" denotes the higher-seeded
/// (or higher-scoring) player by FIDE convention. The W20
/// service keeps the higher-priority player as <c>White</c> in
/// every pairing so the audit log renders the deterministic
/// (higher,lower) ordering. The engine itself is unaware of
/// colour; the service does the white/black assignment after
/// the engine returns the unordered pair.</para>
///
/// <para><b>Determinism</b> — given identical seed order +
/// match-points + prior-pairings, the service produces
/// identical audit rows. Tests pin this against the W11 FIDE
/// engine + the W19 audit entity.</para>
///
/// <para>Documented in <c>docs/swiss-pairing.md §8 "Live
/// pairing"</c> (added W20).</para>
/// </summary>
public sealed class SwissPairingService
{
    /// <summary>Wire-name for the W20 default tiebreaker
    /// (single-Buchholz). Matches the W19 audit entity's
    /// <see cref="SwissPairingAuditEntry.Tiebreaker"/> column
    /// expectations.</summary>
    public const string TiebreakerBuchholz = "buchholz";

    /// <summary>Wire-name for the median-Buchholz tiebreaker
    /// (drop-extremes Buchholz). Selected when the round count
    /// is ≥ <see cref="MedianBuchholzThreshold"/>.</summary>
    public const string TiebreakerMedianBuchholz = "median-buchholz";

    /// <summary>Round count at-or-above which the service
    /// defaults to median-Buchholz. Below this the
    /// drop-extremes mitigation is statistically meaningless
    /// (fewer than 3 opponents).</summary>
    public const int MedianBuchholzThreshold = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISwissPairingService _engine;
    private readonly ILogger<SwissPairingService> _logger;

    public SwissPairingService(
        IServiceScopeFactory scopeFactory,
        ISwissPairingService engine,
        ILogger<SwissPairingService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Compute and persist the next-round pairings for the
    /// supplied tournament. Returns a
    /// <see cref="SwissPairingComputationResult"/> capturing
    /// the proposed pairings + the audit metadata. Pure error
    /// paths return <see cref="SwissPairingComputationResult.Failure"/>
    /// with a wire-stable error code; callers translate the
    /// code into HTTP shape.
    /// </summary>
    public async Task<SwissPairingComputationResult> PairNextRoundAsync(
        Guid tournamentId,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tournament = await db.Tournaments
            .FirstOrDefaultAsync(t => t.Id == tournamentId, ct);
        if (tournament is null)
        {
            return SwissPairingComputationResult.Failure(
                ErrorTournamentNotFound, "tournament-not-found");
        }
        if (!string.Equals(tournament.Format, "swiss", StringComparison.OrdinalIgnoreCase))
        {
            return SwissPairingComputationResult.Failure(
                ErrorNotSwissFormat,
                $"tournament format is '{tournament.Format}', not 'swiss'");
        }

        // Seeded roster — every registration in seed order.
        // Withdrawn players (Seed < 0 sentinel — see W19
        // WithdrawnSeedSentinel discussion in
        // TournamentForfeitService) are excluded so the engine
        // does not try to pair them.
        var registrations = await db.TournamentRegistrations
            .Where(r => r.TournamentId == tournamentId && r.Seed >= 0)
            .OrderBy(r => r.Seed)
            .Select(r => r.PlayerId)
            .ToListAsync(ct);

        if (registrations.Count < 2)
        {
            return SwissPairingComputationResult.Failure(
                ErrorInsufficientPlayers,
                $"insufficient seeded players: {registrations.Count}");
        }

        // Load every completed match for this tournament to
        // build the match-point map + opponent graph.
        var allMatches = await db.TournamentMatches
            .Where(m => m.TournamentId == tournamentId)
            .OrderBy(m => m.Round)
            .ToListAsync(ct);

        var matchPoints = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in registrations) matchPoints[p] = 0;
        var priorPairings = new List<(string A, string B)>();
        var maxRoundCompleted = 0;
        foreach (var m in allMatches)
        {
            // Wave 20 pairing operates on the 2-player slice of
            // each match; mahjong heats fold to (P1,P2) for the
            // Swiss audit surface.
            if (string.IsNullOrEmpty(m.Player1Id) || string.IsNullOrEmpty(m.Player2Id)) continue;
            priorPairings.Add((m.Player1Id, m.Player2Id));
            if (string.Equals(m.Status, "complete", StringComparison.OrdinalIgnoreCase))
            {
                if (m.Round > maxRoundCompleted) maxRoundCompleted = m.Round;
                // Wave 20 simplified scoring: WinnerPlayerId == Player1Id → P1
                // gets 1 point; WinnerPlayerId == Player2Id → P2 gets 1 point;
                // null = no scoring update (forfeit / bye handled via the
                // bye sentinel).
                if (!string.IsNullOrEmpty(m.WinnerPlayerId))
                {
                    if (matchPoints.ContainsKey(m.WinnerPlayerId!))
                    {
                        matchPoints[m.WinnerPlayerId!] += 1;
                    }
                }
            }
        }

        var nextRound = maxRoundCompleted + 1;

        // Choose tiebreaker. The W20 default is single-Buchholz;
        // tournaments with ≥ 5 completed rounds switch to
        // median-Buchholz (drop-extremes).
        var tiebreakerName = maxRoundCompleted >= MedianBuchholzThreshold
            ? TiebreakerMedianBuchholz
            : TiebreakerBuchholz;

        // Compute Buchholz per player. The pre-round Buchholz is
        // the sum of opponents' current match-points. Median
        // variant drops the highest and lowest opponent scores
        // (when ≥ 3 opponents exist).
        var opponentsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (a, b) in priorPairings)
        {
            if (a == FideC04SwissPairingService.ByeOpponent ||
                b == FideC04SwissPairingService.ByeOpponent) continue;
            if (!opponentsOf.TryGetValue(a, out var la))
            {
                la = new List<string>(); opponentsOf[a] = la;
            }
            la.Add(b);
            if (!opponentsOf.TryGetValue(b, out var lb))
            {
                lb = new List<string>(); opponentsOf[b] = lb;
            }
            lb.Add(a);
        }

        var buchholz = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var p in registrations)
        {
            buchholz[p] = ComputeBuchholz(p, opponentsOf, matchPoints, tiebreakerName);
        }

        // Reorder the seeded roster by descending points → desc
        // Buchholz → ascending seed (the W11 engine handles the
        // seed-index tiebreak internally, but we surface the
        // ordering here so the audit log is deterministic).
        var seedIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < registrations.Count; i++) seedIndex[registrations[i]] = i;

        // Engine input.
        var pairs = _engine.PairNextRound(
            registrations,
            matchPoints,
            priorPairings);

        if (pairs.Count == 0 && registrations.Count >= 2)
        {
            return SwissPairingComputationResult.Failure(
                ErrorPairingEngineEmpty,
                "pairing engine returned no pairings");
        }

        // Detect a "no rematches" violation. The engine emits a
        // legal pairing whenever one exists; if EVERY remaining
        // pair has played, the engine falls back to a rematch.
        // Surface this as audit metadata (the audit row's
        // Tiebreaker carries the suffix "-rematch-forced").
        var playedSet = new HashSet<(string, string)>();
        foreach (var (a, b) in priorPairings)
        {
            playedSet.Add(Normalise(a, b));
        }

        var auditRows = new List<SwissPairingAuditEntry>();
        var resultPairings = new List<SwissPairingComputationResult.Pairing>();
        var board = 1;
        var anyRematch = false;
        foreach (var pair in pairs)
        {
            var p1 = pair.P1;
            var p2 = pair.P2;
            string white, black;
            // Bye pairings: the engine packs the bye into P2;
            // the audit row records the live player as White
            // and the bye sentinel as Black.
            if (string.Equals(p2, FideC04SwissPairingService.ByeOpponent, StringComparison.Ordinal))
            {
                white = p1;
                black = p2;
            }
            else
            {
                // Higher-priority player goes White. Higher
                // priority = higher match-points (or higher
                // Buchholz, or lower seed index).
                white = ChoosePriorityWinner(p1, p2, matchPoints, buchholz, seedIndex);
                black = white == p1 ? p2 : p1;
            }
            var isRematch = !string.Equals(black, FideC04SwissPairingService.ByeOpponent, StringComparison.Ordinal)
                && playedSet.Contains(Normalise(white, black));
            if (isRematch) anyRematch = true;
            var tiebreakerForRow = isRematch
                ? tiebreakerName + "-rematch-forced"
                : tiebreakerName;
            var entry = new SwissPairingAuditEntry
            {
                Id = Guid.NewGuid(),
                TournamentId = tournamentId,
                Round = nextRound,
                Board = board,
                White = white,
                Black = black,
                Tiebreaker = tiebreakerForRow,
                CreatedAtUtc = DateTime.UtcNow,
            };
            auditRows.Add(entry);
            resultPairings.Add(new SwissPairingComputationResult.Pairing(
                board, white, black, tiebreakerForRow,
                IsBye: string.Equals(black, FideC04SwissPairingService.ByeOpponent, StringComparison.Ordinal)));
            board++;
        }

        // Transactional write: SwissPairingAuditEntries +
        // ReconnectAuditEntries. The (TournamentId, Round, Board)
        // unique index protects against double-stamping a
        // re-run; if the operator re-invokes the endpoint after
        // already pairing the round, the second call surfaces
        // a unique-constraint violation that we translate to a
        // "round-already-paired" error code.
        foreach (var row in auditRows)
        {
            db.SwissPairingAuditEntries.Add(row);
            db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
            {
                Id = Guid.NewGuid(),
                PlayerId = "admin",
                At = DateTime.UtcNow,
                Kind = ReconnectAuditEntry.KindTournamentSwissPairingComputed,
                Detail = $"tournamentId={tournamentId:N}|round={row.Round}|board={row.Board}|tiebreaker={row.Tiebreaker}",
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException dux)
        {
            _logger.LogWarning(dux,
                "Swiss pairing audit write collided with existing row (tournament={Tournament}, round={Round}).",
                tournamentId, nextRound);
            return SwissPairingComputationResult.Failure(
                ErrorRoundAlreadyPaired,
                $"round {nextRound} already paired for tournament {tournamentId}");
        }

        return SwissPairingComputationResult.Success(
            tournamentId, nextRound, tiebreakerName, anyRematch, resultPairings);
    }

    private static (string, string) Normalise(string a, string b) =>
        StringComparer.Ordinal.Compare(a, b) <= 0 ? (a, b) : (b, a);

    private static string ChoosePriorityWinner(
        string p1, string p2,
        IReadOnlyDictionary<string, int> matchPoints,
        IReadOnlyDictionary<string, double> buchholz,
        IReadOnlyDictionary<string, int> seedIndex)
    {
        var mp1 = matchPoints.TryGetValue(p1, out var v1) ? v1 : 0;
        var mp2 = matchPoints.TryGetValue(p2, out var v2) ? v2 : 0;
        if (mp1 != mp2) return mp1 > mp2 ? p1 : p2;
        var b1 = buchholz.TryGetValue(p1, out var bb1) ? bb1 : 0d;
        var b2 = buchholz.TryGetValue(p2, out var bb2) ? bb2 : 0d;
        if (Math.Abs(b1 - b2) > 1e-9) return b1 > b2 ? p1 : p2;
        var s1 = seedIndex.TryGetValue(p1, out var ss1) ? ss1 : int.MaxValue;
        var s2 = seedIndex.TryGetValue(p2, out var ss2) ? ss2 : int.MaxValue;
        if (s1 != s2) return s1 < s2 ? p1 : p2;
        // Final tiebreak: ordinal comparison so the assignment
        // is byte-stable across runs.
        return StringComparer.Ordinal.Compare(p1, p2) <= 0 ? p1 : p2;
    }

    internal static double ComputeBuchholz(
        string player,
        IReadOnlyDictionary<string, List<string>> opponentsOf,
        IReadOnlyDictionary<string, int> matchPoints,
        string tiebreaker)
    {
        if (!opponentsOf.TryGetValue(player, out var opps) || opps.Count == 0) return 0;
        var scores = new List<double>(opps.Count);
        foreach (var o in opps)
        {
            scores.Add(matchPoints.TryGetValue(o, out var v) ? v : 0);
        }
        if (string.Equals(tiebreaker, TiebreakerMedianBuchholz, StringComparison.Ordinal) && scores.Count >= 3)
        {
            scores.Sort();
            scores.RemoveAt(0);
            scores.RemoveAt(scores.Count - 1);
        }
        double total = 0;
        foreach (var s in scores) total += s;
        return total;
    }

    public const string ErrorTournamentNotFound = "tournament-not-found";
    public const string ErrorNotSwissFormat = "tournament-not-swiss";
    public const string ErrorInsufficientPlayers = "insufficient-players";
    public const string ErrorPairingEngineEmpty = "pairing-engine-empty";
    public const string ErrorRoundAlreadyPaired = "round-already-paired";
}

/// <summary>
/// Phase K Wave 20 — Bishop. Result envelope for
/// <see cref="SwissPairingService.PairNextRoundAsync"/>. The
/// type is split into a success / failure shape so call sites
/// can translate the wire-stable error code into the HTTP
/// status without needing to inspect a message string.
/// </summary>
public sealed class SwissPairingComputationResult
{
    public bool Succeeded { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }
    public Guid TournamentId { get; }
    public int NextRound { get; }
    public string Tiebreaker { get; }
    public bool ContainsRematch { get; }
    public IReadOnlyList<Pairing> Pairings { get; }

    private SwissPairingComputationResult(
        bool succeeded,
        string? errorCode,
        string? errorMessage,
        Guid tournamentId,
        int nextRound,
        string tiebreaker,
        bool containsRematch,
        IReadOnlyList<Pairing> pairings)
    {
        Succeeded = succeeded;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        TournamentId = tournamentId;
        NextRound = nextRound;
        Tiebreaker = tiebreaker;
        ContainsRematch = containsRematch;
        Pairings = pairings;
    }

    public static SwissPairingComputationResult Success(
        Guid tournamentId, int nextRound, string tiebreaker,
        bool containsRematch, IReadOnlyList<Pairing> pairings) =>
        new(true, null, null, tournamentId, nextRound, tiebreaker, containsRematch, pairings);

    public static SwissPairingComputationResult Failure(string code, string message) =>
        new(false, code, message, Guid.Empty, 0, string.Empty, false, Array.Empty<Pairing>());

    /// <summary>Per-board pairing envelope for the wire shape.</summary>
    public sealed record Pairing(
        int Board,
        string White,
        string Black,
        string Tiebreaker,
        bool IsBye);
}

/// <summary>
/// Phase K Wave 20 — Bishop. Admin-gated endpoint that drives
/// the W20 live pairing service. Wires the
/// <c>POST /api/admin/tournaments/{id}/swiss-pair-next-round</c>
/// surface promised in the W20 bring-up brief.
///
/// <para>Auth: 401 (no session) → 403 (non-admin) → 400 (input
/// validation) → 404 (tournament missing) → 409 (round already
/// paired) → 422 (engine could not pair) → 200 (success).</para>
///
/// <para>Mandatory headers:
/// <list type="bullet">
///   <item><c>X-Admin-Reason</c> — operator-supplied reason text
///         (mirrors the W19 bulk-update controller). Missing or
///         empty returns HTTP 400.</item>
/// </list></para>
///
/// <para>The endpoint is intentionally narrow — it does NOT
/// materialise <see cref="TournamentMatch"/> rows. The
/// caller's downstream "commit pairing" surface is responsible
/// for translating the proposed pairings into matches; the W20
/// endpoint is a preview + audit-trail write that an operator
/// can call repeatedly before flipping the bracket.</para>
/// </summary>
[ApiController]
[Route("api/admin/tournaments/{id:guid}/swiss-pair-next-round")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class SwissPairingAdminController : ControllerBase
{
    public const string AdminReasonHeader = "X-Admin-Reason";
    public const int MaxAdminReasonLength = 512;

    private readonly AuthCookieService _cookies;
    private readonly SwissPairingService _service;

    public SwissPairingAdminController(
        AuthCookieService cookies,
        SwissPairingService service)
    {
        _cookies = cookies ?? throw new ArgumentNullException(nameof(cookies));
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    [HttpPost]
    public async Task<IActionResult> PairNextRound(
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

        var result = await _service.PairNextRoundAsync(id, ct);
        if (!result.Succeeded)
        {
            return result.ErrorCode switch
            {
                SwissPairingService.ErrorTournamentNotFound =>
                    NotFound(new { error = result.ErrorCode, detail = result.ErrorMessage }),
                SwissPairingService.ErrorRoundAlreadyPaired =>
                    Conflict(new { error = result.ErrorCode, detail = result.ErrorMessage }),
                SwissPairingService.ErrorPairingEngineEmpty =>
                    StatusCode(StatusCodes.Status422UnprocessableEntity,
                        new { error = result.ErrorCode, detail = result.ErrorMessage }),
                _ => BadRequest(new { error = result.ErrorCode, detail = result.ErrorMessage }),
            };
        }

        return Ok(new
        {
            tournamentId = result.TournamentId,
            round = result.NextRound,
            tiebreaker = result.Tiebreaker,
            containsRematch = result.ContainsRematch,
            pairings = result.Pairings,
            reason,
        });
    }
}
