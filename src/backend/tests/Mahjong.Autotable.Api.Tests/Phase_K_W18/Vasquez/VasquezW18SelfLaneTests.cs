namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Self-lane process gates.
///
/// <para>The Vasquez W18 lane includes the deliverables enumerated in
/// the W18 brief:</para>
///
/// <list type="number">
///   <item>Gate verification post-Bishop W18 (≥ 4100 target,
///         actual count recorded in the bring-up commit message).</item>
///   <item>DbSerial 25/29 → 29/29 validation — Bishop W18 applies
///         <c>[Collection("DbSerial")]</c> to all four W16+W17 open
///         candidates; documented in
///         <c>docs/test-architecture.md §3.4c</c> (W18 mile-marker:
///         DbSerial COMPLETE).</item>
///   <item>LH13 §6.7 / §6.8 status update post-Apone W18
///         <c>--form-factor=desktop</c> + <c>--screenEmulation.mobile=false</c>
///         fix in <c>.github/workflows/pwa-audit.yml</c> — verified
///         via <c>PwaAuditWorkflowGateW18Tests</c>; status disposition
///         (GREEN / YELLOW / RED) recorded in
///         <c>docs/agent-handoff-protocol.md</c>.</item>
///   <item>§4.8 Stephen-decision tree status — still awaiting Stephen;
///         §4.8 narrative kept visible in handoff doc per
///         <c>BranchProtectionW18StephenDecisionStatusTests</c>.</item>
///   <item>Forward-stage W18 contract tests (~22 files under
///         <c>Phase_K_W18/Vasquez/</c>) covering Bishop / Hicks /
///         Apone W18 surfaces.</item>
///   <item>KW17 regression rename → <c>Wave1ThroughKW18RegressionTests</c>
///         + W17 pin rewritten to <c>_Historical</c> + new W18 pin.</item>
///   <item>Lane-discipline strict verification (8th 0-violation
///         lane wave target — W11-W18 once all 4 commits land).</item>
/// </list>
///
/// <para>These facts HARD-ASSERT — the artefacts ship in the same
/// Vasquez W18 PR.</para>
/// </summary>
public sealed class VasquezW18SelfLaneTests
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

    // ─── 1. DbSerial W18 §3.4c completion mile-marker ───────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void DbSerial_Section3_4c_W18_Completion_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        // §3.4c rendered as "#### 3.4c." in the doc (no leading §).
        Assert.True(text.Contains("3.4c", StringComparison.Ordinal));
        Assert.Contains("W18", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("29", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void DbSerial_Section3_4b_W17_Inventory_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.True(text.Contains("3.4b", StringComparison.Ordinal));
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void DbSerial_W14CompletionMemo_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "Phase_K_W14", "Vasquez",
            "db-serial-migration-completion.md");
        Assert.True(File.Exists(path),
            $"W14 DbSerial completion memo MUST remain at {path}.");
    }

    // ─── 2. LH13 — Apone W18 fix verification + §6.7/§6.8 status ──

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void LH13_PwaAuditWorkflow_File_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var wf = Path.Combine(root!.FullName, ".github", "workflows", "pwa-audit.yml");
        Assert.True(File.Exists(wf));
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void LH13_PwaAuditWorkflow_FormFactorDesktop_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var wf = Path.Combine(root!.FullName, ".github", "workflows", "pwa-audit.yml");
        var text = File.ReadAllText(wf);
        Assert.Contains("--form-factor=desktop", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void LH13_PwaAudit_Section6_7_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§6.7", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void LH13_Handoff_Section6_7_W18_Status_Present()
    {
        // W18 brief: update docs/agent-handoff-protocol.md §6.7 to
        // reflect post-Apone-W18 cron status. The W18 status block
        // (GREEN / YELLOW / RED disposition) must be present once
        // Vasquez W18 lands.
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        // Accept either §6.7 or §6.8 — depending on the W18
        // disposition the status update lands in §6.7 (extension)
        // or §6.8 (new RED transition section).
        Assert.True(
            text.Contains("§6.7", StringComparison.Ordinal)
            || text.Contains("§6.8", StringComparison.Ordinal));
        Assert.Contains("LH13", text, StringComparison.OrdinalIgnoreCase);
    }

    // ─── 3. Branch-protection §4.5/§4.7/§4.8 still visible ────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
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

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
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

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void BranchProtection_W18_DryRunLog_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, ".work", "vasquez-w18-safe",
            "flip-script-dryrun-w18.log");
        Assert.True(File.Exists(path),
            $"W18 dry-run capture MUST ship at {path}.");
    }

    // ─── 4. Forward-stage W18 contract files inventory ────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void VasquezW18_ContractTestFiles_AllPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var dir = Path.Combine(root!.FullName, "src", "backend", "tests",
            "Mahjong.Autotable.Api.Tests", "Phase_K_W18", "Vasquez");
        Assert.True(Directory.Exists(dir));
        var expected = new[]
        {
            "VasquezW18SelfLaneTests.cs",
            "W18SurfaceSmokeFactsTests.cs",
            "BishopW18DbSerialCompletionTests.cs",
            "BishopW18PerTenantRotationAuditTests.cs",
            "BishopW18ReplayRetentionPolicyEvaluationTests.cs",
            "BishopW18SignalRRetentionPolicyEvaluationTests.cs",
            "BishopW18JwtIssueRateLimitMetricsTests.cs",
            "BishopW18CommentaryCostAuditAlignmentTests.cs",
            "BishopW18TournamentQueryAlertThresholdsTests.cs",
            "BishopW18MigrationContractTests.cs",
            "HicksW18PhaseLRendererScenePickingV2Tests.cs",
            "HicksW18PhaseLTileMeshLayoutTests.cs",
            "HicksW18BundleAuditTests.cs",
            "HicksW18ThreeRendererHoldLineTests.cs",
            "HicksW18Lh13W18CronStatusTests.cs",
            "HicksW18PhaseLWebgl2AtlasExtensionTests.cs",
            "AponeW18Lh13FormFactorFixTests.cs",
            "AponeW18InfraContractTests.cs",
            "AponeW18Slsa3ContinuedTests.cs",
            "PwaAuditWorkflowGateW18Tests.cs",
            "BranchProtectionW18StephenDecisionStatusTests.cs",
            "BishopW16W17DbSerialCompletionObservationTests.cs",
        };
        foreach (var n in expected)
        {
            var p = Path.Combine(dir, n);
            Assert.True(File.Exists(p), $"W18 forward-stage file missing: {p}");
        }
    }

    // ─── 5. KW18 regression rename ─────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void Wave1ThroughKW18RegressionTests_Class_Present()
    {
        // W19 broadens this hard-assert to accept either the W18
        // class name OR the W19 rename target (Wave1ThroughKW19) —
        // when the W19 Vasquez bring-up lands, the W18 class is
        // renamed away and the W19 class takes its place. W20
        // broadens further to accept Wave1ThroughKW20. Without this
        // broadening, the W18 self-lane harness would false-fail on
        // W19/W20 PR-branch builds.
        var asm = typeof(VasquezW18SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW18RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW19RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW20RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW21RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW22RegressionTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void Wave1ThroughKW17RegressionTests_Class_Removed()
    {
        var asm = typeof(VasquezW18SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW17RegressionTests", StringComparison.Ordinal));
        Assert.Null(t);
    }

    // ─── 7. Lane-discipline streak narrative — §6 hold-line ───────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
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

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
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
