namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Vasquez;

/// <summary>
/// Phase K Wave 15 — Vasquez. Self-lane process gates.
///
/// <para>The Vasquez W15 lane includes the deliverables enumerated in
/// the W15 brief:</para>
///
/// <list type="number">
///   <item>DbSerial Bishop-lane completion sync (collaborate with
///         Bishop; <c>docs/test-architecture.md §3.4</c>).</item>
///   <item>LH13 cron convergence wait — 5-wave deferral escalation
///         (<c>docs/frontend-pwa-audit.md §6.4 + §6.5</c>).</item>
///   <item>§4.4 escalation status update — 8-wave §4.1 deadlock
///         (<c>docs/agent-handoff-protocol.md §4.4</c>).</item>
///   <item>Forward-stage W15 contract tests (60+ facts under
///         <c>Phase_K_W15/Vasquez/</c>).</item>
///   <item>KW14 regression rename → <c>Wave1ThroughKW15RegressionTests</c>
///         + 12+ new W15 smokes.</item>
///   <item>Six new Playwright specs.</item>
///   <item>Lane-discipline maturity narrative
///         (<c>docs/agent-handoff-protocol.md §6</c>).</item>
/// </list>
///
/// <para>These facts HARD-ASSERT — the artefacts ship in the same
/// Vasquez W15 PR.</para>
/// </summary>
public sealed class VasquezW15SelfLaneTests
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

    // ─── 1. DbSerial Bishop-lane completion sync ──────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-15")]
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

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-15")]
    public void DbSerial_W14Predecessor_Memo_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "Phase_K_W14", "Vasquez",
            "db-serial-migration-completion.md");
        Assert.True(File.Exists(path),
            $"W14 DbSerial completion memo MUST remain at {path}.");
    }

    // ─── 2. LH13 cron convergence wait — §6.4 + §6.5 ──────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-15")]
    public void LH13_W15Section6_4_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§6.4", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-15")]
    public void LH13_W15Section6_5_EscalationPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        var text = File.ReadAllText(path);
        Assert.Contains("§6.5", text, StringComparison.Ordinal);
        Assert.Contains("Calibration deadlock", text,
            StringComparison.OrdinalIgnoreCase);
    }

    // ─── 3. §4.4 escalation status update ─────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-15")]
    public void BranchProtection_W15Section4_4_DocPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§4.4", text, StringComparison.Ordinal);
        Assert.Contains("Escalation re-verification W15", text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-15")]
    public void BranchProtection_FlipScript_StillPresent_AndExecutable()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "tests", "ci",
            "lane-discipline-flip-required.sh");
        Assert.True(File.Exists(path),
            $"flip-required.sh MUST remain at {path} (regression-pin from W13).");
        var text = File.ReadAllText(path);
        Assert.Contains("--dry-run", text, StringComparison.Ordinal);
        Assert.Contains("--coordinator-flag", text, StringComparison.Ordinal);
    }

    // ─── 4. Forward-stage W15 contract tests inventory ────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-15")]
    public void VasquezW15_ContractTestFiles_AllPresent()
    {
        var asm = typeof(VasquezW15SelfLaneTests).Assembly;
        var expected = new[]
        {
            "BishopW15ReplayBlobStreamingTests",
            "BishopW15PerTenantJwksRotationTests",
            "BishopW15TournamentPageSizeMetricsTests",
            "BishopW15CommentaryCostForecastTests",
            "BishopW15SpectatorAuditRetentionSweepTests",
            "BishopW15ReplayRetentionSweepTests",
            "BishopW15DbSerialCompletionOnW9FilesTests",
            "HicksW15ThreeRendererHoldLineTests",
            "HicksW15LH13ThirdRetryTests",
            "HicksW15PhaseLRendererBundleTests",
            "HicksW15CostForecastRouteTests",
            "HicksW15BundleAuditCandidatesTests",
            "HicksW15SnapshotPathTemplateTests",
            "AponeW15InfraContractTests",
            "PwaAuditWorkflowGateW15Tests",
            "W15SurfaceSmokeFactsTests",
        };
        foreach (var name in expected)
        {
            var t = asm.GetTypes().FirstOrDefault(x =>
                x.Name.Equals(name, StringComparison.Ordinal));
            Assert.NotNull(t);
        }
    }

    // ─── 5. KW15 regression rename ────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-15")]
    public void Wave1ThroughKW15RegressionTests_Class_Present()
    {
        var asm = typeof(VasquezW15SelfLaneTests).Assembly;
        // W16 renames Wave1ThroughKW15RegressionTests → Wave1ThroughKW16RegressionTests.
        // W17 renames Wave1ThroughKW16RegressionTests → Wave1ThroughKW17RegressionTests.
        // W18 renames Wave1ThroughKW17RegressionTests → Wave1ThroughKW18RegressionTests.
        // W19 renames Wave1ThroughKW18RegressionTests → Wave1ThroughKW19RegressionTests.
        // W20 renames Wave1ThroughKW19RegressionTests → Wave1ThroughKW20RegressionTests.
        // Accept any of {KW15, KW16, KW17, KW18} so this W15 self-lane test stays green across the W16/W17/W18 rename waves.
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW15RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW16RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW17RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW18RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW19RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW20RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW21RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW22RegressionTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-15")]
    public void Wave1ThroughKW14RegressionTests_Class_Removed()
    {
        var asm = typeof(VasquezW15SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW14RegressionTests", StringComparison.Ordinal));
        Assert.Null(t);
    }

    // ─── 6. Playwright specs inventory pin ────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-15")]
    public void VasquezW15_PlaywrightSpecs_AllSixPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var e2eDir = Path.Combine(root!.FullName, "src", "frontend",
            "autotable-src", "tests", "e2e");
        var specs = new[]
        {
            "replay-blob-streaming.spec.ts",
            "cost-forecast-route.spec.ts",
            "phase-l-renderer-bundle.spec.ts",
            "lh13-thresholds-w15.spec.ts",
            "snapshotPathTemplate.spec.ts",
            "bundle-audit-candidates.spec.ts",
        };
        foreach (var name in specs)
        {
            var p = Path.Combine(e2eDir, name);
            Assert.True(File.Exists(p), $"W15 Playwright spec missing: {p}");
        }
    }

    // ─── 7. Lane-discipline maturity narrative — §6 ───────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-15")]
    public void LaneDiscipline_W15Section6_MaturityNarrative_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("Lane-discipline maturity narrative", text,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("§6.1", text, StringComparison.Ordinal);
        Assert.Contains("§6.4", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-15")]
    public void LaneDiscipline_W15Section6_AllowlistTimeline_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        var text = File.ReadAllText(path);
        // The allowlist evolution table must name the W8 / W10 / W11 /
        // W13 milestones.
        Assert.Contains("selectors_md_shared", text, StringComparison.Ordinal);
        Assert.Contains("agent_handoff_protocol_md_shared", text,
            StringComparison.Ordinal);
        Assert.Contains("shims_shared", text, StringComparison.Ordinal);
        Assert.Contains("pwa_audit_workflow_shared", text,
            StringComparison.Ordinal);
        Assert.Contains("bundle_health_workflow_shared", text,
            StringComparison.Ordinal);
        Assert.Contains("visual_regression_baselines_shared", text,
            StringComparison.Ordinal);
    }

    // ─── 8. Wave memo + gate snapshot ─────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-15")]
    public void VasquezW15_Memo_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "Phase_K_W15", "Vasquez",
            "vasquez-phase-k-wave-15.md");
        Assert.True(File.Exists(p),
            $"Vasquez W15 memo MUST exist at {p}.");
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-15")]
    public void VasquezW15_GateSnapshot_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "Phase_K_W15", "Vasquez",
            "gate-snapshot.txt");
        Assert.True(File.Exists(p),
            $"Vasquez W15 gate-snapshot.txt MUST exist at {p}.");
    }

    // ─── 9. Selectors W15 footer ──────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-15")]
    public void VasquezW15_SelectorsFooter_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var candidates = new[]
        {
            Path.Combine(root!.FullName, "src", "frontend", "autotable-src",
                "tests", "selectors.md"),
            Path.Combine(root.FullName, "tests", "selectors.md"),
        };
        string? text = null;
        foreach (var p in candidates)
        {
            if (File.Exists(p)) { text = File.ReadAllText(p); break; }
        }
        Assert.NotNull(text);
        Assert.Contains("Wave 15 — Vasquez", text!, StringComparison.OrdinalIgnoreCase);
    }

    // ─── 10. Wave-discipline retrospective ────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-15")]
    public void LaneDiscipline_W11W14ZeroViolationStreak_Documented()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        var text = File.ReadAllText(path);
        // The W11-W14 4-wave consecutive zero-violation streak must be
        // canonised in §6.
        Assert.Contains("4-wave", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("zero-violation", text, StringComparison.OrdinalIgnoreCase);
    }
}
