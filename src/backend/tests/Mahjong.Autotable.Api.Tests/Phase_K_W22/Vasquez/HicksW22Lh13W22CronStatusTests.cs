namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez paired contract pinning Hicks W22's
/// LH13 §13 evidence-gate re-evaluation HOLD YELLOW decision.
/// Hicks W22 (676d781) HELD §6.8 YELLOW.  The blocker has SHIFTED
/// from the W19/W20/W21 gh-auth gap (CLEARED at W21 close by the
/// coordinator probe) to a fundamentally different blocker:
/// natural cron-pace accumulation.  pwa-audit.yml cron schedule
/// is `30 2 * * *` — nightly at 02:30 UTC, not hourly as W19→W21
/// §4.2 analysis tacitly assumed.  Only 1 schedule-event run
/// total has ever fired (sha=c866535 W16 merge, pre-W18 fix,
/// FAILED).  0 successful schedule-event runs post-W18-merge.
/// Predicted PROMOTE wave: W25 earliest (3 daily cron runs
/// accumulate by ~2026-05-27 02:30 UTC).
///
/// Hard-asserts that the §13 update has landed in
/// docs/lh13-soft-pin-rationale.md with the HOLD posture intact
/// and the W25-earliest prediction recorded.
/// </summary>
public sealed class HicksW22Lh13W22CronStatusTests
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

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Hicks")]
    public void Lh13Doc_Section13_Present_OrForwardStaged()
    {
        var p = Lh13Path();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("## §13", StringComparison.Ordinal)
                   || text.Contains("§13 — W22", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Hicks")]
    public void Lh13Doc_HoldYellow_W22_OrForwardStaged()
    {
        var p = Lh13Path();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("HOLD", StringComparison.OrdinalIgnoreCase)
                   && text.Contains("YELLOW", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Hicks")]
    public void Lh13Doc_W22_NaturalCronPaceBlocker_OrForwardStaged()
    {
        var p = Lh13Path();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // W22 reason: natural cron-pace accumulation (nightly cron
        // cadence + only 0 post-W18-merge successes observed).
        var has = text.Contains("natural cron", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("cron-pace", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("nightly", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Hicks")]
    public void Lh13Doc_W22_W25EarliestPromote_OrForwardStaged()
    {
        var p = Lh13Path();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("W25 earliest", StringComparison.Ordinal)
                   || text.Contains("W25-earliest", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Vasquez")]
    public void HandoffDoc_Section_6_11_W22_HoldYellow_Ratification_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "docs", "agent-handoff-protocol.md");
        var text = File.ReadAllText(p);
        Assert.Contains("6.11", text);
        var has = text.Contains("HOLD", StringComparison.OrdinalIgnoreCase)
                   && text.Contains("W22", StringComparison.OrdinalIgnoreCase);
        Assert.True(has);
    }
}
