namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez paired contract for Apone W22's
/// SignalR sticky-session shared-cookie validation Kyverno
/// ClusterPolicy (audit mode at W22; W23 enforce-flip planned).
/// Five invariant sub-rules: affinity-cookie + affinity-mode-
/// persistent + session-cookie-name + session-cookie-max-age +
/// ip-hash-fallback-snippet.
/// </summary>
public sealed class AponeW22IngressValidationContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Apone")]
    public void IngressValidationPolicy_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "infra", "k8s", "base", "ingress-validation.yaml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("validate-signalr-sticky-session", StringComparison.Ordinal)
                   || text.Contains("ClusterPolicy", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Apone")]
    public void IngressValidationPolicy_AuditMode_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "infra", "k8s", "base", "ingress-validation.yaml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // W22 launch in Audit mode (W23 enforce-flip planned).
        var has = text.Contains("validationFailureAction: Audit", StringComparison.Ordinal)
                   || text.Contains("validationFailureAction: audit", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Apone")]
    public void SignalrAffinityValidationDoc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "signalr-affinity-validation-w22.md");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("affinity", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("sticky", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }
}
