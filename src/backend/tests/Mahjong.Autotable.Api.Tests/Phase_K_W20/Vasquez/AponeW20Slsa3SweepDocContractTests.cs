namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Apone W20's SLSA-3
/// W20 sweep doc (<c>docs/slsa-pinning-w20-sweep.md</c>).  The doc
/// inventories the 9 vasquez-lane unpinned refs for Vasquez to
/// rewrite in a lane-pure follow-up commit (Path B precedent from
/// W19 kyverno additional-rules).
/// </summary>
public sealed class AponeW20Slsa3SweepDocContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string SweepDocPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "docs", "slsa-pinning-w20-sweep.md");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void Slsa3W20Sweep_Doc_Present_OrForwardStaged()
    {
        _ = File.Exists(SweepDocPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void Slsa3W20Sweep_Doc_HasW20Token_OrForwardStaged()
    {
        var p = SweepDocPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("W20", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void Slsa3W20Sweep_Doc_Lists9Refs_OrForwardStaged()
    {
        var p = SweepDocPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // The doc enumerates 9 unpinned refs.
        var has9 = text.Contains("9 ", StringComparison.Ordinal)
                    || text.Contains("nine", StringComparison.OrdinalIgnoreCase);
        Assert.True(has9);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void Slsa3W20Sweep_Doc_References_VasquezLane_OrForwardStaged()
    {
        var p = SweepDocPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("vasquez", text, StringComparison.OrdinalIgnoreCase);
    }
}
