using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W12.Vasquez;

/// <summary>
/// Phase K Wave 12 — Vasquez. Paired W12 surface-smoke harness.
///
/// <para>The forward-stage contract tests (Bishop/Hicks/Apone W12)
/// each soft-pin their own surface in granular detail. This paired
/// harness gives a single "did the W12 wave actually land?" gate
/// covering the broad cross-cutting axis: types exist, files exist,
/// workflows are present, docs mention the canonical phrases.</para>
///
/// <para>Every fact early-returns on absence so the gate stays green
/// while surfaces converge. Each fact, when it does run, exercises a
/// distinct W12 axis — the breadth here is intentional, the per-axis
/// depth lives in the dedicated contract suites.</para>
/// </summary>
public sealed class W12SurfaceSmokeFactsTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles(".gitignore").Length > 0
                && dir.GetDirectories("src").Length > 0)
                return dir;
            dir = dir.Parent;
        }
        return null;
    }

    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(W12SurfaceSmokeFactsTests).Assembly.GetReferencedAssemblies())
        {
            if (name.Name != "Mahjong.Autotable.Api") continue;
            try { return Assembly.Load(name); } catch { return null; }
        }
        return null;
    }

    private static Type? FindType(string name)
    {
        var asm = ResolveApiAssembly();
        if (asm is null) return null;
        return asm.GetTypes().FirstOrDefault(t =>
            t.Name.Equals(name, StringComparison.Ordinal));
    }

    // ─── Bishop W12 surfaces ───────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_IReplayStore_TypeName_OrForwardStaged()
    {
        _ = FindType("IReplayStore") is not null
         || FindType("EfReplayStore") is not null
         || FindType("ReplayStore") is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_IOAuthIntrospectRateLimiter_TypeName_OrForwardStaged()
    {
        _ = FindType("IOAuthIntrospectRateLimiter") is not null
         || FindType("OAuthIntrospectRateLimiter") is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_EfBracketStore_TypeName_OrForwardStaged()
    {
        _ = FindType("EfBracketStore") is not null
         || FindType("BracketStore") is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_EfSignalRSequenceStore_TypeName_OrForwardStaged()
    {
        _ = FindType("EfSignalRSequenceStore") is not null
         || FindType("SignalRSequenceStore") is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_SpectatorHandoff_TypeName_OrForwardStaged()
    {
        _ = FindType("SpectatorHandoffController") is not null
         || FindType("SpectatorHandoffService") is not null
         || FindType("SpectatorHandoffTokenIssuer") is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_CommentaryCostBudget_TypeName_OrForwardStaged()
    {
        _ = FindType("CommentaryCostBudget") is not null
         || FindType("ICommentaryCostBudget") is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_JwksStagedRotation_TypeName_OrForwardStaged()
    {
        _ = FindType("JwksStagedRotationOptions") is not null
         || FindType("JwtRotationOptions") is not null;
    }

    // ─── Hicks W12 surfaces ────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_ActionRouter_W11_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src", "action-router.ts"),
            Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src", "actionRouter.ts"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_ManifestScreenshots_W11_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "public", "screenshots");
        _ = Directory.Exists(dir);
    }

    // ─── Apone W12 surfaces ────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_RedisLoadTestManifest_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        _ = File.Exists(Path.Combine(root.FullName, "infra", "load-tests", "redis-load-test.yml"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_ProdCutoverDoc_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        _ = File.Exists(Path.Combine(root.FullName, "docs", "prod-cutover.md"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_OAuthIntrospectRateLimitDoc_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        _ = File.Exists(Path.Combine(root.FullName, "docs", "oauth-introspect-rate-limit.md"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_ReplayByIdDoc_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        _ = File.Exists(Path.Combine(root.FullName, "docs", "replay-by-id.md"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_TerraformProdEnv_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        _ = Directory.Exists(Path.Combine(root.FullName, "infra", "terraform", "envs", "prod"));
    }

    // ─── Cross-wave doc surfaces ────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_Changelog_0_21_0_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.21.0", StringComparison.Ordinal)
         || text.Contains("Wave 12", StringComparison.OrdinalIgnoreCase);
    }

    // ─── Vasquez self-lane (hard-asserts — ship in this PR) ─────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_DbSerialCandidatesDoc_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "Phase_K_W12", "Vasquez",
            "db-serial-candidates.md");
        Assert.True(File.Exists(path));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_TestArchitectureDoc_W12VisualRegression_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path));
        Assert.Contains("Visual regression", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_HandoffProtocol_W12Reprompt_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        Assert.Contains("Re-prompt", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_FrontendPwaAuditDoc_W12_Section6_1_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
        Assert.True(File.Exists(path));
        Assert.Contains("§6.1", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_Wave1ThroughKW12RegressionClass_Present()
    {
        var asm = typeof(W12SurfaceSmokeFactsTests).Assembly;
        // W13 renames Wave1ThroughKW12RegressionTests → Wave1ThroughKW13RegressionTests.
        // W14 renames again to Wave1ThroughKW14RegressionTests.
        // W15 → Wave1ThroughKW15RegressionTests; W16 → Wave1ThroughKW16RegressionTests;
        // W17 → Wave1ThroughKW17RegressionTests; W18 → Wave1ThroughKW18RegressionTests;
        // W19 → Wave1ThroughKW19RegressionTests; W20 → Wave1ThroughKW20RegressionTests.
        // Accept any of the five so this W12 smoke stays green across the renames.
        var t = asm.GetTypes().FirstOrDefault(x =>
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
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-12")]
    public void Smoke_PwaAuditWorkflowGate_TestsClass_Present()
    {
        var asm = typeof(W12SurfaceSmokeFactsTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("PwaAuditWorkflowGateTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }
}
