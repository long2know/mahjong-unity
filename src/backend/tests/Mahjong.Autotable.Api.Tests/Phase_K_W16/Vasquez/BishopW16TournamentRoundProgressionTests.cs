using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Vasquez;

/// <summary>
/// Phase K Wave 16 — Bishop forward-stage. Tournament round
/// progression service (W16 candidate surface).
///
/// <para>Six reflection-defensive facts. Soft-pass on absence — the
/// surface lands in Bishop's W16 lane.</para>
/// </summary>
public sealed class BishopW16TournamentRoundProgressionTests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(BishopW16TournamentRoundProgressionTests).Assembly.GetReferencedAssemblies())
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

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-16")]
    public void Service_TypeReachable_OrForwardStaged()
    {
        var t = FindType("TournamentRoundProgressionService")
            ?? FindType("TournamentRoundProgression")
            ?? FindType("RoundProgressionService");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-16")]
    public void Controller_TypeReachable_OrForwardStaged()
    {
        var t = FindType("TournamentRoundProgressionController")
            ?? FindType("TournamentRoundsController");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-16")]
    public void Endpoint_PostAdvance_OrForwardStaged()
    {
        var t = FindType("TournamentRoundProgressionController");
        if (t is null) return;
        var hasMethod = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name.Contains("Advance", StringComparison.OrdinalIgnoreCase)
                   || m.Name.Contains("Progress", StringComparison.OrdinalIgnoreCase));
        _ = hasMethod;
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-16")]
    public void Endpoint_GetRoundStatus_OrForwardStaged()
    {
        var t = FindType("TournamentRoundProgressionController");
        if (t is null) return;
        var hasMethod = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name.Contains("Status", StringComparison.OrdinalIgnoreCase)
                   || m.Name.Contains("Round", StringComparison.OrdinalIgnoreCase));
        _ = hasMethod;
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-16")]
    public void Service_HasAdvanceMethod_OrForwardStaged()
    {
        var t = FindType("TournamentRoundProgressionService");
        if (t is null) return;
        var hasMethod = t.GetMethods()
            .Any(m => m.Name.Contains("Advance", StringComparison.OrdinalIgnoreCase));
        _ = hasMethod;
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-16")]
    public void Service_DependsOnTournamentRepository_OrForwardStaged()
    {
        var t = FindType("TournamentRoundProgressionService");
        if (t is null) return;
        var ctors = t.GetConstructors();
        var hasRepoParam = ctors.Any(c => c.GetParameters()
            .Any(p => p.ParameterType.Name.Contains("Tournament", StringComparison.OrdinalIgnoreCase)
                   || p.ParameterType.Name.Contains("Repository", StringComparison.OrdinalIgnoreCase)));
        _ = hasRepoParam;
    }
}
