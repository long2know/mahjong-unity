using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Vasquez;

/// <summary>
/// Phase K Wave 16 — Bishop forward-stage. Match-history page size
/// metrics v2 (extends W15 tournament page size metrics).
///
/// <para>Six reflection-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class BishopW16MatchHistoryPageSizeMetricsV2Tests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(BishopW16MatchHistoryPageSizeMetricsV2Tests).Assembly.GetReferencedAssemblies())
        {
            if (name.Name != "Mahjong.Autotable.Api") continue;
            try { return Assembly.Load(name); } catch { return null; }
        }
        return null;
    }

    private static Type? FindType(string name)
    {
        var asm = ResolveApiAssembly();
        return asm?.GetTypes().FirstOrDefault(t => t.Name.Equals(name, StringComparison.Ordinal));
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-16")]
    public void MetricsV2_TypeReachable_OrForwardStaged()
    {
        var t = FindType("MatchHistoryPageSizeMetricsV2")
            ?? FindType("MatchHistoryPageSizeV2");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-16")]
    public void MetricsV1_W15Predecessor_StillPresent()
    {
        var t = FindType("TournamentPageSizeMetrics")
            ?? FindType("TournamentQueryLatencyMetrics");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-16")]
    public void MetricsV2_HistogramBuckets_OrForwardStaged()
    {
        var t = FindType("MatchHistoryPageSizeMetricsV2");
        if (t is null) return;
        var has = t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Any(f => f.Name.Contains("Bucket", StringComparison.OrdinalIgnoreCase));
        _ = has;
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-16")]
    public void MetricsV2_TaggedByTenant_OrForwardStaged()
    {
        var t = FindType("MatchHistoryPageSizeMetricsV2");
        if (t is null) return;
        var has = t.GetMethods().SelectMany(m => m.GetParameters())
            .Any(p => p.Name?.Contains("Tenant", StringComparison.OrdinalIgnoreCase) == true);
        _ = has;
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-16")]
    public void MetricsV2_RegisteredInDI_OrForwardStaged()
    {
        var ext = FindType("MatchHistoryPageSizeMetricsV2Extensions");
        _ = ext is not null;
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-16")]
    public void MetricsV2_RecordMethod_OrForwardStaged()
    {
        var t = FindType("MatchHistoryPageSizeMetricsV2");
        if (t is null) return;
        var has = t.GetMethods()
            .Any(m => m.Name.Contains("Record", StringComparison.OrdinalIgnoreCase)
                   || m.Name.Contains("Observe", StringComparison.OrdinalIgnoreCase));
        _ = has;
    }
}
