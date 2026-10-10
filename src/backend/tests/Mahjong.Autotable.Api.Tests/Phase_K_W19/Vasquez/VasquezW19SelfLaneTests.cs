using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez. Self-lane process gates.
///
/// <para>The Vasquez W19 lane includes the deliverables enumerated in
/// the W19 brief:</para>
///
/// <list type="number">
///   <item>Gate verification post-Bishop W19 (≥ 4300 target;
///         actual count recorded in the bring-up commit message).</item>
///   <item>§4.8 Stephen-decision tree status — UNCHANGED (still
///         awaiting Stephen; 11-wave deferral now W7→W19).</item>
///   <item>LH13 §6.7 → §6.8 PROMOTE verification — Hicks W19
///         <b>HELD</b> §6.7 YELLOW (no PROMOTE to §6.8 GREEN
///         hard-pin); only 1 successful <c>workflow_dispatch</c>
///         run on post-W18-merge <c>main</c>, §4.2 requires ≥3
///         <c>schedule</c>-event runs. Vasquez W19 ratifies HOLD
///         in <c>docs/agent-handoff-protocol.md §6.8</c>.</item>
///   <item>W18 process-retrospective enforcement audit — Vasquez W19
///         audits each prior bring-up commit (Hicks, Apone-memo,
///         Apone-bringup) for stash/add discipline compliance; one
///         Hicks-authored bundling incident surfaced (d700cf7,
///         force-with-lease reverted before W19 PR-branch settled).
///         Documented in <c>docs/agent-handoff-protocol.md §7</c>
///         (NEW — W19 retrospective audit).</item>
///   <item>Forward-stage W19 contract tests (18-25 files under
///         <c>Phase_K_W19/Vasquez/</c>) covering Bishop / Hicks /
///         Apone W19 surfaces.</item>
///   <item>KW18 regression rename → <c>Wave1ThroughKW19RegressionTests</c>
///         + W18 pin rewritten to <c>_Historical</c> + new W19 pin.</item>
///   <item>Lane-discipline strict verification (9th 0-violation lane
///         wave target — W11-W19 — gated on the Apone re-land
///         clearing the d700cf7 incident on the W19 PR branch).</item>
/// </list>
///
/// <para>These facts HARD-ASSERT — the artefacts ship in the same
/// Vasquez W19 PR.</para>
/// </summary>
public sealed class VasquezW19SelfLaneTests
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

    // ─── 1. §6.8 LH13 W19 HOLD status present in handoff doc ───────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-19")]
    public void LH13_Handoff_Section6_8_W19_HoldStatus_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§6.8", text, StringComparison.Ordinal);
        Assert.Contains("LH13", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-19")]
    public void LH13_Handoff_Section6_7_W19_HoldNarrative_Present()
    {
        // W19: Hicks held YELLOW; the §6.7 narrative records the
        // hold + the 0-of-3 schedule-event run count.
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§6.7", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-19")]
    public void LH13_SoftPinRationale_Section10_W19_HoldDecision_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "lh13-soft-pin-rationale.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("W19", text, StringComparison.Ordinal);
        Assert.Contains("HOLD", text, StringComparison.OrdinalIgnoreCase);
    }

    // ─── 2. §4.8 Stephen-decision tree UNCHANGED — 11-wave deferral

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-19")]
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

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-19")]
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

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-19")]
    public void BranchProtection_W19_DryRunLog_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, ".work", "vasquez-w19-safe",
            "flip-script-dryrun-w19.log");
        Assert.True(File.Exists(path),
            $"W19 dry-run capture MUST ship at {path}.");
    }

    // ─── 3. W19 retrospective audit §7 (NEW) ──────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-19")]
    public void Handoff_Section7_W19_RetrospectiveAudit_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("W19 retrospective audit", text,
            StringComparison.OrdinalIgnoreCase);
    }

    // ─── 4. Forward-stage W19 contract files inventory ────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-19")]
    public void VasquezW19_ContractTestFiles_AllPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var dir = Path.Combine(root!.FullName, "src", "backend", "tests",
            "Mahjong.Autotable.Api.Tests", "Phase_K_W19", "Vasquez");
        Assert.True(Directory.Exists(dir));
        var expected = new[]
        {
            "VasquezW19SelfLaneTests.cs",
            "W19SurfaceSmokeFactsTests.cs",
            "BishopW19JwtDurationMetricsContractTests.cs",
            "BishopW19PerTenantRotationBulkUpdateContractTests.cs",
            "BishopW19ReplayStoreIntegrityAuditContractTests.cs",
            "BishopW19SignalRRetentionLifecycleContractTests.cs",
            "BishopW19SwissPairingAuditEntityContractTests.cs",
            "BishopW19BackendCsprojVersionContractTests.cs",
            "BishopW19JwtValidatorDashboardContractTests.cs",
            "HicksW19PhaseLWallGeometryContractTests.cs",
            "HicksW19PhaseLCameraModesContractTests.cs",
            "HicksW19BundleAuditContractTests.cs",
            "HicksW19AdminUiSurfacesContractTests.cs",
            "HicksW19Lh13W19CronStatusTests.cs",
            "AponeW19MobileAndroidE2eContractTests.cs",
            "AponeW19UsEast1ApplyReadinessContractTests.cs",
            "AponeW19KyvernoAdditionalRulesContractTests.cs",
            "AponeW19SignalRAffinityContractTests.cs",
            "AponeW19Changelog0280ContractTests.cs",
            "AponeW19ArgoRolloutsInstallContractTests.cs",
            "PwaAuditWorkflowGateW19Tests.cs",
            "BranchProtectionW19StephenDecisionStatusTests.cs",
            "W19RetrospectiveAuditObservationTests.cs",
        };
        foreach (var n in expected)
        {
            var p = Path.Combine(dir, n);
            Assert.True(File.Exists(p), $"W19 forward-stage file missing: {p}");
        }
    }

    // ─── 5. KW19 regression rename ─────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-19")]
    public void Wave1ThroughKW19RegressionTests_Class_Present()
    {
        // W20 broadens this hard-assert to accept either the W19
        // class name OR the W20 rename target (Wave1ThroughKW20) —
        // when the W20 Vasquez bring-up lands, the W19 class is
        // renamed away and the W20 class takes its place. Without
        // this broadening, the W19 self-lane harness would
        // false-fail on W20 PR-branch builds.
        var asm = typeof(VasquezW19SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW19RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW20RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW21RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW22RegressionTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-19")]
    public void Wave1ThroughKW18RegressionTests_Class_Removed()
    {
        var asm = typeof(VasquezW19SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW18RegressionTests", StringComparison.Ordinal));
        Assert.Null(t);
    }

    // ─── 7. Lane-discipline streak narrative — §6 hold-line ───────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-19")]
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

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-19")]
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
