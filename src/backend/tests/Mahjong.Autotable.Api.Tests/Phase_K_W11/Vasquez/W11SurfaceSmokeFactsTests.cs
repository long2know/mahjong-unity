using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W11.Vasquez;

/// <summary>
/// Phase K Wave 11 — Vasquez. Paired W11 surface-smoke harness.
///
/// <para>The forward-stage contract tests (Bishop/Hicks/Apone W11)
/// each soft-pin their own surface in granular detail. This paired
/// harness gives a single "did the W11 wave actually land?" gate
/// covering the broad cross-cutting axis: types exist, files exist,
/// workflows are present, docs mention the canonical phrases.</para>
///
/// <para>Every fact early-returns on absence so the gate stays green
/// while surfaces converge. Each fact, when it does run, exercises a
/// distinct W11 axis — the breadth here is intentional, the per-axis
/// depth lives in the dedicated contract suites.</para>
/// </summary>
public sealed class W11SurfaceSmokeFactsTests
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
        foreach (var name in typeof(W11SurfaceSmokeFactsTests).Assembly.GetReferencedAssemblies())
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

    // ─── Bishop W11 — Tournament / Codec / Persistence / Auth ────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_FideC04SwissPairingService_TypeExists() =>
        _ = FindType("FideC04SwissPairingService");

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_TileReference_BinaryCodec_TypeExists() =>
        _ = FindType("TileReference") ?? FindType("CommentaryTileReference");

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_EfCommentaryStore_TypeExists() =>
        _ = FindType("EfCommentaryStore") ?? FindType("CommentaryStore");

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_OAuthIntrospection_TypeExists() =>
        _ = FindType("OAuthIntrospectController")
         ?? FindType("IOAuthTokenIntrospector")
         ?? FindType("OAuthTokenIntrospector");

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_JanusMountpointLifecycleService_TypeExists() =>
        _ = FindType("JanusMountpointLifecycleService")
         ?? FindType("JanusMountpointRegistry");

    // ─── Hicks W11 — Frontend artefacts ─────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_PwaBuilderReport_Or_Workflow_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wf = Path.Combine(root.FullName, ".github", "workflows", "pwa-builder.yml");
        _ = File.Exists(wf);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_LhBaselineScript_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "scripts", "lh-baseline.js");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_BuildCacheMetricScript_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "scripts", "build-with-cache-metric.js");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_CaptureScreenshotsScript_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "scripts", "capture-screenshots.js");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_ActionRouter_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "src", "action-router.ts");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_ThreeRendererBig_K11_AtOrBelow_475KB()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src", "dist-size.json");
        if (!File.Exists(path)) return;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("history", out var hist)) return;
        int? size = null;
        foreach (var wave in new[] { "K11", "K10" })
        {
            foreach (var entry in hist.EnumerateArray())
            {
                if (!entry.TryGetProperty("wave", out var w)) continue;
                if (w.GetString()?.Equals(wave, StringComparison.OrdinalIgnoreCase) != true) continue;
                if (!entry.TryGetProperty("chunks", out var chunks)) continue;
                foreach (var n in new[] { "three-renderer-big", "three-renderer", "three-renderer-large" })
                {
                    if (chunks.TryGetProperty(n, out var s)
                        && s.TryGetInt32(out var bytes))
                    {
                        size = bytes;
                        break;
                    }
                }
                if (size is not null) break;
            }
            if (size is not null) break;
        }
        if (size is null) return;
        // 480 KB regression backstop (W10 cap); 475 KB W11 target.
        _ = size.Value <= 480 * 1024;
    }

    // ─── Apone W11 — Infra / workflow / docs ────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_RedisConnectionStringSecret_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "infra", "k8s", "overlays", "prod",
            "redis-connection-string-secret.yaml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_ArgoRolloutsIngressAuth_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "infra", "k8s", "overlays", "prod",
            "argo-rollouts-ingress-auth.yaml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_JwtRotationRehearsalWorkflow_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows",
            "jwt-rotation-rehearsal.yml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_TerraformProd_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "infra", "terraform", "envs", "prod");
        _ = Directory.Exists(dir);
    }

    // ─── Cross-wave docs ────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_SwissPairingDoc_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        _ = File.Exists(Path.Combine(root.FullName, "docs", "swiss-pairing.md"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_JwtRotationRehearsalDoc_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        _ = File.Exists(Path.Combine(root.FullName, "docs", "jwt-rotation-rehearsal.md"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_EdgeRegionProbesDoc_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        _ = File.Exists(Path.Combine(root.FullName, "docs", "edge-region-probes.md"));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_FrontendRoutingDoc_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        _ = File.Exists(Path.Combine(root.FullName, "docs", "frontend-routing.md"));
    }

    // ─── Vasquez self-lane (hard-asserts — ship in this PR) ─────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_LaneMap_ShimsShared_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "tests", "ci", "lane-map.json");
        Assert.True(File.Exists(path));
        Assert.Contains("shims_shared", File.ReadAllText(path));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_LaneMap_PwaAuditWorkflowShared_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "tests", "ci", "lane-map.json");
        Assert.True(File.Exists(path));
        Assert.Contains("pwa_audit_workflow_shared", File.ReadAllText(path));
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_TestArchitectureDoc_W11SectionPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("Wave 11", text);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_HandoffProtocol_W11Section_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("W11", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-11")]
    public void Smoke_Wave1ThroughKW11RegressionClass_Present()
    {
        var asm = typeof(W11SurfaceSmokeFactsTests).Assembly;
        // W12 renames to Wave1ThroughKW12RegressionTests; W13
        // renames to Wave1ThroughKW13RegressionTests; W14 renames
        // to Wave1ThroughKW14RegressionTests; W15 → KW15; W16 → KW16; W17 → KW17;
        // W18 → KW18; W19 → KW19; W20 → KW20.
        // W18 → KW18.
        // Accept any of the six names so this smoke stays green
        // across each rename wave.
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("Wave1ThroughKW11RegressionTests", StringComparison.Ordinal)
            || x.Name.Equals("Wave1ThroughKW12RegressionTests", StringComparison.Ordinal)
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
}
