using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.Players;

/// <summary>
/// Phase J Wave 6 — issuance endpoint for persistent player identifiers.
///
/// <para><b>POST /api/identity</b> — call once per browser before opening a
/// SignalR or autotable-WS connection. The endpoint reads the existing
/// <c>mahjong_pid</c> cookie (mints + sets one if absent), ensures a
/// <see cref="PlayerProfile"/> row exists (creating one with deterministic
/// default name + avatar colour on first sight), and returns the resolved
/// identity:</para>
///
/// <code>
/// {
///   "playerId":     "9b3a…",
///   "displayName":  "Player-AB12CD",
///   "avatarColor":  "#1E88E5",
///   "createdAt":    "2026-05-…",
///   "lastSeenAt":   "2026-05-…"
/// }
/// </code>
///
/// <para>The response sets the <c>mahjong_pid</c> cookie
/// (<c>HttpOnly; Secure (when HTTPS); SameSite=Lax; Max-Age=31536000; Path=/</c>).
/// Browser cookie jars attach the value to subsequent SignalR / autotable-WS
/// upgrade handshakes; the backend reads it to bind the connection to the
/// persistent identity, so the same browser keeps the same profile + career
/// stats across reloads / reconnects.</para>
/// </summary>
[ApiController]
[Route("api/identity")]
[EnableRateLimiting(Mahjong.Autotable.Api.RateLimiting.RateLimitingExtensions.AnonymousPolicy)]
public sealed class PlayerIdentityController : ControllerBase
{
    private readonly PlayerIdentityService _identity;
    private readonly PlayerProfileService _profiles;

    public PlayerIdentityController(PlayerIdentityService identity, PlayerProfileService profiles)
    {
        _identity = identity;
        _profiles = profiles;
    }

    /// <summary>
    /// Resolves or mints the caller's persistent player id, ensures their
    /// <see cref="PlayerProfile"/> row exists, and writes the
    /// <c>mahjong_pid</c> cookie. Idempotent — repeated calls return the
    /// same identity (the cookie is refreshed each time so the 1-year
    /// max-age slides forward).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> MintOrRefresh(CancellationToken ct)
    {
        var playerId = _identity.ResolveOrMint(HttpContext);
        var profile = await _profiles.GetOrCreateAsync(playerId, ct);
        return Ok(new
        {
            playerId = profile.PlayerId,
            displayName = profile.DisplayName,
            avatarColor = profile.AvatarColor,
            createdAt = profile.CreatedAt,
            lastSeenAt = profile.LastSeenAt,
        });
    }
}
