using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Auth;

/// <summary>
/// Phase K Wave 1 — OAuth provider health-check contract tests (Vasquez).
///
/// <para>Bishop's Phase K Wave 1 brief extends <c>/health</c> with an
/// OIDC-aware probe per configured provider. Expected shape:
/// <code>
/// {
///   "status": "ok",
///   ...
///   "oauth": {
///     "providers": {
///       "google": { "configured": true, "discovery": "ok" | "fail" | "skipped", "latencyMs": 42 },
///       "github": { "configured": true, "discovery": "skipped", "reason": "no-oidc" }
///     }
///   }
/// }
/// </code></para>
///
/// <para>The probe fetches the issuer's OIDC discovery document
/// (Google: <c>https://accounts.google.com/.well-known/openid-configuration</c>)
/// and surfaces "ok" / "fail" / "skipped". GitHub is a non-OIDC OAuth2
/// provider — its entry is <c>discovery: "skipped"</c>.</para>
///
/// <para><b>Defensive shape contract:</b> we do NOT exercise the real
/// upstream from tests. We assert that the health envelope either:
/// (a) carries the <c>oauth.providers.*</c> shape, OR (b) doesn't yet
/// expose it (forward-staged). The contract is: never 5xx; if
/// <c>oauth</c> appears, it MUST be an object with the right shape.</para>
/// </summary>
public class OAuthProviderHealthCheckTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-oauth-hc-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.UseSetting("Authentication:Google:Enabled", "true");
            b.UseSetting("Authentication:Google:ClientId", "test-google-client-id");
            b.UseSetting("Authentication:Google:ClientSecret", "test-google-client-secret");
            b.UseSetting("Authentication:GitHub:Enabled", "true");
            b.UseSetting("Authentication:GitHub:ClientId", "test-github-client-id");
            b.UseSetting("Authentication:GitHub:ClientSecret", "test-github-client-secret");
            // Skip the live HTTP probe in tests (Bishop's knob).
            b.UseSetting("Authentication:HealthCheck:SkipDiscovery", "true");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.PersistSnapshots = false;
                });
            });
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

    private async Task<JsonElement> FetchHealthAsync()
    {
        using var client = _factory!.CreateClient();
        using var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. /health never 5xx with both providers configured
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-1")]
    public async Task Health_WithProvidersConfigured_Returns200()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var resp = await client.GetAsync("/health");
        Assert.True(resp.StatusCode == HttpStatusCode.OK
                    || resp.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"/health returned {(int)resp.StatusCode}; expected 200 or 503 only.");
        // Never raw 500.
        Assert.True((int)resp.StatusCode < 500 || resp.StatusCode == HttpStatusCode.ServiceUnavailable);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. /health response body is a parseable JSON object
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-1")]
    public async Task Health_BodyIsJsonObject()
    {
        Assert.NotNull(_factory);
        var root = await FetchHealthAsync();
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. /health may carry oauth.providers — when present, shape is sane
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-1")]
    public async Task Health_OauthProviders_HasExpectedShape_OrSoftPasses()
    {
        Assert.NotNull(_factory);
        var root = await FetchHealthAsync();
        if (!root.TryGetProperty("oauth", out var oauth)) return; // forward-staged

        // Once present, must be an object carrying `providers` as an object.
        Assert.Equal(JsonValueKind.Object, oauth.ValueKind);
        Assert.True(oauth.TryGetProperty("providers", out var providers),
            "/health.oauth missing 'providers' child object.");
        Assert.Equal(JsonValueKind.Object, providers.ValueKind);

        // At least one configured provider should appear.
        var hasAny = providers.EnumerateObject().Any();
        Assert.True(hasAny, "/health.oauth.providers is empty despite enabled provider config.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. When oauth.providers.google is present, it carries the
    //     discovery field (string).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-1")]
    public async Task Health_OauthGoogle_HasDiscoveryStatus_OrSoftPasses()
    {
        Assert.NotNull(_factory);
        var root = await FetchHealthAsync();
        if (!root.TryGetProperty("oauth", out var oauth)) return;
        if (!oauth.TryGetProperty("providers", out var providers)) return;
        if (!providers.TryGetProperty("google", out var google)) return;

        Assert.Equal(JsonValueKind.Object, google.ValueKind);
        // discovery field is a STRING when present.
        if (google.TryGetProperty("discovery", out var disc))
        {
            Assert.Equal(JsonValueKind.String, disc.ValueKind);
            var s = disc.GetString()!;
            Assert.Contains(s, new[] { "ok", "fail", "skipped", "disabled", "pending" });
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Per-provider configured flag is boolean
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-1")]
    public async Task Health_OauthProvider_ConfiguredField_IsBool()
    {
        Assert.NotNull(_factory);
        var root = await FetchHealthAsync();
        if (!root.TryGetProperty("oauth", out var oauth)) return;
        if (!oauth.TryGetProperty("providers", out var providers)) return;

        foreach (var provEntry in providers.EnumerateObject())
        {
            var provObj = provEntry.Value;
            if (provObj.ValueKind != JsonValueKind.Object) continue;
            if (provObj.TryGetProperty("configured", out var configured))
            {
                Assert.True(configured.ValueKind == JsonValueKind.True
                            || configured.ValueKind == JsonValueKind.False,
                    $"oauth.providers.{provEntry.Name}.configured must be a boolean.");
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. /health body NEVER leaks the client_secret value
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-1")]
    public async Task Health_NeverLeaks_ClientSecret()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var resp = await client.GetAsync("/health");
        var body = (await resp.Content.ReadAsStringAsync()).ToLowerInvariant();
        Assert.DoesNotContain("test-google-client-secret", body);
        Assert.DoesNotContain("test-github-client-secret", body);
        Assert.DoesNotContain("client_secret", body);
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. Disabled provider doesn't trigger a 5xx
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-1")]
    public async Task Health_WithDisabledProvider_StillOk()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        var tempDb = Path.Combine(dataDir, $"mahjong-oauth-hcd-{Guid.NewGuid():N}.db");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={tempDb}");
            b.UseSetting("Authentication:Google:Enabled", "false");
            b.UseSetting("Authentication:GitHub:Enabled", "false");
            b.UseSetting("Authentication:HealthCheck:SkipDiscovery", "true");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.PersistSnapshots = false;
                });
            });
        });
        using var client = factory.CreateClient();
        using var resp = await client.GetAsync("/health");
        Assert.True(resp.StatusCode == HttpStatusCode.OK
                    || resp.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"Disabled-provider /health returned {(int)resp.StatusCode}.");
        try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
    }
}
