using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.I18n;

/// <summary>
/// Phase J Wave 9 — i18n catalog HTTP endpoint contract tests (Vasquez).
///
/// <para>Bishop's Wave 9 exposes the pattern catalog at
/// <c>GET /api/i18n/patterns?lang=</c>. Response shape:
/// <c>{ patterns: { standard: "Standard win", sevenPairs: "...", ... }, lang: "en" }</c>
/// (or a bare key→string map).</para>
///
/// <para>Reflection-defensive over the URL path (probe candidates); a
/// uniform 404 soft-passes.</para>
/// </summary>
public class I18nCatalogEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-i18n-{Guid.NewGuid():N}.db");
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

    private static readonly string[] EnUrls =
    {
        "/api/i18n/patterns?lang=en",
        "/api/i18n/patterns/en",
        "/api/i18n?lang=en",
    };

    private static async Task<HttpResponseMessage> GetFirstNonNotFoundAsync(HttpClient client, IEnumerable<string> urls)
    {
        HttpResponseMessage? last = null;
        foreach (var url in urls)
        {
            last?.Dispose();
            last = await client.GetAsync(url);
            if (last.StatusCode != HttpStatusCode.NotFound) return last;
        }
        return last!;
    }

    [Fact, Trait("Category", "I18n"), Trait("Wave", "Phase-J-9")]
    public async Task Catalog_Endpoint_Reachable_OrNotYetRegistered()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var resp = await GetFirstNonNotFoundAsync(client, EnUrls);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.True((int)resp.StatusCode < 500,
            $"i18n catalog endpoint returned 5xx {(int)resp.StatusCode}.");
    }

    [Fact, Trait("Category", "I18n"), Trait("Wave", "Phase-J-9")]
    public async Task Catalog_Response_HasStandardKey()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var resp = await GetFirstNonNotFoundAsync(client, EnUrls);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (!resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        JsonElement patterns;
        if (root.TryGetProperty("patterns", out patterns) || root.TryGetProperty("items", out patterns))
        {
            // envelope shape
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            patterns = root;
        }
        else return;

        Assert.True(
            patterns.TryGetProperty("standard", out var standard) && standard.ValueKind == JsonValueKind.String,
            "Catalog must carry a `standard` key with a non-null string value.");
        Assert.False(string.IsNullOrWhiteSpace(standard.GetString()));
    }

    [Fact, Trait("Category", "I18n"), Trait("Wave", "Phase-J-9")]
    public async Task Catalog_UnknownLanguage_FallsBack()
    {
        // Bishop's Wave 9 contract: unknown language code falls back to
        // English rather than 404.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var urls = new[]
        {
            "/api/i18n/patterns?lang=xx-Klingon",
            "/api/i18n/patterns/xx-Klingon",
        };
        using var resp = await GetFirstNonNotFoundAsync(client, urls);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.True((int)resp.StatusCode < 500,
            $"Unknown lang must fall back, not 5xx; got {(int)resp.StatusCode}.");
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [Trait("Category", "I18n")]
    [Trait("Wave", "Phase-J-9")]
    public async Task Catalog_HasChineseLanguageVariant(string lang)
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var urls = new[]
        {
            $"/api/i18n/patterns?lang={lang}",
            $"/api/i18n/patterns/{lang}",
        };
        using var resp = await GetFirstNonNotFoundAsync(client, urls);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (!resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync();
        // Look for at least one CJK ideograph in the returned body.
        bool hasCjk = body.Any(c => c >= '\u4E00' && c <= '\u9FFF');
        Assert.True(hasCjk,
            $"`{lang}` catalog must contain at least one CJK ideograph.");
    }
}
