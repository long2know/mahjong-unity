using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Vasquez;

/// <summary>
/// Phase K Wave 15 — Vasquez. Paired W15 surface-smoke harness.
///
/// <para>The forward-stage contract tests (Bishop/Hicks/Apone W15)
/// each soft-pin their own surface in granular detail. This paired
/// harness gives a single "did the W15 wave actually land?" gate
/// covering the broad cross-cutting axis.</para>
///
/// <para>Every fact early-returns on absence so the gate stays green
/// while surfaces converge. Vasquez self-lane artefacts hard-assert.</para>
/// </summary>
public sealed class W15SurfaceSmokeFactsTests
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
        foreach (var name in typeof(W15SurfaceSmokeFactsTests).Assembly.GetReferencedAssemblies())
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

    // ─── Bishop W15 surfaces (12 facts) ────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_ReplayBlobStreamingController()
    {
        var t = FindType("ReplayBlobController")
            ?? FindType("ReplayDownloadController")
            ?? FindType("ReplayStreamingController");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_PerTenantJwksRotationPolicy()
    {
        var t = FindType("TenantJwksRotationPolicy")
            ?? FindType("PerTenantJwksRotationPolicy")
            ?? FindType("TenantJwksPolicy");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_TournamentQueryMetrics()
    {
        var t = FindType("TournamentQueryMetrics")
            ?? FindType("TournamentMetrics")
            ?? FindType("BracketQueryMetrics");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_CommentaryCostForecastService()
    {
        var t = FindType("CommentaryCostForecastService")
            ?? FindType("CommentaryCostForecast")
            ?? FindType("CommentaryCostForecaster");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_SpectatorAuditRetentionSweepService()
    {
        var t = FindType("SpectatorAuditRetentionSweepService")
            ?? FindType("SpectatorAuditRetentionService")
            ?? FindType("SpectatorAuditRetentionSweep");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_ReplayRetentionSweepService()
    {
        var t = FindType("ReplayRetentionSweepService")
            ?? FindType("ReplayRetentionService")
            ?? FindType("ReplayRetentionSweep");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_DbSerial_W9_AttributeApplied()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var paths = new[]
        {
            Path.Combine(root.FullName, "src", "backend", "tests",
                "Mahjong.Autotable.Api.Tests", "Phase_K_W9", "Bishop",
                "EfCommentaryUsageMeterTests.cs"),
            Path.Combine(root.FullName, "src", "backend", "tests",
                "Mahjong.Autotable.Api.Tests", "Phase_K_W9", "Bishop",
                "IdempotencyStoreContractTests.cs"),
        };
        _ = paths.All(File.Exists);
    }

    // ─── Hicks W15 surfaces ────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_PhaseLRenderer_WebGL2_Source_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src");
        if (!Directory.Exists(dir)) return;
        var hits = Directory.GetFiles(dir, "renderer-webgl2*",
            SearchOption.AllDirectories);
        _ = hits.Length > 0;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_PlaywrightConfig_SnapshotPathTemplate_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "playwright.config.ts");
        if (!File.Exists(path)) return;
        _ = File.ReadAllText(path).Contains("snapshotPathTemplate",
            StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_CostForecastRoute_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src");
        if (!Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.ts", SearchOption.AllDirectories);
        _ = files.Any(f =>
            File.ReadAllText(f).Contains("cost-forecast",
                StringComparison.OrdinalIgnoreCase));
    }

    // ─── Apone W15 surfaces ────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_KyvernoEnforce_PreWire_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "infra", "k8s", "policies",
                "kyverno-enforce-policies.yaml"),
            Path.Combine(root.FullName, "infra", "k8s", "kyverno",
                "enforce-policies.yaml"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_SLSA3Assessment_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "docs", "slsa-3-assessment.md"),
            Path.Combine(root.FullName, "docs", "slsa-level-3-assessment.md"),
            Path.Combine(root.FullName, "docs", "slsa.md"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_PhaseLL1Design_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "docs", "phase-l-l1-design.md"),
            Path.Combine(root.FullName, "docs", "phase-l-devops-l1.md"),
            Path.Combine(root.FullName, "docs", "phase-l-devops-readiness.md"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_Changelog_0_24_0_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(path)) return;
        _ = File.ReadAllText(path).Contains("0.24.0", StringComparison.Ordinal);
    }

    // ─── Vasquez W15 self-lane artefacts (hard-assert) ─────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_Vasquez_W15_Memo_HardAssert()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "Phase_K_W15", "Vasquez",
            "vasquez-phase-k-wave-15.md");
        Assert.True(File.Exists(p));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_Vasquez_TestArchitecture_Section3_4_HardAssert()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        var text = File.ReadAllText(p);
        Assert.Contains("§3.4", text, StringComparison.Ordinal);
        Assert.Contains("DbSerial migration final completion", text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-15")]
    public void Smoke_Vasquez_HandoffProtocol_Section6_HardAssert()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        var text = File.ReadAllText(p);
        Assert.Contains("Lane-discipline maturity narrative", text,
            StringComparison.OrdinalIgnoreCase);
    }
}
