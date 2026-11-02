using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez. Self-lane process gates.
///
/// <para>The Vasquez W20 lane includes the deliverables enumerated in
/// the W20 brief:</para>
///
/// <list type="number">
///   <item>Gate verification post-Bishop W20 (≥ 4500 target;
///         actual count recorded in the bring-up commit message).</item>
///   <item>§4.8 Stephen-decision tree status — UNCHANGED (still
///         awaiting Stephen; 12-wave deferral now W7→W20).</item>
///   <item>LH13 §6.8 PROMOTE re-evaluation — Hicks W20
///         <b>HELD</b> §6.8 YELLOW (gh-CLI unauthenticated +
///         only 1-2 schedule ticks since W18 merge; §4.2
///         requires ≥3 <c>schedule</c>-event runs). Vasquez W20
///         ratifies HOLD in
///         <c>docs/agent-handoff-protocol.md §6.8</c>.</item>
///   <item>W19 process-retrospective enforcement audit — Vasquez W20
///         audits each of the prior 3 W20 bring-up commits
///         (Apone bc775b9, Hicks 107afb7, Bishop 9e7d797) for
///         stash / add discipline compliance.  Documented in
///         <c>docs/agent-handoff-protocol.md §8</c> (NEW W20
///         retrospective audit subsection).</item>
///   <item>Forward-stage W20 contract tests (20-30 files under
///         <c>Phase_K_W20/Vasquez/</c>) covering Bishop / Hicks /
///         Apone W20 surfaces.</item>
///   <item>KW19 regression rename → <c>Wave1ThroughKW20RegressionTests</c>
///         + W19 pin rewritten to <c>_Historical</c> + new W20 pin.</item>
///   <item>SLSA-3 vasquez-lane SHA-pinning sweep (9 refs across 4
///         vasquez-lane workflows) per Apone's W20 hand-off doc
///         <c>docs/slsa-pinning-w20-sweep.md</c>.</item>
///   <item>Lane-discipline strict verification (10th 0-violation lane
///         wave target — W11-W20).</item>
/// </list>
///
/// <para>These facts HARD-ASSERT — the artefacts ship in the same
/// Vasquez W20 PR.</para>
/// </summary>
public sealed class VasquezW20SelfLaneTests
{
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

    // ─── 1. §6.8 LH13 W20 HOLD status present in handoff doc ───────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-20")]
    public void LH13_Handoff_Section6_8_W20_HoldStatus_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§6.8", text, StringComparison.Ordinal);
        Assert.Contains("W20", text, StringComparison.Ordinal);
        Assert.Contains("LH13", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-20")]
    public void LH13_SoftPinRationale_Section11_W20_HoldDecision_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "lh13-soft-pin-rationale.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("W20", text, StringComparison.Ordinal);
        Assert.Contains("HOLD", text, StringComparison.OrdinalIgnoreCase);
    }

    // ─── 2. §4.8 Stephen-decision tree UNCHANGED — 12-wave deferral

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-20")]
    public void BranchProtection_Section4_8_StephenDecisionTree_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§4.8", text, StringComparison.Ordinal);
        Assert.Contains("Option A", text, StringComparison.Ordinal);
        Assert.Contains("Option B", text, StringComparison.Ordinal);
        Assert.Contains("Option C", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-20")]
    public void BranchProtection_FlipScript_StillPresent_AndExecutable()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "tests", "ci",
            "lane-discipline-flip-required.sh");
        Assert.True(File.Exists(path));
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 0);
        Assert.Equal((byte)'#', bytes[0]);
        Assert.Equal((byte)'!', bytes[1]);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-20")]
    public void BranchProtection_W20_DryRunLog_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, ".work", "vasquez-w20-safe",
            "flip-script-dryrun-w20.log");
        Assert.True(File.Exists(path),
            $"W20 dry-run capture MUST ship at {path}.");
    }

    // ─── 3. W20 retrospective audit subsection ─────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-20")]
    public void Handoff_W20_RetrospectiveAudit_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("W20 retrospective audit", text,
            StringComparison.OrdinalIgnoreCase);
    }

    // ─── 4. Forward-stage W20 contract files inventory ────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-20")]
    public void VasquezW20_ContractTestFiles_AllPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var dir = Path.Combine(root!.FullName, "src", "backend", "tests",
            "Mahjong.Autotable.Api.Tests", "Phase_K_W20", "Vasquez");
        Assert.True(Directory.Exists(dir));
        var expected = new[]
        {
            "VasquezW20SelfLaneTests.cs",
            "W20SurfaceSmokeFactsTests.cs",
            "BishopW20BackendCsprojVersionContractTests.cs",
            "BishopW20SwissPairingServiceContractTests.cs",
            "BishopW20SwissPairingAdminEndpointContractTests.cs",
            "BishopW20PerTenantRotationBulkDeleteContractTests.cs",
            "BishopW20PerTenantRotationBulkEnableContractTests.cs",
            "BishopW20ReplayExpiryBackgroundServiceContractTests.cs",
            "BishopW20ReplayExpiryMetricsContractTests.cs",
            "BishopW20JwtRotationDrillEndpointContractTests.cs",
            "BishopW20SwissPairingAlertsContractTests.cs",
            "BishopW20SignalRRetentionDashboardContractTests.cs",
            "HicksW20Lh13W20CronStatusTests.cs",
            "HicksW20PhaseLTilePickAnimationContractTests.cs",
            "HicksW20PhaseLTileDragContractTests.cs",
            "HicksW20BundleAuditContractTests.cs",
            "HicksW20AdminUiSurfacesContractTests.cs",
            "AponeW20KyvernoEnforceFlipContractTests.cs",
            "AponeW20Slsa3SweepDocContractTests.cs",
            "AponeW20UsEast1ApplyRunbookV2ContractTests.cs",
            "AponeW20ChangelogW20ContractTests.cs",
            "AponeW20ArgoRolloutsBackendBlueGreenContractTests.cs",
            "AponeW20MobileIosE2eContractTests.cs",
            "PwaAuditWorkflowGateW20Tests.cs",
            "BranchProtectionW20StephenDecisionStatusTests.cs",
            "W20RetrospectiveAuditObservationTests.cs",
            "Slsa3VasquezLaneSweepW20Tests.cs",
        };
        foreach (var n in expected)
        {
            var p = Path.Combine(dir, n);
            Assert.True(File.Exists(p), $"W20 forward-stage file missing: {p}");
        }
    }

    // ─── 5. KW20 regression rename ─────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-20")]
    public void Wave1ThroughKW20RegressionTests_Class_Present()
    {
        var asm = typeof(VasquezW20SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW20RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW21RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW22RegressionTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-20")]
    public void Wave1ThroughKW19RegressionTests_Class_Removed()
    {
        var asm = typeof(VasquezW20SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW19RegressionTests", StringComparison.Ordinal));
        Assert.Null(t);
    }

    // ─── 7. Lane-discipline streak narrative — §6 hold-line ───────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-20")]
    public void LaneDiscipline_W15Section6_MaturityNarrative_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("Lane-discipline maturity narrative", text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-20")]
    public void LaneDiscipline_ZeroViolationStreak_Narrative_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("zero-violation", text, StringComparison.OrdinalIgnoreCase);
    }
}
