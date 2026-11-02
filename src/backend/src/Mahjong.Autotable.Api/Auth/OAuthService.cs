using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase J Wave 8 — OAuth 2.0 helper. Knows the canonical authorize / token
/// / userinfo endpoints for the supported providers (Google, GitHub) and
/// constructs the redirect URLs + handles the code-for-token exchange and
/// userinfo fetch.
///
/// <para>State token: a 32-byte URL-safe nonce stored in a short-lived
/// <c>mahjong_oauth_state</c> cookie. On callback we compare the state
/// query param to the cookie value and refuse mismatches.</para>
///
/// <para>Provider config:
/// <list type="bullet">
///   <item><b>Google</b>: <c>https://accounts.google.com/o/oauth2/v2/auth</c>
///         + <c>https://oauth2.googleapis.com/token</c> +
///         <c>https://www.googleapis.com/oauth2/v3/userinfo</c>. Default scopes:
///         <c>openid profile email</c>.</item>
///   <item><b>GitHub</b>: <c>https://github.com/login/oauth/authorize</c>
///         + <c>https://github.com/login/oauth/access_token</c> +
///         <c>https://api.github.com/user</c>. Default scopes: <c>read:user user:email</c>.</item>
/// </list></para>
/// </summary>
public sealed class OAuthService
{
    public const string StateCookieName = "mahjong_oauth_state";
    public const string ReturnUrlCookieName = "mahjong_oauth_return";

    /// <summary>Phase K Wave 1 — cookie name for the PKCE code-verifier
    /// the client mints on <c>/login</c> and reads back on
    /// <c>/callback</c>. Short TTL (matches state cookie).</summary>
    public const string PkceVerifierCookieName = "mahjong_oauth_pkce";

    /// <summary>Phase K Wave 1 — cookie name for the OIDC nonce claim
    /// we expect to find in Google's <c>id_token</c>.</summary>
    public const string NonceCookieName = "mahjong_oauth_nonce";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AuthOptions _options;

    public OAuthService(IHttpClientFactory httpClientFactory, AuthOptions options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public OAuthProviderOptions GetProviderOptions(string provider) => provider.ToLowerInvariant() switch
    {
        "google" => _options.Google,
        "github" => _options.GitHub,
        // Phase K Wave 3 — Bishop. Microsoft (Azure AD / Entra ID).
        "microsoft" => _options.Microsoft,
        _ => new OAuthProviderOptions(),
    };

    public bool IsConfigured(string provider)
    {
        var opts = GetProviderOptions(provider);
        return opts.Enabled
            && !string.IsNullOrWhiteSpace(opts.ClientId)
            && !string.IsNullOrWhiteSpace(opts.ClientSecret);
    }

    public string BuildAuthorizeUrl(string provider, string redirectUri, string state)
    {
        return BuildAuthorizeUrl(provider, redirectUri, state, codeChallenge: null, nonce: null);
    }

    /// <summary>
    /// Phase K Wave 1 — PKCE + nonce-aware authorize URL builder. When
    /// <paramref name="codeChallenge"/> is non-null we append
    /// <c>code_challenge</c> + <c>code_challenge_method=S256</c>; when
    /// <paramref name="nonce"/> is non-null we append <c>nonce</c> (only
    /// meaningful for providers that issue an OIDC <c>id_token</c> —
    /// e.g. Google).
    /// </summary>
    public string BuildAuthorizeUrl(string provider, string redirectUri, string state, string? codeChallenge, string? nonce)
    {
        var opts = GetProviderOptions(provider);
        var (authorize, _, _, defaultScopes) = ResolveProviderEndpoints(provider, opts);
        var scopes = string.IsNullOrWhiteSpace(opts.Scopes) ? defaultScopes : opts.Scopes;
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = opts.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scopes,
            ["state"] = state,
            ["prompt"] = provider.Equals("google", StringComparison.OrdinalIgnoreCase) ? "select_account" : null,
        };
        if (!string.IsNullOrEmpty(codeChallenge))
        {
            query["code_challenge"] = codeChallenge;
            query["code_challenge_method"] = "S256";
        }
        if (!string.IsNullOrEmpty(nonce)
            && (provider.Equals("google", StringComparison.OrdinalIgnoreCase)
                || provider.Equals("microsoft", StringComparison.OrdinalIgnoreCase)))
        {
            // Phase K Wave 3 — Bishop. Microsoft v2.0 also issues an
            // id_token; carry the nonce through so the discovery-aware
            // verifier can bind it like the Google path.
            query["nonce"] = nonce;
        }
        return QueryHelpers.AddQueryString(authorize, query);
    }

    public async Task<OAuthUserInfo?> ExchangeAndFetchUserInfoAsync(
        string provider,
        string code,
        string redirectUri,
        CancellationToken ct = default)
    {
        return await ExchangeAndFetchUserInfoAsync(provider, code, redirectUri, codeVerifier: null, expectedNonce: null, ct);
    }

    /// <summary>
    /// Phase K Wave 1 — PKCE + nonce-aware variant. When
    /// <paramref name="codeVerifier"/> is supplied it's included in the
    /// token exchange (RFC 7636). When <paramref name="expectedNonce"/>
    /// is supplied and the provider returns an <c>id_token</c>, we
    /// parse the JWT and refuse the response if the <c>nonce</c> claim
    /// doesn't match.
    /// </summary>
    public async Task<OAuthUserInfo?> ExchangeAndFetchUserInfoAsync(
        string provider,
        string code,
        string redirectUri,
        string? codeVerifier,
        string? expectedNonce,
        CancellationToken ct = default)
    {
        var opts = GetProviderOptions(provider);
        var (_, tokenEndpoint, userInfoEndpoint, _) = ResolveProviderEndpoints(provider, opts);

        using var client = _httpClientFactory.CreateClient("oauth");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("mahjong-autotable/1.0");

        var tokenForm = new Dictionary<string, string>
        {
            ["client_id"] = opts.ClientId,
            ["client_secret"] = opts.ClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        };
        if (!string.IsNullOrEmpty(codeVerifier))
        {
            tokenForm["code_verifier"] = codeVerifier;
        }
        using var tokenResp = await client.PostAsync(tokenEndpoint, new FormUrlEncodedContent(tokenForm), ct);
        if (!tokenResp.IsSuccessStatusCode) return null;

        var tokenJson = await tokenResp.Content.ReadAsStringAsync(ct);
        string? accessToken;
        string? idToken = null;
        try
        {
            using var doc = JsonDocument.Parse(tokenJson);
            if (!doc.RootElement.TryGetProperty("access_token", out var tok)) return null;
            accessToken = tok.GetString();
            if (doc.RootElement.TryGetProperty("id_token", out var idTok))
            {
                idToken = idTok.GetString();
            }
        }
        catch (JsonException)
        {
            // GitHub may return form-encoded by default; the Accept header
            // above asks for JSON but be defensive.
            return null;
        }

        if (string.IsNullOrEmpty(accessToken)) return null;

        // Validate the id_token nonce when we have one to compare against.
        // The id_token is JWT-encoded; we parse the payload without
        // verifying the signature here — Google's RS256 signature
        // validation is out of scope for the Wave K-1 surface. The
        // nonce check still gives a strong replay defence because the
        // attacker would need to also steal the nonce cookie.
        if (!string.IsNullOrEmpty(expectedNonce) && !string.IsNullOrEmpty(idToken))
        {
            if (!TryReadIdTokenNonce(idToken, out var actualNonce)
                || !ConstantTimeEquals(expectedNonce, actualNonce ?? string.Empty))
            {
                return null;
            }
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var userResp = await client.GetAsync(userInfoEndpoint, ct);
        if (!userResp.IsSuccessStatusCode) return null;

        var userJson = await userResp.Content.ReadAsStringAsync(ct);
        return ParseUserInfo(provider, userJson);
    }

    /// <summary>
    /// Phase K Wave 1 — JWT payload reader for the <c>nonce</c> claim.
    /// Visible-for-testing; returns true on a parseable header.payload
    /// segment with a string <c>nonce</c>.
    /// </summary>
    public static bool TryReadIdTokenNonce(string idToken, out string? nonce)
    {
        nonce = null;
        if (string.IsNullOrWhiteSpace(idToken)) return false;
        var parts = idToken.Split('.');
        if (parts.Length < 2) return false;
        try
        {
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("nonce", out var n) && n.ValueKind == JsonValueKind.String)
            {
                nonce = n.GetString();
                return true;
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
        return false;
    }

    /// <summary>
    /// Phase K Wave 1 — PKCE verifier generator. Returns a 32-byte
    /// random base64-url string (no padding) per RFC 7636 §4.1.
    /// </summary>
    public static string GeneratePkceVerifier()
    {
        Span<byte> buf = stackalloc byte[32];
        RandomNumberGenerator.Fill(buf);
        return Base64UrlEncode(buf);
    }

    /// <summary>
    /// Phase K Wave 1 — PKCE challenge from verifier (S256).
    /// </summary>
    public static string BuildPkceChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    private static OAuthUserInfo? ParseUserInfo(string provider, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (provider.Equals("google", StringComparison.OrdinalIgnoreCase))
            {
                var sub = root.TryGetProperty("sub", out var s) ? s.GetString() : null;
                if (string.IsNullOrEmpty(sub)) return null;
                var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
                var emailVerified = root.TryGetProperty("email_verified", out var ev) && ev.GetBoolean();
                var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
                return new OAuthUserInfo(sub, email, emailVerified, name);
            }

            if (provider.Equals("github", StringComparison.OrdinalIgnoreCase))
            {
                // GitHub's id is a number; convert to string.
                var idElem = root.TryGetProperty("id", out var i) ? i : default;
                var sub = idElem.ValueKind == JsonValueKind.Number ? idElem.GetInt64().ToString() : idElem.GetString();
                if (string.IsNullOrEmpty(sub)) return null;
                var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
                var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) && root.TryGetProperty("login", out var login))
                    name = login.GetString();
                // GitHub doesn't surface email_verified on /user — emails come
                // verified via /user/emails. For Wave 8 we treat any returned
                // email as unverified unless the user later confirms via
                // magic link.
                return new OAuthUserInfo(sub, email, false, name);
            }

            if (provider.Equals("microsoft", StringComparison.OrdinalIgnoreCase))
            {
                // Phase K Wave 3 — Bishop. Microsoft Graph /me + Entra
                // ID userinfo both surface `id` (Graph) and `sub`/`oid`
                // (OIDC userinfo). Prefer `oid` (immutable across apps)
                // then `sub`, then Graph `id`. Email is `mail` (Graph)
                // / `email` (OIDC userinfo); display name is
                // `displayName` (Graph) / `name` (OIDC).
                string? sub = null;
                if (root.TryGetProperty("oid", out var oid)) sub = oid.GetString();
                if (string.IsNullOrEmpty(sub) && root.TryGetProperty("sub", out var sm)) sub = sm.GetString();
                if (string.IsNullOrEmpty(sub) && root.TryGetProperty("id", out var idm)) sub = idm.GetString();
                if (string.IsNullOrEmpty(sub)) return null;
                string? email = null;
                if (root.TryGetProperty("email", out var em)) email = em.GetString();
                if (string.IsNullOrWhiteSpace(email) && root.TryGetProperty("mail", out var mail)) email = mail.GetString();
                if (string.IsNullOrWhiteSpace(email) && root.TryGetProperty("userPrincipalName", out var upn)) email = upn.GetString();
                string? name = null;
                if (root.TryGetProperty("name", out var nm)) name = nm.GetString();
                if (string.IsNullOrWhiteSpace(name) && root.TryGetProperty("displayName", out var dn)) name = dn.GetString();
                // Microsoft Graph exposes the work-account verification
                // out-of-band; for now treat returned email as
                // unverified pending a magic-link confirmation, matching
                // the GitHub path.
                return new OAuthUserInfo(sub, email, false, name);
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    private static (string AuthorizeEndpoint, string TokenEndpoint, string UserInfoEndpoint, string DefaultScopes)
        ResolveProviderEndpoints(string provider, OAuthProviderOptions opts)
    {
        if (provider.Equals("google", StringComparison.OrdinalIgnoreCase))
        {
            return (
                string.IsNullOrEmpty(opts.AuthorizationEndpoint) ? "https://accounts.google.com/o/oauth2/v2/auth" : opts.AuthorizationEndpoint,
                string.IsNullOrEmpty(opts.TokenEndpoint) ? "https://oauth2.googleapis.com/token" : opts.TokenEndpoint,
                string.IsNullOrEmpty(opts.UserInfoEndpoint) ? "https://www.googleapis.com/oauth2/v3/userinfo" : opts.UserInfoEndpoint,
                "openid profile email");
        }
        if (provider.Equals("github", StringComparison.OrdinalIgnoreCase))
        {
            return (
                string.IsNullOrEmpty(opts.AuthorizationEndpoint) ? "https://github.com/login/oauth/authorize" : opts.AuthorizationEndpoint,
                string.IsNullOrEmpty(opts.TokenEndpoint) ? "https://github.com/login/oauth/access_token" : opts.TokenEndpoint,
                string.IsNullOrEmpty(opts.UserInfoEndpoint) ? "https://api.github.com/user" : opts.UserInfoEndpoint,
                "read:user user:email");
        }
        if (provider.Equals("microsoft", StringComparison.OrdinalIgnoreCase))
        {
            // Phase K Wave 3 — Bishop. Microsoft v2.0 endpoints. The
            // tenant segment is interpolated from `opts.TenantId`
            // (default `common`). Operators can also pin the full URL
            // via the per-endpoint overrides, which take precedence.
            var tenant = string.IsNullOrWhiteSpace(opts.TenantId) ? "common" : opts.TenantId;
            return (
                string.IsNullOrEmpty(opts.AuthorizationEndpoint)
                    ? $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize"
                    : opts.AuthorizationEndpoint,
                string.IsNullOrEmpty(opts.TokenEndpoint)
                    ? $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token"
                    : opts.TokenEndpoint,
                string.IsNullOrEmpty(opts.UserInfoEndpoint)
                    ? "https://graph.microsoft.com/oidc/userinfo"
                    : opts.UserInfoEndpoint,
                "openid profile email User.Read");
        }
        return (opts.AuthorizationEndpoint, opts.TokenEndpoint, opts.UserInfoEndpoint, opts.Scopes);
    }

    /// <summary>Generates a CSRF state nonce.</summary>
    public static string GenerateState()
    {
        Span<byte> buf = stackalloc byte[32];
        RandomNumberGenerator.Fill(buf);
        return Base64UrlEncode(buf);
    }

    /// <summary>Constant-time string comparison for state validation.</summary>
    public static bool ConstantTimeEquals(string a, string b)
    {
        if (a is null || b is null) return false;
        if (a.Length != b.Length) return false;
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
            case 0: break;
            default: throw new FormatException("invalid base64url length");
        }
        return Convert.FromBase64String(padded);
    }
}

/// <summary>Phase J Wave 8 — provider-agnostic user info returned by <see cref="OAuthService.ExchangeAndFetchUserInfoAsync"/>.</summary>
public sealed record OAuthUserInfo(string Subject, string? Email, bool EmailVerified, string? DisplayName);
