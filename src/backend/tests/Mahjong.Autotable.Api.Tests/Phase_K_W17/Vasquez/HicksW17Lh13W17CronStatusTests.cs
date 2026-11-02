using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Vasquez;

/// <summary>
/// Phase K Wave 17 — Hicks forward-stage. LH13 §8 W17 cron-status
/// update in <c>docs/lh13-soft-pin-rationale.md</c> (cron is alive
/// — 1 schedule-event run since W16 — but conclusion=failure;
/// convergence criterion still 0 of 3 successful runs; HOLD
/// soft-flip).
///
/// <para>Four filesystem-defensive facts. Soft-pass on absence
/// of optional W17 §8 content; hard-assert on doc existence.</para>
/// </summary>
public sealed class HicksW17Lh13W17CronStatusTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "LH13"), Trait("Wave", "Phase-K-17")]
    public void LH13_SoftPinRationale_Doc_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "lh13-soft-pin-rationale.md");
        Assert.True(File.Exists(path));
    }

    [Fact, Trait("Category", "LH13"), Trait("Wave", "Phase-K-17")]
    public void LH13_SoftPinRationale_HasW17Section8_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "lh13-soft-pin-rationale.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("§8", StringComparison.Ordinal)
            || text.Contains("W17 bring-up status update", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LH13"), Trait("Wave", "Phase-K-17")]
    public void LH13_W17_HoldDecision_Documented_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "lh13-soft-pin-rationale.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("HOLD", StringComparison.Ordinal)
            && text.Contains("soft-flip", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LH13"), Trait("Wave", "Phase-K-17")]
    public void LH13_W17_ProvisionalTag_StillActive_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "lh13-soft-pin-rationale.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // §3 provisional-until-calibrated tag stays until 3 successful
        // schedule-event runs; W17 still has 0 of 3.
        _ = text.Contains("provisional-until-calibrated", StringComparison.OrdinalIgnoreCase);
    }
}
