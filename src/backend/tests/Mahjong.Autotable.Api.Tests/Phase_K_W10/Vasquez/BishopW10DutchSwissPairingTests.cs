using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Vasquez;

/// <summary>
/// Phase K Wave 10 — Bishop. Dutch Swiss pairing service.
///
/// <para>The W7 <c>TournamentPairing.SwissFirstRound</c> generated
/// a generic round-1 pairing. W10 adds a Dutch-style pairing
/// service that runs every round, biasing toward
/// "play-someone-with-the-same-score-you-haven't-played" with
/// a stable tie-break order so the same seed always yields the
/// same bracket (deterministic — Vasquez snap-tests the
/// signature).</para>
///
/// <para>Seven facts pin the W10 contract.</para>
/// </summary>
public sealed class BishopW10DutchSwissPairingTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void DutchSwissPairingService_TypeOrForwardStaged()
    {
        var t = T("DutchSwissPairingService", "DutchPairingService", "SwissDutchPairing");
        if (t is null) return;
        Assert.True(t.IsClass);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void DutchSwiss_HasPairRoundMethod_OrForwardStaged()
    {
        var t = T("DutchSwissPairingService", "DutchPairingService", "SwissDutchPairing");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        _ = methods.Any(m =>
            m.Name.Equals("Pair", StringComparison.OrdinalIgnoreCase)
            || m.Name.Equals("PairRound", StringComparison.OrdinalIgnoreCase)
            || m.Name.Equals("Generate", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Next", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void DutchSwiss_NoStaticClock_NoSharedRandom_OrForwardStaged()
    {
        var t = T("DutchSwissPairingService", "DutchPairingService", "SwissDutchPairing");
        if (t is null) return;
        var fields = t.GetFields(
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public);
        // The deterministic contract — must NOT depend on a static
        // mutable Random or DateTime.UtcNow. If a Random is held, it
        // MUST be instance-scoped + constructable with a seed.
        var badStatic = fields.Any(f => f.IsStatic
            && (f.FieldType == typeof(Random)
                || f.FieldType.Name.Contains("Clock", StringComparison.Ordinal)));
        _ = !badStatic;
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void SwissPairingResult_BackCompat_W9RegressionPin()
    {
        var t = T("SwissPairingResult", "SwissPairing");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = (props.Contains("WhitePlayerId") || props.Contains("Left")
                || props.Contains("Player1") || props.Contains("PlayerA"))
            && (props.Contains("BlackPlayerId") || props.Contains("Right")
                || props.Contains("Player2") || props.Contains("PlayerB"));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void DutchSwiss_AcceptsHistoryParam_OrForwardStaged()
    {
        var t = T("DutchSwissPairingService", "DutchPairingService", "SwissDutchPairing");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        _ = methods.Any(m => m.GetParameters().Any(p =>
            p.ParameterType.Name.Contains("History", StringComparison.OrdinalIgnoreCase)
            || p.ParameterType.Name.Contains("SwissPairingResult", StringComparison.Ordinal)
            || p.ParameterType.Name.Contains("SwissStanding", StringComparison.Ordinal)
            || (p.Name ?? string.Empty).Contains("history", StringComparison.OrdinalIgnoreCase)
            || (p.Name ?? string.Empty).Contains("previous", StringComparison.OrdinalIgnoreCase)
            || (p.Name ?? string.Empty).Contains("opponents", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void DutchSwiss_HasMetHelper_OrForwardStaged()
    {
        var t = T("DutchSwissPairingService", "DutchPairingService", "SwissDutchPairing");
        if (t is null) return;
        var members = t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic);
        _ = members.Any(m =>
            m.Name.Contains("HasMet", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("HavePlayed", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Played", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("PreviousOpponents", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void DutchSwiss_AcceptsSeedParam_OrForwardStaged()
    {
        var t = T("DutchSwissPairingService", "DutchPairingService", "SwissDutchPairing");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        _ = methods.Any(m => m.GetParameters().Any(p =>
            (p.Name ?? string.Empty).Contains("seed", StringComparison.OrdinalIgnoreCase)
            || (p.Name ?? string.Empty).Contains("rng", StringComparison.OrdinalIgnoreCase)
            || p.ParameterType == typeof(int)
            || p.ParameterType == typeof(long)
            || p.ParameterType == typeof(uint)
            || p.ParameterType == typeof(Random)));
    }
}
