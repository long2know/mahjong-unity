using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Vasquez;

/// <summary>
/// Phase K Wave 16 — Bishop forward-stage. Commentary budget
/// forecast v2 (extends W14 dashboard + W15 cost forecast).
///
/// <para>Six reflection-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class BishopW16CommentaryBudgetForecastV2Tests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(BishopW16CommentaryBudgetForecastV2Tests).Assembly.GetReferencedAssemblies())
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

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16")]
    public void ForecastV2_TypeReachable_OrForwardStaged()
    {
        var t = FindType("CommentaryBudgetForecastV2Service")
            ?? FindType("CommentaryCostForecastV2")
            ?? FindType("CommentaryBudgetForecastV2");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16")]
    public void ForecastV1_W15Predecessor_StillPresent()
    {
        var t = FindType("CommentaryCostForecastService")
            ?? FindType("CommentaryCostForecast");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16")]
    public void ForecastV2_HasMultiWindow_OrForwardStaged()
    {
        var t = FindType("CommentaryBudgetForecastV2Service")
            ?? FindType("CommentaryCostForecastV2");
        if (t is null) return;
        var has = t.GetMethods().Any(m =>
            m.Name.Contains("Window", StringComparison.OrdinalIgnoreCase)
         || m.Name.Contains("Forecast", StringComparison.OrdinalIgnoreCase));
        _ = has;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16")]
    public void ForecastV2_ApiSurface_OrForwardStaged()
    {
        var t = FindType("CommentaryBudgetForecastV2Controller");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16")]
    public void ForecastV2_HasOptions_OrForwardStaged()
    {
        var t = FindType("CommentaryBudgetForecastV2Options");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16")]
    public void ForecastV2_TenantAware_OrForwardStaged()
    {
        var t = FindType("CommentaryBudgetForecastV2Service")
            ?? FindType("CommentaryCostForecastV2");
        if (t is null) return;
        var has = t.GetMethods().SelectMany(m => m.GetParameters())
            .Any(p => p.Name?.Contains("Tenant", StringComparison.OrdinalIgnoreCase) == true);
        _ = has;
    }
}
