using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Players;

/// <summary>
/// Issues + validates the persistent player identity carried by the
/// <see cref="CookieName"/> cookie.
///
/// <para><b>Two distinct values, never conflated:</b></para>
/// <list type="bullet">
///   <item><b>playerId</b> — a PUBLIC, opaque identifier (32-char lowercase hex). It is
///         broadcast in the autotable <c>seats</c>/<c>nicks</c> wire keys, returned by
///         <c>POST /api/identity</c>, and stamped on leaderboard rows. It identifies; it never
///         authenticates.</item>
///   <item><b>identity credential</b> — the cookie value: a versioned, HMAC-signed token that
///         wraps the playerId (see <see cref="PlayerIdentityTokenProtector"/>). Only the server
///         can produce it, so a peer who reads a victim's public playerId cannot present it as
///         their own.</item>
/// </list>
///
/// <para><b>History.</b> Phase J Wave 6 stored the bare playerId in the cookie and validated it
/// by shape only. Because playerIds are public, that made durable identity trivially forgeable:
/// an attacker set <c>mahjong_pid=&lt;victim-id&gt;</c>, connected, and inherited the victim's
/// identity — including the reconnect seat inference that projects the victim's concealed hand
/// and authorises their seat actions. Wave-6 cookies are therefore no longer accepted; they are
/// classified <see cref="PlayerIdentityTokenStatus.LegacyUnsigned"/> and the caller is rotated
/// onto a freshly minted identity rather than being handed an attacker-chosen id.</para>
///
/// <para><b>Rotation.</b> Signing uses the primary JWT signing key; verification accepts every
/// active key. A cookie minted under a rotated-but-still-active key keeps working and is
/// transparently re-signed with the primary key on the next
/// <see cref="ResolveOrMint(HttpContext)"/>.</para>
///
/// <para><b>Cookie attributes:</b>
/// <list type="bullet">
///   <item><c>HttpOnly</c> — JavaScript cannot read the credential (XSS cannot exfiltrate it).</item>
///   <item><c>Secure</c> when the request is HTTPS, or always when
///         <see cref="PlayerIdentityOptions.RequireSecureCookie"/> is set (TLS terminated at a
///         proxy). Plain-HTTP dev / self-hosted deployments keep the cookie.</item>
///   <item><c>SameSite=Lax</c> — survives the OAuth top-level redirect back from a provider
///         (<c>Strict</c> would drop the identity on every callback).</item>
///   <item><c>Max-Age</c> — sliding, 1 year by default; re-applied on every mint/refresh so
///         tightened attributes and key rotations roll forward without a logout.</item>
///   <item><c>Path=/</c> — visible to <c>/api/*</c>, <c>/hubs/changsha</c>,
///         and <c>/autotable/ws</c>.</item>
/// </list></para>
/// </summary>
public sealed class PlayerIdentityService
{
    /// <summary>Cookie name carrying the signed identity credential.</summary>
    public const string CookieName = "mahjong_pid";

    /// <summary>Default cookie lifetime (one year). Slid forward on every mint/refresh.</summary>
    public static readonly TimeSpan CookieMaxAge = TimeSpan.FromDays(365);

    /// <summary><see cref="HttpContext.Items"/> key caching the verified verdict for a request.</summary>
    private const string ResultItemKey = "mahjong.identity.result";

    private readonly PlayerIdentityTokenProtector _protector;
    private readonly PlayerIdentityOptions _options;
    private readonly ILogger<PlayerIdentityService>? _logger;

    public PlayerIdentityService(
        PlayerIdentityTokenProtector protector,
        IOptions<PlayerIdentityOptions>? options = null,
        ILogger<PlayerIdentityService>? logger = null)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _options = options?.Value ?? new PlayerIdentityOptions();
        _logger = logger;
    }

    /// <summary>Configured cookie lifetime.</summary>
    public TimeSpan EffectiveCookieMaxAge =>
        _options.CookieMaxAgeDays >= 1 ? TimeSpan.FromDays(_options.CookieMaxAgeDays) : CookieMaxAge;

    /// <summary>
    /// Mints a fresh PUBLIC player identifier: 32-char lowercase hex (a GUID
    /// without dashes). Indistinguishable from a random string to outside
    /// observers, and — critically — carrying no authority on its own.
    /// </summary>
    public string Mint() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Signs <paramref name="playerId"/> into the cookie credential form. Exposed so tests and
    /// out-of-band tooling can construct a legitimate cookie without reimplementing the format.
    /// </summary>
    public string Protect(string playerId) => _protector.Protect(playerId);

    /// <summary>
    /// Verifies the presented cookie and returns the full verdict (status, key id, whether the
    /// primary key signed it). The result is cached on <see cref="HttpContext.Items"/> so the
    /// MAC is computed at most once per request.
    /// </summary>
    public PlayerIdentityTokenResult Inspect(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Items.TryGetValue(ResultItemKey, out var cached) && cached is PlayerIdentityTokenResult hit)
            return hit;

        var raw = context.Request.Cookies.TryGetValue(CookieName, out var value) ? value : null;
        var result = _protector.Unprotect(raw);
        context.Items[ResultItemKey] = result;

        if (result.WasRejected)
        {
            // Never log the presented value — a valid token is a bearer credential and a
            // rejected one may be a near-miss of one. The status alone is the useful signal.
            _logger?.LogInformation(
                "Rejected a {CookieName} cookie ({Status}); a fresh identity will be issued.",
                CookieName, result.Status);
        }
        else if (result.IsValid)
        {
            context.Items[PlayerIdentityExtensions.PlayerIdItemKey] = result.PlayerId!;
        }

        return result;
    }

    /// <summary>
    /// Returns the verified durable player id, or <c>null</c> when the cookie is absent,
    /// unsigned (legacy), malformed, tampered, or signed by a key that is no longer active.
    /// Fails closed: a rejected cookie NEVER yields the identifier it claims.
    /// </summary>
    public string? ResolveFromCookie(HttpContext context)
    {
        var result = Inspect(context);
        return result.IsValid ? result.PlayerId : null;
    }

    /// <summary>
    /// Returns the caller's durable identity, minting a brand-new one when the presented cookie
    /// cannot be trusted. The response cookie is always (re)written so the max-age slides, a
    /// rotated key is upgraded to the primary key, and tightened attributes roll forward.
    ///
    /// <para><b>Migration:</b> a legacy unsigned cookie is NOT honoured. The caller is issued a
    /// fresh identity — deliberately abandoning the presented id rather than trusting a value
    /// anyone could have copied off the wire.</para>
    /// </summary>
    public string ResolveOrMint(HttpContext context)
    {
        var result = Inspect(context);
        var playerId = result.IsValid ? result.PlayerId! : Mint();
        if (!result.IsValid)
        {
            // The freshly minted identity is authoritative for the rest of this request.
            context.Items[ResultItemKey] = new PlayerIdentityTokenResult(
                PlayerIdentityTokenStatus.Valid, playerId, _protector.PrimaryKid, true);
            context.Items[PlayerIdentityExtensions.PlayerIdItemKey] = playerId;
        }
        WriteCookie(context, playerId);
        return playerId;
    }

    /// <summary>
    /// Signs <paramref name="playerId"/> and writes it to the response under
    /// <see cref="CookieName"/> with the canonical attribute set. <c>Secure</c> follows the
    /// request scheme unless <see cref="PlayerIdentityOptions.RequireSecureCookie"/> forces it.
    /// </summary>
    public void WriteCookie(HttpContext context, string playerId)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsValidPlayerId(playerId))
            throw new ArgumentException("playerId must be a non-empty opaque token.", nameof(playerId));

        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps || _options.RequireSecureCookie,
            SameSite = SameSiteMode.Lax,
            MaxAge = EffectiveCookieMaxAge,
            Path = "/",
            IsEssential = true,
        };
        context.Response.Cookies.Append(CookieName, _protector.Protect(playerId), options);
    }

    /// <summary>
    /// Shape check for the PUBLIC identifier: non-null, 1..128 chars, only safe URL-token chars
    /// (<c>[A-Za-z0-9_-]</c>). Anything outside this is rejected because the
    /// playerId flows into <see cref="ChangshaSeatState.PlayerId"/>, log
    /// scopes, and persistence keys — accepting arbitrary user input would
    /// open a log-forging / log-injection vector. This is a hygiene rule, never an
    /// authentication check: authenticity comes solely from the verified signature.
    /// </summary>
    public static bool IsValidPlayerId(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        if (candidate.Length > 128) return false;
        foreach (var c in candidate)
        {
            var ok = (c >= 'a' && c <= 'z')
                  || (c >= 'A' && c <= 'Z')
                  || (c >= '0' && c <= '9')
                  || c == '_' || c == '-';
            if (!ok) return false;
        }
        return true;
    }
}
