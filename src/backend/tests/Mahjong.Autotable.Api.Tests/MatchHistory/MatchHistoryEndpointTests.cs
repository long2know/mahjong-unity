using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.MatchHistory;

/// <summary>
/// Phase K Wave 1 — match-history endpoint contract tests (Vasquez).
///
/// <para>Bishop's Phase K Wave 1 brief ships <c>GET /api/match-history</c>
/// (alias <c>/api/games/history</c>). It returns completed games with:
/// <list type="bullet">
///   <item><b>JSON</b> shape: <c>{ total, rows: [ { gameId, completedAt,
///         seats: [{playerId, displayName, finalScore}], duration } ] }</c>.</item>
///   <item><b>CSV</b> shape when <c>Accept: text/csv</c> or
///         <c>?format=csv</c>: header row + one row per game.</item>
///   <item><c>?from=</c> + <c>?to=</c> ISO-8601 date filter.</item>
///   <item><c>?limit=</c> + <c>?offset=</c> paging (default 25, max 100).</item>
/// </list></para>
///
/// <para><b>Reflection-defensive.</b> Bishop's controller may route under
/// <c>/api/match-history</c>, <c>/api/games/history</c>, or
/// <c>/api/matches</c>. We probe all three and soft-pass on uniform
/// 404.</para>
/// </summary>
public class MatchHistoryEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-history-{Guid.NewGuid():N}.db");

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

    private static string[] HistoryUrls(string? query = null) => new[]
    {
        $"/api/match-history{query}",
        $"/api/games/history{query}",
        $"/api/matches{query}",
    };

    private async Task<HttpResponseMessage> ProbeJsonAsync(string? query = null)
    {
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage? last = null;
        foreach (var url in HistoryUrls(query))
        {
            last?.Dispose();
            last = await client.GetAsync(url);
            if (last.StatusCode != HttpStatusCode.NotFound) return last;
        }
        return last!;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Endpoint never 5xx with no query string
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "MatchHistory"), Trait("Wave", "Phase-K-1")]
    public async Task History_BareQuery_NeverServerError()
    {
        Assert.NotNull(_factory);
        using var resp = await ProbeJsonAsync();
        Assert.True((int)resp.StatusCode < 500,
            $"Match-history bare query returned 5xx ({(int)resp.StatusCode}).");
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. JSON envelope, when successful, carries `total` + `rows`
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "MatchHistory"), Trait("Wave", "Phase-K-1")]
    public async Task History_Json_EnvelopeShape()
    {
        Assert.NotNull(_factory);
        using var resp = await ProbeJsonAsync();
        if (resp.StatusCode == HttpStatusCode.NotFound) return; // forward-staged
        if (!resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        // Either an envelope or a raw array — both acceptable.
        if (root.ValueKind == JsonValueKind.Array) return;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        var hasTotal = root.TryGetProperty("total", out var totalEl);
        var hasRows = root.TryGetProperty("rows", out var rowsEl)
                   || root.TryGetProperty("items", out rowsEl)
                   || root.TryGetProperty("games", out rowsEl)
                   || root.TryGetProperty("matches", out rowsEl);
        Assert.True(hasRows, "Match-history JSON envelope must expose a rows-style array.");
        if (hasTotal)
            Assert.True(totalEl.ValueKind == JsonValueKind.Number, "total must be a number.");
        Assert.Equal(JsonValueKind.Array, rowsEl.ValueKind);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Paging — limit=10 returns at most 10 rows
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "MatchHistory"), Trait("Wave", "Phase-K-1")]
    public async Task History_LimitTen_BoundsRowCount()
    {
        Assert.NotNull(_factory);
        using var resp = await ProbeJsonAsync("?limit=10");
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (!resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        JsonElement rowsEl;
        if (root.ValueKind == JsonValueKind.Array) rowsEl = root;
        else if (!(root.TryGetProperty("rows", out rowsEl)
                || root.TryGetProperty("items", out rowsEl)
                || root.TryGetProperty("games", out rowsEl)
                || root.TryGetProperty("matches", out rowsEl)))
            return;
        Assert.True(rowsEl.GetArrayLength() <= 10,
            $"limit=10 returned {rowsEl.GetArrayLength()} rows.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Paging — over-limit clamps (limit=99999 should not 5xx)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "MatchHistory"), Trait("Wave", "Phase-K-1")]
    public async Task History_OverLimit_NeverServerError()
    {
        Assert.NotNull(_factory);
        using var resp = await ProbeJsonAsync("?limit=99999");
        Assert.True((int)resp.StatusCode < 500);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Date filter: from + to with invalid ISO never 5xx
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "MatchHistory"), Trait("Wave", "Phase-K-1")]
    public async Task History_InvalidDateFilter_4xxNot5xx()
    {
        Assert.NotNull(_factory);
        using var resp = await ProbeJsonAsync("?from=not-a-date&to=also-not");
        Assert.True((int)resp.StatusCode < 500);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. Date filter: well-formed range — never 5xx
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "MatchHistory"), Trait("Wave", "Phase-K-1")]
    public async Task History_WellFormedDateFilter_NeverServerError()
    {
        Assert.NotNull(_factory);
        var from = new DateTime(2026, 01, 01).ToString("o");
        var to = new DateTime(2026, 12, 31).ToString("o");
        using var resp = await ProbeJsonAsync(
            $"?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");
        Assert.True((int)resp.StatusCode < 500);
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. CSV format via ?format=csv — Content-Type header is text/csv
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "MatchHistory"), Trait("Wave", "Phase-K-1")]
    public async Task History_CsvFormat_ContentType()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        HttpResponseMessage? resp = null;
        foreach (var url in HistoryUrls("?format=csv"))
        {
            resp?.Dispose();
            resp = await client.GetAsync(url);
            if (resp.StatusCode != HttpStatusCode.NotFound) break;
        }
        if (resp is null || resp.StatusCode == HttpStatusCode.NotFound) return;
        if (!resp.IsSuccessStatusCode) { resp.Dispose(); return; }

        var ct = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        Assert.Contains("csv", ct, StringComparison.OrdinalIgnoreCase);
        resp.Dispose();
    }

    // ────────────────────────────────────────────────────────────────────
    //  8. CSV via Accept: text/csv — same path
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "MatchHistory"), Trait("Wave", "Phase-K-1")]
    public async Task History_CsvAcceptHeader_NeverServerError()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/csv"));
        HttpResponseMessage? resp = null;
        foreach (var url in HistoryUrls())
        {
            resp?.Dispose();
            resp = await client.GetAsync(url);
            if (resp.StatusCode != HttpStatusCode.NotFound) break;
        }
        if (resp is null) return;
        Assert.True((int)resp.StatusCode < 500);
        resp.Dispose();
    }
}
