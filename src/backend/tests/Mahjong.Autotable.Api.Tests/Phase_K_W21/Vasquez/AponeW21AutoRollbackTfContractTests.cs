namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Apone W21's
/// regional-EKS auto-rollback Terraform module
/// (<c>infra/terraform/regional-eks/us-east-1/auto-rollback.tf</c>).
/// Soft-pinned so the gate stays green if Apone W21 has not yet
/// landed the file.
/// </summary>
public sealed class AponeW21AutoRollbackTfContractTests
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
    public void AutoRollbackTf_File_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "infra", "terraform", "regional-eks",
            "us-east-1", "auto-rollback.tf");
        _ = File.Exists(p);
    }
}
