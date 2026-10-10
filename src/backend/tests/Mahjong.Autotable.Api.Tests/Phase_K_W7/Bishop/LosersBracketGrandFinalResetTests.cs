using Mahjong.Autotable.Api.Tournament;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W7.Bishop;

/// <summary>
/// Phase K Wave 7 — Bishop. Golden-set behaviour pin for the grand-
/// final "reset" emission added in Wave 7.
///
/// <para>The W6 generator emitted a single grand-final slot per the
/// pre-Wave-7 stub. The W7 generator emits BOTH the canonical grand
/// final (round 1) AND the conditional "reset" round (round 2). The
/// reset is unconditionally present in the bracket schedule —
/// resolved as a walkover by <c>TournamentService</c> when the WB
/// champion wins the first game; played for real when the LB
/// champion wins.</para>
///
/// <para>Three golden facts:</para>
/// <list type="number">
///   <item>The bracket emits exactly two grand-final pairings —
///         <c>(round=1)</c> + <c>(round=2)</c>.</item>
///   <item>The grand-final-reset placeholder uses the dedicated
///         <see cref="DoubleEliminationBracket.GrandFinalResetPlaceholder"/>
///         token (NOT the W6 generic <c>__pending__</c>) so the
///         service layer can distinguish "reset" slots from
///         ordinary placeholders without re-running the round
///         counter.</item>
///   <item>The grand-final reset is emitted for EVERY power-of-two
///         seed count (4, 8, 16). The reset doesn't scale with N
///         — there's always exactly one.</item>
/// </list>
/// </summary>
public sealed class LosersBracketGrandFinalResetTests
{
    private static readonly string[] Seeds4 =
    {
        "alice", "bob", "carol", "dave",
    };

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
    public void GrandFinal_EmitsBothFinalAndReset_For_8_Seeds()
    {
        var gen = new DoubleEliminationBracket();
        var pairings = gen.Generate(Seeds8);
        var grand = pairings.Where(p => p.Bracket == BracketSide.GrandFinal).ToList();

        Assert.Equal(2, grand.Count);
        Assert.Contains(grand, p => p.Round == 1);
        Assert.Contains(grand, p => p.Round == 2);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-7")]
    public void GrandFinal_ResetCarriesDedicatedPlaceholder()
    {
        var gen = new DoubleEliminationBracket();
        var pairings = gen.Generate(Seeds8);
        var reset = pairings.Single(p => p.Bracket == BracketSide.GrandFinal && p.Round == 2);

        Assert.Equal(DoubleEliminationBracket.GrandFinalResetPlaceholder, reset.P1);
        Assert.Equal(DoubleEliminationBracket.GrandFinalResetPlaceholder, reset.P2);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-7")]
    public void GrandFinal_FinalCarriesChampionPlaceholders()
    {
        var gen = new DoubleEliminationBracket();
        var pairings = gen.Generate(Seeds8);
        var final = pairings.Single(p => p.Bracket == BracketSide.GrandFinal && p.Round == 1);

        Assert.Equal("__pending_wb_champion__", final.P1);
        Assert.Equal("__pending_lb_champion__", final.P2);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [Trait("Category", "Tournament")]
    [Trait("Wave", "Phase-K-7")]
    public void GrandFinal_ResetEmittedForEveryPowerOfTwoSeedCount(int seedCount)
    {
        string[] seeds = seedCount switch
        {
            4 => Seeds4,
            8 => Seeds8,
            16 => Seeds16,
            _ => throw new ArgumentOutOfRangeException(nameof(seedCount)),
        };
        var gen = new DoubleEliminationBracket();
        var pairings = gen.Generate(seeds);
        var grand = pairings.Where(p => p.Bracket == BracketSide.GrandFinal).ToList();

        Assert.Equal(2, grand.Count);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-7")]
    public void Generator_IsDeterministic_GrandFinalSlots_Across_Calls()
    {
        var gen = new DoubleEliminationBracket();
        var first = gen.Generate(Seeds16)
            .Where(p => p.Bracket == BracketSide.GrandFinal)
            .ToList();
        var second = gen.Generate(Seeds16)
            .Where(p => p.Bracket == BracketSide.GrandFinal)
            .ToList();

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Round, second[i].Round);
            Assert.Equal(first[i].P1, second[i].P1);
            Assert.Equal(first[i].P2, second[i].P2);
        }
    }

    [Theory]
    [InlineData(2, 1)]   // ceil(log2(2)) = 1
    [InlineData(4, 2)]   // ceil(log2(4)) = 2
    [InlineData(5, 3)]   // ceil(log2(5)) = 3 (next power of 2 is 8)
    [InlineData(8, 3)]   // ceil(log2(8)) = 3
    [InlineData(16, 4)]  // ceil(log2(16)) = 4
    [InlineData(32, 5)]
    [Trait("Category", "Tournament")]
    [Trait("Wave", "Phase-K-7")]
    public void BracketDepth_MatchesCeilLog2(int n, int expected)
    {
        Assert.Equal(expected, DoubleEliminationBracket.BracketDepth(n));
    }
}
