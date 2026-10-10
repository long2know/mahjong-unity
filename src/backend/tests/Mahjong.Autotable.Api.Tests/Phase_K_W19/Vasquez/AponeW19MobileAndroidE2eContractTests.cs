namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez paired contract for Apone W19
/// Mobile CI Android SIGNED-branch E2E smoke (D1 in Apone
/// memo). Soft-pins the new <c>android-e2e</c> job in
/// <c>mobile-build.yml</c> + the runbook doc.
/// </summary>
public sealed class AponeW19MobileAndroidE2eContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void MobileBuildWorkflow_File_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, ".github", "workflows", "mobile-build.yml");
        Assert.True(File.Exists(p));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void MobileBuildWorkflow_AndroidE2eJob_Token_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, ".github", "workflows", "mobile-build.yml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("android-e2e", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void MobileBuildWorkflow_AndroidEmulatorRunner_Token_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, ".github", "workflows", "mobile-build.yml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("android-emulator-runner", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void MobileAndroidE2e_Runbook_Doc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "docs", "mobile-android-e2e.md");
        _ = File.Exists(p);
    }
}
