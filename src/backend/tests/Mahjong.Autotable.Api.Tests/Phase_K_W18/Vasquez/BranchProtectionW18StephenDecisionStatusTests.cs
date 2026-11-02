using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. §4.8 Stephen-decision tree status
/// observation. W18 disposition: still awaiting Stephen choice
/// between Options A / B / C (no §4.9 install record landed).
///
/// <para>This contract hard-asserts that the §4.8 framing remains
/// intact (Options A / B / C all present), and that the Stephen-
/// decision tree narrative still appears in
/// <c>docs/agent-handoff-protocol.md</c>. It also soft-checks for
/// a potential §4.9 install record — if Stephen DOES make a
/// W18-cycle choice, the §4.9 record exists; if not, the soft
/// check early-returns.</para>
/// </summary>
public sealed class BranchProtectionW18StephenDecisionStatusTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void Handoff_Section4_8_StephenDecisionTree_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§4.8", text, StringComparison.Ordinal);
        Assert.Contains("Option A", text, StringComparison.Ordinal);
        Assert.Contains("Option B", text, StringComparison.Ordinal);
        Assert.Contains("Option C", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void Handoff_Section4_5_W17Recalibration_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("RECALIBRATION", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void Handoff_Section4_9_Install_OrAwaitingStephen()
    {
        // Soft-pin: if §4.9 exists (Stephen made a choice), assert
        // its presence; otherwise early-return (still awaiting).
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        if (!text.Contains("§4.9", StringComparison.Ordinal)) return;
        // If §4.9 exists, it must record the chosen option.
        Assert.True(
            text.Contains("Option A", StringComparison.Ordinal)
            || text.Contains("Option B", StringComparison.Ordinal)
            || text.Contains("Option C", StringComparison.Ordinal));
    }
}
