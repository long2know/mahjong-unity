using Mahjong.Autotable.Api.Changsha.Reconnect;
using Microsoft.AspNetCore.Mvc;

namespace Mahjong.Autotable.Api.Changsha.Reconnect;

/// <summary>
/// Phase J Wave 9 — HTTP surface for the reconnect-token lifecycle.
/// The hub flow (<c>ChangshaHub.ReconnectGame</c>) is still the canonical
/// path during a live SignalR session; these endpoints exist so a
/// client can also drive the rotation explicitly (e.g. on page reload
/// before opening a new socket), and so contract tests can probe the
/// behaviour without standing up a hub connection.
///
/// <para>Routes:
/// <list type="bullet">
///   <item><c>POST /api/reconnect/issue</c> — mint a fresh token.</item>
///   <item><c>POST /api/reconnect/rotate</c> — single-use rotation of an
///         existing token to a replacement.</item>
///   <item><c>POST /api/reconnect/verify</c> — validate without rotating
///         (used by the SignalR hub on connect).</item>
/// </list></para>
/// </summary>
[ApiController]
[Route("api/reconnect")]
public sealed class ReconnectController : ControllerBase
{
    private readonly ReconnectTokenService _tokens;

    public ReconnectController(ReconnectTokenService tokens)
    {
        _tokens = tokens;
    }

    [HttpPost("issue")]
    public async Task<IActionResult> Issue([FromBody] IssueBody body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.GameId) || string.IsNullOrWhiteSpace(body.PlayerId))
            return BadRequest(new { error = "gameId and playerId are required." });
        if (body.SeatIndex < 0 || body.SeatIndex > 3)
            return BadRequest(new { error = "seatIndex must be in 0..3." });

        var row = await _tokens.IssueAsync(body.GameId, body.SeatIndex, body.PlayerId, HttpContext, ct);
        return Ok(new
        {
            token = row.Token,
            tokenId = row.Id,
            expiresAt = row.ExpiresAt,
        });
    }

    [HttpPost("rotate")]
    public async Task<IActionResult> Rotate([FromBody] RotateBody body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Token) || string.IsNullOrWhiteSpace(body.GameId))
            return BadRequest(new { error = "token and gameId are required." });
        if (body.SeatIndex < 0 || body.SeatIndex > 3)
            return BadRequest(new { error = "seatIndex must be in 0..3." });

        // The rotate path doesn't take playerId — we resolve it from the
        // prior token row. Look up first so we can audit a rejection too.
        var fresh = await RotateInternalAsync(body, ct);
        if (fresh is null)
            return BadRequest(new { error = "Token cannot be rotated (missing, expired, consumed, or mismatched)." });

        return Ok(new
        {
            token = fresh.Token,
            tokenId = fresh.Id,
            rotatedFromTokenId = fresh.RotatedFromTokenId,
            expiresAt = fresh.ExpiresAt,
        });
    }

    [HttpPost("verify")]
    public async Task<IActionResult> Verify([FromBody] RotateBody body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Token) || string.IsNullOrWhiteSpace(body.GameId))
            return BadRequest(new { error = "token and gameId are required." });
        var row = await _tokens.VerifyAsync(body.Token, body.GameId, body.SeatIndex, ct);
        if (row is null)
            return BadRequest(new { error = "Token is invalid, expired, consumed, or mismatched." });
        return Ok(new
        {
            tokenId = row.Id,
            playerId = row.PlayerId,
            expiresAt = row.ExpiresAt,
            rotatedFromTokenId = row.RotatedFromTokenId,
        });
    }

    private async Task<Data.Entities.ReconnectToken?> RotateInternalAsync(RotateBody body, CancellationToken ct)
    {
        // Resolve the player from the prior row so callers don't have to
        // round-trip /api/auth/me; we already trust the token because the
        // rotation will fail closed if the row doesn't exist.
        var probe = await _tokens.VerifyAsync(body.Token!, body.GameId!, body.SeatIndex, ct);
        if (probe is null) return null;
        return await _tokens.VerifyAndRotateAsync(body.Token!, body.GameId!, body.SeatIndex, probe.PlayerId, HttpContext, ct);
    }

    public sealed class IssueBody
    {
        public string? GameId { get; set; }
        public int SeatIndex { get; set; }
        public string? PlayerId { get; set; }
    }

    public sealed class RotateBody
    {
        public string? Token { get; set; }
        public string? GameId { get; set; }
        public int SeatIndex { get; set; }

        /// <summary>Phase J Wave 9 — accepted but intentionally ignored.
        /// Refresh windows do NOT bypass expiry; the rotation only succeeds
        /// when the prior token row is still within its TTL. Present in
        /// the contract so probing clients can pass it without producing
        /// a body-shape rejection.</summary>
        public bool? Refresh { get; set; }
    }
}
