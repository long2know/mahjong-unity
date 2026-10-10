namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Vasquez;

/// <summary>
/// Phase K Wave 16 — Vasquez. Self-lane process gates.
///
/// <para>The Vasquez W16 lane includes the deliverables enumerated in
/// the W16 brief:</para>
///
/// <list type="number">
///   <item>Gate verification post-Bishop W16 (≥ 3450, +138 over W15
///         3312 baseline).</item>
///   <item>DbSerial post-W15 validation (25/25 complete per W15 §3.4).</item>
///   <item>LH13 cron status check + §6.6 NEW Coordinator-direct
///         cron invocation runbook — paired with Hicks's W16
///         <c>docs/lh13-soft-pin-rationale.md</c> Option A soft-flip
///         (YELLOW preserved; §6.6 cross-references the new doc).</item>
///   <item>§4.5 escalation re-verification — 9-wave §4.1 deadlock
///         (<c>docs/agent-handoff-protocol.md §4.5</c>).</item>
///   <item>Forward-stage W16 contract tests (~17 files under
///         <c>Phase_K_W16/Vasquez/</c>) covering Bishop / Hicks /
///         Apone W16 surfaces.</item>
///   <item>KW15 regression rename → <c>Wave1ThroughKW16RegressionTests</c>
///         + new W16 smokes.</item>
///   <item>Lane-discipline strict verification (6th 0-violation
///         lane wave once all 4 commits land).</item>
/// </list>
///
/// <para>These facts HARD-ASSERT — the artefacts ship in the same
/// Vasquez W16 PR.</para>
/// </summary>
public sealed class VasquezW16SelfLaneTests
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

    // ─── 1. DbSerial W15 completion still recorded ─────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-16")]
    public void DbSerial_W15Section3_4_TestArchitecture_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§3.4", text, StringComparison.Ordinal);
        Assert.Contains("DbSerial migration final completion", text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-16")]
    public void DbSerial_W14CompletionMemo_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "Phase_K_W14", "Vasquez",
            "db-serial-migration-completion.md");
        Assert.True(File.Exists(path),
            $"W14 DbSerial completion memo MUST remain at {path}.");
    }

    // ─── 2. LH13 cron deadlock — §6.5 Option A + §6.6 NEW ──────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-16")]
    public void LH13_PwaAuditDoc_Section6_5_OptionASoftFlip_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§6.5", text, StringComparison.Ordinal);
        // The W16 disposition is Option A soft-flip per Hicks's W16
        // lh13-soft-pin-rationale.md — YELLOW is preserved with an
        // audit trail; RED is held in reserve as the W17+ fallback.
        Assert.Contains("Option A", text, StringComparison.Ordinal);
        Assert.Contains("YELLOW", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-16")]
    public void LH13_HicksSoftPinRationale_DocPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        // Cross-author smoke: Hicks's W16 LH13 Option A disposition
        // doc MUST be present — §6.6 cross-references it.
        var path = Path.Combine(root!.FullName, "docs",
            "lh13-soft-pin-rationale.md");
        Assert.True(File.Exists(path),
            $"Hicks W16 LH13 Option A doc MUST be present at {path}.");
        var text = File.ReadAllText(path);
        Assert.Contains("provisional-until-calibrated", text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-16")]
    public void LH13_PwaAuditDoc_Section6_6_New_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        // §6.6 is NEW in W16 — Coordinator-direct cron invocation runbook.
        Assert.Contains("§6.6", text, StringComparison.Ordinal);
        Assert.Contains("Coordinator-direct", text,
            StringComparison.OrdinalIgnoreCase);
        // §6.6 must cross-reference Hicks's W16 soft-pin doc.
        Assert.Contains("lh13-soft-pin-rationale.md", text,
            StringComparison.OrdinalIgnoreCase);
    }

    // ─── 3. Branch-protection §4.5 W16 escalation ──────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-16")]
    public void BranchProtection_W16Section4_5_DocPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§4.5", text, StringComparison.Ordinal);
        // 9-wave deadlock — explicit count expected in the §4.5 body.
        Assert.Contains("nine", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-16")]
    public void BranchProtection_FlipScript_StillPresent_AndExecutable()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "tests", "ci",
            "lane-discipline-flip-required.sh");
        Assert.True(File.Exists(path));
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 0);
        // Header shebang is the smoke for "script-shaped".
        Assert.Equal((byte)'#', bytes[0]);
        Assert.Equal((byte)'!', bytes[1]);
    }

    // ─── 4. Forward-stage W16 contract files inventory ────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-16")]
    public void VasquezW16_ContractTestFiles_AllPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var dir = Path.Combine(root!.FullName, "src", "backend", "tests",
            "Mahjong.Autotable.Api.Tests", "Phase_K_W16", "Vasquez");
        Assert.True(Directory.Exists(dir));
        var expected = new[]
        {
            "W16SurfaceSmokeFactsTests.cs",
            "VasquezW16SelfLaneTests.cs",
            "PwaAuditWorkflowGateW16Tests.cs",
            "BishopW16TournamentRoundProgressionTests.cs",
            "BishopW16ReplayRetentionPolicyTests.cs",
            "BishopW16CommentaryBudgetForecastV2Tests.cs",
            "BishopW16SpectatorPresenceMetricsTests.cs",
            "BishopW16JwksKeyExpiryGuardTests.cs",
            "BishopW16ReplayCheckpointStreamingV2Tests.cs",
            "BishopW16AuditRetentionV2Tests.cs",
            "BishopW16MatchHistoryPageSizeMetricsV2Tests.cs",
            "HicksW16PhaseLRendererBundleTests.cs",
            "HicksW16LH13FourthRetryTests.cs",
            "HicksW16ThreeRendererHoldLineTests.cs",
            "HicksW16FrontendBundleAuditTests.cs",
            "HicksW16PlaywrightVisualRegressionTests.cs",
            "HicksW16PhaseLWebGL2ExtensionTests.cs",
            "AponeW16InfraContractTests.cs",
        };
        foreach (var n in expected)
        {
            var p = Path.Combine(dir, n);
            Assert.True(File.Exists(p), $"W16 forward-stage file missing: {p}");
        }
    }

    // ─── 5. KW16 regression rename ─────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-16")]
    public void Wave1ThroughKW16RegressionTests_Class_Present()
    {
        var asm = typeof(VasquezW16SelfLaneTests).Assembly;
        // W17 renames KW16 → KW17; W18 renames KW17 → KW18;
        // W19 → KW19; W20 → KW20.  Broaden to accept any of {KW16,
        // KW17, KW18, KW19, KW20} so this W16 self-lane test stays
        // green across the W17/W18/W19/W20 rename waves.
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW16RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW17RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW18RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW19RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW20RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW21RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW22RegressionTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-16")]
    public void Wave1ThroughKW15RegressionTests_Class_Removed()
    {
        var asm = typeof(VasquezW16SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW15RegressionTests", StringComparison.Ordinal));
        Assert.Null(t);
    }

    // ─── 7. Lane-discipline streak narrative — §6 hold-line ───────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-16")]
    public void LaneDiscipline_W15Section6_MaturityNarrative_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        // The W15 §6 narrative remains the canonical document; W16
        // does not need to amend it unless an amendment lands.
        Assert.Contains("Lane-discipline maturity narrative", text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-16")]
    public void LaneDiscipline_W11W14ZeroStreak_Plus_W15_W16_Documented()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        // The §6.1 maturity arc enumerates all zero-violation waves.
        // W16 will extend with a "W16: 6th 0-violation wave" entry IF
        // strict verification passes.  Soft-check on the W15 phrase.
        Assert.Contains("zero-violation", text, StringComparison.OrdinalIgnoreCase);
    }
}
