#if TESTING_SHIM
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Players;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Shims;

/// <summary>
/// Phase K Wave 5 — Vasquez. Test-only auth shim for integration tests
/// that need a <see cref="PlayerAuthSession"/> row + <c>mahjong_pid</c>
/// cookie without driving the full OAuth / dev-login flow.
///
/// <para><b>WHY a shim instead of just calling <c>POST /api/auth/dev-login</c>?</b>
/// dev-login is registered only in <c>IHostEnvironment.IsDevelopment()</c>;
/// production-like factories (the regression-host fixture is one) run
/// under <c>Production</c> so the Wave-8 CSP can be exercised. They have
/// no dev-login route. This shim lets those factories still mint a
/// session for tests that need an authenticated <see cref="HttpClient"/>
/// without flipping the environment.</para>
///
/// <para><b>Gated by <c>TESTING_SHIM</c></b> — the entire file is
/// compiled out when the symbol is not defined. The test project
/// defines it in its csproj; the production assembly never sees this
/// code (the build pipeline emits the test DLL only, this is in
/// <c>Mahjong.Autotable.Api.Tests</c> not the API).</para>
///
/// <para><b>Surface:</b></para>
/// <list type="bullet">
///   <item><see cref="WithDirectSession(HttpClient, Guid)"/> — sets the
///         <c>mahjong_pid</c> cookie on the client's default request
///         headers (anonymous player-identity scope). Use when no
///         server-side row is needed.</item>
///   <item><see cref="WithDirectSession(HttpClient, IServiceProvider, Guid)"/>
///         — additionally inserts a matching <see cref="PlayerAuthSession"/>
///         + <see cref="PlayerAuthIdentity"/> row, sets the
///         <c>mahjong_auth</c> cookie, and resolves any
///         <see cref="AuthCookieService"/>-aware endpoint.</item>
///   <item><see cref="WithDirectSession(HttpClient, IServiceProvider, Guid, string)"/>
///         — same as the 3-arg form but stamps a role
///         (<c>"admin"</c>, <c>"moderator"</c>, etc.).</item>
/// </list>
///
/// <para><b>Idempotent</b> — calling twice with the same playerId
/// upserts (no duplicate identity rows).</para>
///
/// <para>See <c>docs/test-shims.md</c> for the full inventory of
/// test-only shims + the symbol-gating rationale.</para>
/// </summary>
public static class TestHttpClientExtensions
{
    /// <summary>
    /// Sets the <see cref="PlayerIdentityService.CookieName"/>
    /// (<c>mahjong_pid</c>) cookie on the client's default request
    /// headers using the RAW player id.
    ///
    /// <para><b>Unsigned</b> — this overload has no <see cref="IServiceProvider"/> and so
    /// cannot sign the credential. Since Burke's identity hardening the server rejects
    /// unsigned cookies and mints a fresh identity instead, so the server will NOT resolve
    /// <paramref name="playerId"/> from it. Use an overload that takes an
    /// <see cref="IServiceProvider"/> when the endpoint under test must resolve THIS
    /// player id.</para>
    /// </summary>
    public static HttpClient WithDirectSession(this HttpClient client, Guid playerId)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        var pidHex = playerId.ToString("N");
        AddCookieHeader(client, PlayerIdentityService.CookieName, pidHex);
        return client;
    }

    /// <summary>
    /// Sets a <b>signed</b> <c>mahjong_pid</c> cookie AND mints a matching
    /// <see cref="PlayerAuthSession"/> row + <c>mahjong_auth</c>
    /// cookie. Use when the endpoint under test resolves the
    /// session via <see cref="AuthCookieService.ResolveAsync"/>.
    /// </summary>
    public static HttpClient WithDirectSession(
        this HttpClient client,
        IServiceProvider services,
        Guid playerId)
        => WithDirectSession(client, services, playerId, role: null);

    /// <summary>
    /// Sets a <b>signed</b> <c>mahjong_pid</c> cookie, mints a matching
    /// <see cref="PlayerAuthSession"/> row + <c>mahjong_auth</c>
    /// cookie, and stamps the supplied <paramref name="role"/>
    /// (e.g. <c>"admin"</c>) on the session.
    /// </summary>
    public static HttpClient WithDirectSession(
        this HttpClient client,
        IServiceProvider services,
        Guid playerId,
        string? role)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (services is null) throw new ArgumentNullException(nameof(services));

        var pidHex = playerId.ToString("N");
        AddCookieHeader(client, PlayerIdentityService.CookieName, SignedPlayerIdCookie(services, pidHex));

        var sessionToken = MintSessionRow(services, pidHex, role);
        AddCookieHeader(client, AuthCookieService.CookieName, sessionToken);
        return client;
    }

    /// <summary>
    /// Produces the signed <c>mahjong_pid</c> cookie VALUE for
    /// <paramref name="playerId"/> using the host's own
    /// <see cref="PlayerIdentityService"/>. Tests that hand-craft a
    /// <c>Cookie</c> header (raw WebSocket handshakes) use this so the server
    /// resolves the intended durable identity instead of rotating them onto a
    /// fresh one.
    /// </summary>
    public static string SignedPlayerIdCookie(IServiceProvider services, string playerId)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        return services.GetRequiredService<PlayerIdentityService>().Protect(playerId);
    }

    /// <summary>
    /// Convenience form of <see cref="SignedPlayerIdCookie(IServiceProvider, string)"/>
    /// that emits the whole header value (<c>mahjong_pid=&lt;signed&gt;</c>).
    /// </summary>
    public static string SignedPlayerIdCookieHeader(IServiceProvider services, string playerId) =>
        $"{PlayerIdentityService.CookieName}={SignedPlayerIdCookie(services, playerId)}";

    private static void AddCookieHeader(HttpClient client, string name, string value)
    {
        // Replace any existing Cookie header in a single pass so the
        // shim is safe to call multiple times.
        var existing = client.DefaultRequestHeaders.TryGetValues("Cookie", out var vs)
            ? string.Join("; ", vs)
            : null;
        var merged = string.IsNullOrEmpty(existing)
            ? $"{name}={value}"
            : MergeCookie(existing!, name, value);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", merged);
    }

    private static string MergeCookie(string existing, string name, string value)
    {
        var parts = existing.Split(';', StringSplitOptions.RemoveEmptyEntries
                                       | StringSplitOptions.TrimEntries);
        var kept = parts.Where(p =>
        {
            var eq = p.IndexOf('=');
            if (eq <= 0) return true;
            return !p.AsSpan(0, eq).Equals(name.AsSpan(), StringComparison.Ordinal);
        });
        return string.Join("; ", kept.Concat(new[] { $"{name}={value}" }));
    }

    private static string MintSessionRow(IServiceProvider services, string pidHex, string? role)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Ensure a PlayerProfile row exists so the PlayerAuthIdentity
        // FK to PlayerProfiles.PlayerId is satisfied (the Wave-3
        // schema added that FK with cascade-delete).
        var profile = db.PlayerProfiles.FirstOrDefault(p => p.PlayerId == pidHex);
        if (profile is null)
        {
            profile = new PlayerProfile
            {
                PlayerId = pidHex,
                DisplayName = $"shim-{pidHex[..Math.Min(8, pidHex.Length)]}",
            };
            db.PlayerProfiles.Add(profile);
            db.SaveChanges();
        }

        // Find or create an identity row so the FK survives.
        var identity = db.PlayerAuthIdentities
            .FirstOrDefault(i => i.PlayerId == pidHex);
        if (identity is null)
        {
            identity = new PlayerAuthIdentity
            {
                Id = Guid.NewGuid(),
                PlayerId = pidHex,
                Provider = "test-shim",
                ProviderSubject = pidHex,
                Email = $"{pidHex}@test-shim.local",
                EmailVerified = false,
                CreatedAt = DateTime.UtcNow,
                LastUsedAt = DateTime.UtcNow,
            };
            db.PlayerAuthIdentities.Add(identity);
            db.SaveChanges();
        }

        // Mint the session row. Token MUST be unique per session.
        var token = $"shim-{Guid.NewGuid():N}";
        db.PlayerAuthSessions.Add(new PlayerAuthSession
        {
            Id = Guid.NewGuid(),
            Token = token,
            PlayerId = pidHex,
            IdentityId = identity.Id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            LastUsedAt = DateTime.UtcNow,
            Role = role,
        });
        db.SaveChanges();
        return token;
    }
}
#endif
