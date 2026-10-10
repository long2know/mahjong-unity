using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 1 — OAuth provider health probe. Issues a single HEAD/GET
/// against each configured provider's OIDC discovery endpoint
/// (<c>/.well-known/openid-configuration</c> for Google; the
/// <c>/zen</c> liveness endpoint for GitHub, which has no public OIDC
/// discovery surface) and caches the result for the configured TTL.
///
/// <para>The probe is best-effort — every transport failure is recorded
/// in the cached result with <c>Healthy = false</c> + the exception
/// message, but never thrown to the caller. The <c>/health</c> JSON
/// endpoint surfaces the per-provider block so operators can detect
/// outages without log-tail surveillance.</para>
///
/// <para>The <c>verify-oauth</c> CLI mode (<c>dotnet run -- verify-oauth</c>)
/// also calls this service then prints the result; exit code is 0
/// only when every enabled+configured provider returns healthy.</para>
/// </summary>
public sealed class OAuthProviderHealthCheck
{
    /// <summary>Cache TTL for a provider probe result. 1 minute keeps
    /// the surface fresh without hammering provider endpoints when
    /// <c>/health</c> is polled by a load balancer.</summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(1);

    private readonly IOptions<AuthOptions> _authOptions;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<OAuthProviderHealthCheck> _logger;
    private readonly Dictionary<string, CachedResult> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public OAuthProviderHealthCheck(
        IOptions<AuthOptions> authOptions,
        ILogger<OAuthProviderHealthCheck> logger,
        IHttpClientFactory? httpClientFactory = null,
        IConfiguration? configuration = null)
    {
        _authOptions = authOptions;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    /// <summary>Configurable cache TTL (tests can dial down).</summary>
    public TimeSpan CacheTtl { get; set; } = DefaultCacheTtl;

    /// <summary>
    /// When true, <see cref="ProbeAllAsync"/> + <see cref="ProbeAsync"/>
    /// short-circuit the live HTTP call and surface a synthetic
    /// <c>healthy=true</c> result. Tests + air-gapped environments turn
    /// this on via <c>Authentication:HealthCheck:SkipDiscovery=true</c>;
    /// production should leave it off.
    /// </summary>
    public bool SkipDiscovery
    {
        get
        {
            if (_skipDiscoveryOverride.HasValue) return _skipDiscoveryOverride.Value;
            return _configuration?.GetValue<bool>("Authentication:HealthCheck:SkipDiscovery") ?? false;
        }
        set => _skipDiscoveryOverride = value;
    }
    private bool? _skipDiscoveryOverride;

    /// <summary>
    /// Returns the health snapshot for every configured provider. Only
    /// providers with <c>Enabled = true</c> and a populated
    /// <c>ClientId</c>+<c>ClientSecret</c> are probed; disabled
    /// providers are omitted entirely.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ProviderHealth>> ProbeAllAsync(CancellationToken ct = default)
    {
        var opts = _authOptions.Value;
        var result = new Dictionary<string, ProviderHealth>(StringComparer.OrdinalIgnoreCase);
        if (IsConfigured(opts.Google))
        {
            result["google"] = await ProbeAsync("google",
                "https://accounts.google.com/.well-known/openid-configuration", ct);
        }
        if (IsConfigured(opts.GitHub))
        {
            result["github"] = await ProbeAsync("github",
                "https://api.github.com/zen", ct);
        }
        // Phase K Wave 3 — Bishop. Microsoft probe targets the v2.0
        // discovery document on the configured tenant (default `common`).
        if (IsConfigured(opts.Microsoft))
        {
            var tenant = string.IsNullOrWhiteSpace(opts.Microsoft.TenantId) ? "common" : opts.Microsoft.TenantId;
            result["microsoft"] = await ProbeAsync("microsoft",
                $"https://login.microsoftonline.com/{tenant}/v2.0/.well-known/openid-configuration", ct);
        }
        return result;
    }

    /// <summary>Single-provider variant used by tests + the CLI.</summary>
    public async Task<ProviderHealth> ProbeAsync(string providerKey, string discoveryUrl, CancellationToken ct = default)
    {
        // Phase K Wave 1 — honour the SkipDiscovery knob (test + air-gapped
        // env). We still emit an entry so /health.oauth.providers carries
        // the configured-provider list; the discovery field is "skipped"
        // so operators reading the trail can tell it didn't actually
        // round-trip.
        if (SkipDiscovery)
        {
            return new ProviderHealth(providerKey, true, 0, null) { Discovery = "skipped" };
        }
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(providerKey, out var cached) && DateTime.UtcNow - cached.At <= CacheTtl)
            {
                return cached.Result;
            }
            var probed = await DoProbeAsync(providerKey, discoveryUrl, ct);
            _cache[providerKey] = new CachedResult(probed, DateTime.UtcNow);
            return probed;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ProviderHealth> DoProbeAsync(string providerKey, string discoveryUrl, CancellationToken ct)
    {
        try
        {
            using var http = _httpClientFactory?.CreateClient("oauth-health") ?? new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            // GitHub's /zen endpoint requires a User-Agent header per its API contract.
            if (!http.DefaultRequestHeaders.UserAgent.Any())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("mahjong-autotable-health/1.0");
            }

            var resp = await http.GetAsync(discoveryUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new ProviderHealth(providerKey, false, (int)resp.StatusCode, $"HTTP {(int)resp.StatusCode}") { Discovery = "fail" };
            }
            // For Google, sanity-check the JSON contains the expected
            // "issuer" key so we know the well-known doc is well-formed.
            if (providerKey.Equals("google", StringComparison.OrdinalIgnoreCase))
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("issuer", out _))
                    {
                        return new ProviderHealth(providerKey, false, (int)resp.StatusCode, "discovery doc missing 'issuer'") { Discovery = "fail" };
                    }
                }
                catch (JsonException jex)
                {
                    return new ProviderHealth(providerKey, false, (int)resp.StatusCode, "discovery doc parse failed: " + jex.Message) { Discovery = "fail" };
                }
            }
            return new ProviderHealth(providerKey, true, (int)resp.StatusCode, null) { Discovery = "ok" };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OAuth provider health probe for {Provider} failed.", providerKey);
            return new ProviderHealth(providerKey, false, 0, ex.Message) { Discovery = "fail" };
        }
    }

    private static bool IsConfigured(OAuthProviderOptions o) =>
        o.Enabled
        && !string.IsNullOrWhiteSpace(o.ClientId)
        && !string.IsNullOrWhiteSpace(o.ClientSecret);

    private sealed record CachedResult(ProviderHealth Result, DateTime At);
}

/// <summary>
/// Phase K Wave 1 — single provider health entry. Serialised into the
/// <c>/health</c> JSON payload under <c>oauth.providers.{key}</c>.
/// </summary>
/// <param name="Provider">Provider key (<c>google</c> / <c>github</c>).</param>
/// <param name="Healthy">True iff the probe returned a 2xx and (for
/// OIDC discovery) a parseable doc.</param>
/// <param name="StatusCode">HTTP status code returned (0 on transport
/// failure).</param>
/// <param name="Error">Error string when unhealthy; null otherwise.</param>
public sealed record ProviderHealth(string Provider, bool Healthy, int StatusCode, string? Error)
{
    /// <summary>Discovery probe state — <c>"ok"</c> on success,
    /// <c>"fail"</c> on transport / parse failure, <c>"skipped"</c> when
    /// the <c>Authentication:HealthCheck:SkipDiscovery</c> knob is set.</summary>
    public string Discovery { get; init; } = "ok";
}
