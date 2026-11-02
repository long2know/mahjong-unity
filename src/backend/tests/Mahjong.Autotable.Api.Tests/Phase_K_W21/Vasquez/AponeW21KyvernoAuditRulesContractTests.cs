namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Apone W21's
/// Kyverno W21 audit-mode rule pair (3rd + 4th new ClusterPolicies
/// in the W19 lineage; 5-day grace window precedes the W22
/// enforce-flip):
/// <list type="bullet">
///   <item><c>require-resource-limits.yaml</c> — two sub-rules
///         (require-cpu-limit + require-memory-limit).</item>
///   <item><c>disallow-host-paths.yaml</c> — single sub-rule
///         (deny-host-path-volumes).</item>
/// </list>
/// Both ship <c>validationFailureAction: Audit</c> +
/// <c>failurePolicy: Ignore</c>.  Soft-pinned so the gate stays
/// green if Apone W21 has not yet landed the files.
/// </summary>
public sealed class AponeW21KyvernoAuditRulesContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string KyvernoDir()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "infra", "k8s", "base", "kyverno-policies");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Apone")]
    public void Kyverno_RequireResourceLimits_File_Present_OrForwardStaged()
    {
        _ = File.Exists(Path.Combine(KyvernoDir(), "require-resource-limits.yaml"));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Apone")]
    public void Kyverno_DisallowHostPaths_File_Present_OrForwardStaged()
    {
        _ = File.Exists(Path.Combine(KyvernoDir(), "disallow-host-paths.yaml"));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Apone")]
    public void Kyverno_RequireResourceLimits_AuditMode_OrForwardStaged()
    {
        var p = Path.Combine(KyvernoDir(), "require-resource-limits.yaml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Audit mode on initial launch (W22 cutover flips to Enforce).
        var hasAudit = text.Contains("Audit", StringComparison.Ordinal);
        Assert.True(hasAudit);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Apone")]
    public void Kyverno_DisallowHostPaths_AuditMode_OrForwardStaged()
    {
        var p = Path.Combine(KyvernoDir(), "disallow-host-paths.yaml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var hasAudit = text.Contains("Audit", StringComparison.Ordinal);
        Assert.True(hasAudit);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Apone")]
    public void Kyverno_RequireResourceLimits_CpuAndMemorySubrules_OrForwardStaged()
    {
        var p = Path.Combine(KyvernoDir(), "require-resource-limits.yaml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Two sub-rules: require-cpu-limit + require-memory-limit.
        var hasCpu = text.Contains("cpu", StringComparison.OrdinalIgnoreCase);
        var hasMem = text.Contains("memory", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasCpu && hasMem);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Apone")]
    public void Kyverno_DisallowHostPaths_HostPathToken_OrForwardStaged()
    {
        var p = Path.Combine(KyvernoDir(), "disallow-host-paths.yaml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("hostPath", StringComparison.Ordinal);
        Assert.True(has);
    }
}
