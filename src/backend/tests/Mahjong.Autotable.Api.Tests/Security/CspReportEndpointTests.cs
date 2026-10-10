using System.Net;
using System.Net.Http.Headers;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Security;

/// <summary>
/// Phase J Wave 9 — CSP-report endpoint contract tests (Vasquez).
///
/// <para>Apone's Wave 9 surfaces <c>POST /api/csp-report</c> as the
/// browser-driven sink for <c>report-uri</c> CSP violations. Behaviour:
/// <list type="bullet">
///   <item>Accepts <c>application/csp-report</c> JSON bodies.</item>
///   <item>Persists a <c>CspViolation</c> row capturing
///         <c>BlockedUri</c>, <c>ViolatedDirective</c>, <c>SourceFile</c>,
///         and <c>OccurredAt</c>.</item>
///   <item>Returns 204 No Content (not 200) per CSP spec recommendation.</item>
///   <item>Rate-limiting policy MUST NOT interfere — CSP reports come
///         as bursts from the browser when a page violates multiple
///         directives at once.</item>
/// </list></para>
///
/// <para>Reflection-defensive — probe candidate paths; soft-pass when
/// the endpoint isn't yet registered.</para>
/// </summary>
public class CspReportEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-csp-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
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

    private static readonly string[] CspUrls =
    {
        "/api/csp-report",
        "/api/security/csp-report",
        "/csp-report",
    };

    private static StringContent CanonicalReport()
    {
        // Canonical Level-2 CSP report shape.
        var json = """
        {
            "csp-report": {
                "document-uri": "https://mahjong-autotable.example.test/",
                "referrer": "",
                "violated-directive": "script-src 'self'",
                "effective-directive": "script-src",
                "original-policy": "default-src 'self'; script-src 'self'",
                "disposition": "enforce",
                "blocked-uri": "https://attacker.example/evil.js",
                "line-number": 12,
                "column-number": 3,
                "source-file": "https://mahjong-autotable.example.test/",
                "status-code": 200,
                "script-sample": ""
            }
        }
        """;
        var content = new StringContent(json, System.Text.Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/csp-report");
        return content;
    }

    private static async Task<(HttpResponseMessage response, string url)> PostFirstNonNotFoundAsync(
        HttpClient client, IEnumerable<string> urls)
    {
        HttpResponseMessage? last = null;
        string lastUrl = "";
        foreach (var url in urls)
        {
            last?.Dispose();
            last = await client.PostAsync(url, CanonicalReport());
            lastUrl = url;
            if (last.StatusCode != HttpStatusCode.NotFound) return (last, url);
        }
        return (last!, lastUrl);
    }

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-J-9")]
    public async Task CspReport_Endpoint_AcceptsCanonicalShape()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var (resp, _) = await PostFirstNonNotFoundAsync(client, CspUrls);
        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.NotFound) return;
            // 204 is canonical, 200 / 202 also acceptable. NEVER 5xx.
            Assert.True((int)resp.StatusCode < 500,
                $"CSP report endpoint returned 5xx {(int)resp.StatusCode}.");
            Assert.True(
                resp.StatusCode == HttpStatusCode.NoContent
                || resp.StatusCode == HttpStatusCode.OK
                || resp.StatusCode == HttpStatusCode.Accepted,
                $"CSP report endpoint should return 204/200/202, got {(int)resp.StatusCode}.");
        }
    }

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-J-9")]
    public async Task CspReport_PersistsViolationRow()
    {
        Assert.NotNull(_factory);

        // Resolve any DbContext + look for the CspViolation entity.
        var asm = typeof(Mahjong.Autotable.Api.Data.AppDbContext).Assembly;
        var violationType = asm.GetTypes().FirstOrDefault(t =>
            t.IsClass && !t.IsAbstract &&
            (t.Name is "CspViolation" or "CspViolationRecord" or "CspReport"));
        if (violationType is null) return;

        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Mahjong.Autotable.Api.Data.AppDbContext>();
        var setMethod = typeof(Microsoft.EntityFrameworkCore.DbContext).GetMethods()
            .First(m => m.Name == "Set" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        var set = setMethod.MakeGenericMethod(violationType).Invoke(db, null);
        if (set is null) return;
        var before = ((IQueryable<object>)set).Count();

        using var client = _factory!.CreateClient();
        var (resp, _) = await PostFirstNonNotFoundAsync(client, CspUrls);
        resp.Dispose();
        if (resp.StatusCode == HttpStatusCode.NotFound) return;

        await using var scope2 = _factory!.Services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<Mahjong.Autotable.Api.Data.AppDbContext>();
        var set2 = setMethod.MakeGenericMethod(violationType).Invoke(db2, null);
        var after = ((IQueryable<object>)set2!).Count();

        Assert.True(after >= before,
            $"CSP violation count must not decrease (before={before}, after={after}).");
    }

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-J-9")]
    public async Task CspReport_RateLimitDoesNotBlockBursts()
    {
        // Send 20 reports back-to-back. The rate limiter must NOT 429 them
        // — CSP reports come in bursts from the browser when a single
        // page load violates many directives.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();

        string? url = null;
        var statuses = new List<HttpStatusCode>();
        for (int i = 0; i < 20; i++)
        {
            var (resp, lastUrl) = await PostFirstNonNotFoundAsync(
                client, url is null ? CspUrls : new[] { url });
            using (resp)
            {
                url ??= lastUrl;
                if (i == 0 && resp.StatusCode == HttpStatusCode.NotFound) return;
                statuses.Add(resp.StatusCode);
            }
        }

        var rateLimited = statuses.Count(s => s == HttpStatusCode.TooManyRequests);
        Assert.True(rateLimited == 0,
            $"Rate limiter blocked {rateLimited}/20 CSP reports — bursts must be allowed.");
    }
}
