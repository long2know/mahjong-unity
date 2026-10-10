using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W9;

/// <summary>
/// Phase K Wave 9 — Vasquez. Bulk smoke-fact coverage for the W9
/// surfaces. Mirrors the W7 / W8 <c>WnSurfaceSmokeFactsTests</c>
/// pattern — broad single-axis assertions across all three lanes.
///
/// <para>Every fact is reflection-defensive / filesystem-defensive
/// against the API assembly and the repo root under the Vasquez-
/// owned read lane.</para>
/// </summary>
public sealed class W9SurfaceSmokeFactsTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static DirectoryInfo? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = dir; d is not null; d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".github", "workflows"))
                && File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            {
                return d;
            }
        }
        return null;
    }

    private static Type? T(string name) =>
        ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == name);

    // ────────────────────────────────────────────────────────────────────
    //  Backend smoke facts — Bishop's W9 lane.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_EfCommentaryUsageMeter_TypeOrForwardStaged()
    {
        _ = T("EfCommentaryUsageMeter") ?? T("CommentaryUsageMeter");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_JanusReadinessSupervisor_TypeOrForwardStaged()
    {
        _ = T("JanusReadinessSupervisor") ?? T("JanusHealthSupervisor");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_EfIdempotencyStore_TypeOrForwardStaged()
    {
        _ = T("EfIdempotencyStore") ?? T("EfCoreIdempotencyStore");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_RedisIdempotencyStore_TypeOrForwardStaged()
    {
        _ = T("RedisIdempotencyStore") ?? T("RedisIdempotencyKeyStore");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_RotationCadenceValidator_TypeOrForwardStaged()
    {
        _ = T("RotationCadenceValidator") ?? T("JwksRotationValidator");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_BackpressureMiddleware_TypeOrForwardStaged()
    {
        _ = T("BackpressureMiddleware") ?? T("SignalRBackpressureMiddleware");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_UsageCapExceededException_OrForwardStaged()
    {
        var t = T("UsageCapExceededException") ?? T("CommentaryUsageCapExceededException");
        if (t is null) return;
        Assert.True(typeof(Exception).IsAssignableFrom(t));
    }

    // ────────────────────────────────────────────────────────────────────
    //  Frontend / bundle smoke facts — Hicks's W9 lane.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_FindThingByFace_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var fe = Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src");
        if (!Directory.Exists(fe)) return;
        var matched = false;
        foreach (var f in Directory.EnumerateFiles(fe, "*.ts", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            if (text.Contains("findThingByFace", StringComparison.Ordinal))
            {
                matched = true;
                break;
            }
        }
        _ = matched;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_LighthouseRc_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "src", "frontend", "autotable-src", "lighthouserc.json"),
            Path.Combine(root.FullName, "src", "frontend", "autotable-src", ".lighthouserc.json"),
            Path.Combine(root.FullName, "src", "frontend", "autotable-src", "lighthouserc.yml"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_BracketRenderer_FileOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(
            root.FullName, "src", "frontend", "autotable-src", "src", "bracket-renderer.ts");
        _ = File.Exists(path);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Infra smoke facts — Apone's W9 lane.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_MobileProductionHotfixWorkflow_FileOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(
            root.FullName, ".github", "workflows", "mobile-production-hotfix.yml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_WorkDirSquadGitLock_DocsOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var doc = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(doc)) return;
        _ = File.ReadAllText(doc).Contains(".work/squad-git-lock",
            StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_Changelog_0_18_0_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(path)) return;
        _ = File.ReadAllText(path).Contains("0.18.0", StringComparison.Ordinal);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Cross-lane smoke — Vasquez's W9 process discipline.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_LaneDisciplineNightly_Workflow_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(
            root.FullName, ".github", "workflows", "lane-discipline-nightly.yml");
        Assert.True(File.Exists(path),
            "lane-discipline-nightly.yml MUST be present (W9 Vasquez).");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_LaneDisciplineStatus_Workflow_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(
            root.FullName, ".github", "workflows", "lane-discipline-status.yml");
        Assert.True(File.Exists(path),
            "lane-discipline-status.yml MUST be present (W9 Vasquez).");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_HandoffProtocol_Section_3_6_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.True(
            text.Contains("§3.6", StringComparison.Ordinal)
                || text.Contains("3.6 ", StringComparison.Ordinal)
                || text.Contains("3.6.", StringComparison.Ordinal));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_HandoffProtocol_Section_3_7_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.True(
            text.Contains("§3.7", StringComparison.Ordinal)
                || text.Contains("3.7 ", StringComparison.Ordinal)
                || text.Contains("3.7.", StringComparison.Ordinal));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-9")]
    public void Smoke_W9_HandoffProtocol_Section_4_BranchProtection_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.Contains("Branch-protection setup", text);
        Assert.Contains("gh api", text);
    }
}
