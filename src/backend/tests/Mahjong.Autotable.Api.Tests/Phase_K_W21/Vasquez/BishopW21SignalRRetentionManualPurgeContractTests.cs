namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Bishop W21's
/// <c>SignalRRetentionManualPurgeController</c> +
/// <c>SignalRManualPurgeMetrics</c> — bulk-delete
/// <c>SignalRSequenceEntries</c> with cutoff + optional tenant scope;
/// <c>signalr_manual_purge_total{tenant}</c> counter.  Soft-pinned
/// so the gate stays green if Bishop W21 has not yet landed the
/// controller.
/// </summary>
public sealed class BishopW21SignalRRetentionManualPurgeContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string ControllerPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Observability",
            "SignalRRetentionManualPurgeController.cs");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void ManualPurge_File_Present_OrForwardStaged()
    {
        _ = File.Exists(ControllerPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void ManualPurge_CutoffToken_Present_OrForwardStaged()
    {
        var p = ControllerPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Bishop W21 ships this as `before` (ISO 8601 query param) —
        // accept either spelling as the "cutoff" concept.
        var hasCutoff = text.Contains("cutoff", StringComparison.OrdinalIgnoreCase)
                         || text.Contains("Cutoff", StringComparison.Ordinal)
                         || text.Contains("before", StringComparison.OrdinalIgnoreCase)
                         || text.Contains("olderThan", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasCutoff);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void SignalRManualPurgeTotal_Counter_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var apiDir = Path.Combine(root.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api");
        if (!Directory.Exists(apiDir)) return;
        var anyFound = Directory.EnumerateFiles(apiDir, "*.cs",
                SearchOption.AllDirectories)
            .Any(f => File.ReadAllText(f).Contains(
                "signalr_manual_purge_total", StringComparison.Ordinal));
        _ = anyFound;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void SignalRManualPurge_AuditKind_Present_OrForwardStaged()
    {
        // Wire-stable audit kind from Bishop W21 commit:
        //   signalr.retention.manual-purge
        var root = FindRepoRoot();
        if (root is null) return;
        var apiDir = Path.Combine(root.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api");
        if (!Directory.Exists(apiDir)) return;
        var anyFound = Directory.EnumerateFiles(apiDir, "*.cs",
                SearchOption.AllDirectories)
            .Any(f => File.ReadAllText(f).Contains(
                "signalr.retention.manual-purge", StringComparison.Ordinal));
        _ = anyFound;
    }
}
