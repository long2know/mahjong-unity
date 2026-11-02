namespace Mahjong.Autotable.Api.Players;

/// <summary>
/// Configuration for the durable player-identity cookie, bound from the
/// <c>Identity</c> configuration section.
///
/// <para>The credential's <b>signing</b> keys are deliberately NOT configured here — they are
/// the existing <c>Authentication:JwtSigningKeys</c> set consumed through
/// <see cref="PlayerIdentityTokenProtector"/>, so operators keep one key surface and one
/// rotation runbook (<c>docs/jwt-rotation.md</c>).</para>
/// </summary>
public sealed class PlayerIdentityOptions
{
    /// <summary>
    /// Forces the <c>Secure</c> attribute even when the inbound request is plain HTTP.
    ///
    /// <para>Default <c>false</c>: the cookie is already marked <c>Secure</c> whenever the
    /// request itself is HTTPS. Set <c>Identity:RequireSecureCookie=true</c> when TLS is
    /// terminated at a proxy that does not forward the scheme, so the browser still refuses to
    /// send the credential over cleartext. Do NOT set it for a plain-HTTP deployment (the
    /// default Docker image serves <c>http://+:8080</c>) — browsers would drop the cookie and
    /// every request would mint a new identity.</para>
    /// </summary>
    public bool RequireSecureCookie { get; set; }

    /// <summary>
    /// Cookie lifetime in days (sliding — refreshed on every mint/refresh). Values below 1 fall
    /// back to the 365-day default.
    /// </summary>
    public int CookieMaxAgeDays { get; set; } = 365;
}
