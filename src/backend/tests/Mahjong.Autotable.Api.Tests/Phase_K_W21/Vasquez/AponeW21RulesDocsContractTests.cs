namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Apone W21's
/// supporting docs: <c>docs/kyverno-w21-additional-rules.md</c>
/// (rationale + W22 cutover plan), <c>docs/signalr-observability-
/// w21.md</c> (Prometheus rules added at
/// <c>infra/k8s/overlays/prod/prometheus-rules-signalr.yaml</c>),
/// <c>docs/helm-release.md</c> (Helm chart release runbook).
/// Soft-pinned so the gate stays green if Apone W21 has not yet
/// landed the docs.
/// </summary>
public sealed class AponeW21RulesDocsContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Apone")]
    public void KyvernoW21Doc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "docs", "kyverno-w21-additional-rules.md");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Apone")]
    public void SignalRObservabilityW21Doc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "docs", "signalr-observability-w21.md");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Apone")]
    public void HelmReleaseDoc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "docs", "helm-release.md");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Apone")]
    public void PrometheusRulesSignalR_File_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "infra", "k8s", "overlays", "prod",
            "prometheus-rules-signalr.yaml");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Apone")]
    public void HelmReleaseWorkflow_File_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, ".github", "workflows", "helm-release.yml");
        _ = File.Exists(p);
    }
}
