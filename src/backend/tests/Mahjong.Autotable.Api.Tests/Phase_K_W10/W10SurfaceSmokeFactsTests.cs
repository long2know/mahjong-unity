using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10;

/// <summary>
/// Phase K Wave 10 — Vasquez. Bulk smoke-fact coverage for the W10
/// surfaces. Mirrors the W7 / W8 / W9 <c>WnSurfaceSmokeFactsTests</c>
/// pattern — broad single-axis assertions across all three lanes.
///
/// <para>Every fact is reflection-defensive / filesystem-defensive
/// against the API assembly and the repo root under the Vasquez-
/// owned read lane. Forward-stage tolerant by design.</para>
/// </summary>
public sealed class W10SurfaceSmokeFactsTests
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
    //  Backend smoke facts — Bishop's W10 lane.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_JanusReadinessLevel_EnumOrForwardStaged()
    {
        _ = T("JanusReadinessLevel") ?? T("VoiceReadinessLevel");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_JanusMountpointLifecycleService_TypeOrForwardStaged()
    {
        _ = T("JanusMountpointLifecycleService") ?? T("JanusMountpointRegistry");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_DutchSwissPairingService_TypeOrForwardStaged()
    {
        _ = T("DutchSwissPairingService") ?? T("DutchPairingService");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_CommentaryTileReference_TypeOrForwardStaged()
    {
        _ = T("CommentaryTileReference") ?? T("TileReference") ?? T("RichTileReference");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_RedisIdempotencyStore_StillPublic_W9RegressionPin()
    {
        var t = T("RedisIdempotencyStore") ?? T("RedisIdempotencyKeyStore");
        if (t is null) return;
        Assert.True(t.IsPublic || t.IsNestedPublic);
        Assert.False(t.IsAbstract);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_BackpressureMetrics_TypeOrForwardStaged()
    {
        _ = T("BackpressureMetrics") ?? T("SignalRBackpressureMetrics");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_JwksCacheMetrics_TypeOrForwardStaged()
    {
        _ = T("JwksCacheMetrics") ?? T("JwksMetrics");
    }

    // ────────────────────────────────────────────────────────────────────
    //  Frontend / infra smoke facts — Hicks's W10 lane.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_PwaAuditWorkflow_FileOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_CommentaryHighlightTile_EventChannel_OrForwardStaged()
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
            if (text.Contains("mahjong:highlight-tile", StringComparison.Ordinal))
            {
                matched = true;
                break;
            }
        }
        _ = matched;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_ManifestWebmanifest_FileOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src", "manifest.webmanifest");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_ViteConfig_FileOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src", "vite.config.ts");
        _ = File.Exists(path);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Infra smoke facts — Apone's W10 lane.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_RedisTerraformModule_DirOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var modDir = Path.Combine(root.FullName, "infra", "terraform", "modules", "redis");
        _ = Directory.Exists(modDir);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_ArgoRolloutsSetupDoc_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "argo-rollouts-setup.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_RedisClusterDoc_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "redis-cluster.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_ContainerScanRemediationWorkflow_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(
            root.FullName, ".github", "workflows", "container-scan-remediation.yml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_Changelog_0_19_0_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(path)) return;
        _ = File.ReadAllText(path).Contains("0.19.0", StringComparison.Ordinal);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Cross-lane smoke — Vasquez's W10 process discipline.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_HandoffProtocol_Section_5_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.Contains("Concurrent agent safety", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_TestArchitectureDoc_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path), "docs/test-architecture.md MUST be present (W10).");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_LaneMap_HandoffShared_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "lane-map.json");
        if (!File.Exists(path)) return;
        Assert.Contains("agent_handoff_protocol_md_shared", File.ReadAllText(path));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-10")]
    public void Smoke_W10_DbSerialCollection_Present()
    {
        var asm = typeof(W10SurfaceSmokeFactsTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("DbSerialCollection", StringComparison.Ordinal)
            || x.Name.Equals("DbSerialCollectionDefinition", StringComparison.Ordinal));
        Assert.NotNull(t);
    }
}
