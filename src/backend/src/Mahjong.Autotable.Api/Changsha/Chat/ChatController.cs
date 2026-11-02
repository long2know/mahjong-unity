using System.Globalization;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.Mvc;

namespace Mahjong.Autotable.Api.Changsha.Chat;

/// <summary>
/// Signed, joined-room REST chat. Both POST routes share the same authorization
/// and channel validation; invitations never use this persisted history.
///
/// <para>Routes:
/// <list type="bullet">
///   <item><c>POST /api/chat/send</c> — submit a chat message.
///         Returns the persisted row.</item>
///   <item><c>GET /api/games/{gameId}/chat?since=&amp;limit=50</c> — backfill
///         the table conversation.</item>
/// </list></para>
/// </summary>
[ApiController]
public sealed class ChatController : ControllerBase
{
    private readonly ChatService _chat;
    private readonly PlayerIdentityService _playerIdentity;
    private readonly IChangshaGameRuntime _runtime;

    public ChatController(ChatService chat, PlayerIdentityService playerIdentity, IChangshaGameRuntime runtime)
    {
        _chat = chat;
        _playerIdentity = playerIdentity;
        _runtime = runtime;
    }

    [HttpPost("api/chat/send")]
    public Task<IActionResult> Send([FromBody] SendBody body, CancellationToken ct) =>
        SendCore(body?.GameId, body, ct);

    [HttpPost("api/games/{gameId}/chat")]
    public Task<IActionResult> SendToRoom(string gameId, [FromBody] SendBody body, CancellationToken ct) =>
        SendCore(gameId, body, ct);

    private async Task<IActionResult> SendCore(string? gameId, SendBody? body, CancellationToken ct)
    {
        var playerId = _playerIdentity.ResolveFromCookie(HttpContext);
        if (playerId is null) return Unauthorized(new { error = "identity-required" });
        if (body is null || string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(body.Body))
            return BadRequest(new { error = "gameId and body are required." });

        try
        {
            var room = await _runtime.ResolveExistingRoomAsync(gameId, ct);
            if (room is null) return NotFound(new { error = "room-not-found" });
            var (outcome, message) = await _chat.SendForMemberAsync(
                room, playerId, body.Body, body.Channel, body.RecipientPlayerId, ct);
            return outcome switch
            {
                ChatSendOutcome.Ok => Ok(message),
                ChatSendOutcome.RateLimited => StatusCode(429, new { error = "Chat rate limit exceeded." }),
                ChatSendOutcome.Filtered => BadRequest(new { error = "Message blocked by chat filter." }),
                ChatSendOutcome.TooLong => BadRequest(new { error = "Message exceeds 280-character limit." }),
                ChatSendOutcome.NotAllowed => StatusCode(403, new { error = "not-allowed" }),
                ChatSendOutcome.RecipientNotJoined => StatusCode(403, new { error = "recipient-not-joined" }),
                _ => BadRequest(new { error = "Invalid chat send." }),
            };
        }
        catch (PublicRoomRecoveryException ex)
        {
            return StatusCode(ex.Reason == "invalid-public-room" ? 400 : 503, new { error = ex.Reason });
        }
        catch (ChatAccessException ex)
        {
            return StatusCode(ex.Reason == "room-not-found" ? 404 : 403, new { error = ex.Reason });
        }
    }

    [HttpGet("api/games/{gameId}/chat")]
    public async Task<IActionResult> Backfill(string gameId, [FromQuery] string? since, [FromQuery] int? limit, CancellationToken ct)
    {
        var playerId = _playerIdentity.ResolveFromCookie(HttpContext);
        if (playerId is null) return Unauthorized(new { error = "identity-required" });
        if (string.IsNullOrWhiteSpace(gameId))
            return BadRequest(new { error = "gameId is required." });
        DateTime? sinceTs = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (!DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return BadRequest(new { error = "since must be an ISO-8601 timestamp." });
            sinceTs = parsed.UtcDateTime;
        }
        var resolvedLimit = limit.GetValueOrDefault(50);
        try
        {
            var room = await _runtime.ResolveExistingRoomAsync(gameId, ct);
            if (room is null) return NotFound(new { error = "room-not-found" });
            var messages = await _chat.BackfillForMemberAsync(room, playerId, sinceTs, resolvedLimit, ct);
            return Ok(new { gameId = room.RoomId, messages });
        }
        catch (PublicRoomRecoveryException ex)
        {
            return StatusCode(ex.Reason == "invalid-public-room" ? 400 : 503, new { error = ex.Reason });
        }
        catch (ChatAccessException ex)
        {
            return StatusCode(ex.Reason == "room-not-found" ? 404 : 403, new { error = ex.Reason });
        }
    }

    public sealed class SendBody
    {
        public string? GameId { get; set; }
        public string? Channel { get; set; }
        public string? Body { get; set; }
        public string? RecipientPlayerId { get; set; }
    }
}
