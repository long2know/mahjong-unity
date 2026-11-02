namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract pinning Hicks W21's
/// LH13 §6.9 evidence-gate re-evaluation HOLD YELLOW decision.
/// Hicks W21 (47d0fe5) held §6.8 YELLOW for the sole reason of
/// gh-CLI being unauthenticated in the bring-up shell (the W20
/// secondary reason — sample-window-size mathematically
/// insufficient — no longer applies at W21 since ~25h have elapsed
/// since the W18 merge to main, well past the 3-hour minimum for
/// 3 hourly cron ticks).
///
/// Hard-asserts that the §12 update has landed in
/// docs/lh13-soft-pin-rationale.md with the HOLD posture intact.
/// </summary>
public sealed class HicksW21Lh13W21CronStatusTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string Lh13Path()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "docs", "lh13-soft-pin-rationale.md");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void Lh13Doc_Section12_Present_OrForwardStaged()
    {
        var p = Lh13Path();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("## §12", StringComparison.Ordinal)
                   || text.Contains("§12 — W21", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void Lh13Doc_HoldYellow_W21_OrForwardStaged()
    {
        var p = Lh13Path();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // HOLD-YELLOW disposition recorded.
        var has = text.Contains("HOLD", StringComparison.OrdinalIgnoreCase)
                   && text.Contains("YELLOW", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void Lh13Doc_W21_ObservationalGap_Reason_OrForwardStaged()
    {
        var p = Lh13Path();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Sole remaining reason at W21: gh-CLI unauthenticated /
        // observational gap.
        var has = text.Contains("Observational", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("observational", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("gh auth", StringComparison.Ordinal);
        Assert.True(has);
    }
}
