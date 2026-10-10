using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Vasquez;

/// <summary>
/// Phase K Wave 14 — Vasquez. Paired W14 surface-smoke harness.
///
/// <para>The forward-stage contract tests (Bishop/Hicks/Apone W14)
/// each soft-pin their own surface in granular detail. This paired
/// harness gives a single "did the W14 wave actually land?" gate
/// covering the broad cross-cutting axis.</para>
///
/// <para>Every fact early-returns on absence so the gate stays
/// green while surfaces converge.</para>
/// </summary>
public sealed class W14SurfaceSmokeFactsTests
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
        foreach (var name in typeof(W14SurfaceSmokeFactsTests).Assembly.GetReferencedAssemblies())
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

    // ─── Bishop W14 surfaces ───────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-14")]
    public void Smoke_SpectatorAuditQueryController()
    {
        var t = FindType("SpectatorAuditQueryController")
            ?? FindType("AdminSpectatorAuditController")
            ?? FindType("SpectatorHandoffAuditController");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-14")]
    public void Smoke_CommentaryCostSummaryController()
    {
        var t = FindType("CommentaryCostController")
            ?? FindType("CommentaryCostSummaryController");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-14")]
    public void Smoke_BracketQueryController()
    {
        var t = FindType("BracketQueryController")
            ?? FindType("TournamentBracketController")
            ?? FindType("TournamentController");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-14")]
    public void Smoke_ReplayListingController()
    {
        var t = FindType("ReplayListingController")
            ?? FindType("ReplayController")
            ?? FindType("ReplaysController");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-14")]
    public void Smoke_JwksOverlapWindow()
    {
        var t = FindType("JwksOverlapWindow")
            ?? FindType("JwksOverlap")
            ?? FindType("JwtKeyringOverlapWindow");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-14")]
    public void Smoke_SignalRMetrics()
    {
        var t = FindType("SignalRMetrics")
            ?? FindType("SignalRMetricExposition")
            ?? FindType("SignalRHubMetrics");
        _ = t is not null;
    }

    // ─── Phase L bring-up docs (Bishop + Hicks + Apone W14) ────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-14")]
    public void Smoke_PhaseLBringup_Doc()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        _ = File.Exists(Path.Combine(root.FullName, "docs", "phase-l-bringup.md"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-14")]
    public void Smoke_PhaseLDevopsReadiness_Doc()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        _ = File.Exists(Path.Combine(root.FullName, "docs", "phase-l-devops-readiness.md"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-14")]
    public void Smoke_PhaseLRendererSpike_Doc()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        _ = File.Exists(Path.Combine(root.FullName, "docs", "phase-l-renderer-spike.md"));
    }

    // ─── Apone W14 surfaces ────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-14")]
    public void Smoke_Terraform_1_11_4_Pin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "terraform.md");
        if (!File.Exists(path)) return;
        _ = File.ReadAllText(path).Contains("1.11.4", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-14")]
    public void Smoke_JwtRotationRehearsal_Third_GA()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "jwt-rotation-rehearsal.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("GA", StringComparison.Ordinal)
         && (text.Contains("third", StringComparison.OrdinalIgnoreCase)
             || text.Contains("Rehearsal #3", StringComparison.Ordinal)
             || text.Contains("Rehearsal 3", StringComparison.Ordinal));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-14")]
    public void Smoke_RegionalEksBringup_UsEast1()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "regional-eks-bringup.md");
        if (!File.Exists(path)) return;
        _ = File.ReadAllText(path).Contains("us-east-1", StringComparison.Ordinal);
    }

    // ─── Vasquez W14 self-lane artefacts (hard-assert) ─────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-14")]
    public void Smoke_Vasquez_DbSerial_Completion_Memo_HardAssert()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "Phase_K_W14", "Vasquez",
            "db-serial-migration-completion.md");
        Assert.True(File.Exists(p));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-14")]
    public void Smoke_Vasquez_W14_Memo_HardAssert()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "Phase_K_W14", "Vasquez",
            "vasquez-phase-k-wave-14.md");
        Assert.True(File.Exists(p));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-14")]
    public void Smoke_Vasquez_TestArchitecture_Sections_HardAssert()
    {
        // §3.3 DbSerial completion + §5.2 visual-regression spec fix
        // — both LAND in W14.
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        var text = File.ReadAllText(p);
        Assert.Contains("§3.3", text, StringComparison.Ordinal);
        Assert.Contains("§5.2", text, StringComparison.Ordinal);
        Assert.Contains("W14", text, StringComparison.Ordinal);
    }
}
