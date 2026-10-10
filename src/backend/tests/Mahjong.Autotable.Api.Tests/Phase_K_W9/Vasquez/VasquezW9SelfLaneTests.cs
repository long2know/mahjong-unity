using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W9.Vasquez;

/// <summary>
/// Phase K Wave 9 — Vasquez. Self-lane process gates.
///
/// <para>The Vasquez W9 lane includes:</para>
/// <list type="number">
///   <item>Lane-map shared-files entry continues to declare
///         <c>selectors_md_shared</c> (W8 carry-over).</item>
///   <item>Nightly cron workflow
///         (<c>.github/workflows/lane-discipline-nightly.yml</c>)
///         runs <c>--repo-mode</c> on a daily cadence.</item>
///   <item>Opt-in status workflow
///         (<c>.github/workflows/lane-discipline-status.yml</c>)
///         publishes an <c>OPTIONAL-FOR-NOW</c> check name so
///         Stephen can preview before flipping to required.</item>
///   <item>Handoff protocol <c>docs/agent-handoff-protocol.md</c>
///         documents §3.6, §3.7 and §4 branch-protection
///         setup runbook.</item>
///   <item><c>tests/ci/check-cross-lane-bundling.sh</c> retains
///         the <c>--repo-mode</c> flag.</item>
/// </list>
///
/// <para>These facts HARD-ASSERT — the artefacts ship in the same
/// Vasquez W9 PR, so no forward-stage soft-pass is needed.</para>
/// </summary>
public sealed class VasquezW9SelfLaneTests
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

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-9")]
    public void LaneMapSharedFiles_Selectors_MdEntry_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "lane-map.json");
        Assert.True(File.Exists(path), "tests/ci/lane-map.json MUST be present.");
        var text = File.ReadAllText(path);
        Assert.Contains("\"shared_files\"", text);
        Assert.Contains("selectors_md_shared", text);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-9")]
    public void LaneMap_Is_ValidJson()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "lane-map.json");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // Should parse without throw.
        using var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object);
        Assert.True(doc.RootElement.TryGetProperty("lanes", out _));
        Assert.True(doc.RootElement.TryGetProperty("authors", out _));
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-9")]
    public void NightlyCronWorkflow_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(
            root.FullName, ".github", "workflows", "lane-discipline-nightly.yml");
        Assert.True(File.Exists(path),
            "lane-discipline-nightly.yml MUST be present (W9 Vasquez).");
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-9")]
    public void NightlyCronWorkflow_HasSchedule_AndRepoMode()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(
            root.FullName, ".github", "workflows", "lane-discipline-nightly.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // Phase L — Frost (Backend). The nightly schedule was intentionally
        // commented out during the CI-noise iter2 suppression (commit
        // 0b591d4); workflow_dispatch is retained and the `schedule:` /
        // `cron:` block is kept commented for one-line re-enablement. Accept
        // the active OR the disabled (commented) schedule form so this
        // contract tolerates the noise-suppression state instead of forcing
        // the nightly cron back on (which is exactly the CI noise we removed).
        Assert.Matches(new Regex(@"^\s*#?\s*schedule:", RegexOptions.Multiline), text);
        Assert.Contains("--repo-mode", text);
        Assert.Contains("cron:", text);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-9")]
    public void OptInStatusWorkflow_Present_AndOptionalCheckName()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(
            root.FullName, ".github", "workflows", "lane-discipline-status.yml");
        Assert.True(File.Exists(path),
            "lane-discipline-status.yml MUST be present (W9 Vasquez).");
        var text = File.ReadAllText(path);
        Assert.Contains("OPTIONAL-FOR-NOW", text);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-9")]
    public void HandoffProtocol_Section_3_6_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.True(
            text.Contains("§3.6", StringComparison.Ordinal)
                || text.Contains("3.6 ", StringComparison.Ordinal)
                || Regex.IsMatch(text, @"^###\s*3\.6\.?\s", RegexOptions.Multiline),
            "Handoff protocol MUST document §3.6 (W9 lock-file `.work/` discipline).");
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-9")]
    public void HandoffProtocol_Section_3_7_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.True(
            text.Contains("§3.7", StringComparison.Ordinal)
                || text.Contains("3.7 ", StringComparison.Ordinal)
                || Regex.IsMatch(text, @"^###\s*3\.7\.?\s", RegexOptions.Multiline),
            "Handoff protocol MUST document §3.7 (W9 git-fetch-inside-flock).");
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-9")]
    public void HandoffProtocol_BranchProtectionRunbook_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // §4 = branch-protection setup runbook with exact gh api commands.
        Assert.Contains("Branch-protection setup", text);
        Assert.Contains("gh api", text);
        Assert.Contains("repos/long2know/mahjong-autotable/branches/main/protection",
            text);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-9")]
    public void CheckScript_RepoMode_StillSupported()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "check-cross-lane-bundling.sh");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("--repo-mode", text);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-9")]
    public void HandoffProtocol_Includes_GhApi_RollbackInstructions()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // Rollback must be explicit so Stephen can revert the
        // branch-protection flip if a regression appears.
        Assert.True(
            text.Contains("Rollback", StringComparison.OrdinalIgnoreCase)
                || text.Contains("rollback procedure", StringComparison.OrdinalIgnoreCase),
            "Branch-protection runbook MUST include rollback instructions.");
    }
}
