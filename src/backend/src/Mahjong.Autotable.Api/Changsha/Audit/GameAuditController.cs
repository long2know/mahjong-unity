using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Changsha.Reconnect;
using Mahjong.Autotable.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Changsha.Audit;

/// <summary>
/// Phase J Wave 9 — admin-only game audit endpoint.
///
/// <para>Exposes per-game audit metadata (reconnect rotation chains, chat
/// counts, replay schema version) to the admin operator. Gated on
/// <see cref="Data.Entities.PlayerAuthSession.Role"/> == <c>"admin"</c>;
/// anonymous + non-admin sessions get 401 with no body leakage (the
/// error body deliberately does not name any audit-shaped keys).</para>
///
/// <para>Routes:
/// <list type="bullet">
///   <item><c>GET /api/admin/games/{gameId}/audit</c> — canonical path.</item>
///   <item><c>GET /api/games/{gameId}/audit</c> — alias for legacy clients.</item>
/// </list></para>
/// </summary>
[ApiController]
public sealed class GameAuditController : ControllerBase
{
    private readonly AuthCookieService _cookies;
    private readonly AppDbContext _db;

    public GameAuditController(AuthCookieService cookies, AppDbContext db)
    {
        _cookies = cookies;
        _db = db;
    }

    [HttpGet("api/admin/games/{gameId}/audit")]
    [HttpGet("api/games/{gameId}/audit")]
    public async Task<IActionResult> Get(string gameId, CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null)
        {
            // Body is deliberately minimal — must not leak any audit-shaped
            // field names so the unauthorised body can't be used as an
            // existence oracle.
            return Unauthorized(new { error = "Authentication required." });
        }
        if (!string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Admin role required." });
        }
        if (!Guid.TryParse(gameId, out var gameGuid))
        {
            return BadRequest(new { error = "gameId must be a GUID." });
        }

        var replay = await _db.ChangshaGameReplays.AsNoTracking()
            .FirstOrDefaultAsync(r => r.GameId == gameGuid, ct);
        var chatCount = await _db.ChatMessages.AsNoTracking()
            .Where(m => m.GameId == gameId)
            .CountAsync(ct);
        var rotations = await _db.ReconnectTokens.AsNoTracking()
            .Where(t => t.GameId == gameId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new
            {
                tokenId = t.Id,
                playerId = t.PlayerId,
                seatIndex = t.SeatIndex,
                createdAt = t.CreatedAt,
                expiresAt = t.ExpiresAt,
                consumedAt = t.ConsumedAt,
                rotatedFromTokenId = t.RotatedFromTokenId,
            })
            .ToListAsync(ct);

        return Ok(new
        {
            gameId = gameGuid,
            schemaVersion = replay?.SchemaVersion ?? 0,
            replayPresent = replay is not null,
            replayCreatedAt = replay?.CreatedAt,
            chatMessageCount = chatCount,
            rotations,
            events = rotations,
            audit = new
            {
                gameId = gameGuid,
                rotationCount = rotations.Count,
                chatMessageCount = chatCount,
            },
        });
    }
}
