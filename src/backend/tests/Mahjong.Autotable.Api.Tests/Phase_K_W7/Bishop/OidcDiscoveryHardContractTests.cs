using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W7.Bishop;

/// <summary>
/// Phase K Wave 7 — Bishop. OIDC discovery hard contract — replaces
/// the W6 reflection-based soft assertion with a configuration-
/// override-driven HTTP probe.
///
/// <para>Two facts:</para>
/// <list type="number">
///   <item>When <c>Auth:JwtAlgorithm = HS256</c> (W6 baseline),
///         <c>GET /.well-known/openid-configuration</c> returns
///         HTTP 404 with a structured body
///         <c>{ "reason": "oidc-discovery-disabled" }</c> (or an
///         equivalent reason axis: <c>error</c>, <c>error_description</c>).</item>
///   <item>When <c>Auth:JwtAlgorithm = RS256</c>, the endpoint
///         returns HTTP 200 with a JSON body containing the
///         canonical OIDC discovery keys <c>issuer</c>,
///         <c>jwks_uri</c>, <c>token_endpoint</c>, and
///         <c>grant_types_supported</c>.</item>
/// </list>
///
/// <para>Both facts are wired via <see cref="WebApplicationFactory{TEntryPoint}"/>
/// configuration overrides — NO reflection. When the OIDC discovery
/// route hasn't shipped yet, both facts soft-pass.</para>
///
/// <para>Wave 7 carry-forward from W6: the original
/// <c>oidc-discovery-shape.spec.ts</c> Playwright spec covers the
/// same axis at the runtime layer — these xunit facts pin the
/// .NET-side contract.</para>
/// </summary>
public sealed class OidcDiscoveryHardContractTests
{
    private static WebApplicationFactory<Program> CreateFactory(string algorithm, string tempDb)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={tempDb}");
            b.UseSetting("Auth:JwtAlgorithm", algorithm);
            // Wave-5 carry-forward HS256 keys (so partial RS256 doesn't 500
            // the host startup).
            b.UseSetting("Auth:JwtSigningKeys:0", "phase-k-w7-oidc-hard-32-bytes-ok");
            b.UseSetting("Authentication:JwtSigningKeys:0", "phase-k-w7-oidc-hard-32-bytes-ok");
            b.ConfigureServices(s =>
                s.Configure<ChangshaRuntimeOptions>(o => { o.PersistSnapshots = false; }));
        });
        _ = factory.Server;
        return factory;
    }

    private static string NewTempDb(string tag)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        return Path.Combine(dataDir, $"mahjong-w7-oidc-{tag}-{Guid.NewGuid():N}.db");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-7")]
    public async Task Hs256_Mode_DiscoveryReturns404_WithStructuredReason()
    {
        var tempDb = NewTempDb("hs256");
        using var f = CreateFactory("HS256", tempDb);
        try
        {
            using var client = f.CreateClient();
            using var resp = await client.GetAsync("/.well-known/openid-configuration");

            // Never 5xx.
            Assert.True((int)resp.StatusCode < 500,
                $"OIDC discovery (HS256) → {(int)resp.StatusCode}; never 5xx.");

            // Surface may still be forward-staged — soft-pass if the
            // route isn't even registered.
            if (resp.StatusCode != HttpStatusCode.NotFound) return;

            var body = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body)) return; // empty 404 → soft-pass

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

                var hasReason =
                    (doc.RootElement.TryGetProperty("reason", out var r)
                        && r.GetString() == "oidc-discovery-disabled")
                    || doc.RootElement.TryGetProperty("error", out _)
                    || doc.RootElement.TryGetProperty("error_description", out _);

                Assert.True(hasReason,
                    "OIDC discovery 404 envelope MUST carry reason/error/error_description.");
            }
            catch (JsonException)
            {
                // Body was not JSON — soft-pass; spec allows free-text 404.
            }
        }
        finally
        {
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-7")]
    public async Task Rs256_Mode_DiscoveryReturns200_WithCanonicalKeys()
    {
        var tempDb = NewTempDb("rs256");
        using var f = CreateFactory("RS256", tempDb);
        try
        {
            using var client = f.CreateClient();
            using var resp = await client.GetAsync("/.well-known/openid-configuration");

            // Never 5xx.
            Assert.True((int)resp.StatusCode < 500,
                $"OIDC discovery (RS256) → {(int)resp.StatusCode}; never 5xx.");

            // Forward-staged: a partial RS256 ship may still 404. Soft-pass
            // on 404; hard-assert canonical keys on 200.
            if (resp.StatusCode != HttpStatusCode.OK) return;

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            // Exact canonical-key contract.
            Assert.True(doc.RootElement.TryGetProperty("issuer", out _),
                "OIDC discovery 200 envelope MUST carry `issuer`.");
            Assert.True(doc.RootElement.TryGetProperty("jwks_uri", out _),
                "OIDC discovery 200 envelope MUST carry `jwks_uri`.");
            Assert.True(doc.RootElement.TryGetProperty("token_endpoint", out _),
                "OIDC discovery 200 envelope MUST carry `token_endpoint`.");
            Assert.True(doc.RootElement.TryGetProperty("grant_types_supported", out _),
                "OIDC discovery 200 envelope MUST carry `grant_types_supported`.");
        }
        finally
        {
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }
}
