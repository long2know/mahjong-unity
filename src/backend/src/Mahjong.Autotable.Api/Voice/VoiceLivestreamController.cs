using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Players;
using Mahjong.Autotable.Api.RateLimiting;
using Mahjong.Autotable.Api.Tables;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Voice;

/// <summary>
/// Phase K Wave 6 — Bishop. HLS livestream surface for the voice
/// channel. Wave 6 ships the controller + the
/// <see cref="ILivestreamRecorder"/> seam; the real ffmpeg / encoder
/// pipeline lands in Phase L by swapping the DI binding.
///
/// <list type="bullet">
///   <item><c>POST /api/voice/livestream/{gameId}/start</c> — begins
///         HLS recording. Caller MUST be the game owner or an admin
///         (401 anon; 403 non-owner). Audit Kind
///         <see cref="ReconnectAuditEntry.KindVoiceLivestreamStart"/>.</item>
///   <item><c>POST /api/voice/livestream/{gameId}/stop</c> — stops a
///         live recording. Same auth gate. Audit Kind
///         <see cref="ReconnectAuditEntry.KindVoiceLivestreamStop"/>.</item>
///   <item><c>GET  /api/voice/livestream/{gameId}/playlist.m3u8</c>
///         — returns the live m3u8. 200 when live, 404 otherwise.</item>
///   <item><c>GET  /api/voice/livestream/{gameId}/{segment}.ts</c>
///         — returns the named segment bytes. 200 when live + segment
///         exists, 404 otherwise.</item>
/// </list>
///
/// <para>The playlist endpoint advertises
/// <c>application/vnd.apple.mpegurl</c> per RFC 8216 so a downstream
/// HLS-aware player (hls.js, native iOS / Safari) consumes the body
/// directly. The segment endpoint serves <c>video/mp2t</c>; the
/// in-memory stub returns a small payload so the contract test can
/// verify the surface end-to-end without the encoder wiring.</para>
/// </summary>
[ApiController]
[Route("api/voice/livestream")]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public sealed class VoiceLivestreamController : ControllerBase
{
    private const string HlsPlaylistContentType = "application/vnd.apple.mpegurl";
    private const string HlsSegmentContentType = "video/mp2t";

    private readonly ILivestreamRecorder _recorder;
    private readonly AuthCookieService _cookies;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VoiceLivestreamController> _logger;
    private readonly IPlayerTableContext _tableContext;
    private readonly PlayerIdentityService _identity;

    public VoiceLivestreamController(
        ILivestreamRecorder recorder,
        AuthCookieService cookies,
        IServiceScopeFactory scopeFactory,
        ILogger<VoiceLivestreamController> logger,
        IPlayerTableContext tableContext,
        PlayerIdentityService identity)
    {
        _recorder = recorder;
        _cookies = cookies;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _tableContext = tableContext;
        _identity = identity;
    }

    [HttpPost("{gameId:guid}/start")]
    public async Task<IActionResult> Start([FromRoute] Guid gameId, CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null)
            return Unauthorized(new { error = "Authentication required to start a livestream." });
        var gate = await CheckOwnerOrAdminAsync(gameId, session, ct);
        if (gate is not null) return gate;

        var handle = await _recorder.StartAsync(gameId, session.PlayerId, ct);
        await WriteAuditAsync(
            session.PlayerId,
            gameId,
            ReconnectAuditEntry.KindVoiceLivestreamStart,
            ct);
        return Ok(handle);
    }

    [HttpPost("{gameId:guid}/stop")]
    public async Task<IActionResult> Stop([FromRoute] Guid gameId, CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        if (session is null)
            return Unauthorized(new { error = "Authentication required to stop a livestream." });
        var gate = await CheckOwnerOrAdminAsync(gameId, session, ct);
        if (gate is not null) return gate;

        var handle = await _recorder.StopAsync(gameId, session.PlayerId, ct);
        if (handle is null)
        {
            return NotFound(new
            {
                error = "no-livestream-in-progress",
                gameId,
            });
        }
        await WriteAuditAsync(
            session.PlayerId,
            gameId,
            ReconnectAuditEntry.KindVoiceLivestreamStop,
            ct);
        return Ok(handle);
    }

    [HttpGet("{gameId:guid}/playlist.m3u8")]
    public async Task<IActionResult> Playlist([FromRoute] Guid gameId, CancellationToken ct)
    {
        // Phase K Wave 8 — Bishop. Auth gate. The W6/W7 endpoint was
        // anonymous; W8 restricts pulls to admins, table owners,
        // seated players, and active spectators (where the runtime
        // has a hydrated snapshot). Two negative responses:
        //   401 — no session AND no anon-identity cookie.
        //   403 — caller resolved but not associated with the table.
        var gateResult = await GateAsync(gameId, ct);
        if (gateResult is not null) return gateResult;

        var body = _recorder.GetPlaylist(gameId);
        if (body is null)
        {
            // 404 with a structured envelope — contract tests parse the
            // shape, and downstream clients can branch on `reason`.
            Response.Headers.CacheControl = "no-store";
            return StatusCode(StatusCodes.Status404NotFound, new
            {
                error = "livestream-not-live",
                gameId,
                reason = "no-active-stream",
            });
        }
        return Content(body, HlsPlaylistContentType);
    }

    [HttpGet("{gameId:guid}/{segment}.ts")]
    public async Task<IActionResult> Segment(
        [FromRoute] Guid gameId,
        [FromRoute] string segment,
        CancellationToken ct)
    {
        // Phase K Wave 8 — Bishop. Same auth gate as the playlist
        // endpoint. Without this the segment URLs would be a
        // bypass — clients could skip the playlist and pull TS
        // bytes directly.
        var gateResult = await GateAsync(gameId, ct);
        if (gateResult is not null) return gateResult;

        var bytes = _recorder.GetSegment(gameId, $"{segment}.ts");
        if (bytes is null)
        {
            return StatusCode(StatusCodes.Status404NotFound, new
            {
                error = "segment-not-found",
                gameId,
                segment = $"{segment}.ts",
            });
        }
        return File(bytes, HlsSegmentContentType);
    }

    /// <summary>
    /// Phase K Wave 8 — Bishop. Auth gate shared by the playlist +
    /// segment endpoints. Returns null when the caller is permitted;
    /// otherwise returns the wired-up 401 / 403 IActionResult ready
    /// to short-circuit the response.
    /// </summary>
    private async Task<IActionResult?> GateAsync(Guid gameId, CancellationToken ct)
    {
        var session = await _cookies.ResolveAsync(HttpContext, ct);
        var anonId = _identity.ResolveFromCookie(HttpContext);
        var assoc = await _tableContext.ResolveAsync(gameId, session, anonId, ct);

        // Allow: Admin, Owner, Seated, Spectator (snapshot present).
        if (assoc.Role is PlayerTableRole.Admin
            or PlayerTableRole.Owner
            or PlayerTableRole.Seated
            or PlayerTableRole.Spectator)
        {
            return null;
        }

        if (assoc.Role == PlayerTableRole.Anonymous)
        {
            await WriteAuditAsync(
                string.Empty,
                gameId,
                ReconnectAuditEntry.KindLivestreamPlaylistUnauthorized,
                ct);
            return StatusCode(StatusCodes.Status401Unauthorized, new
            {
                error = "livestream-auth-required",
                reason = assoc.Reason,
                gameId,
            });
        }

        await WriteAuditAsync(
            assoc.PlayerId ?? string.Empty,
            gameId,
            ReconnectAuditEntry.KindLivestreamPlaylistForbidden,
            ct);
        return StatusCode(StatusCodes.Status403Forbidden, new
        {
            error = "livestream-not-authorised",
            reason = assoc.Reason,
            gameId,
        });
    }

    private async Task<IActionResult?> CheckOwnerOrAdminAsync(
        Guid gameId,
        Mahjong.Autotable.Api.Data.Entities.PlayerAuthSession session,
        CancellationToken ct)
    {
        var isAdmin = string.Equals(session.Role, "admin", StringComparison.OrdinalIgnoreCase);
        if (isAdmin) return null;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = await db.ChangshaGames
            .Where(g => g.Id == gameId)
            .Select(g => g.OwnerPlayerId)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(owner))
        {
            return NotFound(new { error = "Game not found.", gameId });
        }
        if (!string.Equals(owner, session.PlayerId, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Only the table creator or an admin may control the livestream.",
            });
        }
        return null;
    }

    private async Task WriteAuditAsync(string playerId, Guid gameId, string kind, CancellationToken ct)
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
                Kind = kind,
                Detail = gameId.ToString("N"),
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Livestream audit write failed for kind={Kind} gameId={GameId}", kind, gameId);
        }
    }
}
