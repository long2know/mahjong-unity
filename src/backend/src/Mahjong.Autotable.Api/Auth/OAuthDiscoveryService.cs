using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 2 — Bishop (Backend). Configuration knobs for the
/// <see cref="OAuthDiscoveryService"/> cache. Bound from the
/// <c>Authentication:Discovery</c> section. Defaults follow the brief's
/// 6h TTL + 24h stale-mark cadence; the
/// <see cref="SkipNetwork"/> flag stays opt-in so the xUnit harness can
/// disable the upstream fetch.
/// </summary>
public sealed class OAuthDiscoveryOptions
{
    /// <summary>How long a fetched document stays "live" before the
    /// background refresher reaches out again. 6h matches the brief.</summary>
    public int CacheTtlSeconds { get; set; } = 6 * 60 * 60;

    /// <summary>Once the cached document is older than this, the
    /// provider flips to <see cref="OAuthDiscoveryStatus.Stale"/> +
    /// the /health envelope's `discovery` block marks the provider
    /// unhealthy. Default 24h per the brief.</summary>
    public int StaleThresholdHours { get; set; } = 24;

    /// <summary>Background refresh cadence. 6h ⇒ aligns with the
    /// CacheTtl so a healthy cache always has &lt;1 refresh-window
    /// of staleness on average.</summary>
    public int RefreshIntervalHours { get; set; } = 6;

    /// <summary>
    /// Phase K Wave 3 — Bishop. Optional finer-grained refresh knob in
    /// SECONDS, taking precedence over <see cref="RefreshIntervalHours"/>
    /// when non-zero. Lets operators dial the cadence below the 1h
    /// floor implied by the Hours knob (e.g. set
    /// <c>RefreshIntervalSeconds = 60</c> in integration / staging
    /// environments to verify the refresher round-trips without
    /// waiting six hours). Default 0 ⇒ fall back to
    /// <see cref="RefreshIntervalHours"/>.
    /// </summary>
    public int RefreshIntervalSeconds { get; set; } = 0;

    /// <summary>When true, the service NEVER reaches out to the upstream
    /// — it falls straight back to its cached value (or hardcoded
    /// constants). Xunit + air-gapped environments set this via
    /// <c>Authentication:Discovery:SkipNetwork=true</c>.</summary>
    public bool SkipNetwork { get; set; } = false;
}

/// <summary>Phase K Wave 2 — health status flavour for a single
/// provider's cached discovery document. Operators read this off the
/// /health envelope; the discovery service surfaces it as a string
/// alongside <see cref="OAuthDiscoveryService.GetStatusAsync"/>.</summary>
public enum OAuthDiscoveryStatus
{
    /// <summary>Cache has never been populated for this provider.</summary>
    Unknown = 0,

    /// <summary>Document is from the network within the last TTL.</summary>
    Live = 1,

    /// <summary>Document is from cache; last live fetch failed but the
    /// cached doc is still within the stale threshold.</summary>
    Cached = 2,

    /// <summary>Cached document exceeds the stale threshold —
    /// operator MUST investigate. /health flips unhealthy.</summary>
    Stale = 3,
}

/// <summary>
/// Phase K Wave 2 — Bishop (Backend). Cached snapshot of a single
/// OIDC discovery document. The four fields Vasquez's contract test
/// asserts (issuer, authorization_endpoint, token_endpoint,
/// userinfo_endpoint) round-trip through this record.
/// </summary>
public sealed record OAuthDiscoveryDocument(
    string Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string UserinfoEndpoint,
    string? JwksUri = null);

/// <summary>
/// Phase K Wave 2 — Bishop (Backend). Caches per-provider OIDC
/// discovery documents with a 6h TTL and falls back to the previously
/// cached value when the upstream fetch fails. GitHub has no OIDC
/// discovery surface so the service returns hardcoded constants —
/// <see cref="GithubAuthorizationEndpoint"/> et al. are public + const
/// so Vasquez's contract test can pin them.
///
/// <para>Sibling to <see cref="OAuthProviderHealthCheck"/>: the health
/// check is the quick liveness probe (HEAD/GET, 1m TTL), this is the
/// long-lived cache (6h TTL) that owns the endpoint URLs Bishop's
/// <see cref="OAuthService"/> dispatches into. Both can co-exist; the
/// health check just narrows the failure surface.</para>
/// </summary>
public sealed class OAuthDiscoveryService
{
    /// <summary>GitHub's canonical authorization endpoint — github.com
    /// has no `/.well-known/openid-configuration` document, so the URL
    /// is pinned here as a string constant. Vasquez's contract test
    /// asserts at least one `github.com` literal exists in the assembly.</summary>
    public const string GithubAuthorizationEndpoint = "https://github.com/login/oauth/authorize";

    /// <summary>GitHub's canonical token endpoint.</summary>
    public const string GithubTokenEndpoint = "https://github.com/login/oauth/access_token";

    /// <summary>GitHub's canonical user-info endpoint.</summary>
    public const string GithubUserinfoEndpoint = "https://api.github.com/user";

    /// <summary>GitHub's canonical issuer string for the discovery shape.</summary>
    public const string GithubIssuer = "https://github.com";

    /// <summary>Default Google OIDC discovery URL. Cached for the
    /// configured TTL when the upstream returns 200 + valid JSON.</summary>
    public const string GoogleDiscoveryUrl = "https://accounts.google.com/.well-known/openid-configuration";

    /// <summary>
    /// Phase K Wave 3 — Bishop. Default Microsoft v2.0 OIDC discovery
    /// URL. <c>common</c> is the multi-tenant + personal-account
    /// metadata endpoint; per-tenant overrides flow through
    /// <see cref="OAuthProviderOptions.TenantId"/> and the runtime
    /// re-points <see cref="FetchMicrosoftAsync"/> at the matching
    /// <c>https://login.microsoftonline.com/{tenant}/v2.0/.well-known/openid-configuration</c>
    /// when the tenant is non-default.
    /// </summary>
    public const string MicrosoftDiscoveryUrl = "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration";

    /// <summary>Phase K Wave 3 — canonical Microsoft v2.0 issuer
    /// pattern used as a fallback when the discovery document is
    /// unparseable.</summary>
    public const string MicrosoftIssuer = "https://login.microsoftonline.com/common/v2.0";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly OAuthDiscoveryOptions _options;
    private readonly ILogger<OAuthDiscoveryService> _logger;
    private readonly ConcurrentDictionary<string, CachedEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public OAuthDiscoveryService(
        IOptions<OAuthDiscoveryOptions> options,
        ILogger<OAuthDiscoveryService> logger,
        IHttpClientFactory? httpClientFactory = null)
    {
        _options = options.Value;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Number of cached entries — public so an operator
    /// dashboard or health probe can spot a cold cache.</summary>
    public int CachedProviderCount => _cache.Count;

    /// <summary>Last-fetch timestamp across all providers (UTC), or
    /// <see cref="DateTime.MinValue"/> when the cache is cold.</summary>
    public DateTime LastFetchAt
        => _cache.Values.Count == 0 ? DateTime.MinValue : _cache.Values.Max(v => v.FetchedAtUtc);

    /// <summary>
    /// Fetch (or return cached) discovery document for the supplied
    /// provider. Returns null when the provider is unknown AND the cache
    /// has no entry. Never throws — every transport / parsing failure
    /// surfaces via the cached <see cref="OAuthDiscoveryStatus"/>.
    /// </summary>
    public async Task<OAuthDiscoveryDocument?> GetAsync(string provider, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider)) return null;
        var key = provider.Trim().ToLowerInvariant();

        // Cache hit + within TTL → no network call.
        if (_cache.TryGetValue(key, out var cached))
        {
            var age = DateTime.UtcNow - cached.FetchedAtUtc;
            if (age < TimeSpan.FromSeconds(Math.Max(1, _options.CacheTtlSeconds)))
            {
                return cached.Document;
            }
        }

        return key switch
        {
            "github" => UpsertGitHub(),
            "google" => await FetchGoogleAsync(ct),
            "microsoft" => await FetchMicrosoftAsync(ct),
            _ => cached?.Document,
        };
    }

    /// <summary>
    /// Force-refresh every known provider. Best-effort: each refresh
    /// runs independently so a Google outage does NOT prevent the
    /// GitHub stub from being upserted.
    /// </summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        _ = UpsertGitHub();
        try { _ = await FetchGoogleAsync(ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "OAuth discovery refresh: Google fetch failed (cache retained)"); }
        // Phase K Wave 3 — Bishop. Microsoft refresh runs in the same
        // best-effort sweep; an Entra outage does NOT bleed into the
        // other providers' freshness.
        try { _ = await FetchMicrosoftAsync(ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "OAuth discovery refresh: Microsoft fetch failed (cache retained)"); }
    }

    /// <summary>
    /// Per-provider status snapshot. Returns
    /// <see cref="OAuthDiscoveryStatus.Unknown"/> when the cache is
    /// cold, <see cref="OAuthDiscoveryStatus.Live"/> within TTL,
    /// <see cref="OAuthDiscoveryStatus.Cached"/> when over TTL but
    /// under the stale-threshold, and
    /// <see cref="OAuthDiscoveryStatus.Stale"/> beyond that.
    /// </summary>
    public OAuthDiscoveryStatus GetStatus(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return OAuthDiscoveryStatus.Unknown;
        if (!_cache.TryGetValue(provider.Trim().ToLowerInvariant(), out var cached))
            return OAuthDiscoveryStatus.Unknown;
        var age = DateTime.UtcNow - cached.FetchedAtUtc;
        if (age < TimeSpan.FromSeconds(Math.Max(1, _options.CacheTtlSeconds)))
            return OAuthDiscoveryStatus.Live;
        if (age < TimeSpan.FromHours(Math.Max(1, _options.StaleThresholdHours)))
            return OAuthDiscoveryStatus.Cached;
        return OAuthDiscoveryStatus.Stale;
    }

    /// <summary>Async wrapper for <see cref="GetStatus"/>. Mirror of the
    /// reflection probe Vasquez's contract test issues.</summary>
    public Task<OAuthDiscoveryStatus> GetStatusAsync(string provider, CancellationToken ct = default)
        => Task.FromResult(GetStatus(provider));

    private OAuthDiscoveryDocument UpsertGitHub()
    {
        var doc = new OAuthDiscoveryDocument(
            Issuer: GithubIssuer,
            AuthorizationEndpoint: GithubAuthorizationEndpoint,
            TokenEndpoint: GithubTokenEndpoint,
            UserinfoEndpoint: GithubUserinfoEndpoint,
            JwksUri: null);
        _cache["github"] = new CachedEntry(doc, DateTime.UtcNow, LastError: null);
        return doc;
    }

    private async Task<OAuthDiscoveryDocument?> FetchGoogleAsync(CancellationToken ct)
    {
        if (_options.SkipNetwork || _httpClientFactory is null)
        {
            // Test-mode / air-gapped: keep whatever is in the cache.
            return _cache.TryGetValue("google", out var cached) ? cached.Document : null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("oauth");
            client.Timeout = TimeSpan.FromSeconds(10);
            using var resp = await client.GetAsync(GoogleDiscoveryUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                MarkLastError("google", $"HTTP {(int)resp.StatusCode}");
                return _cache.TryGetValue("google", out var cached) ? cached.Document : null;
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<OidcDiscoveryPayload>(body);
            if (parsed is null
                || string.IsNullOrWhiteSpace(parsed.AuthorizationEndpoint)
                || string.IsNullOrWhiteSpace(parsed.TokenEndpoint))
            {
                MarkLastError("google", "missing-fields");
                return _cache.TryGetValue("google", out var cached) ? cached.Document : null;
            }
            var doc = new OAuthDiscoveryDocument(
                Issuer: parsed.Issuer ?? "https://accounts.google.com",
                AuthorizationEndpoint: parsed.AuthorizationEndpoint!,
                TokenEndpoint: parsed.TokenEndpoint!,
                UserinfoEndpoint: parsed.UserinfoEndpoint ?? string.Empty,
                JwksUri: parsed.JwksUri);
            _cache["google"] = new CachedEntry(doc, DateTime.UtcNow, LastError: null);
            return doc;
        }
        catch (Exception ex)
        {
            MarkLastError("google", ex.Message);
            _logger.LogDebug(ex, "Google OIDC discovery fetch failed; cached doc retained.");
            return _cache.TryGetValue("google", out var cached) ? cached.Document : null;
        }
    }

    /// <summary>
    /// Phase K Wave 3 — Bishop. Mirror of <see cref="FetchGoogleAsync"/>
    /// for the Microsoft v2.0 endpoint. Microsoft publishes a per-tenant
    /// discovery document; we always pull <c>common</c> here so the
    /// cache stays cheap and the per-tenant tenant-id substitution
    /// happens at authorize-URL build time in
    /// <see cref="OAuthService.ResolveProviderEndpoints"/>.
    /// </summary>
    private async Task<OAuthDiscoveryDocument?> FetchMicrosoftAsync(CancellationToken ct)
    {
        if (_options.SkipNetwork || _httpClientFactory is null)
        {
            return _cache.TryGetValue("microsoft", out var cached) ? cached.Document : null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("oauth");
            client.Timeout = TimeSpan.FromSeconds(10);
            using var resp = await client.GetAsync(MicrosoftDiscoveryUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                MarkLastError("microsoft", $"HTTP {(int)resp.StatusCode}");
                return _cache.TryGetValue("microsoft", out var cached) ? cached.Document : null;
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<OidcDiscoveryPayload>(body);
            if (parsed is null
                || string.IsNullOrWhiteSpace(parsed.AuthorizationEndpoint)
                || string.IsNullOrWhiteSpace(parsed.TokenEndpoint))
            {
                MarkLastError("microsoft", "missing-fields");
                return _cache.TryGetValue("microsoft", out var cached) ? cached.Document : null;
            }
            var doc = new OAuthDiscoveryDocument(
                Issuer: parsed.Issuer ?? MicrosoftIssuer,
                AuthorizationEndpoint: parsed.AuthorizationEndpoint!,
                TokenEndpoint: parsed.TokenEndpoint!,
                UserinfoEndpoint: parsed.UserinfoEndpoint ?? "https://graph.microsoft.com/oidc/userinfo",
                JwksUri: parsed.JwksUri);
            _cache["microsoft"] = new CachedEntry(doc, DateTime.UtcNow, LastError: null);
            return doc;
        }
        catch (Exception ex)
        {
            MarkLastError("microsoft", ex.Message);
            _logger.LogDebug(ex, "Microsoft OIDC discovery fetch failed; cached doc retained.");
            return _cache.TryGetValue("microsoft", out var cached) ? cached.Document : null;
        }
    }

    private void MarkLastError(string provider, string error)
    {
        if (_cache.TryGetValue(provider, out var existing))
        {
            _cache[provider] = existing with { LastError = error };
        }
    }

    private sealed record CachedEntry(OAuthDiscoveryDocument Document, DateTime FetchedAtUtc, string? LastError);

    private sealed class OidcDiscoveryPayload
    {
        [JsonPropertyName("issuer")] public string? Issuer { get; set; }
        [JsonPropertyName("authorization_endpoint")] public string? AuthorizationEndpoint { get; set; }
        [JsonPropertyName("token_endpoint")] public string? TokenEndpoint { get; set; }
        [JsonPropertyName("userinfo_endpoint")] public string? UserinfoEndpoint { get; set; }
        [JsonPropertyName("jwks_uri")] public string? JwksUri { get; set; }
    }
}
