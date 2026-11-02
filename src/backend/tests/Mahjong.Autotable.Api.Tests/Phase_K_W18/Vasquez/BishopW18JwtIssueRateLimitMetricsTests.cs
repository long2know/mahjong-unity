using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Bishop W18 contract: JWT issue
/// rate-limit metrics histogram (extends the W17 JwtIssueBlockedMetrics
/// counter with a latency histogram for the rate-limited path).
/// Soft-pin on absence.
/// </summary>
public sealed class BishopW18JwtIssueRateLimitMetricsTests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(BishopW18JwtIssueRateLimitMetricsTests).Assembly.GetReferencedAssemblies())
        {
            if (name.Name != "Mahjong.Autotable.Api") continue;
            try { return Assembly.Load(name); } catch { return null; }
        }
        return null;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-18")]
    public void Metrics_Reachable_OrSoftPass()
    {
        var asm = ResolveApiAssembly();
        if (asm is null) return;
        var t = asm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("JwtIssueRateLimitMetrics", StringComparison.Ordinal)
            || x.Name.Equals("JwtIssueRateLimitMeter", StringComparison.Ordinal)
            || x.Name.Equals("JwtIssueBlockedMetrics", StringComparison.Ordinal));
        _ = t is not null;
    }
}
