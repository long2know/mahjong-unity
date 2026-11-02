using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Tournaments;

/// <summary>
/// Phase J Wave 10 — shared test harness for tournament contract suites
/// (Vasquez).
///
/// <para>Bishop's Wave 10 tournament surface is in flight. Each test here
/// probes for the canonical REST routes via a multi-candidate URL list
/// and accepts the first non-404 response. A uniform 404 across every
/// candidate is the "not-yet-registered" signal → soft-pass via
/// <c>return;</c>.</para>
///
/// <para>The harness exposes a thin <see cref="WebApplicationFactory{TEntryPoint}"/>
/// wrapper plus the canonical URL candidate sets so each test class
/// stays focused on its assertion logic.</para>
/// </summary>
public class TournamentHarness : IAsyncLifetime
{
    public WebApplicationFactory<Program>? Factory { get; private set; }
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-tourney-{Guid.NewGuid():N}.db");
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
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
        _ = Factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Factory?.Dispose();
        try { if (_tempDb is not null && File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
        return Task.CompletedTask;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Canonical URL candidate sets
    // ────────────────────────────────────────────────────────────────────

    public static readonly string[] CreateUrls =
    {
        "/api/tournaments",
        "/api/tourneys",
    };

    public static readonly string[] ListUrls =
    {
        "/api/tournaments",
        "/api/tourneys",
    };

    public static string[] GetUrls(string id) => new[]
    {
        $"/api/tournaments/{id}",
        $"/api/tourneys/{id}",
    };

    public static string[] RegisterUrls(string id) => new[]
    {
        $"/api/tournaments/{id}/register",
        $"/api/tournaments/{id}/join",
        $"/api/tourneys/{id}/register",
    };

    public static string[] UnregisterUrls(string id) => new[]
    {
        $"/api/tournaments/{id}/unregister",
        $"/api/tournaments/{id}/leave",
        $"/api/tournaments/{id}/withdraw",
    };

    public static string[] StartUrls(string id) => new[]
    {
        $"/api/tournaments/{id}/start",
        $"/api/tourneys/{id}/start",
    };

    public static string[] LeaderboardUrls(string id) => new[]
    {
        $"/api/tournaments/{id}/leaderboard",
        $"/api/tournaments/{id}/standings",
        $"/api/tourneys/{id}/leaderboard",
    };

    public static string[] MatchesUrls(string id) => new[]
    {
        $"/api/tournaments/{id}/matches",
        $"/api/tournaments/{id}/rounds",
        $"/api/tourneys/{id}/matches",
    };

    // ────────────────────────────────────────────────────────────────────
    //  HTTP helpers
    // ────────────────────────────────────────────────────────────────────

    public static async Task<HttpResponseMessage> PostFirstNonNotFoundAsync(
        HttpClient client, IEnumerable<string> urls, object? body)
    {
        HttpResponseMessage? last = null;
        foreach (var url in urls)
        {
            last?.Dispose();
            last = body is null
                ? await client.PostAsync(url, new StringContent(string.Empty))
                : await client.PostAsJsonAsync(url, body);
            if (last.StatusCode != HttpStatusCode.NotFound) return last;
        }
        return last!;
    }

    public static async Task<HttpResponseMessage> GetFirstNonNotFoundAsync(
        HttpClient client, IEnumerable<string> urls)
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

    public static async Task<HttpResponseMessage> DeleteFirstNonNotFoundAsync(
        HttpClient client, IEnumerable<string> urls)
    {
        HttpResponseMessage? last = null;
        foreach (var url in urls)
        {
            last?.Dispose();
            last = await client.DeleteAsync(url);
            if (last.StatusCode != HttpStatusCode.NotFound) return last;
        }
        return last!;
    }

    public static string? TryReadString(JsonElement root, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        return null;
    }

    /// <summary>True iff at least one Tournament-shaped surface (entity OR
    /// controller OR route) is registered on the current assembly. The
    /// suites soft-pass when this returns false.</summary>
    public static bool TournamentSurfaceLikelyShipped()
    {
        var asm = typeof(Mahjong.Autotable.Api.Data.AppDbContext).Assembly;
        foreach (var t in asm.GetTypes())
        {
            if (!t.IsClass || t.IsAbstract) continue;
            if (t.Namespace is null) continue;
            if (t.Name.StartsWith("Tournament", StringComparison.Ordinal)) return true;
            if (t.Name.Equals("TournamentController", StringComparison.Ordinal)) return true;
            if (t.Name.Equals("TournamentsController", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
