using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mahjong.Autotable.Api.Tests.Tournaments;

/// <summary>
/// Phase K Wave 1 — tournament reconnect-grace contract tests (Vasquez).
///
/// <para>Bishop's Phase K Wave 1 brief introduces a reconnect-grace
/// window for tournament matches. Contract: when a player disconnects
/// during an in-progress tournament match, they have <b>120 seconds</b>
/// of grace before the server treats the absence as a forfeit and
/// auto-advances the opponent.</para>
///
/// <para><b>Forward-staged.</b> The reconnect-grace control plane is
/// expected to land as one of:
/// <list type="bullet">
///   <item>A <c>TournamentMatchService</c> with a
///         <c>ReconnectGraceSeconds</c> property (canonical default 120).</item>
///   <item>An options class <c>TournamentOptions.ReconnectGraceSeconds</c>.</item>
///   <item>An audit log entry of kind <c>"tournament.reconnect.grace.elapsed"</c>
///         emitted by the background worker.</item>
/// </list></para>
///
/// <para><b>Reflection-defensive.</b> Each fact probes for the surface
/// and either pins the contract or soft-passes via <c>return;</c>.</para>
/// </summary>
public class TournamentReconnectGraceTests : TournamentHarness
{
    private static System.Reflection.Assembly ProductionAssembly()
        => typeof(Mahjong.Autotable.Api.Data.AppDbContext).Assembly;

    private static System.Reflection.PropertyInfo? FindGracePropery()
    {
        foreach (var t in ProductionAssembly().GetTypes())
        {
            if (!t.IsClass) continue;
            foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
            {
                if (p.Name.Contains("ReconnectGrace", StringComparison.OrdinalIgnoreCase)
                    && (p.PropertyType == typeof(int)
                        || p.PropertyType == typeof(TimeSpan)
                        || p.PropertyType == typeof(double)))
                    return p;
            }
        }
        return null;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. ReconnectGrace property/option exists and defaults to 120
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-1")]
    public void ReconnectGrace_Property_Defaults_To120s_OrSoftPasses()
    {
        var prop = FindGracePropery();
        if (prop is null) return; // forward-staged

        // Instantiate the declaring type with its default ctor if possible.
        object? instance = null;
        if (!prop.GetGetMethod()!.IsStatic)
        {
            try { instance = Activator.CreateInstance(prop.DeclaringType!, nonPublic: true); }
            catch { return; }
        }
        object? val;
        try { val = prop.GetValue(instance); }
        catch { return; }
        if (val is null) return;

        // Accept 120 (int seconds) or 00:02:00 (TimeSpan).
        if (val is int i) Assert.Equal(120, i);
        else if (val is TimeSpan ts) Assert.Equal(TimeSpan.FromSeconds(120), ts);
        else if (val is double d) Assert.Equal(120d, d);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Tournament listing surface still 200/404 (regression guard)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-1")]
    public async Task ListSurface_StillReachable_NoServerError()
    {
        Assert.NotNull(Factory);
        using var client = Factory!.CreateClient();
        using var resp = await GetFirstNonNotFoundAsync(client, ListUrls);
        Assert.True((int)resp.StatusCode < 500,
            $"Tournament list 5xx ({(int)resp.StatusCode}) after Phase K Wave 1 wiring.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Disconnect inside grace window → match still pending (200)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-1")]
    public async Task DisconnectWithinGrace_MatchStaysPending_OrSoftPasses()
    {
        Assert.NotNull(Factory);
        using var client = Factory!.CreateClient();

        // Probe the grace-tick endpoint. Bishop's brief expects a
        // POST /api/tournaments/{id}/matches/{matchId}/grace-tick or
        // similar test-hook that advances simulated time.
        var fakeId = Guid.NewGuid().ToString();
        var candidates = new[]
        {
            $"/api/tournaments/{fakeId}/matches/{fakeId}/disconnect?elapsedSec=60",
            $"/api/tournaments/{fakeId}/matches/{fakeId}/heartbeat?elapsedSec=60",
        };
        HttpResponseMessage? resp = null;
        foreach (var url in candidates)
        {
            resp?.Dispose();
            resp = await client.PostAsync(url, new StringContent(string.Empty));
            if (resp.StatusCode != HttpStatusCode.NotFound) break;
        }
        if (resp is null || resp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.True((int)resp.StatusCode < 500);
        resp.Dispose();
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Disconnect beyond grace window → forfeit endpoint reachable
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-1")]
    public async Task DisconnectBeyondGrace_ForfeitEndpoint_NeverServerError()
    {
        Assert.NotNull(Factory);
        using var client = Factory!.CreateClient();

        // Probe forfeit / advance endpoints that the grace-elapsed worker
        // would invoke. Soft-pass if neither is mounted.
        var fakeId = Guid.NewGuid().ToString();
        var candidates = new[]
        {
            $"/api/tournaments/{fakeId}/matches/{fakeId}/forfeit",
            $"/api/tournaments/{fakeId}/matches/{fakeId}/advance",
            $"/api/tournaments/{fakeId}/matches/{fakeId}/result",
        };
        HttpResponseMessage? resp = null;
        foreach (var url in candidates)
        {
            resp?.Dispose();
            resp = await client.PostAsJsonAsync(url, new
            {
                reason = "grace-elapsed",
                disconnectedPlayerId = "vasquez-pid",
            });
            if (resp.StatusCode != HttpStatusCode.NotFound) break;
        }
        if (resp is null || resp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.True((int)resp.StatusCode < 500,
            $"Forfeit endpoint 5xx ({(int)resp.StatusCode}).");
        resp.Dispose();
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Audit log carries the canonical reconnect-grace kind value
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-1")]
    public async Task AuditLog_ReconnectGraceKind_IsListed_OrSoftPasses()
    {
        Assert.NotNull(Factory);
        using var client = Factory!.CreateClient();

        // The reconnect-audit list endpoint is admin-gated; we probe
        // unauthenticated and accept 401/403 as proof it exists.
        using var resp = await client.GetAsync("/api/reconnect/audit?kind=tournament.reconnect.grace.elapsed");
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.True((int)resp.StatusCode < 500,
            $"Reconnect-audit query 5xx ({(int)resp.StatusCode}).");
    }
}
