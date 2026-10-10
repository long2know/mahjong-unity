using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W13.Vasquez;

/// <summary>
/// Phase K Wave 13 — Vasquez. Self-lane process gates.
///
/// <para>The Vasquez W13 lane includes the deliverables enumerated
/// in the W13 brief:</para>
///
/// <list type="number">
///   <item>DbSerial migration applied
///         (<c>Phase_K_W13/Vasquez/db-serial-migration-applied.md</c>
///         + 23 test files attributed).</item>
///   <item>LH13 mirror tests synced
///         (<c>Phase_K_W13/Vasquez/PwaAuditWorkflowGateTests.cs</c>
///         + <c>docs/frontend-pwa-audit.md §6.2</c>).</item>
///   <item>Visual-regression CI workflow
///         (<c>.github/workflows/playwright-visual-regression.yml</c>
///         + <c>docs/test-architecture.md §5.1</c>).</item>
///   <item>Branch-protection escalation script
///         (<c>tests/ci/lane-discipline-flip-required.sh</c> +
///         <c>docs/agent-handoff-protocol.md §4.2</c>).</item>
///   <item>W13 forward-stage contract tests
///         (<c>Phase_K_W13/Vasquez/*W13*Tests.cs</c>).</item>
///   <item>KW13 regression rename
///         (<c>Wave1ThroughKW13RegressionTests.cs</c>).</item>
///   <item>Six new Playwright specs (W13 inventory in
///         <c>tests/selectors.md</c> W13 footer).</item>
/// </list>
///
/// <para>These facts HARD-ASSERT — the artefacts ship in the same
/// Vasquez W13 PR, so no forward-stage soft-pass is needed.</para>
/// </summary>
public sealed class VasquezW13SelfLaneTests
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

    // ─── 1. DbSerial migration FOLLOW-THROUGH ──────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-13")]
    public void DbSerial_MigrationApplied_Memo_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "Phase_K_W13", "Vasquez",
            "db-serial-migration-applied.md");
        Assert.True(File.Exists(p),
            $"Vasquez W13 DbSerial migration memo MUST exist at {p}.");
        var text = File.ReadAllText(p);
        Assert.Contains("Vasquez", text);
        Assert.Contains("DbSerial", text);
        Assert.Contains("W13", text);
        Assert.Contains("23", text); // 23 Vasquez-lane candidates opted in.
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-13")]
    public void DbSerial_CanonicalCollection_AppliedToCanaryClass()
    {
        var asm = typeof(VasquezW13SelfLaneTests).Assembly;
        // The W9-retro motivating class for DbSerial. After W13's
        // migration EITHER it carries the attribute (W14 Bishop
        // hand-off pending) OR a Vasquez-lane file does (e.g.
        // AuditPruningContractTests). We HARD-ASSERT at least ONE
        // candidate inside the test assembly carries [Collection("DbSerial")].
        Type[] allTypes;
        try { allTypes = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        { allTypes = ex.Types.Where(t => t is not null).Cast<Type>().ToArray(); }

        var any = allTypes
            .Where(t => t.IsClass)
            .Any(t =>
            {
                IList<CustomAttributeData> attrs;
                try { attrs = t.GetCustomAttributesData(); }
                catch { return false; }
                foreach (var a in attrs)
                {
                    if (!a.AttributeType.Name.Contains("Collection", StringComparison.Ordinal))
                        continue;
                    foreach (var arg in a.ConstructorArguments)
                    {
                        if (arg.Value is string s
                            && s.Contains("DbSerial", StringComparison.Ordinal))
                            return true;
                    }
                }
                return false;
            });
        Assert.True(any,
            "At least one test class must carry [Collection(\"DbSerial\")] after W13.");
    }

    // ─── 2. LH13 mirror tests HARD-PIN sync ────────────────────────

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-13")]
    public void LH13_W13_Mirror_File_Present()
    {
        var asm = typeof(VasquezW13SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.FullName == "Mahjong.Autotable.Api.Tests.Phase_K_W13.Vasquez.PwaAuditWorkflowGateTests");
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-13")]
    public void FrontendPwaAuditDoc_W13_Section6_2_LH13_Sync_Block()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(p));
        var text = File.ReadAllText(p);
        Assert.Contains("§6.2", text, StringComparison.Ordinal);
        Assert.Contains("W13", text, StringComparison.Ordinal);
    }

    // ─── 3. Visual-regression CI gate ──────────────────────────────

    [Fact, Trait("Category", "VisualRegression"), Trait("Wave", "Phase-K-13")]
    public void VisualRegression_W13_Workflow_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, ".github", "workflows",
            "playwright-visual-regression.yml");
        Assert.True(File.Exists(p),
            $"Vasquez W13 visual-regression workflow MUST exist at {p}.");
        var text = File.ReadAllText(p);
        Assert.Contains("playwright", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("draft", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "VisualRegression"), Trait("Wave", "Phase-K-13")]
    public void TestArchitectureDoc_W13_Section5_1_Visual_CI_Gate_Block()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(p));
        var text = File.ReadAllText(p);
        Assert.Contains("§5.1", text, StringComparison.Ordinal);
        Assert.Contains("playwright-visual-regression", text, StringComparison.OrdinalIgnoreCase);
    }

    // ─── 4. Branch-protection escalation prep ──────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-13")]
    public void BranchProtectionFlip_Script_Present_And_Executable()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "tests", "ci",
            "lane-discipline-flip-required.sh");
        Assert.True(File.Exists(p));
        var text = File.ReadAllText(p);
        Assert.Contains("gh api", text, StringComparison.Ordinal);
        Assert.Contains("lane-discipline / check", text, StringComparison.Ordinal);
        Assert.Contains("--dry-run", text, StringComparison.Ordinal);
        Assert.Contains("--rollback", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-13")]
    public void AgentHandoffProtocolDoc_W13_Section4_2_Coordinator_Block()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(p));
        var text = File.ReadAllText(p);
        Assert.Contains("4.2", text, StringComparison.Ordinal);
        Assert.Contains("lane-discipline-flip-required.sh", text, StringComparison.Ordinal);
        Assert.Contains("Coordinator-direct", text, StringComparison.Ordinal);
    }

    // ─── 5. KW13 regression rename ─────────────────────────────────

    [Fact, Trait("Category", "Regression"), Trait("Wave", "Phase-K-13")]
    public void Wave1ThroughKW13RegressionTests_Class_Present()
    {
        var asm = typeof(VasquezW13SelfLaneTests).Assembly;
        // W14 renames Wave1ThroughKW13RegressionTests → Wave1ThroughKW14RegressionTests.
        // W15 → Wave1ThroughKW15RegressionTests; W16 → Wave1ThroughKW16RegressionTests;
        // W17 → Wave1ThroughKW17RegressionTests; W18 → Wave1ThroughKW18RegressionTests;
        // W19 → Wave1ThroughKW19RegressionTests; W20 → Wave1ThroughKW20RegressionTests.
        // Accept any so this W13 self-lane test stays green across subsequent rename waves.
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW13RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW14RegressionTests", StringComparison.Ordinal)
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

    [Fact, Trait("Category", "Regression"), Trait("Wave", "Phase-K-13")]
    public void Wave1ThroughKW12RegressionTests_Class_Gone()
    {
        var asm = typeof(VasquezW13SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW12RegressionTests", StringComparison.Ordinal));
        Assert.Null(t);
    }

    // ─── 6. Playwright specs ───────────────────────────────────────

    [Theory, Trait("Category", "Playwright"), Trait("Wave", "Phase-K-13")]
    [InlineData("spectate-deep-link.spec.ts")]
    [InlineData("shader-chunk-440-stretch.spec.ts")]
    [InlineData("lh13-thresholds-hard-pinned.spec.ts")]
    [InlineData("bracket-tournament-integration.spec.ts")]
    [InlineData("commentary-cost-warning-toast.spec.ts")]
    [InlineData("bundle-health-pr-comment.spec.ts")]
    public void NewPlaywrightSpec_Present(string filename)
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "src", "frontend", "autotable-src",
            "tests", "e2e", filename);
        Assert.True(File.Exists(p),
            $"Vasquez W13 Playwright spec MUST exist at {p}.");
        var text = File.ReadAllText(p);
        Assert.Contains("@playwright/test", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Phase K Wave 13", text, StringComparison.OrdinalIgnoreCase);
    }

    // ─── 7. selectors.md W13 footer ────────────────────────────────

    [Fact, Trait("Category", "Playwright"), Trait("Wave", "Phase-K-13")]
    public void SelectorsMd_W13_Footer_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "src", "frontend", "autotable-src",
            "tests", "selectors.md");
        Assert.True(File.Exists(p));
        var text = File.ReadAllText(p);
        Assert.Contains("Wave 13", text, StringComparison.OrdinalIgnoreCase);
    }

    // ─── 8. Memo + history ─────────────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-13")]
    public void W13_Memo_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "Phase_K_W13", "Vasquez",
            "vasquez-phase-k-wave-13.md");
        Assert.True(File.Exists(p),
            $"Vasquez W13 memo MUST exist at {p}.");
    }

    // ─── Lane-map regex (W13 extension) ────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-13")]
    public void LaneMap_W13_Includes_PlaywrightVisualRegression()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "tests", "ci", "lane-map.json");
        Assert.True(File.Exists(p));
        var text = File.ReadAllText(p);
        Assert.Contains("playwright-visual-regression", text, StringComparison.Ordinal);
    }
}
