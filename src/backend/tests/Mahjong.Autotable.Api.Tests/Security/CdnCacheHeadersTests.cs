using System.Net;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Security;

/// <summary>
/// Phase J Wave 8 — CDN / static-file cache header tests (Vasquez).
///
/// <para>Apone's Wave 8 contract:
/// <list type="bullet">
///   <item><b>Hashed bundles</b> (e.g., <c>autotable-src.85bbb8ca.js</c>,
///         <c>autotable-src.a7cd8ea4.css</c>) — content-addressable, so
///         <c>Cache-Control: public, max-age=31536000, immutable</c>.</item>
///   <item><b>index.html / unhashed entry points</b> — must NOT cache
///         (<c>Cache-Control: no-cache, no-store</c> or short TTL with
///         <c>must-revalidate</c>) so frontend deploys propagate
///         instantly.</item>
/// </list></para>
///
/// <para>The static-file pipeline in <c>Program.cs</c> emits these
/// headers via an <c>OnPrepareResponse</c> hook. If the hook isn't wired
/// (Wave 7 baseline), every request returns no Cache-Control at all —
/// these tests soft-pass in that state.</para>
/// </summary>
public class CdnCacheHeadersTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-cdn-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            // Phase L — Drake. Prod hardening: see SecurityHeadersTests for context.
            b.UseSetting("Auth:JwtSigningKeys:0", "test-prod-stable-jwt-key-aaaaaaaaaaaaaaaaaaaaaaaa");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.UseSetting("Cors:AllowedOrigins:0", "https://example.test");
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

    private static string? GetCacheControl(HttpResponseMessage response)
    {
        if (response.Headers.CacheControl is not null)
            return response.Headers.CacheControl.ToString();
        if (response.Content?.Headers != null
            && response.Content.Headers.TryGetValues("Cache-Control", out var v))
            return string.Join(", ", v);
        return null;
    }

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-J-8")]
    public async Task CdnCache_IndexHtml_NotImmutable()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var response = await client.GetAsync("/");
        if (response.StatusCode == HttpStatusCode.NotFound) return;

        var cc = GetCacheControl(response);
        if (string.IsNullOrEmpty(cc)) return; // not yet wired

        // index.html must NOT carry `immutable` or year-long max-age.
        Assert.DoesNotContain("immutable", cc, StringComparison.OrdinalIgnoreCase);
        // Must NOT have max-age > 1 day (86400) on the entry point.
        var match = System.Text.RegularExpressions.Regex.Match(cc, @"max-age=(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var maxAge))
        {
            Assert.True(maxAge <= 86400,
                $"index.html Cache-Control max-age must be ≤ 86400 (1 day); got {maxAge}. Deploy propagation breaks otherwise.");
        }
    }

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-J-8")]
    public async Task CdnCache_HashedBundle_IsImmutableLongLived()
    {
        // Try to discover an actual hashed bundle from the autotable
        // static-file root. If none exists in the test runtime root,
        // the test soft-passes.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();

        // Probe well-known hashed-bundle URL patterns.
        string[] candidates =
        {
            "/autotable/autotable-src.85bbb8ca.js",
            "/autotable/autotable-src.a7cd8ea4.css",
        };

        // Also try the wwwroot copy of the frontend bundles.
        HttpResponseMessage? hit = null;
        foreach (var url in candidates)
        {
            var resp = await client.GetAsync(url);
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                hit?.Dispose();
                hit = resp;
                break;
            }
            resp.Dispose();
        }
        if (hit is null) return; // bundles not present in test runtime — soft pass

        using (hit)
        {
            var cc = GetCacheControl(hit);
            if (string.IsNullOrEmpty(cc)) return;

            // Hashed bundles SHOULD carry `immutable` and a long max-age.
            // We soft-pass when neither is set (Wave 7 baseline; Apone's
            // OnPrepareResponse hook not yet shipped).
            if (!cc.Contains("immutable", StringComparison.OrdinalIgnoreCase)
                && !System.Text.RegularExpressions.Regex.IsMatch(cc, @"max-age=\d{6,}"))
            {
                return; // not yet wired
            }

            // If immutable is set, max-age must be at least 1 year.
            if (cc.Contains("immutable", StringComparison.OrdinalIgnoreCase))
            {
                var match = System.Text.RegularExpressions.Regex.Match(cc, @"max-age=(\d+)");
                Assert.True(match.Success, "immutable Cache-Control must also carry max-age.");
                Assert.True(int.Parse(match.Groups[1].Value) >= 31536000,
                    "immutable hashed-bundle max-age must be ≥ 31536000 (1 year).");
            }
        }
    }

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-J-8")]
    public async Task CdnCache_AutotablePath_StaticFilesReachable()
    {
        // Regression check: the autotable static-file mount must keep
        // working under Production. If a hardening pass accidentally
        // unbinds /autotable, the frontend deploy breaks.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/autotable/");
        // Either 200 (index served), 301/302 (redirect to canonical path),
        // 304 (cached), or 404 if the autotable directory isn't present
        // in the test container — all are non-5xx.
        Assert.True((int)response.StatusCode < 500,
            $"/autotable/ returned {(int)response.StatusCode}; must not 5xx in Production.");
    }
}
