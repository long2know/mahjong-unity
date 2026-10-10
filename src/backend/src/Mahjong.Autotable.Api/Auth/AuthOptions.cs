namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase J Wave 8 — root configuration section for the Wave 8 auth surface.
///
/// <para>Bound from the <c>Authentication</c> section of <c>appsettings.json</c>:</para>
/// <code>
/// "Authentication": {
///   "Google":          { "Enabled": true, "ClientId": "…", "ClientSecret": "…" },
///   "GitHub":          { "Enabled": true, "ClientId": "…", "ClientSecret": "…" },
///   "EmailMagicLink":  { "Enabled": true, "BaseUrl": "https://…" }
/// }
/// </code>
///
/// <para>A provider is treated as "configured" when its
/// <see cref="OAuthProviderOptions.Enabled"/> is true AND it has a non-empty
/// <see cref="OAuthProviderOptions.ClientId"/> + <see cref="OAuthProviderOptions.ClientSecret"/>.
/// Configured providers surface in <c>GET /api/auth/providers</c>. The
/// dev-login fallback is registered separately when
/// <c>IHostEnvironment.IsDevelopment()</c> is true.</para>
/// </summary>
public sealed class AuthOptions
{
    /// <summary>Google OAuth provider config.</summary>
    public OAuthProviderOptions Google { get; set; } = new();

    /// <summary>GitHub OAuth provider config.</summary>
    public OAuthProviderOptions GitHub { get; set; } = new();

    /// <summary>
    /// Phase K Wave 3 — Bishop. Microsoft (Azure AD / Entra ID) OAuth
    /// provider config. The Microsoft endpoints accept the same
    /// authorization-code + PKCE flow used by Google; the
    /// <see cref="OAuthProviderOptions.TenantId"/> knob inlines the
    /// tenant segment of the authorize/token URLs (default
    /// <c>common</c> ⇒ multi-tenant + personal accounts).
    /// </summary>
    public OAuthProviderOptions Microsoft { get; set; } = new();

    /// <summary>Email magic-link config.</summary>
    public EmailMagicLinkOptions EmailMagicLink { get; set; } = new();

    /// <summary>Session cookie lifetime. Default 30 days.</summary>
    public int SessionLifetimeDays { get; set; } = 30;

    /// <summary>Magic-link token TTL in minutes. Default 15.</summary>
    public int MagicLinkTtlMinutes { get; set; } = 15;

    /// <summary>
    /// Phase K Wave 1 — HMAC signing key for OAuth state tokens.
    /// When empty, the application mints a per-process random key
    /// (logged once at startup). Production deployments should pin
    /// a stable secret so a rolling restart doesn't invalidate
    /// in-flight authorize redirects. Minimum recommended length:
    /// 32 bytes (256 bits). Hex / base64 / arbitrary string all work
    /// — the secret is hashed into a 256-bit key on use.
    /// </summary>
    public string StateSigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Phase K Wave 4 — Bishop. Ordered fallback list of HMAC-SHA256
    /// signing keys used by <see cref="JwtIssuingService"/> +
    /// <see cref="JwtValidationService"/>. Position 0 is the ACTIVE
    /// signer (new tokens are minted with this key); positions 1..N
    /// are PREVIOUS keys accepted for validation only. See
    /// docs/jwt-rotation.md §2 for the operator runbook.
    ///
    /// <para>Bound from <c>Authentication:JwtSigningKeys</c> as a
    /// string array. Each entry is the raw key material — a value of
    /// at least 32 bytes is recommended (HMAC-SHA256 lower bound).
    /// Production deployments source these from ESO / SSM /
    /// k8s-Secret; appsettings.json carries the empty schema only.</para>
    ///
    /// <para>When the array is empty AND no legacy
    /// <see cref="JwtSigningKey"/> is configured, the host process
    /// mints a per-process random key + logs a warning so
    /// development workflows keep working without operator
    /// intervention.</para>
    /// </summary>
    public string[] JwtSigningKeys { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Phase K Wave 4 — Bishop. Legacy singular HMAC signing key
    /// retained for one-wave backward compatibility with pre-Wave-3
    /// deployments. When <see cref="JwtSigningKeys"/> is non-empty,
    /// this value is ignored. When the array is empty AND this is
    /// set, the host treats it as the active signer. Slated for
    /// removal in Wave 5 per docs/jwt-rotation.md §7.
    /// </summary>
    public string JwtSigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Phase K Wave 6 — Bishop. Active JWT signing algorithm. Two
    /// values supported during the Phase-L bring-up:
    /// <list type="bullet">
    ///   <item><c>HS256</c> (default) — HMAC-SHA256, keys sourced from
    ///         <see cref="JwtSigningKeys"/>. JWKS endpoint cannot publish
    ///         the shared secret so the endpoint returns 404 with a
    ///         briefly-cacheable negative.</item>
    ///   <item><c>RS256</c> — RSASSA-PKCS1-v1_5 SHA-256, private keys
    ///         (PEM-encoded) sourced from <see cref="JwtRsaKeys"/>. JWKS
    ///         endpoint publishes the matching public-key set so
    ///         downstream verifiers can resolve the kid header without
    ///         sharing any secret material.</item>
    /// </list>
    /// Wave 6 ships the surface; production flip from HS256→RS256 is a
    /// later operational step. <see cref="JwtAlgorithmStartupLogger"/>
    /// emits a startup warning whenever the resolved algorithm is HS256
    /// to encourage the migration.
    /// </summary>
    public string JwtAlgorithm { get; set; } = "HS256";

    /// <summary>
    /// Phase K Wave 6 — Bishop. PEM-encoded RSA private keys used when
    /// <see cref="JwtAlgorithm"/> is <c>RS256</c>. Position 0 is the
    /// ACTIVE signer (new tokens are minted with this key); positions
    /// 1..N are PREVIOUS keys accepted for validation only — the same
    /// fallback shape as <see cref="JwtSigningKeys"/>.
    /// <para>Each entry must be a PEM blob in either PKCS#1
    /// (<c>-----BEGIN RSA PRIVATE KEY-----</c>) or PKCS#8
    /// (<c>-----BEGIN PRIVATE KEY-----</c>) encoding. A minimum modulus
    /// length of 2048 bits is recommended. The <c>kid</c> is derived
    /// deterministically from the SPKI of the matching public key so
    /// two processes loading the same key bytes derive the same
    /// identifier — JWKS round-trips without an external key catalog.</para>
    /// </summary>
    public string[] JwtRsaKeys { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Phase K Wave 7 — Bishop. Issuer URL stamped into the
    /// <c>iss</c> claim of RS256-signed tokens AND advertised as the
    /// <c>issuer</c> field on the OIDC discovery document. When empty
    /// the discovery endpoint falls back to the request's own
    /// scheme+host (matches the Wave-6 behaviour); when set the
    /// configured value wins so a service behind a reverse proxy can
    /// advertise its public origin without depending on
    /// <c>X-Forwarded-Host</c>.
    /// <para>The OIDC <b>hard contract</b> in Wave 7 requires this
    /// field non-empty when <see cref="JwtAlgorithm"/> is
    /// <c>RS256</c> for <c>/.well-known/openid-configuration</c> to
    /// hard-assert 200. With HS256 the field is ignored — the
    /// discovery endpoint returns 404 regardless.</para>
    /// </summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// Phase K Wave 9 — Bishop. JWT rotation grace period in
    /// seconds. The canonical <c>docs/jwt-rotation.md §3</c> cadence
    /// keeps the prior 2 keys in <see cref="JwtRsaKeys"/> /
    /// <see cref="JwtSigningKeys"/> for a multi-day grace window;
    /// this knob encodes that window in seconds so the W9
    /// <c>IRotationCadenceValidator</c> can hard-assert
    /// <c>JwksCacheTtlSeconds &lt;= RotationGracePeriodSeconds / 2</c>
    /// at startup.
    /// <para>Default 600 seconds (10 minutes) — short enough that
    /// the JWKS-TTL invariant is trivially satisfied at the W8 60s
    /// TTL, long enough that the 1 h JWT lifetime ceiling is well
    /// covered. Operators running the canonical
    /// <c>docs/jwt-rotation.md §4</c> 30-day grace window set this
    /// to 2592000 in production.</para>
    /// </summary>
    public int RotationGracePeriodSeconds { get; set; } = 600;

    /// <summary>
    /// Phase K Wave 12 — Bishop. Staged-rotation overlap window
    /// in days. During this window the issuer continues to mint
    /// new tokens with the active key (index 0) but the
    /// validator accepts any of the configured keys in
    /// <see cref="JwtSigningKeys"/> / <see cref="JwtRsaKeys"/>.
    /// Defaults to 30 days per <c>docs/jwt-rotation.md §13</c>.
    /// </summary>
    public int RotationOverlapDays { get; set; } = 30;

    /// <summary>
    /// Phase K Wave 12 — Bishop. Optional UTC timestamp when the
    /// current staged rotation window opened. When set, the
    /// <see cref="JwtStagedRotationPolicy"/> reports the
    /// remaining days in the window. Null = no rotation in
    /// progress.
    /// </summary>
    public DateTime? RotationStartUtc { get; set; }

    /// <summary>
    /// Phase K Wave 4 — Bishop. Canonical sub-section for per-provider
    /// OAuth config. Replaces the flat <see cref="Microsoft"/> /
    /// <see cref="Google"/> / <see cref="GitHub"/> properties. When
    /// the legacy flat path is also populated, a startup warning is
    /// emitted; the canonical path always wins. See
    /// docs/oauth-production-setup.md §Microsoft for the migration.
    /// </summary>
    public OAuthProvidersOptions Providers { get; set; } = new();

    /// <summary>
    /// Phase K Wave 11 — Bishop. Allowlist of client credentials
    /// accepted by the RFC 7662 token-introspection endpoint
    /// (<c>POST /api/auth/introspect</c>). Each entry binds a
    /// <c>ClientId</c> + <c>ClientSecret</c> pair used to gate
    /// HTTP Basic auth on the endpoint. Empty list = endpoint
    /// returns 401 for every request (introspection effectively
    /// disabled).
    ///
    /// <para>The endpoint is meant for programmatic verifiers —
    /// the Janus mountpoint health probe, bot frameworks, etc. —
    /// that need to confirm a token's <c>active</c> status without
    /// implementing the full JWT validation themselves. Documented
    /// in <c>docs/oauth.md §7</c>.</para>
    /// </summary>
    public IntrospectionClient[] IntrospectionClients { get; set; } = Array.Empty<IntrospectionClient>();

    /// <summary>
    /// Phase K Wave 11 — Bishop. Single allowlisted client for the
    /// introspection endpoint. <see cref="ClientSecret"/> is
    /// constant-time compared at validation; the surface accepts
    /// either a literal secret or the <c>env:VAR_NAME</c>
    /// indirection so secrets stay out of the appsettings.json
    /// blob (the indirection is resolved at startup time).
    /// </summary>
    public sealed class IntrospectionClient
    {
        /// <summary>Client identifier — surfaced as the
        /// <c>client_id</c> field in the introspection response.</summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>Shared secret. Accepts the <c>env:VAR_NAME</c>
        /// indirection to keep the secret out of the JSON
        /// blob.</summary>
        public string ClientSecret { get; set; } = string.Empty;

        /// <summary>Free-form scope label echoed back in the
        /// introspection response. Empty by default.</summary>
        public string Scope { get; set; } = string.Empty;

        /// <summary>Resolves the effective shared secret,
        /// expanding the <c>env:VAR_NAME</c> indirection when
        /// present. Empty string when unset.</summary>
        public string ResolveSecret()
        {
            if (string.IsNullOrEmpty(ClientSecret)) return string.Empty;
            if (ClientSecret.StartsWith("env:", StringComparison.Ordinal))
            {
                var varName = ClientSecret.Substring("env:".Length).Trim();
                if (string.IsNullOrEmpty(varName)) return string.Empty;
                return Environment.GetEnvironmentVariable(varName) ?? string.Empty;
            }
            return ClientSecret;
        }
    }
}

/// <summary>
/// Phase K Wave 4 — Bishop. Container for per-provider OAuth options
/// under the canonical <c>Authentication:Providers:{provider}</c>
/// configuration path. Only providers whose <see cref="OAuthProviderOptions.ClientId"/>
/// + <see cref="OAuthProviderOptions.ClientSecret"/> are populated
/// surface in <c>GET /api/auth/providers</c>; empty entries are
/// equivalent to "provider not configured".
/// </summary>
public sealed class OAuthProvidersOptions
{
    /// <summary>Google OAuth provider — canonical config path.</summary>
    public OAuthProviderOptions Google { get; set; } = new();

    /// <summary>GitHub OAuth provider — canonical config path.</summary>
    public OAuthProviderOptions GitHub { get; set; } = new();

    /// <summary>Microsoft Entra ID provider — canonical config path
    /// (replaces the legacy <c>Authentication:Microsoft:*</c> shape).</summary>
    public OAuthProviderOptions Microsoft { get; set; } = new();
}

/// <summary>
/// Phase J Wave 8 — per-provider OAuth config. The default authorize /
/// token / userinfo endpoints are hard-coded per provider in
/// <see cref="OAuthService"/> (Google's and GitHub's URLs are stable). The
/// only required tenant-specific values are <see cref="ClientId"/> +
/// <see cref="ClientSecret"/>, both of which are pulled from configuration
/// secrets (env var, k8s secret, user-secrets) — never committed.
/// </summary>
public sealed class OAuthProviderOptions
{
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Optional override for the OAuth authorize endpoint.
    /// Empty falls back to the provider-default in <see cref="OAuthService"/>.</summary>
    public string AuthorizationEndpoint { get; set; } = string.Empty;

    /// <summary>Optional override for the OAuth token endpoint.</summary>
    public string TokenEndpoint { get; set; } = string.Empty;

    /// <summary>Optional override for the userinfo endpoint.</summary>
    public string UserInfoEndpoint { get; set; } = string.Empty;

    /// <summary>Optional override for the requested OAuth scopes (space-delimited).</summary>
    public string Scopes { get; set; } = string.Empty;

    /// <summary>
    /// Phase K Wave 3 — Bishop. Tenant segment for Microsoft (Azure AD
    /// / Entra ID). Only consumed by the <c>microsoft</c> provider.
    /// Defaults to <c>common</c> (multi-tenant + personal Microsoft
    /// accounts). Set to a specific tenant GUID or domain to restrict
    /// to a single tenant. Ignored by every other provider.
    /// </summary>
    public string TenantId { get; set; } = "common";
}

/// <summary>
/// Phase J Wave 8 — email magic-link config.
/// </summary>
public sealed class EmailMagicLinkOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Public base URL that the magic-link verify path is appended to.
    /// Defaults to the request's own origin when unset.</summary>
    public string BaseUrl { get; set; } = string.Empty;
}

/// <summary>
/// Phase J Wave 8 — SMTP settings consumed by <see cref="SmtpEmailSender"/>.
/// When <see cref="Host"/> is empty the SMTP sender falls back to the
/// <see cref="LogEmailSender"/> stub so dev / test never need real SMTP.
/// </summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string User { get; set; } = string.Empty;
    public string Pass { get; set; } = string.Empty;
    public string From { get; set; } = "no-reply@mahjong-autotable.local";
    public bool UseSsl { get; set; } = true;
}
