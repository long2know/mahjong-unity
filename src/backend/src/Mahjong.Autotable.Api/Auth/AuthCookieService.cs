using System.Security.Cryptography;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase J Wave 8 — owns the <c>mahjong_auth</c> cookie. Distinct from the
/// <see cref="Mahjong.Autotable.Api.Players.PlayerIdentityService"/> cookie
/// (<c>mahjong_pid</c>) which identifies an anonymous browser; the auth
/// cookie pins the browser to a server-side <see cref="PlayerAuthSession"/>
/// row that links to a verified <see cref="PlayerAuthIdentity"/>.
///
/// <para><b>Session token:</b> 64-char URL-safe base64 of 48 random bytes,
/// stored server-side. Cookie is HttpOnly + Secure (HTTPS only) + SameSite=Lax
/// + 30-day default lifetime (configurable). Logout deletes the session row
/// (server-side revocation) and clears the cookie.</para>
///
/// <para>Logout deliberately leaves the <c>mahjong_pid</c> cookie alone —
/// the player keeps their anonymous identity + stats. Re-linking is a
/// separate action.</para>
/// </summary>
public sealed class AuthCookieService
{
    public const string CookieName = "mahjong_auth";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AuthOptions _options;

    public AuthCookieService(IServiceScopeFactory scopeFactory, AuthOptions options)
    {
        _scopeFactory = scopeFactory;
        _options = options;
    }

    public TimeSpan SessionLifetime => TimeSpan.FromDays(Math.Max(1, _options.SessionLifetimeDays));

    /// <summary>
    /// Issues a fresh auth session for <paramref name="playerId"/> tied to
    /// <paramref name="identityId"/>, persists the row, and writes the
    /// cookie. Returns the resolved <see cref="PlayerAuthSession"/>.
    /// <para>Phase J Wave 9 — the optional <paramref name="role"/>
    /// argument stamps <see cref="PlayerAuthSession.Role"/>, used by
    /// the admin gate on <c>/api/admin/games/{id}/audit</c>. Null =
    /// ordinary player.</para>
    /// </summary>
    public async Task<PlayerAuthSession> IssueAsync(HttpContext context, string playerId, Guid identityId, string? role = null, CancellationToken ct = default)
    {
        var token = GenerateOpaqueToken();
        var session = new PlayerAuthSession
        {
            Token = token,
            PlayerId = playerId,
            IdentityId = identityId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(SessionLifetime),
            LastUsedAt = DateTime.UtcNow,
            Role = role,
        };
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PlayerAuthSessions.Add(session);
            await db.SaveChangesAsync(ct);
        }
        WriteCookie(context, token);
        return session;
    }

    /// <summary>
    /// Reads the cookie + looks up the matching session row. Touches
    /// <see cref="PlayerAuthSession.LastUsedAt"/> as a side effect (best-effort).
    /// Returns null when missing, expired, or not found.
    /// </summary>
    public async Task<PlayerAuthSession?> ResolveAsync(HttpContext context, CancellationToken ct = default)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token) || string.IsNullOrEmpty(token))
            return null;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.PlayerAuthSessions.FirstOrDefaultAsync(s => s.Token == token, ct);
        if (session is null) return null;
        if (session.ExpiresAt < DateTime.UtcNow)
        {
            db.PlayerAuthSessions.Remove(session);
            await db.SaveChangesAsync(ct);
            return null;
        }
        session.LastUsedAt = DateTime.UtcNow;
        try { await db.SaveChangesAsync(ct); } catch { /* best effort touch */ }
        return session;
    }

    /// <summary>
    /// Revokes the current session and clears the auth cookie. Idempotent —
    /// returns true if a session row was actually removed.
    /// </summary>
    public async Task<bool> RevokeAsync(HttpContext context, CancellationToken ct = default)
    {
        var removed = false;
        if (context.Request.Cookies.TryGetValue(CookieName, out var token) && !string.IsNullOrEmpty(token))
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.PlayerAuthSessions.FirstOrDefaultAsync(s => s.Token == token, ct);
            if (session is not null)
            {
                db.PlayerAuthSessions.Remove(session);
                await db.SaveChangesAsync(ct);
                removed = true;
            }
        }
        context.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
        });
        return removed;
    }

    /// <summary>Generates a 64-char URL-safe opaque token from 48 random bytes.</summary>
    public static string GenerateOpaqueToken()
    {
        Span<byte> buf = stackalloc byte[48];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToBase64String(buf)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private void WriteCookie(HttpContext context, string token)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = SessionLifetime,
            Path = "/",
            IsEssential = true,
        };
        context.Response.Cookies.Append(CookieName, token, options);
    }
}
