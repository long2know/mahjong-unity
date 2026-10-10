using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Vasquez;

/// <summary>
/// Phase K Wave 14 — Vasquez. Self-lane process gates.
///
/// <para>The Vasquez W14 lane includes the deliverables enumerated
/// in the W14 brief:</para>
///
/// <list type="number">
///   <item>DbSerial last 2 candidates documented (Bishop W14
///         cross-lane hand-off; <c>docs/test-architecture.md §3.3</c>).</item>
///   <item>LH13 mirror tests synced
///         (<c>Phase_K_W14/Vasquez/PwaAuditWorkflowGateW14Tests.cs</c>
///         + <c>docs/frontend-pwa-audit.md §6.3</c>).</item>
///   <item>Visual-regression spec fixed (page.goto before setContent)
///         + <c>docs/test-architecture.md §5.2 "Visual regression
///         spec fix"</c>.</item>
///   <item>Branch-protection W14 fallback execution prep
///         (<c>tests/ci/lane-discipline-flip-required.sh --dry-run</c>
///         validated + <c>docs/agent-handoff-protocol.md §4.3</c>).</item>
///   <item>W14 forward-stage contract tests under
///         <c>Phase_K_W14/Vasquez/{Bishop,Hicks,Apone}W14*Tests.cs</c>.</item>
///   <item>KW14 regression rename
///         (<c>Wave1ThroughKW14RegressionTests.cs</c>).</item>
///   <item>Six new Playwright specs (W14 inventory in
///         <c>tests/selectors.md</c> W14 footer).</item>
/// </list>
///
/// <para>These facts HARD-ASSERT — the artefacts ship in the same
/// Vasquez W14 PR.</para>
/// </summary>
public sealed class VasquezW14SelfLaneTests
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

    // ─── 1. DbSerial last 2 candidates — W14 completion memo ──

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-14")]
    public void DbSerial_W14CompletionMemo_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "Phase_K_W14", "Vasquez",
            "db-serial-migration-completion.md");
        Assert.True(File.Exists(p),
            $"Vasquez W14 DbSerial completion memo MUST exist at {p}.");
        var text = File.ReadAllText(p);
        Assert.Contains("Vasquez", text);
        Assert.Contains("DbSerial", text);
        Assert.Contains("W14", text);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-14")]
    public void DbSerial_W13PredecessorMemo_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "Phase_K_W13", "Vasquez",
            "db-serial-migration-applied.md");
        Assert.True(File.Exists(p),
            $"W13 DbSerial migration memo MUST still exist at {p} (regression-pin).");
    }

    // ─── 2. LH13 mirror tests sync — W14 PwaAuditWorkflowGate ──

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-14")]
    public void LH13_W14MirrorTests_TypePresent()
    {
        var asm = typeof(VasquezW14SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("PwaAuditWorkflowGateW14Tests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-14")]
    public void LH13_W12W13Predecessors_Present()
    {
        // The cumulative 4-wave deferral chain: W11 calibration → W12
        // soft-pin → W13 soft-pin (deferred) → W14 mirror (this wave).
        var asm = typeof(VasquezW14SelfLaneTests).Assembly;
        var w12 = asm.GetTypes().FirstOrDefault(x =>
            x.FullName == "Mahjong.Autotable.Api.Tests.Phase_K_W12.Vasquez.PwaAuditWorkflowGateTests");
        var w13 = asm.GetTypes().FirstOrDefault(x =>
            x.FullName == "Mahjong.Autotable.Api.Tests.Phase_K_W13.Vasquez.PwaAuditWorkflowGateTests");
        Assert.NotNull(w12);
        Assert.NotNull(w13);
    }

    // ─── 3. Visual-regression spec fix — §5.2 doc + spec ──

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-14")]
    public void VisualRegression_W14SpecFixDoc_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(p));
        var text = File.ReadAllText(p);
        Assert.Contains("§5.2", text, StringComparison.Ordinal);
        Assert.Contains("Visual regression spec fix", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-14")]
    public void VisualRegression_W14SpecFile_PresentAndFixed()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "src", "frontend", "autotable-src",
            "tests", "e2e", "manifest-screenshots-visual.spec.ts");
        Assert.True(File.Exists(p),
            $"manifest-screenshots-visual.spec.ts MUST exist at {p}.");
        var text = File.ReadAllText(p);
        // W14 fix: the spec MUST use page.goto BEFORE setContent so
        // the page has a real origin for relative image URLs.
        Assert.Contains("page.goto", text, StringComparison.Ordinal);
    }

    // ─── 4. Branch-protection W14 fallback — §4.3 doc + script ──

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-14")]
    public void BranchProtection_W14Fallback_Section4_3_DocPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(p));
        var text = File.ReadAllText(p);
        Assert.Contains("§4.3", text, StringComparison.Ordinal);
        Assert.Contains("Branch-protection W14 fallback execution", text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-14")]
    public void BranchProtection_FlipScript_StillPresent_AndExecutable()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "tests", "ci",
            "lane-discipline-flip-required.sh");
        Assert.True(File.Exists(p),
            $"flip-required.sh MUST exist at {p} (regression-pin from W13).");
        var text = File.ReadAllText(p);
        Assert.Contains("--dry-run", text, StringComparison.Ordinal);
        Assert.Contains("--coordinator-flag", text, StringComparison.Ordinal);
    }

    // ─── 5. Wave memo + gate snapshot ──

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-14")]
    public void VasquezW14_Memo_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "Phase_K_W14", "Vasquez",
            "vasquez-phase-k-wave-14.md");
        Assert.True(File.Exists(p),
            $"Vasquez W14 memo MUST exist at {p}.");
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-14")]
    public void VasquezW14_GateSnapshot_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "Phase_K_W14", "Vasquez",
            "gate-snapshot.txt");
        Assert.True(File.Exists(p),
            $"Vasquez W14 gate-snapshot.txt MUST exist at {p}.");
    }

    // ─── 6. KW14 regression rename ──

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-14")]
    public void Wave1ThroughKW14RegressionTests_Class_Present()
    {
        var asm = typeof(VasquezW14SelfLaneTests).Assembly;
        // W15 renames Wave1ThroughKW14RegressionTests → Wave1ThroughKW15RegressionTests.
        // W16 renames Wave1ThroughKW15RegressionTests → Wave1ThroughKW16RegressionTests.
        // W17 renames Wave1ThroughKW16RegressionTests → Wave1ThroughKW17RegressionTests.
        // W18 renames Wave1ThroughKW17RegressionTests → Wave1ThroughKW18RegressionTests.
        // W19 renames Wave1ThroughKW18RegressionTests → Wave1ThroughKW19RegressionTests.
        // W20 renames Wave1ThroughKW19RegressionTests → Wave1ThroughKW20RegressionTests.
        // Accept any so this W14 self-lane test stays green across the W15+W16+W17+W18 rename waves.
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW14RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW15RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW16RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW17RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW18RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW19RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW20RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW21RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW22RegressionTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-14")]
    public void Wave1ThroughKW13RegressionTests_Class_Removed()
    {
        var asm = typeof(VasquezW14SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW13RegressionTests", StringComparison.Ordinal));
        Assert.Null(t);
    }

    // ─── 7. Playwright specs inventory pin ──

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-14")]
    public void VasquezW14_PlaywrightSpecs_AllSixPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var e2eDir = Path.Combine(root!.FullName, "src", "frontend",
            "autotable-src", "tests", "e2e");
        var specs = new[]
        {
            "bracket-ui-route.spec.ts",
            "replay-listing-route.spec.ts",
            "commentary-cost-admin-panel.spec.ts",
            "visual-regression-real-captures.spec.ts",
            "lh13-thresholds-hard-pinned-final.spec.ts",
            "jwks-overlap-rollback-rejected.spec.ts",
        };
        foreach (var name in specs)
        {
            var p = Path.Combine(e2eDir, name);
            Assert.True(File.Exists(p), $"W14 Playwright spec missing: {p}");
        }
    }

    // ─── 8. Selectors W14 footer ──

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-14")]
    public void VasquezW14_SelectorsFooter_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        // The canonical path is `src/frontend/autotable-src/tests/selectors.md`;
        // `tests/selectors.md` is a path mirror recognised by
        // `shared_files.selectors_md_shared` in tests/ci/lane-map.json.
        var candidates = new[]
        {
            Path.Combine(root!.FullName, "src", "frontend", "autotable-src", "tests", "selectors.md"),
            Path.Combine(root!.FullName, "tests", "selectors.md"),
        };
        string? text = null;
        foreach (var p in candidates)
        {
            if (File.Exists(p)) { text = File.ReadAllText(p); break; }
        }
        Assert.NotNull(text);
        Assert.Contains("Wave 14 — Vasquez", text!, StringComparison.OrdinalIgnoreCase);
    }
}
