using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Players;

/// <summary>
/// Phase K Wave 3 — Bishop (Backend). Server-authoritative onboarding
/// tour progress, surfaced via:
/// <list type="bullet">
///   <item><c>GET  /api/players/me/onboarding-status</c> — returns the
///         persisted envelope, defaulting to
///         <c>{ completed: false, stepsCompleted: 0 }</c> when no row
///         exists yet.</item>
///   <item><c>POST /api/players/me/onboarding-status</c> — upserts the
///         row. Body: <c>{ "completed": bool?, "stepsCompleted": int? }</c>.
///         <see cref="PlayerOnboardingStatus.StepsCompleted"/> climbs
///         monotonically; lower values are ignored. Once
///         <see cref="PlayerOnboardingStatus.Completed"/> is true, it
///         stays true.</item>
/// </list>
///
/// <para>Identity: anonymous-only — the <c>mahjong_pid</c> cookie is
/// the canonical key. Logged-in callers' sessions are read for the
/// same player id so anon → signed-in progression doesn't reset.</para>
/// </summary>
[ApiController]
[Route("api/players/me/onboarding-status")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class PlayerOnboardingController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PlayerIdentityService _identity;
    private readonly AuthCookieService _cookies;

    public PlayerOnboardingController(
        AppDbContext db,
        PlayerIdentityService identity,
        AuthCookieService cookies)
    {
        _db = db;
        _identity = identity;
        _cookies = cookies;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var playerId = await ResolvePlayerIdAsync(ct);
        if (playerId is null)
        {
            // No identity at all — return the default envelope without
            // minting a cookie (GET shouldn't have side effects).
            return Ok(new
            {
                completed = false,
                stepsCompleted = 0,
                lastStepCompletedUtc = (DateTime?)null,
            });
        }

        var row = await _db.PlayerOnboardingStatuses
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.PlayerId == playerId, ct);
        return Ok(new
        {
            completed = row?.Completed ?? false,
            stepsCompleted = row?.StepsCompleted ?? 0,
            lastStepCompletedUtc = row?.LastStepCompletedUtc,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] OnboardingUpdateBody? body, CancellationToken ct)
    {
        if (body is null)
        {
            return BadRequest(new { error = "Body must include at least one of `completed` or `stepsCompleted`." });
        }

        // Phase K Wave 4 — Bishop. Apone flagged a Wave-3 failure where
        // POST stepsCompleted=999 was persisted verbatim. The canonical
        // tour has 8 steps (CompletedStepCount); the controller now
        // clamps any inbound stepsCompleted to [0, MaxStepsCompleted]
        // BEFORE any persistence logic. Negative values clamp to 0;
        // values above the ceiling clamp to MaxStepsCompleted (8).
        // A no-op POST (4 → 4) stays a no-op; the clamp never
        // introduces a 400.
        int? clampedSteps = body.StepsCompleted is int requestedSteps
            ? Math.Clamp(requestedSteps, MinStepsCompleted, MaxStepsCompleted)
            : null;

        // POST mints a cookie if none exists so the client can persist
        // progress across the first session.
        var playerId = _identity.ResolveOrMint(HttpContext);
        var now = DateTime.UtcNow;
        var row = await _db.PlayerOnboardingStatuses
            .FirstOrDefaultAsync(s => s.PlayerId == playerId, ct);
        if (row is null)
        {
            row = new PlayerOnboardingStatus
            {
                PlayerId = playerId,
                Completed = body.Completed ?? false,
                StepsCompleted = clampedSteps ?? 0,
                LastStepCompletedUtc = (body.Completed == true || (clampedSteps ?? 0) > 0) ? now : null,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.PlayerOnboardingStatuses.Add(row);
        }
        else
        {
            // Monotonic step count — a lower value never regresses
            // the persisted progress. Clamp before the comparison so
            // overflow inputs cap at MaxStepsCompleted.
            if (clampedSteps is int steps && steps > row.StepsCompleted)
            {
                row.StepsCompleted = steps;
                row.LastStepCompletedUtc = now;
            }
            // Completed flips one-way to true.
            if (body.Completed == true && !row.Completed)
            {
                row.Completed = true;
                row.LastStepCompletedUtc = now;
            }
            row.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            completed = row.Completed,
            stepsCompleted = row.StepsCompleted,
            lastStepCompletedUtc = row.LastStepCompletedUtc,
        });
    }

    /// <summary>Phase K Wave 4 — Bishop. Lower bound for the
    /// onboarding clamp; matches the storage column's non-negative
    /// invariant.</summary>
    public const int MinStepsCompleted = 0;

    /// <summary>Phase K Wave 4 — Bishop. Upper bound for the
    /// onboarding clamp; matches the canonical 8-step tour shipped by
    /// the frontend. Inbound POSTs above this value clamp to the
    /// ceiling instead of rejecting (Apone's Wave-3 failure note).</summary>
    public const int MaxStepsCompleted = 8;

    private async Task<string?> ResolvePlayerIdAsync(CancellationToken ct)
    {
        // Prefer the persistent anon cookie — that's the canonical
        // onboarding scope. Fall back to the auth session player id
        // if the cookie is missing but a logged-in session exists.
        var cookieId = _identity.ResolveFromCookie(HttpContext);
        if (!string.IsNullOrEmpty(cookieId)) return cookieId;
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        return session?.PlayerId;
    }

    /// <summary>
    /// Phase K Wave 3 — body shape for the onboarding-status POST.
    /// Both fields are optional; clients typically POST
    /// <c>{ stepsCompleted: N }</c> after each tour step and a final
    /// <c>{ completed: true, stepsCompleted: total }</c> when done.
    /// </summary>
    public sealed class OnboardingUpdateBody
    {
        public bool? Completed { get; set; }
        public int? StepsCompleted { get; set; }
    }
}
