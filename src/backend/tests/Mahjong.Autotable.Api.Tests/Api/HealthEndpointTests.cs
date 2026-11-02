using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Api;

/// <summary>
/// Phase J Wave 3 — <c>/health</c> endpoint contract tests (Vasquez).
///
/// <para>Bishop's Phase J Wave 3 task adds <c>GET /health</c> as the canonical
/// Docker HEALTHCHECK / Kubernetes liveness probe surface (Apone's deployment
/// work consumes it). The endpoint is distinct from the legacy frontend probe
/// at <c>/api/health</c> so the deployment infra has a stable wire contract
/// independent of any future frontend-API churn.</para>
///
/// <para>The endpoint returns <c>200 OK</c> with a JSON body containing four
/// fields: <c>status</c>, <c>buildSha</c>, <c>uptime</c>, <c>version</c>.
/// <c>buildSha</c> defaults to <c>"dev"</c> when the <c>BUILD_SHA</c>
/// environment variable is unset — the Dockerfile passes the real Git SHA
/// in at build time so deployed images carry their provenance.</para>
///
/// <para><b>Test strategy.</b> Spin up a <see cref="WebApplicationFactory{TEntryPoint}"/>
/// over <c>Program</c> with the standard test-host configuration (per-instance
/// temp SQLite DB to avoid collisions, snapshot persistence disabled), issue a
/// real HTTP GET through the in-memory server, and parse the response body to
/// assert the contract. The factory pattern follows
/// <c>SpectatorModeTests.InitializeAsync</c> and
/// <c>ChangshaHubTestHarness</c> verbatim — same env, same per-test temp DB,
/// same options snapshot.</para>
/// </summary>
public class HealthEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-health-{Guid.NewGuid():N}.db");

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
    //  1. 200 OK + 4-field shape contract
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-3")]
    public async Task HealthEndpoint_ReturnsOk_WithExpectedShape()
    {
        // Phase J Wave 3 (Apone deployment / Bishop endpoint): GET /health must
        // return 200 with all four documented fields present. The Docker
        // HEALTHCHECK directive reads this endpoint and the smoke script
        // (tests/smoke/docker-build-smoke.sh) grep-asserts the same four
        // keys — this test is the in-process contract pin so the smoke
        // script's wider assertion (live container, real port) does not
        // have to be the only line of defence.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // All four fields documented in Bishop's commit message
        // (`status, buildSha, uptime, version`) must be present. The smoke
        // script (`docker-build-smoke.sh`) makes the same grep-level check
        // against the live container; this is the in-process counterpart.
        Assert.True(root.TryGetProperty("status", out _),
            "/health response missing 'status' field — Phase J Wave 3 wire contract regression.");
        Assert.True(root.TryGetProperty("buildSha", out _),
            "/health response missing 'buildSha' field — Phase J Wave 3 wire contract regression.");
        Assert.True(root.TryGetProperty("uptime", out _),
            "/health response missing 'uptime' field — Phase J Wave 3 wire contract regression.");
        Assert.True(root.TryGetProperty("version", out _),
            "/health response missing 'version' field — Phase J Wave 3 wire contract regression.");

        // Defence-in-depth: status must be a non-empty string (matches the
        // "healthy" / "ok" payload Bishop ships). The smoke script does NOT
        // pin the literal value, so the unit test holds the line on shape
        // without over-constraining the literal vocabulary.
        Assert.Equal(JsonValueKind.String, root.GetProperty("status").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("status").GetString()),
            "/health 'status' field must be a non-empty string.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. buildSha defaults to "dev" when BUILD_SHA env var is unset
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-3")]
    public async Task HealthEndpoint_BuildSha_DefaultsToDev_WhenUnset()
    {
        // Phase J Wave 3 (Apone Dockerfile contract): the `BUILD_SHA` env var
        // is set at `docker build` time to the real Git SHA so deployed
        // images carry their provenance. In the local test environment the
        // variable is unset, in which case the endpoint must fall back to
        // the literal string "dev" so devs running `dotnet run` locally
        // never see a misleading empty/null sha. This test pins that
        // fallback (which would otherwise be invisible against a deployed
        // image carrying a real SHA).
        //
        // We defensively snapshot+clear the env var on this process so the
        // test is stable regardless of how the developer's shell is
        // configured. The factory is recreated under the cleared env so
        // the static `processStartTime` capture in Program.cs is not
        // affected — what matters is that the `/health` request executes
        // with BUILD_SHA == null.
        Assert.NotNull(_factory);

        var previous = Environment.GetEnvironmentVariable("BUILD_SHA");
        try
        {
            Environment.SetEnvironmentVariable("BUILD_SHA", null);

            using var client = _factory!.CreateClient();
            using var response = await client.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var buildSha = doc.RootElement.GetProperty("buildSha").GetString();

            Assert.Equal("dev", buildSha);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BUILD_SHA", previous);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Phase J Wave 7 — detailed shape exposes db + activeGames
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-7")]
    public async Task HealthEndpoint_Detailed_ExposesDbAndActiveGames()
    {
        // Phase J Wave 7 extends /health to carry an operational detail
        // payload alongside the Wave-3 shape: a nested `db: {connected,
        // latencyMs}` object surfaces the DB round-trip health, and
        // `activeGames` returns the in-memory runtime's GameCount. The
        // Wave-3 fields stay where they were, so any consumer that
        // already parses the legacy shape keeps working. This test pins
        // both:
        //   (a) the new fields exist and are typed correctly,
        //   (b) db.connected is true against the in-memory SQLite test DB,
        //   (c) activeGames is a non-negative integer (zero when no games
        //       have been created in the harness).
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Wave-3 legacy fields must still be present (back-compat — the
        // Docker HEALTHCHECK grep-asserts these).
        Assert.True(root.TryGetProperty("status", out _),
            "/health (detailed) missing 'status'.");
        Assert.True(root.TryGetProperty("buildSha", out _),
            "/health (detailed) missing 'buildSha'.");
        Assert.True(root.TryGetProperty("uptime", out _),
            "/health (detailed) missing 'uptime'.");
        Assert.True(root.TryGetProperty("version", out _),
            "/health (detailed) missing 'version'.");

        // Wave-7 additions.
        Assert.True(root.TryGetProperty("db", out var dbEl),
            "/health (detailed) missing 'db' object — Wave 7 contract regression.");
        Assert.Equal(JsonValueKind.Object, dbEl.ValueKind);
        Assert.True(dbEl.TryGetProperty("connected", out var connEl),
            "/health.db missing 'connected'.");
        Assert.Equal(JsonValueKind.True, connEl.ValueKind);
        Assert.True(dbEl.TryGetProperty("latencyMs", out var latencyEl),
            "/health.db missing 'latencyMs'.");
        Assert.Equal(JsonValueKind.Number, latencyEl.ValueKind);
        Assert.True(latencyEl.GetInt64() >= 0,
            "/health.db.latencyMs must be a non-negative integer.");

        Assert.True(root.TryGetProperty("activeGames", out var gamesEl),
            "/health (detailed) missing 'activeGames' — Wave 7 contract regression.");
        Assert.Equal(JsonValueKind.Number, gamesEl.ValueKind);
        Assert.True(gamesEl.GetInt32() >= 0,
            "/health.activeGames must be a non-negative integer.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Phase J Wave 7 — `?simple=1` falls back to Wave-3 4-field shape
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-7")]
    public async Task HealthEndpoint_SimpleQuery_OmitsDetailedFields()
    {
        // Phase J Wave 7 introduces a `?simple=1` opt-out so any
        // load-balancer probe (Docker HEALTHCHECK, k8s liveness probe,
        // tests/smoke/docker-build-smoke.sh) that grep-asserts the
        // exact Wave-3 4-field shape keeps working without picking up
        // the Wave-7 detail object. The simple shape MUST omit `db` and
        // `activeGames` so older grep checks of the form
        // `grep -c '"status"\|"buildSha"' …` don't double-count.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync("/health?simple=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("status", out _));
        Assert.True(root.TryGetProperty("buildSha", out _));
        Assert.True(root.TryGetProperty("uptime", out _));
        Assert.True(root.TryGetProperty("version", out _));

        Assert.False(root.TryGetProperty("db", out _),
            "/health?simple=1 must NOT carry the Wave-7 'db' detail object.");
        Assert.False(root.TryGetProperty("activeGames", out _),
            "/health?simple=1 must NOT carry the Wave-7 'activeGames' field.");
    }
}
