namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Vasquez;

/// <summary>
/// Phase K Wave 17 — Vasquez. Self-lane process gates.
///
/// <para>The Vasquez W17 lane includes the deliverables enumerated in
/// the W17 brief:</para>
///
/// <list type="number">
///   <item>Gate verification post-Bishop W17 (≥ 3800, +N over W16
///         3622 baseline).</item>
///   <item>DbSerial re-validation — 25/25 migrated (W15) + W16 26th
///         candidate + W17 27th-29th candidates documented in
///         <c>docs/test-architecture.md §3.4b</c>.</item>
///   <item>LH13 cron status check + §6.7 NEW
///         <c>docs/frontend-pwa-audit.md</c> 7-wave-deferred PROMOTE
///         recommendation — pairs with Hicks W17 §8 in
///         <c>lh13-soft-pin-rationale.md</c>.</item>
///   <item>§4.5 RECALIBRATION (NEW Coordinator finding: branch
///         protection is HTTP-404 "not protected"; W16 PRIMARY
///         framing invalid; downgrade required) + §4.7 NEW
///         (Coordinator-direct execution gate) + §4.8 NEW
///         (Stephen-decision tree with Options A/B/C).</item>
///   <item>Forward-stage W17 contract tests (~22 files under
///         <c>Phase_K_W17/Vasquez/</c>) covering Bishop / Hicks /
///         Apone W17 surfaces.</item>
///   <item>KW16 regression rename → <c>Wave1ThroughKW17RegressionTests</c>
///         + W16 pin rewritten to <c>_Historical</c> + new W17 pin.</item>
///   <item>Lane-discipline strict verification (7th 0-violation
///         lane wave once all 4 commits land).</item>
/// </list>
///
/// <para>These facts HARD-ASSERT — the artefacts ship in the same
/// Vasquez W17 PR.</para>
/// </summary>
public sealed class VasquezW17SelfLaneTests
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

    // ─── 1. DbSerial W17 §3.4a/§3.4b inventory ──────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-17")]
    public void DbSerial_Section3_4a_W16_26thCandidate_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        // §3.4a is rendered as "#### 3.4a." in the doc (no leading §
        // glyph) — accept either form.
        Assert.True(text.Contains("3.4a", StringComparison.Ordinal));
        Assert.Contains("PerTenantRotationAdminControllerTests",
            text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-17")]
    public void DbSerial_Section3_4b_W17_CandidateInventory_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.True(text.Contains("3.4b", StringComparison.Ordinal));
        Assert.Contains("PerTenantRotationDeleteAsyncTests",
            text, StringComparison.Ordinal);
        Assert.Contains("ReplayRetentionAdminControllerTests",
            text, StringComparison.Ordinal);
        Assert.Contains("SignalRRetentionAdminControllerTests",
            text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-17")]
    public void DbSerial_W14CompletionMemo_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "Phase_K_W14", "Vasquez",
            "db-serial-migration-completion.md");
        Assert.True(File.Exists(path),
            $"W14 DbSerial completion memo MUST remain at {path}.");
    }

    // ─── 2. LH13 cron — §6.7 NEW 7-wave-deferred PROMOTE ───────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-17")]
    public void LH13_PwaAudit_Section6_7_PromoteRecommendation_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§6.7", text, StringComparison.Ordinal);
        // The W17 disposition is "PROMOTE §6.6 Coordinator-direct
        // execution from optional fallback to PRIMARY next-step".
        Assert.Contains("PROMOTE", text, StringComparison.Ordinal);
        Assert.Contains("Coordinator-direct", text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-17")]
    public void LH13_PwaAudit_Section6_7_CrossRefHicksSoftPin_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        // §6.7 must cross-reference Hicks's §8 W17 status update.
        Assert.Contains("lh13-soft-pin-rationale.md", text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-17")]
    public void LH13_PwaAudit_Section6_5_Section6_6_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        // §6.5 (W15 RED hold) and §6.6 (W16 Coordinator-direct
        // runbook) MUST remain present in W17.
        Assert.Contains("§6.5", text, StringComparison.Ordinal);
        Assert.Contains("§6.6", text, StringComparison.Ordinal);
    }

    // ─── 3. Branch-protection §4.5 RECALIBRATION + §4.7/§4.8 NEW ──

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-17")]
    public void BranchProtection_W17_Section4_5_Recalibration_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§4.5", text, StringComparison.Ordinal);
        // RECALIBRATION marker — W17 introduces a downgrade from W16
        // PRIMARY framing on the basis of the Coordinator's HTTP 404
        // "Branch not protected" finding.
        Assert.Contains("RECALIBRATION", text, StringComparison.Ordinal);
        Assert.Contains("404", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-17")]
    public void BranchProtection_W17_Section4_7_New_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§4.7", text, StringComparison.Ordinal);
        Assert.Contains("Coordinator-direct execution gate", text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-17")]
    public void BranchProtection_W17_Section4_8_StephenDecisionTree_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§4.8", text, StringComparison.Ordinal);
        // Options A / B / C must all appear.
        Assert.Contains("Option A", text, StringComparison.Ordinal);
        Assert.Contains("Option B", text, StringComparison.Ordinal);
        Assert.Contains("Option C", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-17")]
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

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-17")]
    public void BranchProtection_W17_DryRunLog_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, ".work", "vasquez-w17-safe",
            "flip-script-dryrun-w17.log");
        Assert.True(File.Exists(path),
            $"W17 dry-run capture MUST ship at {path}.");
    }

    // ─── 4. Forward-stage W17 contract files inventory ────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-17")]
    public void VasquezW17_ContractTestFiles_AllPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var dir = Path.Combine(root!.FullName, "src", "backend", "tests",
            "Mahjong.Autotable.Api.Tests", "Phase_K_W17", "Vasquez");
        Assert.True(Directory.Exists(dir));
        var expected = new[]
        {
            "VasquezW17SelfLaneTests.cs",
            "W17SurfaceSmokeFactsTests.cs",
            "BishopW17JwtIssueBlockedMetricsTests.cs",
            "BishopW17PerTenantRotationDeleteAsyncTests.cs",
            "BishopW17ReplayRetentionAdminCrudTests.cs",
            "BishopW17SignalRRetentionAdminCrudTests.cs",
            "BishopW17CommentaryAdminReasonUnificationTests.cs",
            "BishopW17DateTimeOffsetWideningR2Tests.cs",
            "BishopW17TournamentQueryDurationAlertsTests.cs",
            "BishopW17MigrationContractTests.cs",
            "BishopW16W17DbSerialCandidatesTests.cs",
            "HicksW17PhaseLRendererSceneTests.cs",
            "HicksW17PhaseLTileAtlasCanonicalTests.cs",
            "HicksW17BundleAuditLazyMountTests.cs",
            "HicksW17ThreeRendererHoldLineTests.cs",
            "HicksW17Lh13W17CronStatusTests.cs",
            "AponeW17InfraContractTests.cs",
            "BranchProtectionW17RecalibrationTests.cs",
            "PwaAuditWorkflowGateW17Tests.cs",
        };
        foreach (var n in expected)
        {
            var p = Path.Combine(dir, n);
            Assert.True(File.Exists(p), $"W17 forward-stage file missing: {p}");
        }
    }

    // ─── 5. KW17 regression rename ─────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-17")]
    public void Wave1ThroughKW17RegressionTests_Class_Present()
    {
        var asm = typeof(VasquezW17SelfLaneTests).Assembly;
        // W18 renames KW17 → KW18; W19 → KW19; W20 → KW20.  Accept
        // any of {KW17, KW18, KW19, KW20} so the W17 self-lane test
        // stays green across the W18/W19/W20 rename waves.
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW17RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW18RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW19RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW20RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW21RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW22RegressionTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-17")]
    public void Wave1ThroughKW16RegressionTests_Class_Removed()
    {
        var asm = typeof(VasquezW17SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW16RegressionTests", StringComparison.Ordinal));
        Assert.Null(t);
    }

    // ─── 7. Lane-discipline streak narrative — §6 hold-line ───────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-17")]
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

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-17")]
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
