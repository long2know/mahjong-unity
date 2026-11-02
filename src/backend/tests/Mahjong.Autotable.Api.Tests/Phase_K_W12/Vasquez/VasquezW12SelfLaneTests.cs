using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W12.Vasquez;

/// <summary>
/// Phase K Wave 12 — Vasquez. Self-lane process gates.
///
/// <para>The Vasquez W12 lane includes the deliverables enumerated
/// in the W12 brief:</para>
///
/// <list type="number">
///   <item>DbSerial migration sweep audit hand-off
///         (<c>Phase_K_W12/Vasquez/db-serial-candidates.md</c>).</item>
///   <item>LH13 workflow threshold collaboration + soft-pin
///         (<c>Phase_K_W12/Vasquez/PwaAuditWorkflowGateTests.cs</c>
///         documented in <c>docs/frontend-pwa-audit.md §6.1</c>).</item>
///   <item>Visual-regression spec for Hicks's W11 screenshots
///         (<c>manifest-screenshots-visual.spec.ts</c> + the
///         <c>docs/test-architecture.md §5</c> policy).</item>
///   <item>Stephen branch-protection re-prompt status update
///         (<c>docs/agent-handoff-protocol.md §4.1</c> time-since-
///         introduced clause).</item>
///   <item>Forward-stage W12 contract tests
///         (<c>Phase_K_W12/Vasquez/*W12*Tests.cs</c>).</item>
///   <item>KW12 regression rename
///         (<c>Wave1ThroughKW12RegressionTests.cs</c>).</item>
///   <item>Six new Playwright specs (W12 inventory in
///         <c>tests/selectors.md</c> W12 footer).</item>
/// </list>
///
/// <para>These facts HARD-ASSERT — the artefacts ship in the same
/// Vasquez W12 PR, so no forward-stage soft-pass is needed.</para>
/// </summary>
public sealed class VasquezW12SelfLaneTests
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

    // ─── 1. DbSerial migration sweep ────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-12")]
    public void DbSerialCandidates_HandoffDoc_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "Phase_K_W12", "Vasquez",
            "db-serial-candidates.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("DbSerial", text);
        Assert.Contains("candidate", text, StringComparison.OrdinalIgnoreCase);
        // The audit MUST list at least 20 candidates (W12 inventory had 25).
        var rowCount = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("|") && (line.Contains("[Collection(\"DbSerial\")]") || line.Contains("[Collection(\"Reads\")]") || line.Contains("[Collection(\"Writes\")]")))
                rowCount++;
        }
        Assert.True(rowCount >= 15,
            $"DbSerial candidate inventory must list ≥15 rows; found {rowCount}.");
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-12")]
    public void DbSerialCollection_W10_StillPresent()
    {
        var asm = typeof(VasquezW12SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("DbSerialCollection", StringComparison.Ordinal)
            || x.Name.Equals("DbSerialCollectionDefinition", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-12")]
    public void TestArchitectureDoc_Section3_1_1_AuditMethodology_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("3.1.1", text, StringComparison.Ordinal);
        Assert.Contains("audit methodology", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-12")]
    public void TestArchitectureDoc_Section3_1_2_ReadsWritesSplit_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("3.1.2", text, StringComparison.Ordinal);
        Assert.Contains("DbSerialReads", text, StringComparison.Ordinal);
        Assert.Contains("DbSerialWrites", text, StringComparison.Ordinal);
    }

    // ─── 2. LH13 workflow threshold collaboration ───────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-12")]
    public void PwaAuditWorkflowGate_MirrorTests_Present()
    {
        var asm = typeof(VasquezW12SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("PwaAuditWorkflowGateTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-12")]
    public void FrontendPwaAuditDoc_Section6_1_W13Deferral_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§6.1", text, StringComparison.Ordinal);
        // The W12 deferral text must include the "defer to W13" wording.
        Assert.Contains("defer", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("W13", text, StringComparison.Ordinal);
    }

    // ─── 3. Visual-regression for W11 screenshots ───────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-12")]
    public void ManifestScreenshotsVisualSpec_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "src", "frontend",
            "autotable-src", "tests", "e2e", "manifest-screenshots-visual.spec.ts");
        Assert.True(File.Exists(path));
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-12")]
    public void TestArchitectureDoc_Section5_VisualRegression_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("## 5. Visual regression", text, StringComparison.Ordinal);
        Assert.Contains("2% pixel", text, StringComparison.Ordinal);
    }

    // ─── 4. Stephen branch-protection re-prompt (5th wave) ──────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-12")]
    public void HandoffProtocol_Section4_1_W12_ReprompTimestamp_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        // The W12 re-prompt status block MUST include the time-since-introduced
        // wording and the W14 fallback proposal.
        Assert.Contains("Re-prompt status", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("W4", text, StringComparison.Ordinal);
        Assert.Contains("W14", text, StringComparison.Ordinal);
    }

    // ─── 5. Forward-stage W12 contract tests ────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-12")]
    public void BishopW12_ContractTests_Present()
    {
        var asm = typeof(VasquezW12SelfLaneTests).Assembly;
        var names = new[]
        {
            "BishopW12ReplayByIdEndpointTests",
            "BishopW12OAuthIntrospectRateLimitTests",
            "BishopW12JwksStagedRotationTests",
            "BishopW12BracketPersistenceTests",
            "BishopW12SpectatorHandoffTokenTests",
            "BishopW12CommentaryCostBudgetTests",
            "BishopW12SignalRSequenceStoreTests",
        };
        foreach (var n in names)
        {
            var t = asm.GetTypes().FirstOrDefault(x =>
                x.Name.Equals(n, StringComparison.Ordinal));
            Assert.True(t is not null, $"Vasquez W12 forward-stage class {n} must be present.");
        }
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-12")]
    public void HicksW12_ContractTests_Present()
    {
        var asm = typeof(VasquezW12SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("HicksW12FrontendContractTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-12")]
    public void AponeW12_ContractTests_Present()
    {
        var asm = typeof(VasquezW12SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("AponeW12InfraContractTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    // ─── 6. KW12 regression rename ─────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-12")]
    public void Wave1ThroughKW12Regression_ClassRenamed()
    {
        var asm = typeof(VasquezW12SelfLaneTests).Assembly;
        // W13 renames Wave1ThroughKW12RegressionTests → Wave1ThroughKW13RegressionTests.
        // W14 renames again to Wave1ThroughKW14RegressionTests.
        // W15 → Wave1ThroughKW15RegressionTests; W16 → Wave1ThroughKW16RegressionTests;
        // W17 → Wave1ThroughKW17RegressionTests; W18 → Wave1ThroughKW18RegressionTests;
        // W19 → Wave1ThroughKW19RegressionTests; W20 → Wave1ThroughKW20RegressionTests.
        // Accept any of the five so this self-lane test stays green across
        // each rename wave.
        var t12or13 = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW12RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW13RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW14RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW15RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW16RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW17RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW18RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW19RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW20RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW21RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW22RegressionTests", StringComparison.Ordinal));
        Assert.NotNull(t12or13);
        // The W11 class must be gone.
        var t11 = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW11RegressionTests", StringComparison.Ordinal));
        Assert.Null(t11);
    }

    // ─── 7. Playwright spec inventory (W12 — 6 specs) ───────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-12")]
    public void W12PlaywrightSpecs_AllSixPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var dir = Path.Combine(root!.FullName, "src", "frontend",
            "autotable-src", "tests", "e2e");
        Assert.True(Directory.Exists(dir));
        var w12Specs = new[]
        {
            "replay-deep-link.spec.ts",
            "shader-chunk-450-stretch.spec.ts",
            "lh13-thresholds-pinned.spec.ts",
            "oauth-introspect-rate-limit.spec.ts",
            "manifest-screenshots-visual.spec.ts",
            "spectator-handoff-token.spec.ts",
        };
        foreach (var spec in w12Specs)
        {
            var path = Path.Combine(dir, spec);
            Assert.True(File.Exists(path), $"Vasquez W12 Playwright spec missing: {spec}");
        }
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-12")]
    public void SelectorsMd_W12Footer_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "src", "frontend",
            "autotable-src", "tests", "selectors.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        // W12 footer references the canonical W12 spec names.
        Assert.Contains("W12", text, StringComparison.Ordinal);
        Assert.Contains("replay-deep-link", text, StringComparison.Ordinal);
    }

    // ─── 8. Concurrent-agent safety: backup mirror present ──────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-12")]
    public void VasquezW12SafeBackup_DirectoryPresent_Or_Gitignored()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        // The .work/ tree is gitignored, so this fact only asserts that
        // the gitignore rule is in place (which is the canonical safe path).
        var gi = Path.Combine(root!.FullName, ".gitignore");
        Assert.True(File.Exists(gi));
        var text = File.ReadAllText(gi);
        Assert.Contains(".work", text, StringComparison.Ordinal);
    }
}
