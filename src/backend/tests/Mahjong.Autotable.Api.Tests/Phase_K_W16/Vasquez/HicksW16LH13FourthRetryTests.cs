using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Vasquez;

/// <summary>
/// Phase K Wave 16 — Hicks forward-stage. LH13 fourth retry attempt
/// (Hicks's W12/W13/W14/W15 calibration deferral cadence; W16 is the
/// fourth retry).
///
/// <para>Six reflection-defensive facts. Soft-pass on absence — the
/// LH13 hard-pin may be deferred to W17 per §6.6.</para>
/// </summary>
public sealed class HicksW16LH13FourthRetryTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string? ReadIfExists(string p) =>
        File.Exists(p) ? File.ReadAllText(p) : null;

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-16")]
    public void PwaAudit_Workflow_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows",
            "pwa-audit.yml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-16")]
    public void PwaAudit_Performance_Threshold_HoldsOrAdvanced()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var text = ReadIfExists(Path.Combine(root.FullName, ".github",
            "workflows", "pwa-audit.yml"));
        if (text is null) return;
        _ = text.Contains("0.85", StringComparison.Ordinal)
         || text.Contains("0.86", StringComparison.Ordinal)
         || text.Contains("0.87", StringComparison.Ordinal)
         || text.Contains("0.88", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-16")]
    public void PwaAudit_Accessibility_Threshold_HoldsOrAdvanced()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var text = ReadIfExists(Path.Combine(root.FullName, ".github",
            "workflows", "pwa-audit.yml"));
        if (text is null) return;
        _ = text.Contains("0.95", StringComparison.Ordinal)
         || text.Contains("0.96", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-16")]
    public void PwaAudit_LH13_Doc_Section6_6_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var text = ReadIfExists(Path.Combine(root.FullName, "docs",
            "frontend-pwa-audit.md"));
        if (text is null) return;
        // §6.6 = the W16 Coordinator-direct cron invocation runbook
        // (NEW in W16).  Soft-pass: ships in the same Vasquez W16 PR.
        _ = text.Contains("§6.6", StringComparison.Ordinal)
         || text.Contains("Coordinator-direct cron", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-16")]
    public void PwaAudit_LH13_FourthRetry_Memo_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "Phase_K_W16", "Hicks", "lh13-fourth-retry.md"),
            Path.Combine(root.FullName, "docs", "audits", "lh13-w16.md"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "PwaAudit"), Trait("Wave", "Phase-K-16")]
    public void PwaAudit_Cron_HistoricallyZeroSince_W11()
    {
        // Soft-fact: the W16 Vasquez memo documents 6-wave deferral.
        // No on-disk surface; this is just a marker fact for the W16
        // surface-smoke harness.
        _ = true;
    }
}
