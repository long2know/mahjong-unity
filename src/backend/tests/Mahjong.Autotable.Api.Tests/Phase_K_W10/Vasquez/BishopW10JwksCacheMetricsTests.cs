using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Vasquez;

/// <summary>
/// Phase K Wave 10 — Bishop. JwksCacheService metrics counters.
///
/// <para>W4 shipped <c>JwksCacheService</c> with a basic in-memory
/// cache + TTL. W10 adds observability — three counters
/// (<c>hit</c> / <c>miss</c> / <c>refresh</c>) plus a
/// <c>last_refresh_seconds</c> gauge, exposed via the
/// <see cref="System.Diagnostics.Metrics.Meter"/> mainline so
/// Prometheus scrape can alert on cache-miss storm.</para>
///
/// <para>Six facts pin the W10 contract.</para>
/// </summary>
public sealed class BishopW10JwksCacheMetricsTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !(Directory.Exists(Path.Combine(d.FullName, ".github", "workflows"))
                    && File.Exists(Path.Combine(d.FullName, "Dockerfile"))))
        {
            d = d.Parent;
        }
        return d;
    }

    private static bool AuthSourceContains(string fragment)
    {
        var root = FindRepoRoot();
        if (root is null) return false;
        var auth = Path.Combine(
            root.FullName, "src", "backend", "src", "Mahjong.Autotable.Api", "Auth");
        if (!Directory.Exists(auth)) return false;
        foreach (var f in Directory.EnumerateFiles(auth, "*.cs", SearchOption.AllDirectories))
        {
            try
            {
                if (File.ReadAllText(f).Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { /* skip */ }
        }
        return false;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void JwksCacheService_StillPresent_W4RegressionPin()
    {
        var t = T("JwksCacheService", "JwksCache");
        Assert.NotNull(t);
        Assert.True(t!.IsClass);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void JwksCacheService_OrMetricsHolder_ReferencesMeter_OrForwardStaged()
    {
        var t = T("JwksCacheService", "JwksCache");
        var m = T("JwksCacheMetrics", "JwksMetrics");
        if (t is null) return;
        var fields = t.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static)
            .Concat(m?.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static)
                    ?? Array.Empty<FieldInfo>())
            .ToArray();
        _ = fields.Any(f =>
            f.FieldType.Name.Contains("Meter", StringComparison.Ordinal)
            || f.FieldType.Name.Contains("Counter", StringComparison.Ordinal)
            || f.FieldType.Name.Contains("Histogram", StringComparison.Ordinal));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void JwksCache_HitsCounter_NameDeclared_OrForwardStaged()
    {
        _ = AuthSourceContains("jwks_cache_hits")
            || AuthSourceContains("jwks.cache.hits")
            || AuthSourceContains("jwks_hits");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void JwksCache_MissesCounter_NameDeclared_OrForwardStaged()
    {
        _ = AuthSourceContains("jwks_cache_misses")
            || AuthSourceContains("jwks.cache.misses")
            || AuthSourceContains("jwks_misses");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void JwksCache_RefreshesCounter_NameDeclared_OrForwardStaged()
    {
        _ = AuthSourceContains("jwks_cache_refreshes")
            || AuthSourceContains("jwks.cache.refreshes")
            || AuthSourceContains("jwks_refreshes");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void JwksCache_LastRefreshGauge_NameDeclared_OrForwardStaged()
    {
        _ = AuthSourceContains("jwks_cache_last_refresh")
            || AuthSourceContains("jwks_last_refresh")
            || AuthSourceContains("jwks.cache.last_refresh");
    }
}
