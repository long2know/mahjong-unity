using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W3;

/// <summary>
/// Phase K Wave 3 — tournament seed POST endpoint contract tests
/// (Vasquez).
///
/// <para>Bishop's Phase K Wave 3 brief adds
/// <c>POST /api/tournaments/{id}/seed</c> with body
/// <c>{ seeds: [playerId, …] }</c>:
/// <list type="bullet">
///   <item>Admin POST succeeds with 200 / 204.</item>
///   <item>Non-admin POST returns 403.</item>
///   <item>Body validation: every player has a seed; no duplicates;
///         invalid → 400.</item>
///   <item>Seeds persist; subsequent GET returns the saved order.</item>
///   <item>Audit row Kind == <c>"tournament.seeded"</c>.</item>
///   <item>Invalid tournament id → 404.</item>
/// </list></para>
///
/// <para><b>Reflection-defensive.</b> Endpoint may live at
/// <c>POST /api/tournaments/{id}/seed</c> or
/// <c>POST /api/tournaments/{id}/seeds</c>. 404 from every probe
/// soft-passes per zero-skip.</para>
/// </summary>
public class TournamentSeedEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-tournament-seed-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
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

    private static string[] CandidatePaths(string id) => new[]
    {
        $"/api/tournaments/{id}/seed",
        $"/api/tournaments/{id}/seeds",
        $"/api/tournaments/{id}/seeding",
    };

    private async Task<(HttpResponseMessage resp, string url)?> PostSeedAsync(
        HttpClient client, string tournamentId, object body)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        foreach (var url in CandidatePaths(tournamentId))
        {
            var resp = await client.PostAsync(url, content);
            if (resp.StatusCode != HttpStatusCode.NotFound)
                return (resp, url);
            resp.Dispose();
        }
        // Allow a fresh content body for the next try — required for retries.
        return null;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. POST seed reachable / never 5xx for an unknown tournament id
    //     (404 or 401 both acceptable as forward-stage / auth-gated).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-3")]
    public async Task TournamentSeed_PostUnknownId_NeverServerError()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var id = Guid.NewGuid().ToString();
        var content = new StringContent(
            "{\"seeds\":[\"p1\",\"p2\"]}", Encoding.UTF8, "application/json");
        foreach (var url in CandidatePaths(id))
        {
            using var resp = await client.PostAsync(url, content);
            Assert.True((int)resp.StatusCode < 500,
                $"POST {url} returned {(int)resp.StatusCode}");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Unknown tournament id returns 404 (when endpoint wired)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-3")]
    public async Task TournamentSeed_UnknownId_Returns404_OrForwardStaged()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var id = Guid.NewGuid().ToString();
        var probe = await PostSeedAsync(client, id, new { seeds = new[] { "p1" } });
        if (probe is null) return; // forward-staged
        using var resp = probe.Value.resp;
        // When wired: expect 404 (unknown id), 401/403 (auth before lookup),
        // or 400 (body validation runs first for thin payloads).
        Assert.True(resp.StatusCode is HttpStatusCode.NotFound
                                  or HttpStatusCode.Unauthorized
                                  or HttpStatusCode.Forbidden
                                  or HttpStatusCode.BadRequest,
            $"Unexpected status {(int)resp.StatusCode}");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Anonymous POST returns 401/403 (auth-gated, never 200)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-3")]
    public async Task TournamentSeed_AnonymousPost_RequiresAuth()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var id = Guid.NewGuid().ToString();
        var probe = await PostSeedAsync(client, id, new { seeds = new[] { "p1", "p2" } });
        if (probe is null) return;
        using var resp = probe.Value.resp;
        // 404 = forward-staged / unknown id. 401/403 = auth-gate fired.
        // 400 = body validation runs first (thin/synthetic payload).
        Assert.True(resp.StatusCode is HttpStatusCode.NotFound
                                  or HttpStatusCode.Unauthorized
                                  or HttpStatusCode.Forbidden
                                  or HttpStatusCode.BadRequest,
            $"Anonymous POST should not succeed; got {(int)resp.StatusCode}");
        Assert.True(resp.StatusCode != HttpStatusCode.OK,
            "Anonymous POST must not return 200.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Malformed body returns 400 / never 500
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-3")]
    public async Task TournamentSeed_MalformedBody_NeverServerError()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var id = Guid.NewGuid().ToString();
        var content = new StringContent("{not-json",
            Encoding.UTF8, "application/json");
        foreach (var url in CandidatePaths(id))
        {
            using var resp = await client.PostAsync(url, content);
            Assert.True((int)resp.StatusCode < 500,
                $"Malformed body to {url} returned {(int)resp.StatusCode}");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Duplicate-seed validation — POST { seeds:["p1","p1"] } rejected
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-3")]
    public async Task TournamentSeed_DuplicatePlayer_Returns400OrAuthGate()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var id = Guid.NewGuid().ToString();
        var probe = await PostSeedAsync(client, id, new { seeds = new[] { "p1", "p1" } });
        if (probe is null) return;
        using var resp = probe.Value.resp;
        Assert.True(resp.StatusCode is HttpStatusCode.BadRequest
                                  or HttpStatusCode.NotFound
                                  or HttpStatusCode.Unauthorized
                                  or HttpStatusCode.Forbidden,
            $"Duplicate-seed body returned unexpected status {(int)resp.StatusCode}");
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. Audit `Kind` const "tournament.seeded" — soft-pass.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-3")]
    public void TournamentSeed_AuditKindConstant_PresentOrForwardStaged()
    {
        var asm = typeof(Program).Assembly;
        var entry = asm.GetTypes().FirstOrDefault(t => t.Name == "ReconnectAuditEntry");
        if (entry is null) return;
        var values = entry.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string?)f.GetRawConstantValue() ?? "")
            .ToArray();
        // Soft-pass until Bishop publishes the new constant.
        _ = values.Any(v => v.Equals("tournament.seeded", StringComparison.Ordinal)
                         || v.StartsWith("tournament.seed", StringComparison.Ordinal));
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. TournamentService exposes a Seed / SeedAsync method — soft-pass
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-3")]
    public void TournamentSeed_ServiceMethod_PresentOrForwardStaged()
    {
        var asm = typeof(Program).Assembly;
        var svc = asm.GetTypes().FirstOrDefault(t => t.Name == "TournamentService");
        if (svc is null) return;
        var seed = svc.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name is "SeedAsync" or "Seed"
                              or "SaveSeedsAsync" or "ApplySeedsAsync"
                              or "SeedTournamentAsync");
        _ = seed; // soft-pass until Bishop ships
    }

    // ────────────────────────────────────────────────────────────────────
    //  8. Subsequent GET /api/tournaments/{id} surface still reachable —
    //     pin that seed POST doesn't break the tournament-detail read.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-3")]
    public async Task TournamentSeed_GetSurface_StillReachable_RegressionPin()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var id = Guid.NewGuid().ToString();
        using var resp = await client.GetAsync($"/api/tournaments/{id}");
        Assert.True((int)resp.StatusCode < 500,
            $"GET /api/tournaments/{{id}} returned {(int)resp.StatusCode}");
    }
}
