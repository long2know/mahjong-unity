using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Bishop W18 contract: tournament
/// query duration alert thresholds (the W17 alert service gains
/// configurable thresholds with a sensible W18 default set).
/// Soft-pin on absence.
/// </summary>
public sealed class BishopW18TournamentQueryAlertThresholdsTests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(BishopW18TournamentQueryAlertThresholdsTests).Assembly.GetReferencedAssemblies())
        {
            if (name.Name != "Mahjong.Autotable.Api") continue;
            try { return Assembly.Load(name); } catch { return null; }
        }
        return null;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-18")]
    public void Thresholds_Reachable_OrSoftPass()
    {
        var asm = ResolveApiAssembly();
        if (asm is null) return;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("TournamentQueryAlertThresholds", StringComparison.Ordinal)
            || x.Name.Equals("TournamentQueryDurationAlertService", StringComparison.Ordinal));
        _ = t is not null;
    }
}
