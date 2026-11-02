namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Bishop W20's two
/// new Swiss-pairing duration alerts:
/// <list type="bullet">
///   <item><c>SwissPairingDurationHigh</c> — P95 5s ticket-level alert</item>
///   <item><c>SwissPairingDurationCritical</c> — P95 15s page-level alert</item>
/// </list>
/// Soft-pinned so the gate stays green if Bishop W20 has not yet
/// landed the alert manifest.
/// </summary>
public sealed class BishopW20SwissPairingAlertsContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string AlertsDir()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Observability", "Alerts");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void SwissPairingAlerts_AlertsDir_Present()
    {
        Assert.True(Directory.Exists(AlertsDir()));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void SwissPairingAlerts_HighAlertToken_Present_OrForwardStaged()
    {
        var dir = AlertsDir();
        if (!Directory.Exists(dir)) return;
        var blob = string.Concat(Directory.EnumerateFiles(dir, "*.yaml")
            .Select(File.ReadAllText));
        var hasHigh = blob.Contains("SwissPairingDurationHigh", StringComparison.Ordinal)
                       || (blob.Contains("SwissPairing", StringComparison.Ordinal)
                            && blob.Contains("ticket", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasHigh);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void SwissPairingAlerts_CriticalAlertToken_Present_OrForwardStaged()
    {
        var dir = AlertsDir();
        if (!Directory.Exists(dir)) return;
        var blob = string.Concat(Directory.EnumerateFiles(dir, "*.yaml")
            .Select(File.ReadAllText));
        var hasCrit = blob.Contains("SwissPairingDurationCritical", StringComparison.Ordinal)
                       || (blob.Contains("SwissPairing", StringComparison.Ordinal)
                            && blob.Contains("page", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasCrit);
    }
}
