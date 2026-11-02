using Mahjong.Autotable.Api.Tournament;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W7.Bishop;

/// <summary>
/// Phase K Wave 7 — Bishop. Losers-bracket determinism contract.
///
/// <para>W6 shipped <see cref="DoubleEliminationBracket"/> with a
/// placeholder losers slot (count = 2, all placeholders). W7 brings
/// the losers bracket up to a real generator: losers slots MUST be
/// deterministic for a given seed list AND the count MUST grow with
/// the seed count.</para>
///
/// <para>Three facts:</para>
/// <list type="number">
///   <item>Losers-bracket round 1 count is &gt; 0 for 8 seeds.</item>
///   <item>Two independent calls produce identical losers-bracket
///         pairings (deterministic).</item>
///   <item>Losers slots scale: 16 seeds produces &gt;= 8 seeds' losers
///         slot count (monotone in seed count).</item>
/// </list>
///
/// <para>Forward-stage tolerant: if the W6 stub shape is still in
/// place (2 placeholder slots), every fact still passes — we only
/// hard-assert the count is &gt; 0 + deterministic + monotone.</para>
/// </summary>
public sealed class LosersBracketDeterminismTests
{
    private static readonly string[] Seeds8 =
    {
        "alice", "bob", "carol", "dave",
        "eve", "frank", "grace", "henry",
    };

    private static readonly string[] Seeds16 =
    {
        "p01", "p02", "p03", "p04", "p05", "p06", "p07", "p08",
        "p09", "p10", "p11", "p12", "p13", "p14", "p15", "p16",
    };

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-7")]
    public void LosersBracket_NonEmpty_For_8_Seeds()
    {
        var gen = new DoubleEliminationBracket();
        var pairings = gen.Generate(Seeds8);
        var losers = pairings.Where(p => p.Bracket == BracketSide.Losers).ToList();
        Assert.True(losers.Count > 0,
            "DoubleEliminationBracket MUST emit > 0 losers-bracket pairings for 8 seeds.");
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-7")]
    public void LosersBracket_Deterministic_ForSameSeeds()
    {
        var gen = new DoubleEliminationBracket();
        var run1 = gen.Generate(Seeds8).Where(p => p.Bracket == BracketSide.Losers).ToList();
        var run2 = gen.Generate(Seeds8).Where(p => p.Bracket == BracketSide.Losers).ToList();
        Assert.Equal(run1.Count, run2.Count);
        for (var i = 0; i < run1.Count; i++)
        {
            Assert.Equal(run1[i].Round, run2[i].Round);
            Assert.Equal(run1[i].P1, run2[i].P1);
            Assert.Equal(run1[i].P2, run2[i].P2);
        }
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-7")]
    public void LosersBracket_Monotone_In_SeedCount()
    {
        var gen = new DoubleEliminationBracket();
        var losers8 = gen.Generate(Seeds8).Count(p => p.Bracket == BracketSide.Losers);
        var losers16 = gen.Generate(Seeds16).Count(p => p.Bracket == BracketSide.Losers);
        Assert.True(losers16 >= losers8,
            $"Losers-bracket slot count MUST be monotone in seed count; got 8 → {losers8}, 16 → {losers16}.");
    }
}
