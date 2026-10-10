using Mahjong.Autotable.Api.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 1 — REST surface for the per-(player, season) Elo
/// leaderboard.
///
/// <para><b>GET /api/ratings/leaderboard?season=&amp;limit=&amp;offset=</b>
/// — when <c>season</c> is the current season (or omitted) returns the
/// live <see cref="Data.Entities.PlayerRating"/> page; when
/// <c>season</c> names a closed season, returns the frozen snapshot
/// from <see cref="Data.Entities.PlayerRatingHistory"/>.</para>
///
/// <para>Shape mirrors <c>GET /api/leaderboard</c> deliberately so the
/// frontend can reuse its table component:
/// <code>
/// {
///   "season": "2026-Q1",
///   "total": 42,
///   "rows": [
///     { "rank": 1, "playerId": "9b3a…", "eloRating": 1431,
///       "gamesPlayed": 18, "lastUpdatedAt": "2026-05-23T…" },
///     …
///   ]
/// }
/// </code></para>
/// </summary>
[ApiController]
[Route("api/ratings")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class RatingsController : ControllerBase
{
    private readonly PlayerRatingService _ratings;

    public RatingsController(PlayerRatingService ratings)
    {
        _ratings = ratings;
    }

    [HttpGet("leaderboard")]
    public async Task<IActionResult> Leaderboard(
        [FromQuery] string? season,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken ct = default)
    {
        var current = _ratings.CurrentSeason();
        var requestedSeason = string.IsNullOrWhiteSpace(season) ? current : season.Trim();
        var resp = string.Equals(requestedSeason, current, StringComparison.Ordinal)
            ? await _ratings.LeaderboardAsync(requestedSeason, limit ?? 50, offset ?? 0, ct)
            : await _ratings.SnapshotLeaderboardAsync(requestedSeason, limit ?? 50, offset ?? 0, ct);
        return Ok(new
        {
            season = resp.Season,
            total = resp.Total,
            rows = resp.Rows,
        });
    }

    /// <summary>
    /// Returns the current season code as resolved by the service.
    /// Useful for the frontend to default the leaderboard query without
    /// having to derive the season independently.
    /// </summary>
    [HttpGet("season")]
    public IActionResult Season()
    {
        return Ok(new
        {
            current = _ratings.CurrentSeason(),
            defaultElo = _ratings.DefaultElo,
            kFactor = _ratings.KFactor,
        });
    }
}
