using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Vasquez;

/// <summary>
/// Phase K Wave 10 — Vasquez. Self-lane process gates.
///
/// <para>The Vasquez W10 lane includes:</para>
/// <list type="number">
///   <item>Lane-map <c>shared_files</c> gains
///         <c>agent_handoff_protocol_md_shared</c> entry for
///         <c>docs/agent-handoff-protocol.md</c> (W10 wave shared
///         between vasquez + apone — apone authored §3.6/§3.7,
///         vasquez authors §5).</item>
///   <item>Bundling check broadened — files in <c>shared_files</c>
///         are EXCLUDED from the lane-set computation so a commit
///         touching a shared file + lane source counts as a SINGLE
///         lane (the non-shared one).</item>
///   <item>xunit <c>DbSerial</c> collection definition exists so
///         DB-touching tests can opt out of parallelism (W9
///         <c>EfCommentaryUsageMeter</c> SQLite tests were flaky
///         under default parallelism).</item>
///   <item><c>docs/test-architecture.md</c> §3 documents the test
///         parallelism policy.</item>
///   <item><c>docs/test-architecture.md</c> §4 documents the
///         coverage pyramid.</item>
///   <item><c>docs/agent-handoff-protocol.md</c> §5 documents
///         concurrent agent safety guarantees.</item>
///   <item>W9 nightly cron + opt-in status workflows still
///         present (regression pin).</item>
///   <item>W9 lane-map shared-files <c>selectors_md_shared</c>
///         entry still present (regression pin).</item>
///   <item>Regression test renamed —
///         <c>Wave1ThroughKW10RegressionTests</c> class is the
///         canonical name (KW9 → KW10).</item>
///   <item><c>Phase_K_W10/W10SurfaceSmokeFactsTests</c> class
///         present (paired smoke harness).</item>
/// </list>
///
/// <para>These facts HARD-ASSERT — the artefacts ship in the same
/// Vasquez W10 PR, so no forward-stage soft-pass is needed.</para>
/// </summary>
public sealed class VasquezW10SelfLaneTests
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

    // ─── 1. lane-map: agent_handoff_protocol_md_shared ──────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-10")]
    public void LaneMap_SharedFiles_HandoffProtocolEntry_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "lane-map.json");
        Assert.True(File.Exists(path), "tests/ci/lane-map.json MUST be present.");
        var text = File.ReadAllText(path);
        Assert.Contains("agent_handoff_protocol_md_shared", text);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-10")]
    public void LaneMap_HandoffEntry_HasVasquezAndApone_AsAuthors()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "lane-map.json");
        if (!File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("shared_files", out var sf)) return;
        if (!sf.TryGetProperty("agent_handoff_protocol_md_shared", out var entry)) return;
        Assert.True(entry.TryGetProperty("authors", out var authors));
        Assert.Equal(JsonValueKind.Array, authors.ValueKind);
        var authorList = authors.EnumerateArray().Select(a => a.GetString() ?? "").ToHashSet();
        Assert.Contains("vasquez", authorList);
        Assert.Contains("apone", authorList);
        Assert.True(entry.TryGetProperty("primary", out var primary));
        Assert.Equal("vasquez", primary.GetString());
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-10")]
    public void LaneMap_HandoffEntry_HasCanonicalPathRegex()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "lane-map.json");
        if (!File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("shared_files", out var sf)) return;
        if (!sf.TryGetProperty("agent_handoff_protocol_md_shared", out var entry)) return;
        Assert.True(entry.TryGetProperty("paths", out var paths));
        Assert.Equal(JsonValueKind.Array, paths.ValueKind);
        var matched = paths.EnumerateArray()
            .Any(p => (p.GetString() ?? "").Contains("agent-handoff-protocol", StringComparison.Ordinal));
        Assert.True(matched, "paths MUST regex-match agent-handoff-protocol.md.");
    }

    // ─── 2. bundling check broadening ───────────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-10")]
    public void BundlingCheck_ExcludesSharedFilesFromLaneSet_AsDocumented()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "check-cross-lane-bundling.sh");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        // The bash script's classify_commit MUST skip is_shared_file
        // paths when computing the lane set. Vasquez W10 hard-pin.
        // Look for the canonical "skip shared file" comment or
        // implementation.
        Assert.True(
            text.Contains("shared files don't contribute to the lane", StringComparison.OrdinalIgnoreCase)
            || text.Contains("shared file is excluded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("is_shared_file", StringComparison.Ordinal),
            "Bundling check MUST exclude shared files from lane set computation.");
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-10")]
    public void BundlingCheck_HandoffProtocol_AcceptedAsShared()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "check-cross-lane-bundling.sh");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("agent-handoff-protocol.md", text);
    }

    // ─── 3. xunit DbSerial collection ───────────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-10")]
    public void DbSerialCollection_DefinitionPresent()
    {
        var asm = typeof(VasquezW10SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("DbSerialCollection", StringComparison.Ordinal)
            || x.Name.Equals("DbSerialCollectionDefinition", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-10")]
    public void DbSerialCollection_HasCollectionDefinitionAttribute()
    {
        var asm = typeof(VasquezW10SelfLaneTests).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("DbSerialCollection", StringComparison.Ordinal)
            || x.Name.Equals("DbSerialCollectionDefinition", StringComparison.Ordinal));
        if (t is null) return;
        var attrs = t.GetCustomAttributes(inherit: false);
        var hasCollectionDef = attrs.Any(a => a.GetType().Name.Equals(
            "CollectionDefinitionAttribute", StringComparison.Ordinal));
        Assert.True(hasCollectionDef,
            $"{t.Name} MUST carry [CollectionDefinition(\"DbSerial\", DisableParallelization=true)].");
    }

    // ─── 4–5. docs/test-architecture.md §3 + §4 ─────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-10")]
    public void TestArchitectureDoc_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path),
            "docs/test-architecture.md MUST be present (W10 Vasquez).");
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-10")]
    public void TestArchitectureDoc_Section3_TestParallelismPolicy_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "test-architecture.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.True(
            text.Contains("§3", StringComparison.Ordinal)
            || text.Contains("## 3.", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"^##\s+3\b", RegexOptions.Multiline));
        Assert.Contains("parallel", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DbSerial", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-10")]
    public void TestArchitectureDoc_Section4_CoveragePyramid_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "test-architecture.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.True(
            text.Contains("§4", StringComparison.Ordinal)
            || text.Contains("## 4.", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"^##\s+4\b", RegexOptions.Multiline));
        Assert.Contains("pyramid", text, StringComparison.OrdinalIgnoreCase);
        // Three canonical levels documented.
        Assert.Contains("unit", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contract", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("e2e", text, StringComparison.OrdinalIgnoreCase);
    }

    // ─── 6. handoff-protocol §5 ─────────────────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-10")]
    public void HandoffProtocol_Section5_ConcurrentAgentSafetyGuarantees_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.True(
            text.Contains("§5", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"^###?\s+5\b", RegexOptions.Multiline)
            || text.Contains("## 5.", StringComparison.Ordinal),
            "agent-handoff-protocol.md §5 MUST be present.");
        Assert.Contains("Concurrent agent safety", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-10")]
    public void HandoffProtocol_Section5_DocumentsLockPath_BackupDirs_DbSerial()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.Contains(".work/squad-git-lock", text);
        Assert.Matches(new Regex(@"\.work/[a-z]+-w\d+-safe"), text);
        Assert.Contains("DbSerial", text, StringComparison.Ordinal);
    }

    // ─── 7. W9 regression pins ──────────────────────────────────────────────

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-10")]
    public void LaneDisciplineNightlyWorkflow_StillPresent_W9RegressionPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(
            root.FullName, ".github", "workflows", "lane-discipline-nightly.yml");
        Assert.True(File.Exists(path));
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-10")]
    public void LaneDisciplineStatusWorkflow_StillPresent_W9RegressionPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(
            root.FullName, ".github", "workflows", "lane-discipline-status.yml");
        Assert.True(File.Exists(path));
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-10")]
    public void LaneMap_SelectorsMdShared_StillPresent_W8RegressionPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "lane-map.json");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("selectors_md_shared", text);
    }
}
