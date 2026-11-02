using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Players;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Commentary;

/// <summary>
/// Phase K Wave 6 — Bishop. REST surface for the LLM-driven play-by-
/// play commentary feature. Wave 6 ships the contract + the stub
/// generator; Phase L re-binds <see cref="ICommentaryGenerator"/>
/// to a real implementation without changing the URL shape.
///
/// <list type="bullet">
///   <item><c>POST /api/games/{gameId}/commentary</c> — triggers
///         generation. Admin-only — anonymous callers get 401, non-
///         admin sessions get 403. Audited via
///         <see cref="ReconnectAuditEntry.KindCommentaryReplayRequested"/>.</item>
///   <item><c>GET  /api/games/{gameId}/commentary</c> — returns the
///         previously-generated commentary. Anonymous-allowed (the
///         lobby reads it for spectator playback).</item>
///   <item><c>POST /api/games/{gameId}/commentary/replay</c> +
///         <c>GET /api/games/{gameId}/commentary/replay</c> — aliases
///         matching the user-spec URL shape; identical semantics to
///         the base route.</item>
/// </list>
///
/// <para>The route prefix uses <c>games</c> to match the existing
/// <c>/api/games/{gameId}/audit</c> + <c>/api/games/{gameId}/chat</c>
/// surfaces — operators reach for commentary via the same
/// game-scoped path.</para>
/// </summary>
[ApiController]
[Route("api/games/{gameId:guid}/commentary")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class CommentaryController : ControllerBase
{
    private readonly ICommentaryGenerator _generator;
    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CommentaryController> _logger;
    private readonly ICommentaryStore? _store;
    private readonly Mahjong.Autotable.Api.Tables.IPlayerTableContext? _tableContext;
    private readonly CommentaryCostBudget? _budget;
    private readonly StubCommentaryGenerator? _stubGenerator;
    private readonly CommentaryCostBudgetEnforcer? _enforcer;

    public CommentaryController(
        ICommentaryGenerator generator,
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory,
        ILogger<CommentaryController> logger,
        ICommentaryStore? store = null,
        Mahjong.Autotable.Api.Tables.IPlayerTableContext? tableContext = null,
        CommentaryCostBudget? budget = null,
        StubCommentaryGenerator? stubGenerator = null,
        CommentaryCostBudgetEnforcer? enforcer = null)
    {
        _generator = generator;
        _cookies = cookies;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _store = store;
        _tableContext = tableContext;
        _budget = budget;
        _stubGenerator = stubGenerator;
        _enforcer = enforcer;
    }

    [HttpPost]
    [HttpPost("replay")]
    public async Task<IActionResult> Trigger([FromRoute] Guid gameId, CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null)
            return Unauthorized(new { error = "Authentication required to trigger commentary." });
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Admin role required to trigger commentary generation.",
            });

        // Phase K Wave 16 — Bishop. Hard-cap enforcement. When
        // the monthly cost budget is in the Exhausted state the
        // enforcer short-circuits with HTTP 402 Payment
        // Required. Admins may bypass via the
        // X-Cost-Budget-Override: 1 header AND the
        // Commentary:CostBudget:AdminOverride toggle (default
        // true). Healthy + Warning states still pass through.
        //
        // Phase K Wave 17 — Bishop. The override path now ALSO
        // accepts the canonical <c>X-Admin-Reason</c> header
        // (unified with the W17 replay-retention + signalr-
        // retention admin surfaces). When BOTH headers are
        // present the request is accepted; when ONLY
        // X-Admin-Reason is present the request is accepted and
        // the reason is captured on the
        // <see cref="ReconnectAuditEntry.KindCommentaryAdminOverride"/>
        // audit row. When only X-Cost-Budget-Override is present
        // the W16 path is preserved (audit row uses
        // "legacy-x-cost-budget-override" as the captured reason
        // so dashboards can spot the legacy bucket and migrate
        // operators off it). An explicit X-Admin-Reason header
        // with EMPTY / whitespace value short-circuits with 400
        // so operators don't accidentally silently override.
        if (_enforcer is not null)
        {
            var (overrideEngaged, adminReason, badEmptyReason) = ResolveAdminOverride();
            if (badEmptyReason)
            {
                return BadRequest(new
                {
                    error = "admin-reason-empty",
                    detail = $"The {CommentaryAdminReasonHeader} header was supplied but is empty / whitespace.",
                });
            }
            var verdict = _enforcer.Evaluate(
                tenantId: null,
                isAdmin: overrideEngaged,
                utcNow: DateTime.UtcNow);
            if (verdict.IsAdminOverride)
            {
                // Stamp an audit row capturing WHY the operator
                // engaged the override. This is the single
                // signal the W17 audit dashboard renders for the
                // "admin bypassed budget" bucket.
                await WriteAdminOverrideAuditAsync(
                    session.PlayerId,
                    gameId,
                    adminReason ?? "legacy-x-cost-budget-override",
                    ct);
            }
            if (verdict.ShouldShortCircuit)
            {
                return StatusCode(
                    CommentaryCostBudgetEnforcer.StatusOverBudget,
                    verdict.ToWireEnvelope());
            }
        }

        try
        {
            var generator = SelectGenerator();
            var replay = await generator.GenerateAsync(gameId, ct);
            await WriteAuditAsync(session.PlayerId, gameId, replay.Generator, ct);
            return Ok(BuildEnvelope(replay));
        }
        catch (UsageCapExceededException ex)
        {
            // Phase K Wave 9 — Bishop. Hard cap surfaces as HTTP 429
            // when Commentary:ThrowOnMonthlyCap is true. The envelope
            // carries the canonical "monthly-token-cap" reason so
            // clients can branch on the error name without parsing
            // the human-readable message.
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                error = "monthly-token-cap",
                detail = ex.Message,
                gameId,
            });
        }
    }

    private bool HasOverrideHeader()
    {
        if (HttpContext is null || HttpContext.Request is null) return false;
        if (!HttpContext.Request.Headers.TryGetValue("X-Cost-Budget-Override", out var values))
        {
            return false;
        }
        foreach (var v in values)
        {
            if (string.Equals(v, "1", StringComparison.Ordinal)
                || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Phase K Wave 17 — Bishop. Wire-stable header name for the
    /// unified admin-override surface. Mirrors the W17
    /// <see cref="Mahjong.Autotable.Api.Replays.ReplayRetentionAdminController.AdminReasonHeader"/>
    /// + the W17 SignalR-retention admin controller so a single
    /// audit dashboard renders all three surfaces' overrides
    /// under one header convention.
    /// </summary>
    public const string CommentaryAdminReasonHeader = "X-Admin-Reason";

    /// <summary>
    /// Phase K Wave 17 — Bishop. Resolves the unified admin-
    /// override headers. Returns a triple:
    /// <list type="bullet">
    ///   <item><c>overrideEngaged</c> — true when the request
    ///         should be treated as admin-override (either
    ///         X-Admin-Reason populated with a non-empty value
    ///         OR the legacy X-Cost-Budget-Override: 1 path).</item>
    ///   <item><c>reason</c> — the X-Admin-Reason value (trimmed)
    ///         or null when only the legacy header was supplied.</item>
    ///   <item><c>badEmptyReason</c> — true when X-Admin-Reason
    ///         was supplied but is empty / whitespace-only. The
    ///         caller short-circuits with HTTP 400 in this case
    ///         so operators don't silently engage the override.</item>
    /// </list>
    /// </summary>
    private (bool overrideEngaged, string? reason, bool badEmptyReason) ResolveAdminOverride()
    {
        if (HttpContext is null || HttpContext.Request is null)
            return (false, null, false);

        string? adminReason = null;
        if (HttpContext.Request.Headers.TryGetValue(CommentaryAdminReasonHeader, out var reasonValues))
        {
            foreach (var v in reasonValues)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    adminReason = v.ToString().Trim();
                    break;
                }
            }
            if (adminReason is null)
            {
                // Header explicitly supplied but empty — fail
                // closed so operators don't accidentally engage
                // the override by sending an empty value.
                return (false, null, true);
            }
        }

        var legacy = HasOverrideHeader();
        var engaged = adminReason is not null || legacy;
        return (engaged, adminReason, false);
    }

    /// <summary>
    /// Phase K Wave 17 — Bishop. Writes the
    /// <see cref="ReconnectAuditEntry.KindCommentaryAdminOverride"/>
    /// audit row when the override engages so the audit
    /// dashboard renders WHO bypassed the budget cap, WHEN, and
    /// WHY (the operator-supplied reason verbatim). Failures are
    /// swallowed — the row is a debugging convenience, not a
    /// hard prerequisite for the override.
    /// </summary>
    private async Task WriteAdminOverrideAuditAsync(
        string playerId, Guid gameId, string reason, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
            {
                Id = Guid.NewGuid(),
                PlayerId = playerId,
                At = DateTime.UtcNow,
                Kind = ReconnectAuditEntry.KindCommentaryAdminOverride,
                Detail = $"{gameId:N}|{reason}",
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Commentary admin-override audit write failed for player={PlayerId}, gameId={GameId}.",
                playerId, gameId);
        }
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromRoute] Guid gameId,
        [FromQuery(Name = "after")] string? after = null,
        [FromQuery(Name = "limit")] int? limit = null,
        CancellationToken ct = default)
    {
        // Phase K Wave 11 — Bishop. When `after` is supplied we
        // switch to the paginated-records branch backed by the
        // ICommentaryStore. The W6 envelope path is the default
        // (no `after`) — preserves the W6 contract test pin.
        if (!string.IsNullOrWhiteSpace(after) || (limit.HasValue && limit.Value > 0))
        {
            return await PaginateAsync(gameId, after, limit, ct);
        }
        var replay = await _generator.GetAsync(gameId, ct);
        return Ok(BuildEnvelope(replay));
    }

    /// <summary>
    /// Phase K Wave 11 — Bishop. Paginated record reader backed by
    /// <see cref="ICommentaryStore"/>. Capped at 100 records per
    /// page. 401 when no session AND the table context returns
    /// no spectator association; 403 when the player has no
    /// relationship with the game.
    /// </summary>
    private async Task<IActionResult> PaginateAsync(
        Guid gameId,
        string? after,
        int? limit,
        CancellationToken ct)
    {
        // Gate: must be seated, owner, spectating, or admin. The
        // gate degrades gracefully when the table context isn't
        // wired (e.g. in the W6/W7 test harnesses that don't
        // construct the W8 surface) — the unauthenticated path
        // still resolves via the cookie service.
        if (_tableContext is not null)
        {
            var session = await _cookies.ResolveAsync(HttpContext, ct);
            var anonId = HttpContext.GetPlayerIdOrNull();
            var assoc = await _tableContext.ResolveAsync(gameId, session, anonId, ct);
            if (assoc.Role == Mahjong.Autotable.Api.Tables.PlayerTableRole.Anonymous)
            {
                return Unauthorized(new { error = "Authentication required to read commentary records." });
            }
            if (assoc.Role == Mahjong.Autotable.Api.Tables.PlayerTableRole.Unknown)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    error = "Player is not associated with this game.",
                    reason = assoc.Reason,
                });
            }
        }

        if (_store is null)
        {
            // No store wired — fall back to the generator's
            // in-memory list so the surface still produces a usable
            // response (mirrors the W7 behaviour).
            var records = await _generator.GetRecordsAsync(gameId, ct);
            return Ok(ProjectRecords(records, after, limit));
        }

        DateTimeOffset? afterTs = null;
        if (!string.IsNullOrWhiteSpace(after))
        {
            if (!DateTimeOffset.TryParse(after, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            {
                return BadRequest(new { error = "after must be an ISO 8601 timestamp." });
            }
            afterTs = parsed;
        }

        var page = Math.Clamp(limit ?? PageSizeDefault, 1, PageSizeMaximum);
        var stored = await _store.ReadAsync(gameId, afterTs, page, ct);
        return Ok(ProjectRecords(stored, after, page));
    }

    /// <summary>Maximum page size for the paginated reader.</summary>
    public const int PageSizeMaximum = 100;

    /// <summary>Default page size when the caller omits the
    /// <c>limit</c> query parameter.</summary>
    public const int PageSizeDefault = 50;

    private static object ProjectRecords(IReadOnlyList<CommentaryRecord> records, string? after, int? limit)
    {
        var projected = records.Select(r => new
        {
            gameId = r.GameId,
            turnNumber = r.TurnNumber,
            phase = r.Phase,
            speaker = r.Speaker,
            text = r.Text,
            emotionIntensity = r.EmotionIntensity,
            tileReferences = (r.TileReferences ?? (IReadOnlyList<TileReference>)Array.Empty<TileReference>())
                .Select(tref => new { tileId = tref.TileId, suit = tref.Suit, rank = tref.Rank })
                .ToArray(),
            tileReferencesBinary = r.TileReferencesBinary
                .Select(Convert.ToBase64String)
                .ToArray(),
            generatedAt = r.GeneratedAt,
        }).ToArray();
        return new
        {
            items = projected,
            count = projected.Length,
            after,
            limit = limit ?? PageSizeDefault,
        };
    }

    /// <summary>
    /// Phase K Wave 7 — Bishop. Records-flavoured replay endpoint.
    /// Returns the per-turn <see cref="CommentaryRecord"/> array per
    /// the finalised Phase-L JSON contract (one record per speaker
    /// utterance, with phase/speaker/intensity/tileReferences fields).
    /// Anonymous-allowed so the lobby spectator UI can pull commentary
    /// without an authenticated session.
    /// </summary>
    [HttpGet("replay")]
    public async Task<IActionResult> Replay([FromRoute] Guid gameId, CancellationToken ct)
    {
        var records = await _generator.GetRecordsAsync(gameId, ct);
        // Project to a wire-explicit anonymous object so the field
        // casing (camelCase wire / PascalCase record) is fixed at the
        // controller boundary instead of leaking the System.Text.Json
        // default for record properties.
        var wire = records.Select(r => new
        {
            gameId = r.GameId,
            turnNumber = r.TurnNumber,
            phase = r.Phase,
            speaker = r.Speaker,
            text = r.Text,
            emotionIntensity = r.EmotionIntensity,
            tileReferences = (r.TileReferences ?? (IReadOnlyList<TileReference>)Array.Empty<TileReference>())
                .Select(tref => new
                {
                    tileId = tref.TileId,
                    suit = tref.Suit,
                    rank = tref.Rank,
                })
                .ToArray(),
            // Phase K Wave 11 — Bishop. Base64-encoded binary
            // tile-reference array. Each entry is the 3-byte
            // payload produced by TileReference.ToBinary(); a
            // base64 string survives JSON without an extra wrapper.
            // Bandwidth-sensitive consumers read this field
            // instead of the verbose `tileReferences` array.
            tileReferencesBinary = r.TileReferencesBinary
                .Select(Convert.ToBase64String)
                .ToArray(),
            generatedAt = r.GeneratedAt,
        }).ToArray();
        return Ok(wire);
    }

    private static object BuildEnvelope(CommentaryReplay replay) => new
    {
        gameId = replay.GameId,
        generator = replay.Generator,
        status = replay.Status,
        items = replay.Items.Select(i => new
        {
            sequence = i.Sequence,
            text = i.Text,
            roundOrdinal = i.RoundOrdinal,
            tone = i.Tone,
        }).ToArray(),
    };

    private async Task WriteAuditAsync(string playerId, Guid gameId, string generatorId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
            {
                Id = Guid.NewGuid(),
                PlayerId = playerId,
                At = DateTime.UtcNow,
                Kind = ReconnectAuditEntry.KindCommentaryReplayRequested,
                Detail = $"{gameId:N}:{generatorId}",
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Commentary audit write failed for gameId={GameId}", gameId);
        }
    }

    /// <summary>
    /// Phase K Wave 12 — Bishop. Cost-budget aware generator
    /// selection. When the budget evaluates to
    /// <see cref="BudgetState.Exhausted"/> AND a stub generator is
    /// registered, route to the deterministic stub for the rest of
    /// the month. Logged once per month inside
    /// <see cref="CommentaryCostBudget.Evaluate"/>.
    /// </summary>
    private ICommentaryGenerator SelectGenerator()
    {
        if (_budget is null || _stubGenerator is null) return _generator;
        var evaluation = _budget.Evaluate(DateTime.UtcNow);
        if (evaluation.State == BudgetState.Exhausted
            && !ReferenceEquals(_generator, _stubGenerator))
        {
            return _stubGenerator;
        }
        return _generator;
    }
}
