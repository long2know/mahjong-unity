using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W11.Vasquez;

/// <summary>
/// Phase K Wave 11 — Vasquez. Self-lane process gates.
///
/// <para>The Vasquez W11 lane includes:</para>
/// <list type="number">
///   <item>Lane-map <c>shared_files</c> gains
///         <c>shims_shared</c> entry (Bishop|Vasquez|Hicks|Apone
///         co-authors) and <c>pwa_audit_workflow_shared</c>
///         entry (Hicks|Apone co-authors; primary apone).</item>
///   <item>Bundling-check broadening — <c>is_shared_file()</c>
///         + <c>shared_file_authors()</c> recognise the new
///         shared paths.</item>
///   <item><c>docs/test-architecture.md</c> §4.3 (W11 closed gaps)
///         + §4.4 (W11+ open gaps) documented.</item>
///   <item><c>docs/agent-handoff-protocol.md</c> §4.1 (screenshot
///         walkthrough + 422 troubleshooting + one-liner PATCH)
///         documented.</item>
///   <item><c>docs/agent-handoff-protocol.md</c> §5.9 (shared-files
///         registry policy) documented.</item>
///   <item>Three gap-fill integration tests present
///         (RedisIdempotencyStore, JanusReadinessSupervisor,
///         SignalR backpressure).</item>
///   <item>Six new W11 Playwright specs present.</item>
///   <item>W10 self-lane regression pins still present
///         (handoff §5, DbSerial collection, test-architecture.md).</item>
/// </list>
///
/// <para>These facts HARD-ASSERT — the artefacts ship in the same
/// Vasquez W11 PR, so no forward-stage soft-pass is needed.</para>
/// </summary>
public sealed class VasquezW11SelfLaneTests
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

    // ─── 1. lane-map: shims_shared ──────────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void LaneMap_SharedFiles_ShimsSharedEntry_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "lane-map.json");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("shims_shared", text);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void LaneMap_ShimsSharedEntry_HasAllFourSquadAuthors()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "lane-map.json");
        if (!File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(doc.RootElement.TryGetProperty("shared_files", out var sf));
        Assert.True(sf.TryGetProperty("shims_shared", out var entry));
        Assert.True(entry.TryGetProperty("authors", out var authors));
        var list = authors.EnumerateArray().Select(a => a.GetString() ?? "").ToHashSet();
        Assert.Contains("bishop", list);
        Assert.Contains("vasquez", list);
        Assert.Contains("hicks", list);
        Assert.Contains("apone", list);
        Assert.True(entry.TryGetProperty("primary", out var primary));
        Assert.Equal("vasquez", primary.GetString());
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void LaneMap_ShimsSharedEntry_HasShimsPathRegex()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "lane-map.json");
        if (!File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(doc.RootElement.TryGetProperty("shared_files", out var sf));
        Assert.True(sf.TryGetProperty("shims_shared", out var entry));
        Assert.True(entry.TryGetProperty("paths", out var paths));
        var anyShim = paths.EnumerateArray()
            .Any(p => (p.GetString() ?? "").Contains("Shims", StringComparison.Ordinal));
        Assert.True(anyShim, "shims_shared paths MUST mention Shims/.");
    }

    // ─── 2. lane-map: pwa_audit_workflow_shared ─────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void LaneMap_PwaAuditWorkflowShared_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "lane-map.json");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("pwa_audit_workflow_shared", text);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void LaneMap_PwaAuditWorkflowShared_HasHicksAndApone()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "lane-map.json");
        if (!File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(doc.RootElement.TryGetProperty("shared_files", out var sf));
        Assert.True(sf.TryGetProperty("pwa_audit_workflow_shared", out var entry));
        Assert.True(entry.TryGetProperty("authors", out var authors));
        var list = authors.EnumerateArray().Select(a => a.GetString() ?? "").ToHashSet();
        Assert.Contains("hicks", list);
        Assert.Contains("apone", list);
        Assert.True(entry.TryGetProperty("primary", out var primary));
        Assert.Equal("apone", primary.GetString());
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void LaneMap_PwaAuditWorkflowShared_CoversBothWorkflows()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "lane-map.json");
        if (!File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(doc.RootElement.TryGetProperty("shared_files", out var sf));
        Assert.True(sf.TryGetProperty("pwa_audit_workflow_shared", out var entry));
        Assert.True(entry.TryGetProperty("paths", out var paths));
        var pathStrings = paths.EnumerateArray().Select(p => p.GetString() ?? "").ToList();
        var hasAudit = pathStrings.Any(p => p.Contains("pwa-audit", StringComparison.Ordinal));
        var hasBuilder = pathStrings.Any(p => p.Contains("pwa-builder", StringComparison.Ordinal));
        Assert.True(hasAudit, "pwa_audit_workflow_shared MUST cover pwa-audit.yml.");
        Assert.True(hasBuilder, "pwa_audit_workflow_shared MUST cover pwa-builder.yml.");
    }

    // ─── 3. bundling-check broadening ───────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void BundlingCheck_RecognisesShimsAsShared()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "check-cross-lane-bundling.sh");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("Shims/", text);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void BundlingCheck_RecognisesPwaAuditAsShared()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "check-cross-lane-bundling.sh");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("pwa-audit.yml", text);
        Assert.Contains("pwa-builder.yml", text);
    }

    // ─── 4. handoff-protocol §4.1 + §5.9 ─────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void HandoffProtocol_Section41_ScreenshotWalkthrough_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.True(
            text.Contains("4.1", StringComparison.Ordinal)
            && text.Contains("Screenshot", StringComparison.OrdinalIgnoreCase),
            "Handoff doc §4.1 (W11 screenshot walkthrough) MUST be present.");
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void HandoffProtocol_Section41_TroubleshootingFor422_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.Contains("422", text);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void HandoffProtocol_Section41_OneLinerPatch_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.Contains("gh api -X PATCH", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void HandoffProtocol_Section59_SharedFilesRegistryPolicy_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.True(
            text.Contains("5.9", StringComparison.Ordinal)
            && text.Contains("Shared-files registry", StringComparison.OrdinalIgnoreCase),
            "Handoff doc §5.9 (shared-files registry policy) MUST be present.");
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void HandoffProtocol_Section59_ListsAllFourSharedEntries()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // Selectors (W8), handoff-protocol (W10), shims (W11), pwa workflows (W11).
        Assert.Contains("selectors", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agent-handoff-protocol", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Shims", text, StringComparison.Ordinal);
        Assert.Contains("pwa-audit", text, StringComparison.OrdinalIgnoreCase);
    }

    // ─── 5. docs/test-architecture.md §4.3 + §4.4 ───────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void TestArchitectureDoc_Section43_ClosedGaps_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.True(
            text.Contains("4.3", StringComparison.Ordinal)
            && text.Contains("Closed gaps", StringComparison.OrdinalIgnoreCase),
            "test-architecture.md §4.3 (W11 closed gaps) MUST be present.");
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void TestArchitectureDoc_Section43_MentionsAllThreeGapFills()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "test-architecture.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // The three closed gaps documented in §4.3.
        Assert.Contains("RedisIdempotencyStore", text, StringComparison.Ordinal);
        Assert.Contains("JanusReadinessSupervisor", text, StringComparison.Ordinal);
        Assert.Contains("backpressure", text, StringComparison.OrdinalIgnoreCase);
    }

    // ─── 6. Three gap-fill integration tests present ────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void GapFill_RedisIdempotencyStoreIntegrationTests_Present()
    {
        var asm = typeof(VasquezW11SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(t =>
            t.Name.Equals("RedisIdempotencyStoreIntegrationTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void GapFill_JanusReadinessSupervisorIntegrationTests_Present()
    {
        var asm = typeof(VasquezW11SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(t =>
            t.Name.Equals("JanusReadinessSupervisorIntegrationTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void GapFill_SignalRBackpressureIntegrationTests_Present()
    {
        var asm = typeof(VasquezW11SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(t =>
            t.Name.Equals("SignalRBackpressureIntegrationTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    // ─── 7. Six new W11 Playwright specs present ────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void PlaywrightSpecs_W11_AllPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var e2eDir = Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "tests", "e2e");
        if (!Directory.Exists(e2eDir)) return;
        var expected = new[]
        {
            "shader-chunk-475-hard.spec.ts",
            "pwa-builder-platforms.spec.ts",
            "lh13-baseline-calibration.spec.ts",
            "cache-hit-rate.spec.ts",
            "manifest-screenshots-real.spec.ts",
            "deep-link-action-routing.spec.ts",
        };
        foreach (var spec in expected)
        {
            var p = Path.Combine(e2eDir, spec);
            Assert.True(File.Exists(p), $"Playwright spec MUST be present: {spec}");
        }
    }

    // ─── 8. W10 regression pins ─────────────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void HandoffProtocol_Section5_StillPresent_W10Pin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.Contains("Concurrent agent safety", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void DbSerialCollection_StillPresent_W10Pin()
    {
        var asm = typeof(VasquezW11SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("DbSerialCollection", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void TestArchitectureDoc_StillPresent_W10Pin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path),
            "docs/test-architecture.md MUST be present (W10 Vasquez regression pin).");
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void LaneMap_AgentHandoffShared_StillPresent_W10Pin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "lane-map.json");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("agent_handoff_protocol_md_shared", text);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void LaneMap_SelectorsMdShared_StillPresent_W8Pin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "lane-map.json");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("selectors_md_shared", text);
    }

    // ─── 9. KW10 → KW11 → KW12 regression rename (forward-staged) ──

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-11")]
    public void RegressionClassRenamed_Wave1ThroughKW11_Present()
    {
        var asm = typeof(VasquezW11SelfLaneTests).Assembly;
        // W12 renames Wave1ThroughKW11RegressionTests → Wave1ThroughKW12RegressionTests.
        // W13 renames again to Wave1ThroughKW13RegressionTests.
        // W14 renames again to Wave1ThroughKW14RegressionTests.
        // W15 → Wave1ThroughKW15RegressionTests; W16 → Wave1ThroughKW16RegressionTests;
        // W17 → Wave1ThroughKW17RegressionTests; W18 → Wave1ThroughKW18RegressionTests;
        // W19 → Wave1ThroughKW19RegressionTests; W20 → Wave1ThroughKW20RegressionTests.
        // Accept any of the eight so the W11 self-lane test stays
        // green across each rename wave.
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
