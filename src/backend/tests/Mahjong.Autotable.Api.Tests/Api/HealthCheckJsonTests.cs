using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Api;

/// <summary>
/// Phase J Wave 7 — <c>/health</c> JSON-shape contract tests (Vasquez).
///
/// <para><see cref="HealthEndpointTests"/> already covers the field-existence
/// surface and the <c>?simple=1</c> opt-out shipped by Bishop in Wave 7.
/// These complementary facts pin the JSON-shape invariants that the
/// load-balancer / k8s probe consumers depend on but that the existing
/// suite does not yet assert:</para>
///
/// <list type="bullet">
///   <item><b>Field-typing invariants.</b> <c>db.latencyMs</c> is integer
///         (not string / float / "Number" wrapped in quotes — operators
///         grep numeric values to plot in dashboards), <c>activeGames</c>
///         is integer (Prometheus scrapes the same number from
///         <c>/metrics</c> and a type drift here would silently break the
///         alert routing).</item>
///   <item><b>Strict envelope.</b> <c>?simple=1</c> emits EXACTLY the 4
///         Wave-3 fields — no <c>db</c>, no <c>activeGames</c>, no
///         leakage of internal envelope keys. Without strict checking
///         here a regression that always emits the Wave-7 shape could
///         pass the existing tests (which only assert the field is
///         absent) while accidentally adding new fields the probe
///         scripts would also accidentally parse.</item>
///   <item><b>Concurrency.</b> <c>/health</c> can be hit from many threads
///         simultaneously without deadlocking on the EF Core scope or
///         the runtime <c>GameCount</c> read. Pins the non-blocking
///         contract for load-balancer poll cadence.</item>
/// </list>
/// </summary>
public class HealthCheckJsonTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-health-json-{Guid.NewGuid():N}.db");

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
    //  1. db.latencyMs is a Number kind (not string / not object)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-7")]
    public async Task HealthDetailed_DbLatencyMs_IsNumericJsonKind()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var db = doc.RootElement.GetProperty("db");
        var latency = db.GetProperty("latencyMs");

        // Bishop's contract: latencyMs is a Stopwatch.ElapsedMilliseconds
        // integer (long). The wire shape MUST be Number, not String — a
        // grafana / prom-alert dashboard that parses
        // `value=$(jq '.db.latencyMs' resp.json)` would silently break on
        // a stringification regression.
        Assert.Equal(JsonValueKind.Number, latency.ValueKind);
        // Range invariant — non-negative + sane upper bound (any single
        // SELECT 1 round-trip over an in-memory SQLite below 30s; the
        // 30s cap is dead-loose because TestServer is in-process).
        Assert.InRange(latency.GetInt64(), 0, 30_000);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. activeGames is a Number kind (not string)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-7")]
    public async Task HealthDetailed_ActiveGames_IsNumericJsonKind()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var active = doc.RootElement.GetProperty("activeGames");

        // Same wire-shape contract reasoning as db.latencyMs — a Prometheus
        // exporter and the k8s autoscaler both consume the count as a
        // numeric metric.
        Assert.Equal(JsonValueKind.Number, active.ValueKind);
        // Fresh factory → no games created → expect 0.
        Assert.Equal(0, active.GetInt32());
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. ?simple=1 emits ONLY the Wave-3 4 fields (strict envelope check)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-7")]
    public async Task HealthSimple_EmitsExactly_Wave3FourFields()
    {
        // Strict envelope: ?simple=1 must NOT add any field beyond the
        // Wave-3 contract. A regression that emits a future "newField"
        // alongside the simple shape could pass HealthEndpoint_SimpleQuery_OmitsDetailedFields
        // (which only asserts db + activeGames are absent), so we count
        // the top-level keys explicitly.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var response = await client.GetAsync("/health?simple=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(4, keys.Count);
        Assert.Contains("status", keys);
        Assert.Contains("buildSha", keys);
        Assert.Contains("uptime", keys);
        Assert.Contains("version", keys);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Detailed mode's `db` object carries EXACTLY connected + latencyMs
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-10")]
    public async Task HealthDetailed_DbObject_ExposesWave10Shape()
    {
        // Phase J Wave 7 pinned a strict 2-key db payload (connected,
        // latencyMs). Phase J Wave 10 (Bishop) extended the contract to
        // include provider/migration introspection so operators can tell
        // SQLite-bootstrap deployments from SqlServer/Postgres deployments
        // from a single curl. Any future field addition should still
        // land via a deliberate test edit (not silently) — so we pin
        // the exact key set for Wave 10.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var response = await client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var db = doc.RootElement.GetProperty("db");
        var keys = db.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(5, keys.Count);
        Assert.Contains("connected", keys);
        Assert.Contains("latencyMs", keys);
        Assert.Contains("providerName", keys);
        Assert.Contains("canQuery", keys);
        Assert.Contains("migrationsApplied", keys);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. ?simple=0 (and unrecognised values) returns detailed shape
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-7")]
    public async Task HealthQuery_SimpleNotOne_FallsBackToDetailed()
    {
        // Bishop's contract: only the literal `?simple=1` short-circuits
        // to the legacy shape. `?simple=0`, `?simple=true`, `?simple=`
        // and missing query string all return the detailed envelope. The
        // permissive parsing (`simpleVal == "1"`) keeps the contract
        // unambiguous — operators get the rich payload by default and
        // must opt into the legacy shape explicitly.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();

        foreach (var qs in new[] { "?simple=0", "?simple=true", "?simple=", "?other=1", "" })
        {
            using var response = await client.GetAsync($"/health{qs}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.TryGetProperty("db", out _),
                $"/health{qs} must return the detailed shape (db missing).");
            Assert.True(doc.RootElement.TryGetProperty("activeGames", out _),
                $"/health{qs} must return the detailed shape (activeGames missing).");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. /health is concurrency-safe (no deadlock under parallel poll)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-7")]
    public async Task HealthDetailed_ParallelRequests_AllReturn200()
    {
        // Probe loop cadence in k8s defaults to 10-30s per replica; a 4-pod
        // deployment scraped by a Prometheus exporter every 15s plus the
        // load balancer's own check is easily 20+ requests/s under load.
        // EF Core scope-per-call + IServiceProvider.GetService<runtime>()
        // must be non-blocking. This test fires 32 concurrent requests
        // and asserts every one returns 200 — a deadlock would surface
        // as a TaskCanceledException from the HttpClient default timeout.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => client.GetAsync("/health"))
            .ToArray();
        var responses = await Task.WhenAll(tasks);
        try
        {
            foreach (var response in responses)
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            foreach (var r in responses) r.Dispose();
        }
    }
}
