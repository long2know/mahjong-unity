using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Players;

/// <summary>
/// Phase J Wave 6 — uniform <c>GetPlayerId()</c> helpers that resolve the
/// persistent player identifier across the three entry points: SignalR hub
/// methods, raw <see cref="HttpContext"/> request handlers, and the
/// autotable-WS endpoint.
///
/// <para><b>Resolution order:</b>
/// <list type="number">
///   <item><c>HubCallerContext.Items["playerId"]</c> (set by
///         <c>ChangshaHub.OnConnectedAsync</c> from the cookie).</item>
///   <item><c>HttpContext.Items["playerId"]</c> (set by any earlier
///         middleware / endpoint, including
///         <see cref="PlayerIdentityService.Inspect"/>).</item>
///   <item>The <b>verified</b> <c>mahjong_pid</c> credential, resolved through
///         <see cref="PlayerIdentityService.ResolveFromCookie"/>.</item>
///   <item>A defensive fall-back to the SignalR connection id (legacy
///         behaviour; reached only when no cookie was provided AND the
///         hub didn't pre-resolve).</item>
/// </list></para>
///
/// <para><b>Never reads the raw cookie.</b> The cookie value is a signed credential, not a
/// player id: only <see cref="PlayerIdentityService"/> may interpret it. Reading
/// <c>Request.Cookies["mahjong_pid"]</c> directly would (a) hand callers a bearer credential
/// they might log or persist, and (b) resurrect the forgeable-identity hole this seam
/// closes.</para>
/// </summary>
public static class PlayerIdentityExtensions
{
    /// <summary>Items-bag key under which the resolved player id is stored.</summary>
    public const string PlayerIdItemKey = "playerId";

    /// <summary>
    /// Returns the caller's persistent player id. See <see cref="PlayerIdentityExtensions"/>
    /// for the resolution order. Never returns <c>null</c> — the worst case
    /// is the SignalR connection id fall-back, which preserves Wave-5
    /// semantics for any code path that somehow bypasses the cookie.
    /// </summary>
    public static string GetPlayerId(this HubCallerContext context)
    {
        if (context.Items.TryGetValue(PlayerIdItemKey, out var v) && v is string s && !string.IsNullOrEmpty(s))
            return s;

        var http = context.GetHttpContext();
        if (http is not null)
        {
            var resolved = http.GetPlayerIdOrNull();
            if (!string.IsNullOrEmpty(resolved)) return resolved;
        }

        return context.ConnectionId;
    }

    /// <summary>
    /// Returns the caller's persistent player id from an
    /// <see cref="HttpContext"/> (REST controllers, autotable-WS endpoint).
    /// Reads <see cref="HttpContext.Items"/> first (so middleware / earlier
    /// handlers can pin the value), then verifies the signed
    /// <c>mahjong_pid</c> credential. Returns <c>null</c> when neither source
    /// carries a trustworthy value — an unsigned / tampered cookie resolves to
    /// <c>null</c>, never to the identifier it claims.
    /// </summary>
    public static string? GetPlayerIdOrNull(this HttpContext context)
    {
        if (context.Items.TryGetValue(PlayerIdItemKey, out var v) && v is string s && !string.IsNullOrEmpty(s))
            return s;

        // Fail closed when the identity service isn't resolvable (e.g. a bare DefaultHttpContext
        // in a unit test): the raw cookie is a credential and must never be trusted here.
        var identity = context.RequestServices?.GetService<PlayerIdentityService>();
        return identity?.ResolveFromCookie(context);
    }
}
