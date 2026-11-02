using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Observability;

/// <summary>
/// Phase J Wave 5 — <c>GET /metrics</c> Prometheus-exposition endpoint
/// contract tests (Vasquez).
///
/// <para>Apone's Phase J Wave 5 work adds a <c>/metrics</c> route that
/// publishes three gauges (process uptime, active game count, build SHA
/// label) in the canonical Prometheus text exposition format. The endpoint
/// is intentionally implemented without a new NuGet dependency (see
/// <c>Observability/MetricsEndpoint.cs</c>): a Prometheus scrape job ingests
/// the text/plain v0.0.4 body directly, and any heavier instrument library
/// (<c>prometheus-net.AspNetCore</c>) is reserved for a follow-up wave.</para>
///
/// <para>These three facts pin the wire contract end-to-end:
/// <list type="number">
///   <item>200 OK + <c>text/plain</c> content-type with the
///         <c>version=0.0.4</c> token (the parser key for the Prometheus
///         scrape codec).</item>
///   <item>The three named gauges all appear in the body (catches a future
///         rename / accidental drop when someone refactors the exposition
///         writer).</item>
///   <item>The <c>mahjong_build_info{sha="..."}</c> label tracks
///         <c>BUILD_SHA</c> in both the <c>set-to-a-value</c> and the
///         <c>unset</c> case — the latter must collapse to the canonical
///         <c>"dev"</c> sentinel (matches <c>/health</c>'s buildSha contract;
///         operators must not see a blank label in the wild).</item>
/// </list></para>
///
/// <para><b>Test strategy.</b> Spin up a <see cref="WebApplicationFactory{TEntryPoint}"/>
/// over <c>Program</c> with the standard per-test temp-SQLite + snapshot-off
/// configuration that <see cref="Api.HealthEndpointTests"/> and
/// <see cref="Api.PatternOrderingEndpointTests"/> already use. The
/// <c>BUILD_SHA</c> env var is mutated process-wide for the third fact —
/// xUnit serialises tests within a class by default, but we restore the
/// pre-test value in a <c>finally</c> so concurrent collection-parallel
/// runs don't observe stale state.</para>
/// </summary>
public class MetricsEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-metrics-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.BotClaimDelayMs = 1;
                    o.ClaimWindowTimeoutMs = 50;
                    o.DealBatchDelayMs = 0;
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

    // ────────────────────────────────────────────────────────────────────
    //  1. 200 OK + text/plain Prometheus content-type
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-J-5")]
    public async Task Metrics_Returns200_AndPrometheusContentType()
    {
        // Apone's deployment contract pins both axes of the response:
        //   • status 200 — anything else and Prometheus reports the target
        //     as DOWN even if the body is parseable
        //   • Content-Type text/plain with the `version=0.0.4` token —
        //     this is the Prometheus exposition v0.0.4 codec key; a missing
        //     or wrong version token degrades the scraper to "best-effort"
        //     parsing which silently drops labels.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var contentType = response.Content.Headers.ContentType;
        Assert.NotNull(contentType);
        // Prometheus exposition format spec: media-type is text/plain. Some
        // libraries emit `text/plain; version=0.0.4`, others additionally
        // append `; charset=utf-8` — pin the media-type baseline + require
        // the version token; charset is optional per Apone's chosen flavour.
        Assert.Equal("text/plain", contentType.MediaType);
        var rawHeader = response.Content.Headers.GetValues("Content-Type").FirstOrDefault() ?? string.Empty;
        Assert.Contains("version=0.0.4", rawHeader);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Body contains all three required metric names
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-J-5")]
    public async Task Metrics_IncludesExpectedMetrics()
    {
        // The three gauges below are the operator-visibility baseline. Any
        // future refactor that renames or drops them must update this test
        // OR the deployed dashboards (PR review will surface the contract
        // change either way). Asserting on raw metric names (not labels)
        // keeps this test cheap and decoupled from the exact value space.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();

        var body = await client.GetStringAsync("/metrics");

        // Each metric must appear at least once with its TYPE annotation so
        // the Prometheus scraper recognises it as a gauge (not an
        // un-typed counter, which would skew rate() queries downstream).
        Assert.Contains("mahjong_uptime_seconds", body);
        Assert.Contains("mahjong_active_games_total", body);
        Assert.Contains("mahjong_build_info", body);

        // # TYPE lines pin the metric kind. Without them the Prometheus
        // scraper falls back to "untyped" which suppresses delta/rate query
        // semantics on these gauges — a silent observability regression.
        Assert.Contains("# TYPE mahjong_uptime_seconds gauge", body);
        Assert.Contains("# TYPE mahjong_active_games_total gauge", body);
        Assert.Contains("# TYPE mahjong_build_info gauge", body);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. mahjong_build_info{sha="..."} tracks BUILD_SHA env var
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Observability"), Trait("Wave", "Phase-J-5")]
    public async Task Metrics_BuildInfo_IncludesSha()
    {
        // Two-flavour assertion mirrors `/health`'s buildSha contract:
        //   • BUILD_SHA="test123"   → sha="test123" (operators see the
        //     deployed image's commit SHA in the gauge label)
        //   • BUILD_SHA unset/empty → sha="dev"     (local dev / un-tagged
        //     CI runs surface as "dev" not "" — the empty-string trap
        //     Vasquez flagged in Wave 3 for `/health` applies here too).
        //
        // Note: MetricsEndpoint.Render reads Environment.GetEnvironmentVariable
        // and treats both null and "" as "unset" (`string.IsNullOrEmpty(sha) =>
        // sha = "dev"`). The unset-flavour assertion exercises both branches
        // depending on host env state, so we explicitly clear via SetEnvironmentVariable(null).
        Assert.NotNull(_factory);
        var originalSha = Environment.GetEnvironmentVariable("BUILD_SHA");
        try
        {
            // — set-to-a-value branch — exact label match against the value.
            Environment.SetEnvironmentVariable("BUILD_SHA", "test123");
            using (var client = _factory!.CreateClient())
            {
                var body = await client.GetStringAsync("/metrics");
                Assert.Contains("mahjong_build_info{sha=\"test123\"} 1", body);
                Assert.DoesNotContain("sha=\"\"", body);
            }

            // — unset branch — must fall through to the canonical "dev"
            // sentinel so dashboards never display a blank label.
            Environment.SetEnvironmentVariable("BUILD_SHA", null);
            using (var client = _factory.CreateClient())
            {
                var body = await client.GetStringAsync("/metrics");
                Assert.Contains("mahjong_build_info{sha=\"dev\"} 1", body);
            }
        }
        finally
        {
            // Restore so concurrent collections in the same process
            // (xUnit parallelisation) don't observe a polluted env.
            Environment.SetEnvironmentVariable("BUILD_SHA", originalSha);
        }
    }
}
