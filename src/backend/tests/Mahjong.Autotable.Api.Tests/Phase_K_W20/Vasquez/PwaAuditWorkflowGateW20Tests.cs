namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez self-lane. PWA-Audit workflow gate
/// W20 follow-up: confirm Apone W18's <c>--form-factor=desktop</c>
/// + <c>--screenEmulation.mobile=false</c> flags are STILL in
/// <c>.github/workflows/pwa-audit.yml</c> (no W19 / W20 regression
/// — the W18 root-cause fix must persist across all subsequent
/// waves until the §6.8 LH13 hard-pin lands).
/// </summary>
public sealed class PwaAuditWorkflowGateW20Tests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string PwaAuditPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, ".github", "workflows", "pwa-audit.yml");
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void PwaAudit_Workflow_File_Still_Present()
    {
        var p = PwaAuditPath();
        Assert.True(File.Exists(p),
            $".github/workflows/pwa-audit.yml MUST remain at {p}.");
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void PwaAudit_FormFactorDesktop_Flag_StillPresent_W20()
    {
        var text = File.ReadAllText(PwaAuditPath());
        Assert.Contains("--form-factor=desktop", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void PwaAudit_ScreenEmulationMobileFalse_Flag_StillPresent_W20()
    {
        var text = File.ReadAllText(PwaAuditPath());
        Assert.Contains("--screenEmulation.mobile=false", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void PwaAudit_Lighthouse_Token_StillPresent_W20()
    {
        var text = File.ReadAllText(PwaAuditPath());
        Assert.Contains("lighthouse", text, StringComparison.OrdinalIgnoreCase);
    }
}
