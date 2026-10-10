namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Vasquez;

/// <summary>
/// Phase K Wave 17 — Vasquez. Hard-assert pin for the
/// branch-protection §4.5 RECALIBRATION + §4.7 NEW
/// (Coordinator-direct execution gate) + §4.8 NEW
/// (Stephen-decision tree with Options A/B/C) doc changes in
/// <c>docs/agent-handoff-protocol.md</c>.
///
/// <para>The RECALIBRATION is triggered by the W17 Coordinator
/// finding that <c>main</c> has ZERO branch protection
/// (HTTP 404 from <c>gh api -X GET
/// repos/long2know/mahjong-autotable/branches/main/protection</c>).
/// This invalidates the W14/W15/W16 dry-run framing which
/// assumed partial protection existed and required a PATCH —
/// from-zero install requires PUT with full payload (8 policy
/// choices simultaneously) and is a bigger decision than the
/// W16 PRIMARY framing implied.</para>
/// </summary>
public sealed class BranchProtectionW17RecalibrationTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string DocPath()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        return Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
    }

    [Fact, Trait("Category", "BranchProtection"), Trait("Wave", "Phase-K-17")]
    public void Section4_5_W17_Recalibration_KeyTerms_Present()
    {
        var text = File.ReadAllText(DocPath());
        Assert.Contains("§4.5", text, StringComparison.Ordinal);
        Assert.Contains("RECALIBRATION", text, StringComparison.Ordinal);
        Assert.Contains("HTTP 404", text, StringComparison.Ordinal);
        Assert.Contains("Branch not protected", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "BranchProtection"), Trait("Wave", "Phase-K-17")]
    public void Section4_5_W17_InvalidatesPriorPATCHFraming()
    {
        var text = File.ReadAllText(DocPath());
        // §4.5 W17 must call out that the prior W14/W15/W16 PATCH/dry-run
        // framing is invalidated and that install-from-zero requires
        // PUT with a full payload.
        Assert.Contains("PUT", text, StringComparison.Ordinal);
        Assert.Contains("from-zero", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "BranchProtection"), Trait("Wave", "Phase-K-17")]
    public void Section4_7_New_CoordinatorDirectExecutionGate_Present()
    {
        var text = File.ReadAllText(DocPath());
        Assert.Contains("§4.7", text, StringComparison.Ordinal);
        Assert.Contains("Coordinator-direct execution gate", text,
            StringComparison.OrdinalIgnoreCase);
        // The gate names the 4 pre-flight checks (token / scope /
        // confirmation / audit-log capture) as the minimum required
        // before any branch-protection write.
        Assert.Contains("pre-flight", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "BranchProtection"), Trait("Wave", "Phase-K-17")]
    public void Section4_8_New_StephenDecisionTree_OptionsABCPresent()
    {
        var text = File.ReadAllText(DocPath());
        Assert.Contains("§4.8", text, StringComparison.Ordinal);
        Assert.Contains("Stephen", text, StringComparison.Ordinal);
        Assert.Contains("Option A", text, StringComparison.Ordinal);
        Assert.Contains("Option B", text, StringComparison.Ordinal);
        Assert.Contains("Option C", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "BranchProtection"), Trait("Wave", "Phase-K-17")]
    public void Section4_8_New_HasGhApiPutPayloadExemplars()
    {
        var text = File.ReadAllText(DocPath());
        // §4.8 must include working `gh api -X PUT` payload exemplars
        // so Stephen can act without having to assemble JSON by hand.
        Assert.Contains("gh api", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-X PUT", text, StringComparison.Ordinal);
        Assert.Contains("required_status_checks", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "BranchProtection"), Trait("Wave", "Phase-K-17")]
    public void DryRunLog_RecalibrationCapture_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, ".work", "vasquez-w17-safe",
            "flip-script-dryrun-w17.log");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("404", text, StringComparison.Ordinal);
        Assert.Contains("RECALIBRATION", text, StringComparison.OrdinalIgnoreCase);
    }
}
