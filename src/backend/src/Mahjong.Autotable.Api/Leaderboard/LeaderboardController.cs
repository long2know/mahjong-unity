using Microsoft.AspNetCore.Mvc;

namespace Mahjong.Autotable.Api.Leaderboard;

/// <summary>
/// Phase J Wave 6 — REST surface for the career-stat leaderboard.
///
/// <para><b>GET /api/leaderboard</b> — returns the top-N players ordered by
/// the requested axis, filtered by a minimum games-played threshold to
/// keep the board meaningful.</para>
///
/// <para>Supported query parameters:
/// <list type="bullet">
///   <item><c>sort</c> — <c>gamesWon</c> (default) | <c>totalScore</c> |
///         <c>winRate</c> | <c>longestStreak</c> | <c>highestScore</c>.
///         Parsing is case-insensitive; unknown values fall back to
///         <c>gamesWon</c>.</item>
///   <item><c>limit</c> — default 50, max 100. Larger values are clamped
///         silently; the frontend should paginate via <c>offset</c>.</item>
///   <item><c>offset</c> — default 0; negative values are clamped to 0.</item>
///   <item><c>minGames</c> — default 5; filters out profiles below the
///         threshold. Set to 0 to include everyone (useful for testing /
///         admin views).</item>
/// </list></para>
///
/// <para>Response shape:</para>
/// <code>
/// {
///   "total": 142,
///   "rows": [
///     {
///       "rank": 1,
///       "playerId": "9b3a…",
///       "displayName": "Bishop",
///       "avatarColor": "#1E88E5",
///       "gamesPlayed": 87,
///       "gamesWon": 42,
///       "winRate": 0.4827586206896552,
///       "totalScore": 1240,
///       "highestSingleGameScore": 96,
///       "longestWinStreak": 7
///     }, …
///   ]
/// }
/// </code>
/// </summary>
[ApiController]
[Route("api/leaderboard")]
public sealed class LeaderboardController : ControllerBase
{
    private readonly LeaderboardService _service;

    public LeaderboardController(LeaderboardService service)
    {
        _service = service;
    }

    /// <summary>
    /// Lists the leaderboard. See the class-level docstring for query
    /// semantics. All clamping happens inside
    /// <see cref="LeaderboardService.GetAsync(LeaderboardSort, int, int, int, CancellationToken)"/>.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? sort = null,
        [FromQuery] int? limit = null,
        [FromQuery] int? offset = null,
        [FromQuery] int? minGames = null,
        CancellationToken ct = default)
    {
        var parsedSort = LeaderboardService.ParseSort(sort);
        var response = await _service.GetAsync(
            parsedSort,
            limit ?? LeaderboardService.DefaultLimit,
            offset ?? 0,
            minGames ?? LeaderboardService.DefaultMinGames,
            ct);
        return Ok(response);
    }
}
