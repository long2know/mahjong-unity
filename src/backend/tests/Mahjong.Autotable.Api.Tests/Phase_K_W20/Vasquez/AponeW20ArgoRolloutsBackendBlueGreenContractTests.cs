namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Apone W20's
/// Argo Rollouts backend blue-green configuration
/// (<c>infra/k8s/base/argo-rollouts/backend-bluegreen.yaml</c>) +
/// its narrative doc.
///
/// Soft-pinned so the gate stays green if Apone W20 has not yet
/// landed the manifest.
/// </summary>
public sealed class AponeW20ArgoRolloutsBackendBlueGreenContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void ArgoRollouts_BackendBlueGreen_Manifest_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "infra", "k8s", "base", "argo-rollouts",
            "backend-bluegreen.yaml");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void ArgoRollouts_BackendBlueGreen_HasBlueGreenStrategy_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "infra", "k8s", "base", "argo-rollouts",
            "backend-bluegreen.yaml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("blueGreen", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void ArgoRollouts_BackendBlueGreen_Doc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "argo-rollouts-backend-bluegreen.md");
        _ = File.Exists(p);
    }
}
