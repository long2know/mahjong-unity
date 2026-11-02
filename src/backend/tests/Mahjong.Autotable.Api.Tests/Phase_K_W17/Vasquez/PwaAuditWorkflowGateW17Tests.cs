namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Vasquez;

/// <summary>
/// Phase K Wave 17 — Vasquez. Hard-assert pin for the
/// <c>docs/frontend-pwa-audit.md §6.7</c> NEW W17 7-wave-deferred
/// PROMOTE recommendation (promote §6.6 Coordinator-direct cron
/// invocation from optional fallback to PRIMARY next-step).
///
/// <para>This pairs with the branch-protection §4.5 RECALIBRATION
/// — the W17 doc bundle ships both:
/// <list type="bullet">
///   <item>§4.5 RECALIBRATION downgrades a W16 PRIMARY framing
///         (branch-protection install) because reversibility is
///         hard (DELETE cannot restore null prior state).</item>
///   <item>§6.7 PROMOTES a W12-W17 fallback framing
///         (Coordinator-direct cron seed) because reversibility
///         is trivial (append-only history; only affects the
///         pwa-audit.yml workflow).</item>
/// </list>
/// The two together demonstrate consistent reversibility-first
/// disposition logic across both axes.</para>
/// </summary>
public sealed class PwaAuditWorkflowGateW17Tests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string DocPath()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        return Path.Combine(root!.FullName, "docs", "frontend-pwa-audit.md");
    }

    [Fact, Trait("Category", "LH13"), Trait("Wave", "Phase-K-17")]
    public void Section6_7_New_KeyTerms_Present()
    {
        var text = File.ReadAllText(DocPath());
        Assert.Contains("§6.7", text, StringComparison.Ordinal);
        // §6.7 must capture the W11 → W17 deferral arc (the cron
        // saga has now spanned seven consecutive waves).
        Assert.Contains("7 waves", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PROMOTE", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LH13"), Trait("Wave", "Phase-K-17")]
    public void Section6_7_Cites_W17CronStatus_AliveButFailed()
    {
        var text = File.ReadAllText(DocPath());
        // §6.7 captures the W17 status: cron alive (1 schedule-event
        // run between W16 and W17) but conclusion=failure;
        // convergence still 0 of 3 successful runs.
        Assert.Contains("0 of 3", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LH13"), Trait("Wave", "Phase-K-17")]
    public void Section6_7_CrossRefHicksW17Section8_Present()
    {
        var text = File.ReadAllText(DocPath());
        Assert.Contains("lh13-soft-pin-rationale.md", text,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LH13"), Trait("Wave", "Phase-K-17")]
    public void Section6_7_ReversibilityComparison_With_Section4_5_Present()
    {
        var text = File.ReadAllText(DocPath());
        // §6.7 spells out the reversibility comparison vs §4.5 so the
        // PROMOTE/DOWNGRADE pairing is auditable.
        Assert.Contains("§4.5", text, StringComparison.Ordinal);
        Assert.Contains("reversibility", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "LH13"), Trait("Wave", "Phase-K-17")]
    public void PwaAuditWorkflow_StillReachable()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, ".github", "workflows", "pwa-audit.yml");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        // Workflow must still expose a `schedule:` block (the cron is
        // alive but failing — §6.7 acknowledges this).
        Assert.Contains("schedule:", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LH13"), Trait("Wave", "Phase-K-17")]
    public void Section6_6_CoordinatorDirectRunbook_StillPresent()
    {
        var text = File.ReadAllText(DocPath());
        // §6.7's PROMOTE only makes sense if §6.6 is still on the page.
        Assert.Contains("§6.6", text, StringComparison.Ordinal);
        Assert.Contains("Coordinator-direct", text, StringComparison.OrdinalIgnoreCase);
    }
}
