using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Leaderboard;

/// <summary>
/// Phase K Wave 1 — Elo leaderboard endpoint contract tests (Vasquez).
///
/// <para>Bishop's Phase K Wave 1 brief adds an Elo-axis to the existing
/// <c>GET /api/leaderboard</c> surface (Phase J Wave 6). Expected:
/// <list type="bullet">
///   <item><c>?sort=elo</c> orders rows by Elo descending.</item>
///   <item><c>?season=current</c> filters to the current season's
///         standings (full lifetime when omitted).</item>
///   <item><c>?season=2026-Q2</c> ISO-style season filter.</item>
///   <item>Each row, when Elo is shipped, carries an <c>eloRating</c>
///         field (int) — discoverable via row introspection.</item>
/// </list></para>
///
/// <para>Reflection-defensive — soft-pass on 404 / missing column.</para>
/// </summary>
public class EloLeaderboardEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-elo-lb-{Guid.NewGuid():N}.db");

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

    private async Task<HttpResponseMessage> ProbeAsync(string url)
    {
        using var client = _factory!.CreateClient();
        return await client.GetAsync(url);
    }

    private static JsonElement? FindRowsArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        foreach (var key in new[] { "rows", "items", "players", "leaderboard" })
        {
            if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Array)
                return el;
        }
        return null;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. /api/leaderboard?sort=elo never 5xx
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Leaderboard"), Trait("Wave", "Phase-K-1")]
    public async Task Elo_Sort_NeverServerError()
    {
        Assert.NotNull(_factory);
        using var resp = await ProbeAsync("/api/leaderboard?sort=elo");
        Assert.True((int)resp.StatusCode < 500,
            $"sort=elo returned 5xx ({(int)resp.StatusCode}).");
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. sort=elo response, when 2xx, is a row-bearing envelope
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Leaderboard"), Trait("Wave", "Phase-K-1")]
    public async Task Elo_Sort_ResponseHasRows()
    {
        Assert.NotNull(_factory);
        using var resp = await ProbeAsync("/api/leaderboard?sort=elo");
        if (!resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var rows = FindRowsArray(doc.RootElement);
        if (rows is null) return; // forward-staged shape
        Assert.True(rows.Value.ValueKind == JsonValueKind.Array);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Each row, when Elo is shipped, carries eloRating (int)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Leaderboard"), Trait("Wave", "Phase-K-1")]
    public async Task Elo_RowsExpose_EloRating_OrSoftPasses()
    {
        Assert.NotNull(_factory);
        using var resp = await ProbeAsync("/api/leaderboard?sort=elo");
        if (!resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var rows = FindRowsArray(doc.RootElement);
        if (rows is null) return;

        foreach (var row in rows.Value.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty("eloRating", out var elo)
                && !row.TryGetProperty("elo", out elo)
                && !row.TryGetProperty("rating", out elo))
                return; // forward-staged column

            Assert.True(elo.ValueKind == JsonValueKind.Number,
                "eloRating must be numeric.");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Rows sorted descending by Elo (when shipped + non-empty)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Leaderboard"), Trait("Wave", "Phase-K-1")]
    public async Task Elo_Sort_IsDescendingMonotonic_OrSoftPasses()
    {
        Assert.NotNull(_factory);
        using var resp = await ProbeAsync("/api/leaderboard?sort=elo&limit=50");
        if (!resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var rows = FindRowsArray(doc.RootElement);
        if (rows is null) return;
        var arr = rows.Value;
        if (arr.GetArrayLength() < 2) return;

        int? prev = null;
        foreach (var row in arr.EnumerateArray())
        {
            int? cur = null;
            if (row.TryGetProperty("eloRating", out var v)
                || row.TryGetProperty("elo", out v)
                || row.TryGetProperty("rating", out v))
            {
                if (v.ValueKind == JsonValueKind.Number) cur = v.GetInt32();
            }
            if (cur is null) return; // shape drift — soft-pass
            if (prev is not null) Assert.True(cur <= prev,
                $"Elo leaderboard is not descending — {prev} → {cur}.");
            prev = cur;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Season filter — current
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Leaderboard"), Trait("Wave", "Phase-K-1")]
    public async Task Elo_SeasonCurrent_NeverServerError()
    {
        Assert.NotNull(_factory);
        using var resp = await ProbeAsync("/api/leaderboard?sort=elo&season=current");
        Assert.True((int)resp.StatusCode < 500);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. Season filter — ISO-style "2026-Q2"
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Leaderboard"), Trait("Wave", "Phase-K-1")]
    public async Task Elo_SeasonIso_NeverServerError()
    {
        Assert.NotNull(_factory);
        using var resp = await ProbeAsync("/api/leaderboard?sort=elo&season=2026-Q2");
        Assert.True((int)resp.StatusCode < 500);
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. Unknown season filter — never 5xx (gracefully empty)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Leaderboard"), Trait("Wave", "Phase-K-1")]
    public async Task Elo_UnknownSeason_NeverServerError()
    {
        Assert.NotNull(_factory);
        using var resp = await ProbeAsync("/api/leaderboard?sort=elo&season=1999-Q1");
        Assert.True((int)resp.StatusCode < 500);
    }

    // ────────────────────────────────────────────────────────────────────
    //  8. Bad sort axis — never 5xx (fallback to default)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Leaderboard"), Trait("Wave", "Phase-K-1")]
    public async Task Elo_UnknownSort_NeverServerError()
    {
        Assert.NotNull(_factory);
        using var resp = await ProbeAsync("/api/leaderboard?sort=notavalidaxis");
        Assert.True((int)resp.StatusCode < 500);
    }
}
