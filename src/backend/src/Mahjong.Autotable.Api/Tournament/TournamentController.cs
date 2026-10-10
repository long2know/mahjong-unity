using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase J Wave 10 — REST surface for tournament mode:
/// <list type="bullet">
///   <item><c>GET    /api/tournaments[?status=]</c> — list (anonymous).</item>
///   <item><c>GET    /api/tournaments/{id}</c>      — details + matches + standings (anonymous).</item>
///   <item><c>POST   /api/tournaments</c>           — create (auth required → 401).</item>
///   <item><c>POST   /api/tournaments/{id}/register</c>   — register (auth required).</item>
///   <item><c>DELETE /api/tournaments/{id}/register</c>   — unregister (auth required).</item>
///   <item><c>POST   /api/tournaments/{id}/start</c>      — start (creator only → 403).</item>
///   <item><c>GET    /api/tournaments/{id}/leaderboard</c>— standings (anonymous).</item>
/// </list>
///
/// <para>Per Wave-10 spec the service is the canonical owner of state
/// transitions; the controller is a thin adapter. Auth is resolved via
/// <see cref="AuthCookieService"/> — anonymous reads remain accessible
/// (the lobby is public) but every mutation requires a session.</para>
/// </summary>
[ApiController]
[Route("api/tournaments")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class TournamentController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AuthCookieService _cookies;
    private readonly IBracketStore? _bracketStore;
    private readonly BracketQueryOptions? _bracketQueryOptions;
    private readonly Mahjong.Autotable.Api.Observability.TournamentQueryLatencyMetrics? _latencyMetrics;

    public TournamentController(
        IServiceScopeFactory scopeFactory,
        AuthCookieService cookies,
        IBracketStore? bracketStore = null,
        BracketQueryOptions? bracketQueryOptions = null,
        Mahjong.Autotable.Api.Observability.TournamentQueryLatencyMetrics? latencyMetrics = null)
    {
        _scopeFactory = scopeFactory;
        _cookies = cookies;
        _bracketStore = bracketStore;
        _bracketQueryOptions = bracketQueryOptions;
        _latencyMetrics = latencyMetrics;
    }

    /// <summary>
    /// Phase K Wave 14 — Bishop. Paginated query over the durable
    /// bracket store landed in W12 + wired through
    /// <see cref="TournamentService"/> in W13. Returns every
    /// <see cref="BracketRecord"/> for the supplied tournament in
    /// <c>(RoundNumber, MatchSlot)</c> order. The page is pinned by
    /// the <c>skip</c> + <c>limit</c> query parameters; the
    /// server-side page-size default is configurable via
    /// <c>Tournament:BracketPageSize</c>.
    ///
    /// <para>Anonymous-allowed — bracket listings are public, the
    /// same posture as <see cref="Bracket"/>. See
    /// <c>docs/bracket-shape.md §5 "Bracket query API"</c>.</para>
    /// </summary>
    [HttpGet("{id:guid}/brackets")]
    public async Task<IActionResult> BracketRecords(
        [FromRoute] Guid id,
        [FromQuery(Name = "skip")] int? skip,
        [FromQuery(Name = "limit")] int? limit,
        CancellationToken ct = default)
    {
        if (_bracketStore is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "bracket-store-unavailable",
            });
        }

        var configuredPageSize = _bracketQueryOptions?.PageSize ?? BracketQueryOptions.DefaultPageSize;
        if (configuredPageSize <= 0) configuredPageSize = BracketQueryOptions.DefaultPageSize;
        if (configuredPageSize > BracketQueryOptions.MaxPageSize)
            configuredPageSize = BracketQueryOptions.MaxPageSize;
        var take = Math.Clamp(limit ?? configuredPageSize, 1, BracketQueryOptions.MaxPageSize);
        var skipN = Math.Max(0, skip ?? 0);

        // Phase K Wave 15 — Bishop. Time the query so the
        // tournament-scale latency histogram can surface a p99 by
        // page-size bucket. See docs/bracket-shape.md §6.
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var rows = await _bracketStore.ListAsync(id, ct);
        var pageRows = rows.Skip(skipN).Take(take).ToArray();
        _latencyMetrics?.ObserveTimestamp("bracket-records", take, t0);
        return Ok(new
        {
            tournamentId = id,
            totalCount = rows.Count,
            count = pageRows.Length,
            skip = skipN,
            limit = take,
            pageSize = configuredPageSize,
            items = pageRows.Select(r => new
            {
                id = r.Id,
                tournamentId = r.TournamentId,
                roundNumber = r.RoundNumber,
                matchSlot = r.MatchSlot,
                seedA = r.SeedA,
                seedB = r.SeedB,
                winnerSeed = r.WinnerSeed,
                status = r.Status,
                completedAt = r.CompletedAt,
            }).ToArray(),
        });
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TournamentService>();
        var tournaments = await svc.ListAsync(status, ct);
        return Ok(new { tournaments = tournaments.Select(ToDto).ToArray() });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TournamentService>();
        var t = await svc.GetAsync(id, ct);
        if (t is null) return NotFound(new { error = "Tournament not found.", id });
        var regs = await svc.ListRegistrationsAsync(id, ct);
        var matches = await svc.ListMatchesAsync(id, ct);
        return Ok(new
        {
            tournament = ToDto(t),
            registrations = regs.Select(r => new { r.PlayerId, r.Seed, r.RegisteredAt }).ToArray(),
            matches = matches.Select(MatchToDto).ToArray(),
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBody? body, CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "Authentication required to create tournaments." });
        if (body is null || string.IsNullOrWhiteSpace(body.Name))
            return BadRequest(new { error = "name is required." });
        if (string.IsNullOrWhiteSpace(body.Format))
            return BadRequest(new { error = "format is required." });

        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TournamentService>();
        try
        {
            var t = await svc.CreateAsync(
                body.Name,
                body.Format,
                session.PlayerId,
                body.MaxPlayers ?? 16,
                body.GamesPerMatch ?? 1,
                ct);
            return CreatedAtAction(nameof(Get), new { id = t.Id }, ToDto(t));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id:guid}/register")]
    public async Task<IActionResult> Register([FromRoute] Guid id, CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "Authentication required to register." });

        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TournamentService>();
        try
        {
            var reg = await svc.RegisterAsync(id, session.PlayerId, ct);
            return Ok(new { registration = new { reg.Id, reg.PlayerId, reg.Seed, reg.RegisteredAt } });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpDelete("{id:guid}/register")]
    public async Task<IActionResult> Unregister([FromRoute] Guid id, CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "Authentication required to unregister." });

        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TournamentService>();
        try
        {
            var removed = await svc.UnregisterAsync(id, session.PlayerId, ct);
            return removed ? NoContent() : NotFound(new { error = "Registration not found.", id });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("{id:guid}/start")]
    public async Task<IActionResult> Start([FromRoute] Guid id, CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "Authentication required to start a tournament." });

        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TournamentService>();
        try
        {
            var matches = await svc.StartAsync(id, session.PlayerId, ct);
            return Ok(new { matches = matches.Select(MatchToDto).ToArray() });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpGet("{id:guid}/leaderboard")]
    public async Task<IActionResult> Leaderboard([FromRoute] Guid id, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TournamentService>();
        var rows = await svc.LeaderboardAsync(id, ct);
        return Ok(new { leaderboard = rows.Select(r => new { r.PlayerId, r.Wins, r.Buchholz }).ToArray() });
    }

    /// <summary>
    /// Phase K Wave 8 — Bishop. UI-facing bracket snapshot endpoint
    /// powering Hicks's W8 renderer. Returns the canonical
    /// <see cref="BracketSnapshot"/> envelope with winnersBracket /
    /// losersBracket / grandFinal sections; per-slot fields carry
    /// the seeds + winner + status the bracket-tree component
    /// consumes.
    ///
    /// <para>The optional <c>?format=</c> query parameter is purely
    /// informational — the response always reports the tournament's
    /// persisted format. Passing <c>?format=double-elimination</c>
    /// matches the W8 spec verbatim (frontend round-trip discovery)
    /// but mismatches do NOT throw; the snapshot honours the
    /// persisted format and reflects it back in the response body.</para>
    /// </summary>
    [HttpGet("{id:guid}/bracket")]
    public async Task<IActionResult> Bracket(
        [FromRoute] Guid id,
        [FromQuery] string? format,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var bracketService = scope.ServiceProvider
            .GetRequiredService<TournamentBracketSnapshotService>();
        var snapshot = await bracketService.BuildAsync(id, ct);
        if (snapshot is null)
        {
            return NotFound(new { error = "Tournament not found.", id });
        }
        return Ok(new
        {
            format = snapshot.Format,
            requestedFormat = format,
            tournamentId = snapshot.TournamentId,
            winnersBracket = snapshot.WinnersBracket.Select(RoundToDto).ToArray(),
            losersBracket = snapshot.LosersBracket.Select(RoundToDto).ToArray(),
            grandFinal = snapshot.GrandFinal is null ? null : new
            {
                match = SlotToDto(snapshot.GrandFinal.Match),
                resetMatch = SlotToDto(snapshot.GrandFinal.ResetMatch),
            },
        });
    }

    private static object RoundToDto(BracketRound round) => new
    {
        roundNumber = round.RoundNumber,
        slots = round.Slots.Select(SlotToDto).ToArray(),
    };

    private static object? SlotToDto(BracketSlot? slot)
    {
        if (slot is null) return null;
        return new
        {
            matchIndex = slot.MatchIndex,
            seedA = slot.SeedA,
            seedB = slot.SeedB,
            winnerSeed = slot.WinnerSeed,
            status = slot.Status,
            bracketSide = slot.BracketSide,
        };
    }

    /// <summary>
    /// Phase K Wave 3 — Bishop. Admin-only seeding endpoint. Body:
    /// <c>{ "seeds": [{ "playerId": "…", "seedNumber": 1 }, … ] }</c>.
    /// Updates <see cref="Data.Entities.TournamentRegistration.Seed"/>
    /// for each entry whose <c>playerId</c> matches a registered
    /// player. Unknown players are silently skipped so partial-bracket
    /// re-seeding works.
    ///
    /// <para>401 when the caller has no session; 403 when the
    /// session's role is not <c>admin</c>; 409 when the tournament has
    /// already started; 200 on success with
    /// <c>{ "updated": &lt;int&gt; }</c>.</para>
    /// </summary>
    [HttpPost("{id:guid}/seed")]
    public async Task<IActionResult> Seed(
        [FromRoute] Guid id,
        [FromBody] SeedBody? body,
        CancellationToken ct)
    {
        // Phase K Wave 4 — Bishop. HTTP precedence is:
        //   401 (no session)
        //   → 403 (non-admin)
        //   → 404 (tournament missing)
        //   → 400 (body validation)
        // Wave-3 returned 400 ahead of 404 because the controller
        // validated the body before reading the tournament row. The
        // reorder lets clients distinguish "wrong route" (404) from
        // "right route, malformed payload" (400) — Vasquez's
        // contract test pins the order.
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null)
            return Unauthorized(new { error = "Authentication required to seed." });
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Admin role required." });

        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TournamentService>();
        var tournament = await svc.GetAsync(id, ct);
        if (tournament is null)
            return NotFound(new { error = "Tournament not found.", id });

        var entries = body?.Seeds;
        if (entries is null || entries.Length == 0)
            return BadRequest(new { error = "Body must include a non-empty `seeds` array." });

        try
        {
            var assignments = entries
                .Where(e => !string.IsNullOrWhiteSpace(e?.PlayerId) && e!.SeedNumber > 0)
                .Select(e => new TournamentService.TournamentSeedAssignment(e!.PlayerId!, e.SeedNumber))
                .ToList();
            var updated = await svc.SeedAsync(id, assignments, ct);
            return Ok(new { tournamentId = id, updated });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Phase K Wave 2 — manual-surrender forfeit endpoint. <c>POST
    /// /api/tournaments/{tid}/matches/{mid}/forfeit</c> with body
    /// <c>{ playerId, reason? }</c>. Auth required; the resolved
    /// session player MUST be either the forfeiting player themselves
    /// or the tournament creator. Returns 404 if the match doesn't
    /// exist or isn't in-progress (idempotent — re-forfeit returns
    /// 404 rather than 500 because the first call already settled it).
    ///
    /// <para>An <see cref="Data.Entities.ReconnectAuditEntry"/> row is
    /// written with <c>Kind = "tournament.forfeit"</c> (Vasquez's
    /// contract pin); the disconnect-driven background sweeper uses the
    /// game-id path so the two surfaces don't collide.</para>
    /// </summary>
    [HttpPost("{tid:guid}/matches/{mid:guid}/forfeit")]
    public async Task<IActionResult> ForfeitMatch(
        [FromRoute] Guid tid,
        [FromRoute] Guid mid,
        [FromBody] ForfeitBody? body,
        CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "Authentication required to forfeit." });
        var forfeitPlayerId = body?.PlayerId?.Trim();
        if (string.IsNullOrWhiteSpace(forfeitPlayerId))
            forfeitPlayerId = session.PlayerId;

        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TournamentService>();
        try
        {
            var match = await svc.ForfeitMatchByIdAsync(tid, mid, forfeitPlayerId!, ct);
            if (match is null)
                return NotFound(new { error = "Match not found or not in progress.", tournamentId = tid, matchId = mid });
            return Ok(new
            {
                match = MatchToDto(match),
                kind = Data.Entities.ReconnectAuditEntry.KindTournamentForfeit,
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private static object ToDto(Data.Entities.Tournament t) => new
    {
        t.Id,
        t.Name,
        t.Format,
        t.Status,
        t.CreatedByPlayerId,
        t.MaxPlayers,
        t.GamesPerMatch,
        t.CreatedAt,
        t.StartedAt,
        t.CompletedAt,
    };

    private static object MatchToDto(TournamentMatch m) => new
    {
        m.Id,
        m.TournamentId,
        m.Round,
        m.Player1Id,
        m.Player2Id,
        m.Player3Id,
        m.Player4Id,
        m.WinnerPlayerId,
        m.Status,
        m.CreatedAt,
        m.CompletedAt,
    };

    public sealed class CreateBody
    {
        public string? Name { get; set; }
        public string? Format { get; set; }
        public int? MaxPlayers { get; set; }
        public int? GamesPerMatch { get; set; }
    }

    /// <summary>Phase K Wave 2 — body shape for the
    /// <c>POST /api/tournaments/{tid}/matches/{mid}/forfeit</c>
    /// endpoint. <see cref="PlayerId"/> identifies the player
    /// forfeiting; when omitted, defaults to the resolved session
    /// player (the typical "I'm surrendering" case).</summary>
    public sealed class ForfeitBody
    {
        public string? PlayerId { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>
    /// Phase K Wave 19 — Bishop. Admin-only read surface for the
    /// Swiss pairing audit trail introduced in W19. Returns every
    /// <see cref="SwissPairingAuditEntry"/> row for the supplied
    /// tournament, ordered by <c>(Round, Board)</c> so an
    /// operator can step through the round-by-round pairing
    /// decisions taken by <c>FideC04SwissPairingService</c>.
    ///
    /// <para>Auth: 401 (no session) → 403 (non-admin) → 200
    /// (success). The endpoint always succeeds when the
    /// tournament has no rows — callers receive an empty
    /// <c>entries</c> array so they can distinguish "no audit"
    /// from "endpoint disabled".</para>
    ///
    /// <para>Every successful call writes a single
    /// <see cref="ReconnectAuditEntry.KindTournamentSwissPairingAuditRead"/>
    /// row with <c>Detail = "tournament=&lt;id&gt;|rows=&lt;n&gt;"</c>
    /// so the audit trail itself is auditable.</para>
    ///
    /// <para>See <c>docs/swiss-pairing.md §7 "Audit trail"</c>
    /// (added W19) for the operator runbook.</para>
    /// </summary>
    [HttpGet("{id:guid}/swiss-pairing-audit")]
    public async Task<IActionResult> GetSwissPairingAudit(
        [FromRoute] Guid id,
        CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null) return Unauthorized(new { error = "session-required" });
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "admin-required" });
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entries = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.SwissPairingAuditEntries
                .Where(e => e.TournamentId == id)
                .OrderBy(e => e.Round).ThenBy(e => e.Board)
                .Select(e => new
                {
                    e.Id,
                    e.TournamentId,
                    e.Round,
                    e.Board,
                    white = e.White,
                    black = e.Black,
                    isBye = e.Black == FideC04SwissPairingService.ByeOpponent,
                    tiebreaker = e.Tiebreaker,
                    e.CreatedAtUtc,
                }), ct);

        try
        {
            db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
            {
                Id = Guid.NewGuid(),
                PlayerId = session.PlayerId ?? "admin",
                At = DateTime.UtcNow,
                Kind = ReconnectAuditEntry.KindTournamentSwissPairingAuditRead,
                Detail = $"tournament={id:N}|rows={entries.Count}",
            });
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Best-effort — failure to write the audit row must
            // not poison the read.
        }

        return Ok(new
        {
            tournamentId = id,
            entries,
            rowCount = entries.Count,
        });
    }

    /// <summary>
    /// Phase K Wave 3 — Bishop. Body shape for the
    /// <c>POST /api/tournaments/{id}/seed</c> admin endpoint. The
    /// outer envelope keeps <c>seeds</c> as an array so a single push
    /// can rebrand the entire bracket atomically. <see cref="SeedEntry"/>
    /// carries the per-player assignment.
    /// </summary>
    public sealed class SeedBody
    {
        public SeedEntry[]? Seeds { get; set; }
    }

    /// <summary>Phase K Wave 3 — single seed assignment.</summary>
    public sealed class SeedEntry
    {
        public string? PlayerId { get; set; }
        public int SeedNumber { get; set; }
    }
}
