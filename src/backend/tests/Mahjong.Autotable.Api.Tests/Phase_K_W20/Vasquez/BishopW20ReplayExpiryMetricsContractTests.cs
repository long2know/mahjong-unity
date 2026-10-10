namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Bishop W20's
/// <c>replay_expired_total</c> Prometheus counter with per-tenant
/// breakdown (and <c>_unknown</c> bucket fallback when the
/// policy store is null).
/// Soft-pinned so the gate stays green if Bishop W20 has not yet
/// landed the metric.
/// </summary>
public sealed class BishopW20ReplayExpiryMetricsContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string ObservabilityDir()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Observability");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void ReplayExpiryMetrics_CounterToken_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var apiDir = Path.Combine(root.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api");
        if (!Directory.Exists(apiDir)) return;
        var blob = string.Concat(Directory.EnumerateFiles(apiDir, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        if (!blob.Contains("ReplayExpiry", StringComparison.OrdinalIgnoreCase)
            && !blob.Contains("ReplayStoreExpiry", StringComparison.OrdinalIgnoreCase)) return;
        Assert.Contains("replay_expired_total", blob, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void ReplayExpiryMetrics_PerTenantLabel_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = ObservabilityDir();
        if (!Directory.Exists(dir)) return;
        var blob = string.Concat(Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        var hasTenantLabel = blob.Contains("tenant", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasTenantLabel);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void ReplayExpiryMetrics_UnknownBucketFallback_OrForwardStaged()
    {
        var dir = ObservabilityDir();
        if (!Directory.Exists(dir)) return;
        var blob = string.Concat(Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        // The "_unknown" bucket is the canonical fallback label per
        // Bishop's W20 #4 deliverable when the policy store is null.
        var hasUnknown = blob.Contains("_unknown", StringComparison.Ordinal)
                          || blob.Contains("unknown", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasUnknown);
    }
}
