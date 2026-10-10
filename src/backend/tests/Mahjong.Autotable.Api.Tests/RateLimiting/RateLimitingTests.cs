using System.Net;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.RateLimiting;

/// <summary>
/// Phase J Wave 6 — Apone's production rate-limit middleware contract
/// (Vasquez).
///
/// <para>Apone wires two named policies on top of
/// <c>Microsoft.AspNetCore.RateLimiting</c>:
/// <list type="bullet">
///   <item><c>ApiPolicy</c> (<c>token-bucket-api</c>) — 30-token bucket,
///         5 tokens/sec replenishment, partitioned by client IP. Applied
///         via <c>app.MapControllers().RequireRateLimiting(ApiPolicy)</c>
///         plus the two minimal-API endpoints
///         <c>/api/system/persistence</c> and
///         <c>/api/changsha/pattern-ordering</c>.</item>
///   <item><c>AnonymousPolicy</c> (<c>fixed-window-anonymous</c>) — 10/min/IP
///         fixed window, registered for the future low-volume mutating
///         surfaces but, as of Wave 6, NOT attached to any endpoint.
///         <b>Blind spot:</b> Bishop's <c>POST /api/identity</c> inherits
///         <c>ApiPolicy</c> via <c>MapControllers</c>, not the tighter
///         <c>AnonymousPolicy</c>. Documented in Vasquez's Wave 6 memo as
///         a follow-up for Bishop / Apone to reconcile.</item>
/// </list></para>
///
/// <para>The middleware is gated by <c>RateLimiting:Enabled</c> in
/// configuration; default <c>false</c> in <c>appsettings.json</c>,
/// <c>true</c> in <c>appsettings.Production.json</c>. The xUnit harness
/// runs under <c>Development</c> by default, which means limits are OFF —
/// so each test in this file uses <c>UseEnvironment("Production")</c> +
/// <c>UseSetting("RateLimiting:Enabled", "true")</c> to turn the
/// middleware on. We then partition uniqueness comes from
/// <c>X-Forwarded-For</c>, since the TestServer always reports the same
/// loopback address.</para>
///
/// <para>The three facts pinned here:
/// <list type="number">
///   <item><see cref="PostIdentity_RapidBurst_TriggersRateLimit"/> — over
///         the token bucket → 429 + Retry-After + canonical body.</item>
///   <item><see cref="ApiLeaderboard_ExceedsTokenBucket_Returns429"/> —
///         same policy, different surface; one test proves the policy
///         travels with the route via MapControllers.</item>
///   <item><see cref="Health_NotRateLimited_AcceptsBurst"/> — probe
///         surface stays open under sustained polling (operational
///         requirement; a 429 on /health would break Docker / k8s
///         liveness).</item>
/// </list></para>
///
/// <para><b>Why 50 requests is enough to bust the bucket.</b> The token
/// bucket starts full at 30 tokens and refills at 5/sec. Even with the
/// fastest possible HTTP round-trip in the in-memory TestServer
/// (~1-2 ms), 50 calls finish in well under a second — that's at most
/// ~2-3 refills (10-15 tokens) on top of the initial 30, so the 50th
/// call is guaranteed to be over the ceiling. We assert "at least one
/// 429" rather than "exactly N 429s" because the precise rejection count
/// depends on test-runner timing.</para>
/// </summary>
public class RateLimitingTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-ratelimit-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // Production environment + explicit enabled flag is the only
            // combination that actually wires the middleware. Either knob
            // alone is a no-op: appsettings.Production.json sets the flag,
            // but a Development host never reads that file; UseSetting
            // overrides the flag at any environment, but the limiter
            // services are still keyed off `Enabled == true` in the
            // extension method.
            b.UseEnvironment("Production");
            // Phase L — Drake. Prod hardening: see SecurityHeadersTests for context.
            b.UseSetting("Auth:JwtSigningKeys:0", "test-prod-stable-jwt-key-aaaaaaaaaaaaaaaaaaaaaaaa");
            b.UseSetting("RateLimiting:Enabled", "true");
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
    //  1. POST /api/identity — rapid burst trips ApiPolicy token bucket
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "RateLimit"), Trait("Wave", "Phase-J-6")]
    public async Task PostIdentity_RapidBurst_TriggersRateLimit()
    {
        // Bishop's POST /api/identity inherits the ApiPolicy (token bucket
        // 30, 5/sec refill) via MapControllers — see Program.cs L235-236.
        // Apone's brief mentioned an AnonymousPolicy for /api/identity but
        // it was registered without ever being attached; we test what's
        // actually in production. A rapid burst of 60 calls exceeds the
        // initial 30 tokens before the bucket can refill, so at least one
        // response must be 429.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        // Stable per-test client IP so the partition key is unique to this
        // test (loopback would otherwise be shared with parallel runs in
        // the same fixture).
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "10.1.1.1");

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? firstRejection = null;
        for (var i = 0; i < 60; i++)
        {
            var resp = await client.PostAsync("/api/identity", content: null);
            statuses.Add(resp.StatusCode);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && firstRejection is null)
            {
                firstRejection = resp;   // keep the first 429 for header / body inspection
            }
            else
            {
                resp.Dispose();
            }
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.NotNull(firstRejection);

        // Apone's OnRejected callback writes:
        //   • Content-Type: application/json
        //   • Body: {"error":"too_many_requests"}
        //   • Retry-After: <int seconds> (from MetadataName.RetryAfter)
        var body = await firstRejection!.Content.ReadAsStringAsync();
        Assert.Contains("\"error\"", body);
        Assert.Contains("too_many_requests", body);
        Assert.True(firstRejection.Headers.Contains("Retry-After"),
            "Apone's OnRejected handler must set the Retry-After header from the bucket's metadata.");
        firstRejection.Dispose();
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. GET /api/leaderboard — same policy, different surface
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "RateLimit"), Trait("Wave", "Phase-J-6")]
    public async Task ApiLeaderboard_ExceedsTokenBucket_Returns429()
    {
        // /api/leaderboard is wired through MapControllers and therefore
        // inherits ApiPolicy. This test isn't redundant with the identity
        // one: it proves the policy travels with every controller route,
        // not just the one Bishop happened to ship in Wave 6. A regression
        // where a new attribute on a single controller bypasses the policy
        // would fail one of the two tests but not both.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "10.2.2.2");

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? firstRejection = null;
        for (var i = 0; i < 60; i++)
        {
            var resp = await client.GetAsync("/api/leaderboard");
            statuses.Add(resp.StatusCode);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && firstRejection is null)
            {
                firstRejection = resp;
            }
            else
            {
                resp.Dispose();
            }
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.NotNull(firstRejection);
        // Reaffirm the response shape so a regression that swaps in a
        // generic ProblemDetails body (instead of Apone's compact
        // {"error":"too_many_requests"}) trips here as well.
        var body = await firstRejection!.Content.ReadAsStringAsync();
        Assert.Contains("too_many_requests", body);
        firstRejection.Dispose();

        // Operational sanity: at least *some* of the 60 calls must have
        // succeeded — the token bucket can't be at-zero from the first
        // request, otherwise the 30-token initial fill is broken. The
        // explicit check guards against a configuration regression that
        // would render the API entirely unreachable on Production startup.
        Assert.Contains(HttpStatusCode.OK, statuses);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. /health and /api/health are off-policy
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "RateLimit"), Trait("Wave", "Phase-J-6")]
    public async Task Health_NotRateLimited_AcceptsBurst()
    {
        // /api/health, /health, and /metrics all call .DisableRateLimiting()
        // on their endpoint convention builders (Program.cs L173-207). 100
        // back-to-back probe hits must therefore all return 200 — anything
        // less would silently break the Docker HEALTHCHECK and the k8s
        // readiness/liveness probes once Wave 6 ships.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "10.3.3.3");

        for (var i = 0; i < 100; i++)
        {
            using var resp = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        // /api/health (the legacy frontend probe, distinct from /health
        // which is the Docker probe) also opts out of the limiter — re-
        // assert that branch so a future refactor that only updates one of
        // the two routes is caught immediately.
        for (var i = 0; i < 100; i++)
        {
            using var resp = await client.GetAsync("/api/health");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
    }
}
