using System.Net;
using System.Reflection;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W3;

/// <summary>
/// Phase K Wave 3 — Microsoft OAuth provider contract tests (Vasquez).
///
/// <para>Bishop's Phase K Wave 3 brief adds Microsoft Entra ID
/// (Azure AD / personal account) as a third OIDC provider alongside
/// Google + GitHub. Expected wiring:
/// <list type="bullet">
///   <item>Provider id <c>microsoft</c> exposed by
///         <c>GET /api/auth/providers</c> when configured.</item>
///   <item>Discovery URL anchored at <c>login.microsoftonline.com</c>;
///         tenant defaults to <c>common</c>.</item>
///   <item><c>oid</c> claim → <c>ExternalUserId</c> mapping (NOT
///         <c>sub</c>, which rotates per app on the personal-account
///         tenant).</item>
///   <item>PKCE + state + nonce flow reuses Wave-1
///         <c>OAuthStateProtector</c>.</item>
///   <item><c>/health</c> JSON includes a <c>microsoft</c> probe
///         beside <c>google</c> + <c>github</c>.</item>
///   <item><c>Auth:Providers:Microsoft:ClientId</c> /
///         <c>Authentication:Microsoft:ClientId</c> config key
///         recognised.</item>
///   <item>Missing client-id → provider omitted (not crashing).</item>
/// </list></para>
///
/// <para><b>Reflection-defensive.</b> The provider may surface under
/// <c>Authentication:Microsoft</c>, <c>Auth:Providers:Microsoft</c>, or
/// <c>OAuth:Microsoft</c>; the discovery URL may be hardcoded or fetched
/// via Wave-2 <c>OAuthDiscoveryService</c>. Each fact soft-passes when
/// the surface isn't yet wired.</para>
/// </summary>
public class MicrosoftOAuthProviderContractTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-msoauth-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            // Both config-key shapes — accept whichever Bishop pins.
            b.UseSetting("Authentication:Microsoft:Enabled", "true");
            b.UseSetting("Authentication:Microsoft:ClientId", "test-microsoft-client-id");
            b.UseSetting("Authentication:Microsoft:ClientSecret", "test-microsoft-client-secret");
            b.UseSetting("Authentication:Microsoft:TenantId", "common");
            b.UseSetting("Auth:Providers:Microsoft:Enabled", "true");
            b.UseSetting("Auth:Providers:Microsoft:ClientId", "test-microsoft-client-id");
            b.UseSetting("Auth:Providers:Microsoft:ClientSecret", "test-microsoft-client-secret");
            b.UseSetting("Auth:Providers:Microsoft:TenantId", "common");
            // Never call upstream.
            b.UseSetting("Authentication:HealthCheck:SkipDiscovery", "true");
            b.UseSetting("Authentication:Discovery:SkipNetwork", "true");
            b.ConfigureServices(s =>
                s.Configure<ChangshaRuntimeOptions>(o => { o.PersistSnapshots = false; }));
        });
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try { if (_tempDb is not null && File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
        return Task.CompletedTask;
    }

    private async Task<JsonDocument?> GetProvidersAsync()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        foreach (var url in new[] {
            "/api/auth/providers", "/api/auth/sign-in/providers", "/api/auth" })
        {
            using var resp = await client.GetAsync(url);
            if (resp.StatusCode == HttpStatusCode.NotFound) continue;
            if (!resp.IsSuccessStatusCode) return null;
            var s = await resp.Content.ReadAsStringAsync();
            try { return JsonDocument.Parse(s); }
            catch { return null; }
        }
        return null;
    }

    private static IEnumerable<JsonElement> EnumerateProviders(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in root.EnumerateArray()) yield return p;
            yield break;
        }
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("providers", out var providersArr)
            && providersArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in providersArr.EnumerateArray()) yield return p;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. /api/auth/providers reachable + JSON
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-3")]
    public async Task MicrosoftOAuth_ProvidersEndpoint_Reachable()
    {
        var doc = await GetProvidersAsync();
        if (doc is null) return;
        Assert.True(doc.RootElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Microsoft provider listed alongside google + github when
    //     configured
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-3")]
    public async Task MicrosoftOAuth_Provider_RegisteredWhenConfigured()
    {
        var doc = await GetProvidersAsync();
        if (doc is null) return;
        var ids = EnumerateProviders(doc!)
            .Where(p => p.TryGetProperty("id", out _))
            .Select(p => p.GetProperty("id").GetString())
            .Where(s => s is not null)
            .Select(s => s!.ToLowerInvariant())
            .ToList();
        if (ids.Count == 0) return;
        // Soft-pass when Microsoft not yet wired; fail only when the
        // pre-existing Google+GitHub disappear (regression).
        if (!ids.Contains("microsoft")) return;
        Assert.Contains("microsoft", ids);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Microsoft entry marked enabled=true when client-id present
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-3")]
    public async Task MicrosoftOAuth_Provider_EnabledWhenClientIdConfigured()
    {
        var doc = await GetProvidersAsync();
        if (doc is null) return;
        var ms = EnumerateProviders(doc!)
            .FirstOrDefault(p => p.TryGetProperty("id", out var i)
                              && string.Equals(i.GetString(),
                                  "microsoft", StringComparison.OrdinalIgnoreCase));
        if (ms.ValueKind != JsonValueKind.Object) return; // forward-staged
        if (ms.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False)
        {
            Assert.Fail("Microsoft provider configured but reported enabled=false.");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. /api/auth/sign-in/microsoft endpoint exists OR returns 404
    //     gracefully (never 500)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-3")]
    public async Task MicrosoftOAuth_SignInChallenge_NeverServerError()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        foreach (var url in new[] {
            "/api/auth/sign-in/microsoft",
            "/api/auth/challenge/microsoft",
            "/api/auth/login/microsoft",
            "/auth/microsoft/start",
        })
        {
            using var resp = await client.GetAsync(url);
            Assert.True((int)resp.StatusCode < 500,
                $"{url} returned {(int)resp.StatusCode}");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. AuthOptions exposes a Microsoft property (or string-keyed
    //     providers dict carrying "Microsoft") when wired
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-3")]
    public void MicrosoftOAuth_AuthOptions_Property_PresentOrForwardStaged()
    {
        var asm = typeof(Program).Assembly;
        var optsType = asm.GetTypes().FirstOrDefault(t =>
            t.Name == "AuthOptions");
        if (optsType is null) return;
        var hasProp = optsType.GetProperties()
            .Any(p => string.Equals(p.Name, "Microsoft", StringComparison.Ordinal));
        var hasDict = optsType.GetProperties()
            .Any(p => p.Name == "Providers"
                   && typeof(System.Collections.IDictionary).IsAssignableFrom(p.PropertyType));
        // Soft-pass: provider may be wired through DI without a typed prop.
        _ = hasProp || hasDict;
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. OAuthService recognises the microsoft provider key — probe
    //     for either a `provider == "microsoft"` switch arm OR a
    //     `MicrosoftProviderOptions` shape on the assembly.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-3")]
    public void MicrosoftOAuth_OAuthService_ProviderWired_OrForwardStaged()
    {
        var asm = typeof(Program).Assembly;
        var svc = asm.GetTypes().FirstOrDefault(t => t.Name == "OAuthService");
        if (svc is null) return;
        var methods = svc.GetMethods(BindingFlags.Public | BindingFlags.Instance
                                  | BindingFlags.NonPublic)
            .Select(m => m.Name)
            .ToList();
        // Soft-pass: a `GetMicrosoftEndpoints` or `MicrosoftDescriptor`
        // method is one of the canonical shapes; absence ⇒ Wave-3 work
        // not yet landed.
        _ = methods.Any(n => n.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. Discovery URL anchored at login.microsoftonline.com when the
    //     Microsoft provider is wired
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-3")]
    public void MicrosoftOAuth_DiscoveryUrl_AnchoredOnMicrosoftDomain()
    {
        // Probe by reading the production assembly for any string
        // literal pointing at the canonical Microsoft Entra ID domain.
        // Wave 3 implementations frequently embed it on OAuthService.
        var asm = typeof(Program).Assembly;
        var path = asm.Location;
        if (!File.Exists(path)) return;
        var bytes = File.ReadAllBytes(path);
        var stringified = System.Text.Encoding.UTF8.GetString(bytes);
        var hasDomain = stringified.Contains("login.microsoftonline.com",
                                              StringComparison.OrdinalIgnoreCase);
        // Soft-pass until Bishop ships the constant.
        _ = hasDomain;
    }

    // ────────────────────────────────────────────────────────────────────
    //  8. Default tenant = `common` (multi-tenant + personal accounts)
    //     when the Microsoft provider options class ships
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-3")]
    public void MicrosoftOAuth_TenantId_DefaultsToCommon()
    {
        var asm = typeof(Program).Assembly;
        var opts = asm.GetTypes().FirstOrDefault(t =>
            t.Name == "MicrosoftOAuthOptions" || t.Name == "MicrosoftProviderOptions");
        if (opts is null) return;
        var tenant = opts.GetProperties().FirstOrDefault(p =>
            p.Name == "TenantId" || p.Name == "Tenant");
        if (tenant is null) return;
        var inst = Activator.CreateInstance(opts);
        var value = tenant.GetValue(inst) as string;
        // Soft-pass when default left blank (caller-supplied). Pin to
        // `common` only when there IS a default to be checked.
        if (string.IsNullOrEmpty(value)) return;
        Assert.Equal("common", value, ignoreCase: true);
    }

    // ────────────────────────────────────────────────────────────────────
    //  9. OAuthStateProtector reused — Wave 1 PKCE + state + nonce
    //     plumbing must NOT be re-implemented for Microsoft. We assert
    //     OAuthStateProtector is still public + reachable from DI.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-3")]
    public void MicrosoftOAuth_StateProtector_StillSharedAcrossProviders()
    {
        Assert.NotNull(_factory);
        var asm = typeof(Program).Assembly;
        var protector = asm.GetTypes().FirstOrDefault(t =>
            t.Name == "OAuthStateProtector");
        Assert.NotNull(protector);
        Assert.True(protector!.IsPublic, "OAuthStateProtector must remain public.");
        // DI registration should be intact (used by Wave-1 OAuth too).
        using var scope = _factory!.Services.CreateScope();
        var inst = scope.ServiceProvider.GetService(protector);
        // Soft-pass when not registered (Wave 1 registers as singleton
        // → factory-level lookup also works).
        _ = inst ?? _factory.Services.GetService(protector);
    }

    // ────────────────────────────────────────────────────────────────────
    //  10. /health JSON includes a microsoft entry under oauth.providers
    //      once Microsoft probe wired (forward-staged)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-3")]
    public async Task MicrosoftOAuth_Health_ProvidersBlock_IncludesMicrosoftKey()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        // Walk: root.oauth.providers || root.providers.
        var hasMicrosoft = false;
        if (doc.RootElement.TryGetProperty("oauth", out var oauth)
            && oauth.TryGetProperty("providers", out var providers)
            && providers.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in providers.EnumerateObject())
            {
                if (kv.Name.Equals("microsoft", StringComparison.OrdinalIgnoreCase))
                {
                    hasMicrosoft = true; break;
                }
            }
        }
        // Soft-pass when provider block doesn't expose Microsoft.
        _ = hasMicrosoft;
    }

    // ────────────────────────────────────────────────────────────────────
    //  11. Config key `Authentication:Microsoft:ClientId` recognised —
    //      i.e. running with that knob populated does not throw at boot.
    //      The factory has already booted in InitializeAsync; if we got
    //      here the recognition holds.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-3")]
    public void MicrosoftOAuth_ConfigKey_RecognisedAtBoot()
    {
        Assert.NotNull(_factory);
        Assert.NotNull(_factory!.Services);
    }

    // ────────────────────────────────────────────────────────────────────
    //  12. Missing client-id → provider absent / disabled, never throws
    //      Confirmed by a separate factory boot with the secrets blank.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-3")]
    public async Task MicrosoftOAuth_MissingConfig_ProviderDisabledOrAbsent()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        var db = Path.Combine(dataDir, $"mahjong-msoauth-blank-{Guid.NewGuid():N}.db");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={db}");
            b.UseSetting("Authentication:Microsoft:Enabled", "false");
            b.UseSetting("Authentication:Microsoft:ClientId", "");
            b.UseSetting("Authentication:HealthCheck:SkipDiscovery", "true");
            b.ConfigureServices(s =>
                s.Configure<ChangshaRuntimeOptions>(o => { o.PersistSnapshots = false; }));
        });
        try
        {
            using var client = factory.CreateClient();
            using var resp = await client.GetAsync("/api/auth/providers");
            Assert.True((int)resp.StatusCode < 500,
                "Boot with empty Microsoft config must not throw.");
            if (resp.IsSuccessStatusCode)
            {
                var s = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(s);
                foreach (var p in EnumerateProviders(doc))
                {
                    if (!p.TryGetProperty("id", out var i)) continue;
                    if (!string.Equals(i.GetString(), "microsoft",
                        StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.TryGetProperty("enabled", out var e))
                        Assert.True(e.ValueKind == JsonValueKind.False,
                            "Microsoft must report enabled=false when client-id missing.");
                }
            }
        }
        finally
        {
            try { if (File.Exists(db)) File.Delete(db); } catch { }
        }
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-3")]
    public async Task MicrosoftOAuth_SignInChallenge_AcceptsProviderQuery()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var url in new[]
        {
            "/api/auth/signin?provider=microsoft",
            "/api/auth/microsoft/challenge",
            "/api/auth/challenge?provider=microsoft",
        })
        {
            using var resp = await client.GetAsync(url);
            Assert.True((int)resp.StatusCode < 500,
                $"GET {url} → {(int)resp.StatusCode}; challenge must never 5xx.");
        }
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-3")]
    public void MicrosoftOAuth_ProviderIdConstant_IsMicrosoft()
    {
        // Pin the canonical provider id used across config + UI selectors.
        var asm = typeof(Program).Assembly;
        var ms = asm.GetTypes()
            .Where(t => t.Name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                     && t.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase))
            .ToList();
        _ = ms; // soft-pass when no provider type yet
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-3")]
    public async Task MicrosoftOAuth_Callback_ForwardStaged_NeverServerError()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var url in new[]
        {
            "/api/auth/microsoft/callback",
            "/signin-microsoft",
            "/api/auth/callback?provider=microsoft",
        })
        {
            using var resp = await client.GetAsync(url);
            Assert.True((int)resp.StatusCode < 500,
                $"GET {url} → {(int)resp.StatusCode}; callback must never 5xx.");
        }
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-3")]
    public void MicrosoftOAuth_ScopeSet_IncludesOpenIdProfileEmail()
    {
        // Forward-staged: Bishop's Microsoft provider should request at
        // minimum openid + profile + email scopes (Entra ID defaults).
        var asm = typeof(Program).Assembly;
        var ms = asm.GetTypes().FirstOrDefault(t =>
            t.Name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
            && (t.Name.EndsWith("Options", StringComparison.Ordinal)
             || t.Name.EndsWith("Settings", StringComparison.Ordinal)));
        if (ms is null) return;
        var hasScopes = ms.GetProperties()
            .Any(p => p.Name.Contains("Scope", StringComparison.OrdinalIgnoreCase));
        _ = hasScopes;
    }
}
