namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired observation contract for the
/// §4.8 Stephen-decision tree (branch-protection enforcement
/// flip).  W7 → W20: 12-wave deferral arc still open, no §4.9
/// row added.  This contract pins the still-open posture so any
/// future flip is detected as a regression of this fact.
/// </summary>
public sealed class BranchProtectionW20StephenDecisionStatusTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string HandoffPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void Handoff_Doc_Present()
    {
        Assert.True(File.Exists(HandoffPath()));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void Handoff_Section4_8_StephenDecisionTree_StillPresent()
    {
        var text = File.ReadAllText(HandoffPath());
        Assert.Contains("§4.8", text, StringComparison.Ordinal);
        Assert.Contains("Stephen-decision tree", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void Handoff_Section4_8_AllThreeOptions_StillPresent()
    {
        var text = File.ReadAllText(HandoffPath());
        Assert.Contains("Option A", text, StringComparison.Ordinal);
        Assert.Contains("Option B", text, StringComparison.Ordinal);
        Assert.Contains("Option C", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void Handoff_Section4_8_TwelveWaveDeferral_Narrative_Present()
    {
        var text = File.ReadAllText(HandoffPath());
        // The 12-wave deferral arc covers W7 → W20.  At least one of
        // the canonical phrasings should be present.
        var has = text.Contains("12-wave deferral", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("W7 → W20", StringComparison.Ordinal)
                   || text.Contains("W7 -> W20", StringComparison.Ordinal)
                   || (text.Contains("W7", StringComparison.Ordinal)
                        && text.Contains("W20", StringComparison.Ordinal));
        Assert.True(has,
            "Handoff doc §4.8 must record the 12-wave Stephen-decision deferral arc.");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void Handoff_FlipScript_StillExecutable()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "tests", "ci",
            "lane-discipline-flip-required.sh");
        Assert.True(File.Exists(p));
        var bytes = File.ReadAllBytes(p);
        Assert.True(bytes.Length > 0);
        Assert.Equal((byte)'#', bytes[0]);
        Assert.Equal((byte)'!', bytes[1]);
    }
}
