using Microsoft.AspNetCore.Mvc;

namespace Mahjong.Autotable.Api.Matchmaking;

/// <summary>
/// Phase J Wave 5 — REST surface for the public matchmaking lobby. Only one
/// endpoint today: <c>GET /api/matchmaking/lobby</c> returning a
/// newest-first listing of public, lobby-phase games. All mutating
/// operations live on the SignalR hub (<c>SetGamePublic</c>,
/// <c>JoinRandom</c>) so we can broadcast state updates from a single
/// transport.
/// </summary>
[ApiController]
[Route("api/matchmaking")]
public sealed class MatchmakingController : ControllerBase
{
    private readonly MatchmakingService _matchmaking;

    public MatchmakingController(MatchmakingService matchmaking)
    {
        _matchmaking = matchmaking;
    }

    /// <summary>
    /// Lists the current public-game listings (capped at
    /// <see cref="MatchmakingService.LobbyCap"/>, newest-first). Returns
    /// <c>{ games: [...] }</c> so a future wave can add pagination /
    /// filter metadata without breaking clients.
    /// </summary>
    [HttpGet("lobby")]
    public async Task<IActionResult> GetLobby(CancellationToken ct)
    {
        var games = await _matchmaking.GetLobbyAsync(ct);
        return Ok(new { games });
    }
}
