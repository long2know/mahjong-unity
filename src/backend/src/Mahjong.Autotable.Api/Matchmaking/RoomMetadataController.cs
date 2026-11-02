using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Matchmaking;

public sealed record RoomMetadataDto(
    string GameId,
    string? OwnerId,
    bool ViewerIsOwner,
    string Phase,
    bool IsPublic,
    string? PublicName,
    bool VoiceEnabled,
    bool ViewerCanManageVoice,
    int BotCount,
    int SeatedCount,
    int OpenHumanSeats,
    bool CanMakePublic,
    bool CanInvite);

[ApiController]
public sealed class RoomMetadataController(
    IChangshaGameRuntime runtime,
    PlayerIdentityService identity,
    LobbyPresenceService presence,
    AuthCookieService cookies,
    AppDbContext db) : ControllerBase
{
    [HttpGet("api/games/{gameId}")]
    [HttpGet("api/games/{gameId}/settings")]
    public async Task<IActionResult> Get(string gameId, CancellationToken ct)
    {
        var playerId = identity.ResolveFromCookie(HttpContext);
        if (playerId is null) return Unauthorized(new { error = "identity-required" });

        RoomReference? room;
        try { room = await runtime.ResolveExistingRoomAsync(gameId, ct); }
        catch (PublicRoomRecoveryException ex)
        {
            return StatusCode(ex.Reason == "invalid-public-room" ? 400 : 503, new { error = ex.Reason });
        }
        if (room is null) return NotFound(new { error = "room-not-found" });
        var access = await runtime.GetRoomAccessAsync(room.RuntimeGameId, playerId, ct);
        if (access is null) return NotFound(new { error = "room-not-found" });

        var runtimeId = Guid.Parse(room.RuntimeGameId);
        var voice = await db.ChangshaGames.AsNoTracking().Where(game => game.Id == runtimeId)
            .Select(game => new { game.VoiceEnabled, game.OwnerPlayerId }).SingleOrDefaultAsync(ct);
        var session = await cookies.ResolveAsync(HttpContext, ct);
        var canManageVoice = voice is not null && session is not null
            && (string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(voice.OwnerPlayerId)
                    && string.Equals(voice.OwnerPlayerId, session.PlayerId, StringComparison.Ordinal)));
        var isOwner = string.Equals(access.OwnerId, playerId, StringComparison.Ordinal);
        return Ok(new RoomMetadataDto(
            room.RoomId, access.OwnerId, isOwner, access.Phase.ToString(),
            access.IsPublic, access.PublicName, voice?.VoiceEnabled ?? false, canManageVoice,
            access.BotCount, access.SeatedCount, access.OpenHumanSeats,
            isOwner && access.Phase == ChangshaPhase.Seating,
            access.CanInvite(playerId) && presence.IsJoined(playerId, room.RuntimeGameId)));
    }
}
