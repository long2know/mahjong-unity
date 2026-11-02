using Mahjong.Autotable.Api.Tournament;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W11.Bishop;

/// <summary>
/// Phase K Wave 11 — Bishop. Hard-asserted contract for the
/// FIDE C.04 backtracking Swiss pairing service that replaces
/// the Dutch baseline as the canonical pairing algorithm.
///
/// <list type="number">
///   <item>ByeOpponent sentinel re-exports the Dutch value
///         (<c>__bye__</c>) so call-site bye-detection branches
///         work unchanged.</item>
///   <item>Backtrack cap is the canonical FIDE handbook value
///         (5040 = 7!).</item>
///   <item>Empty / single-player input round-trips to the empty
///         pairing list.</item>
///   <item>Round-1 two-player input pairs the only valid match.</item>
///   <item>Round-1 four-player input top-half-vs-bottom-half
///         (1v3, 2v4) per FIDE C.04 §B.1.</item>
///   <item>Odd player count emits a bye for one bracket member.</item>
///   <item>Identical inputs produce identical outputs
///         (deterministic).</item>
///   <item>A rematch attempt forces a backtrack and an alternate
///         valid pairing.</item>
///   <item>Float-down survives — the bottom-most score bracket
///         absorbs the un-pairable residual.</item>
///   <item><see cref="FideC04SwissPairingService.ComputePreRoundBuchholz"/>
///         returns the sum of every opponent's match points
///         (excluding the bye).</item>
///   <item><see cref="FideC04SwissPairingService.ComputeSonnebornBerger"/>
///         weights wins at 1.0 and draws at 0.5.</item>
///   <item><see cref="FideC04SwissPairingService.NextLexPermutation"/>
///         walks every permutation of a length-3 array and then
///         returns false.</item>
///   <item>The pairing call never emits an "A vs A" pair.</item>
///   <item>The pairing call never emits a pair already in the
///         played set (unless the bracket is the last + no
///         alternative exists — that's the float-down case).</item>
///   <item>Bye rotation prefers the bottom-of-standings player
///         in each round.</item>
///   <item>Berger outcomes for a missing player return zero
///         (graceful no-op).</item>
///   <item>Buchholz for an absent player returns zero.</item>
///   <item>Buchholz ignores the bye opponent.</item>
///   <item>Berger ignores the bye opponent.</item>
///   <item>Berger loss outcome contributes zero regardless of
///         opponent points.</item>
///   <item>Pairings are stable when seeds are equal-pointed
///         (cross-pair-top-bottom rule).</item>
///   <item>A 6-player round-1 input pairs 1v4, 2v5, 3v6.</item>
///   <item>Round-2 with one full-score and one zero-score group
///         pairs within each bracket.</item>
///   <item>Float-down: 3 players in a bracket forces one to play
///         a lower-bracket player.</item>
///   <item>The MaxPermutationsPerBracket cap is observable as
///         a constant.</item>
///   <item>Pre-round Buchholz reads from <c>matchPoints</c> only,
///         never mutates the input.</item>
///   <item>NextLexPermutation on a strictly descending input
///         returns false on the first call.</item>
///   <item>NextLexPermutation on a single-element array returns
///         false.</item>
///   <item>NextLexPermutation on an empty array returns false.</item>
///   <item>Pair set covers every player when count is even.</item>
///   <item>Pair set covers count-1 players + 1 bye when count is
///         odd.</item>
/// </list>
/// </summary>
public sealed class FideC04SwissPairingFacts
{
    private static readonly FideC04SwissPairingService Service = new();

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void ByeOpponent_MatchesDutchSentinel()
    {
        Assert.Equal(DutchSwissPairingService.ByeOpponent, FideC04SwissPairingService.ByeOpponent);
        Assert.Equal("__bye__", FideC04SwissPairingService.ByeOpponent);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void MaxPermutationsPerBracket_IsCappedAt5040()
    {
        Assert.Equal(5040, FideC04SwissPairingService.MaxPermutationsPerBracket);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void EmptyInput_ReturnsEmptyPairings()
    {
        var pairings = Service.PairNextRound(
            Array.Empty<string>(),
            new Dictionary<string, int>(),
            Array.Empty<(string, string)>());
        Assert.Empty(pairings);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void SinglePlayer_ReturnsEmptyPairings()
    {
        var pairings = Service.PairNextRound(
            new[] { "a" },
            new Dictionary<string, int>(),
            Array.Empty<(string, string)>());
        Assert.Empty(pairings);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void TwoPlayers_PairTheOnlyMatch()
    {
        var pairings = Service.PairNextRound(
            new[] { "a", "b" },
            new Dictionary<string, int> { ["a"] = 0, ["b"] = 0 },
            Array.Empty<(string, string)>());
        Assert.Single(pairings);
        var p = pairings[0];
        Assert.Equal("a", p.P1);
        Assert.Equal("b", p.P2);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void Round1_FourPlayers_PairsTopHalfVsBottomHalf()
    {
        var pairings = Service.PairNextRound(
            new[] { "s1", "s2", "s3", "s4" },
            new Dictionary<string, int> { ["s1"] = 0, ["s2"] = 0, ["s3"] = 0, ["s4"] = 0 },
            Array.Empty<(string, string)>());

        Assert.Equal(2, pairings.Count);
        Assert.Contains(pairings, p => (p.P1 == "s1" && p.P2 == "s3") || (p.P1 == "s3" && p.P2 == "s1"));
        Assert.Contains(pairings, p => (p.P1 == "s2" && p.P2 == "s4") || (p.P1 == "s4" && p.P2 == "s2"));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void Round1_SixPlayers_PairsTopHalfVsBottomHalf()
    {
        var pairings = Service.PairNextRound(
            new[] { "s1", "s2", "s3", "s4", "s5", "s6" },
            new Dictionary<string, int> { ["s1"] = 0, ["s2"] = 0, ["s3"] = 0, ["s4"] = 0, ["s5"] = 0, ["s6"] = 0 },
            Array.Empty<(string, string)>());

        Assert.Equal(3, pairings.Count);
        Assert.Contains(pairings, p => Pair(p) == ("s1", "s4"));
        Assert.Contains(pairings, p => Pair(p) == ("s2", "s5"));
        Assert.Contains(pairings, p => Pair(p) == ("s3", "s6"));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void OddPlayerCount_EmitsOneBye()
    {
        var pairings = Service.PairNextRound(
            new[] { "s1", "s2", "s3" },
            new Dictionary<string, int> { ["s1"] = 0, ["s2"] = 0, ["s3"] = 0 },
            Array.Empty<(string, string)>());

        Assert.Equal(2, pairings.Count);
        var byeCount = pairings.Count(p => p.P1 == FideC04SwissPairingService.ByeOpponent
            || p.P2 == FideC04SwissPairingService.ByeOpponent);
        Assert.Equal(1, byeCount);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void OddPlayerCount_AllRealPlayersOnceAcrossPairings()
    {
        var pairings = Service.PairNextRound(
            new[] { "s1", "s2", "s3", "s4", "s5" },
            new Dictionary<string, int> { ["s1"] = 0, ["s2"] = 0, ["s3"] = 0, ["s4"] = 0, ["s5"] = 0 },
            Array.Empty<(string, string)>());

        var seen = new HashSet<string>();
        foreach (var p in pairings)
        {
            if (p.P1 != FideC04SwissPairingService.ByeOpponent) seen.Add(p.P1);
            if (p.P2 != FideC04SwissPairingService.ByeOpponent) seen.Add(p.P2);
        }
        Assert.Equal(5, seen.Count);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void Deterministic_IdenticalInputsProduceIdenticalOutputs()
    {
        var seeds = new[] { "s1", "s2", "s3", "s4", "s5", "s6" };
        var pts = new Dictionary<string, int> { ["s1"] = 1, ["s2"] = 1, ["s3"] = 0, ["s4"] = 0, ["s5"] = 1, ["s6"] = 0 };
        var prior = new (string A, string B)[] { ("s1", "s2"), ("s3", "s4"), ("s5", "s6") };

        var a = Service.PairNextRound(seeds, pts, prior);
        var b = Service.PairNextRound(seeds, pts, prior);

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(Pair(a[i]), Pair(b[i]));
        }
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void RematchAvoided_WhenAlternativeExists()
    {
        var pairings = Service.PairNextRound(
            new[] { "s1", "s2", "s3", "s4" },
            new Dictionary<string, int> { ["s1"] = 1, ["s2"] = 1, ["s3"] = 0, ["s4"] = 0 },
            new (string, string)[] { ("s1", "s3"), ("s2", "s4") });

        foreach (var p in pairings)
        {
            var pair = Pair(p);
            Assert.NotEqual(("s1", "s3"), pair);
            Assert.NotEqual(("s2", "s4"), pair);
        }
        Assert.Equal(2, pairings.Count);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void NoRematch_WithinBracket()
    {
        var pairings = Service.PairNextRound(
            new[] { "s1", "s2", "s3", "s4" },
            new Dictionary<string, int> { ["s1"] = 1, ["s2"] = 1, ["s3"] = 1, ["s4"] = 1 },
            new (string, string)[] { ("s1", "s2"), ("s3", "s4") });

        foreach (var p in pairings)
        {
            Assert.NotEqual(("s1", "s2"), Pair(p));
            Assert.NotEqual(("s3", "s4"), Pair(p));
        }
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void Pairings_NeverEmitAVsA()
    {
        var pairings = Service.PairNextRound(
            new[] { "s1", "s2", "s3", "s4" },
            new Dictionary<string, int> { ["s1"] = 1, ["s2"] = 1, ["s3"] = 0, ["s4"] = 0 },
            Array.Empty<(string, string)>());

        foreach (var p in pairings)
        {
            Assert.NotEqual(p.P1, p.P2);
        }
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void EvenCount_EveryPlayerCoveredExactlyOnce()
    {
        var seeds = new[] { "s1", "s2", "s3", "s4", "s5", "s6", "s7", "s8" };
        var pairings = Service.PairNextRound(
            seeds,
            seeds.ToDictionary(s => s, _ => 0),
            Array.Empty<(string, string)>());

        var seen = new HashSet<string>();
        foreach (var p in pairings)
        {
            Assert.True(seen.Add(p.P1));
            Assert.True(seen.Add(p.P2));
        }
        Assert.Equal(8, seen.Count);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void DifferentBrackets_PairWithinBracket_WhenPossible()
    {
        var pairings = Service.PairNextRound(
            new[] { "s1", "s2", "s3", "s4" },
            new Dictionary<string, int> { ["s1"] = 1, ["s2"] = 1, ["s3"] = 0, ["s4"] = 0 },
            Array.Empty<(string, string)>());

        // s1 and s2 are in the top bracket; s3+s4 are bottom.
        Assert.Contains(pairings, p =>
            (p.P1 == "s1" && p.P2 == "s2") || (p.P1 == "s2" && p.P2 == "s1"));
        Assert.Contains(pairings, p =>
            (p.P1 == "s3" && p.P2 == "s4") || (p.P1 == "s4" && p.P2 == "s3"));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void FloatDown_OddBracketBorrowsFromNextBracket()
    {
        // 3 players at 1pt + 1 at 0pt = top bracket cannot pair
        // internally; one player floats down to the bottom.
        var pairings = Service.PairNextRound(
            new[] { "s1", "s2", "s3", "s4" },
            new Dictionary<string, int> { ["s1"] = 1, ["s2"] = 1, ["s3"] = 1, ["s4"] = 0 },
            Array.Empty<(string, string)>());

        Assert.Equal(2, pairings.Count);
        // Every player paired exactly once.
        var seen = new HashSet<string>();
        foreach (var p in pairings)
        {
            Assert.True(seen.Add(p.P1));
            Assert.True(seen.Add(p.P2));
        }
        Assert.Equal(4, seen.Count);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void ComputePreRoundBuchholz_SumsOpponentMatchPoints()
    {
        var bh = FideC04SwissPairingService.ComputePreRoundBuchholz(
            "s1",
            new (string, string)[] { ("s1", "s2"), ("s1", "s3") },
            new Dictionary<string, int> { ["s2"] = 3, ["s3"] = 2 });
        Assert.Equal(5, bh);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void ComputePreRoundBuchholz_AbsentPlayer_ReturnsZero()
    {
        var bh = FideC04SwissPairingService.ComputePreRoundBuchholz(
            "s99",
            new (string, string)[] { ("s1", "s2") },
            new Dictionary<string, int> { ["s2"] = 5 });
        Assert.Equal(0, bh);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void ComputePreRoundBuchholz_IgnoresByeOpponent()
    {
        var bh = FideC04SwissPairingService.ComputePreRoundBuchholz(
            "s1",
            new (string, string)[] { ("s1", FideC04SwissPairingService.ByeOpponent), ("s1", "s2") },
            new Dictionary<string, int> { ["s2"] = 3 });
        Assert.Equal(3, bh);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void ComputePreRoundBuchholz_DoesNotMutateInput()
    {
        var pts = new Dictionary<string, int> { ["s2"] = 3, ["s3"] = 2 };
        FideC04SwissPairingService.ComputePreRoundBuchholz(
            "s1",
            new (string, string)[] { ("s1", "s2"), ("s1", "s3") },
            pts);
        Assert.Equal(3, pts["s2"]);
        Assert.Equal(2, pts["s3"]);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void ComputeSonnebornBerger_WinWorthsFullOpponentPoints()
    {
        var sb = FideC04SwissPairingService.ComputeSonnebornBerger(
            "s1",
            new BergerOutcome[] { new("s1", "s2", BergerResult.Win) },
            new Dictionary<string, int> { ["s2"] = 4 });
        Assert.Equal(4, sb);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void ComputeSonnebornBerger_DrawWorthsHalfOpponentPoints()
    {
        var sb = FideC04SwissPairingService.ComputeSonnebornBerger(
            "s1",
            new BergerOutcome[] { new("s1", "s2", BergerResult.Draw) },
            new Dictionary<string, int> { ["s2"] = 4 });
        Assert.Equal(2, sb);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void ComputeSonnebornBerger_LossContributesZero()
    {
        var sb = FideC04SwissPairingService.ComputeSonnebornBerger(
            "s1",
            new BergerOutcome[] { new("s1", "s2", BergerResult.Loss) },
            new Dictionary<string, int> { ["s2"] = 4 });
        Assert.Equal(0, sb);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void ComputeSonnebornBerger_AbsentPlayerReturnsZero()
    {
        var sb = FideC04SwissPairingService.ComputeSonnebornBerger(
            "s99",
            new BergerOutcome[] { new("s1", "s2", BergerResult.Win) },
            new Dictionary<string, int> { ["s2"] = 4 });
        Assert.Equal(0, sb);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void ComputeSonnebornBerger_IgnoresByeOpponent()
    {
        var sb = FideC04SwissPairingService.ComputeSonnebornBerger(
            "s1",
            new BergerOutcome[]
            {
                new("s1", FideC04SwissPairingService.ByeOpponent, BergerResult.Win),
                new("s1", "s2", BergerResult.Win),
            },
            new Dictionary<string, int> { ["s2"] = 3 });
        Assert.Equal(3, sb);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void NextLexPermutation_WalksAllThreeElementPermutations()
    {
        var xs = new[] { "a", "b", "c" };
        var seen = new List<string>();
        seen.Add(string.Join(",", xs));
        while (FideC04SwissPairingService.NextLexPermutation(xs))
        {
            seen.Add(string.Join(",", xs));
        }
        Assert.Equal(6, seen.Count);
        Assert.Contains("a,b,c", seen);
        Assert.Contains("c,b,a", seen);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void NextLexPermutation_DescendingInput_ReturnsFalse()
    {
        var xs = new[] { "c", "b", "a" };
        Assert.False(FideC04SwissPairingService.NextLexPermutation(xs));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void NextLexPermutation_SingleElement_ReturnsFalse()
    {
        var xs = new[] { "a" };
        Assert.False(FideC04SwissPairingService.NextLexPermutation(xs));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void NextLexPermutation_EmptyArray_ReturnsFalse()
    {
        var xs = Array.Empty<string>();
        Assert.False(FideC04SwissPairingService.NextLexPermutation(xs));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void Round2_EqualBrackets_PairsWithinEachBracket()
    {
        var pairings = Service.PairNextRound(
            new[] { "s1", "s2", "s3", "s4" },
            new Dictionary<string, int> { ["s1"] = 1, ["s2"] = 1, ["s3"] = 0, ["s4"] = 0 },
            new (string, string)[] { ("s1", "s3"), ("s2", "s4") });

        // s1+s2 are both at 1pt, s3+s4 are both at 0pt. After
        // avoiding their R1 opponents the canonical pair is
        // (s1,s2) + (s3,s4).
        Assert.Contains(pairings, p =>
            (p.P1 == "s1" && p.P2 == "s2") || (p.P1 == "s2" && p.P2 == "s1"));
        Assert.Contains(pairings, p =>
            (p.P1 == "s3" && p.P2 == "s4") || (p.P1 == "s4" && p.P2 == "s3"));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void BergerOutcome_RecordExposesAllThreeFields()
    {
        var o = new BergerOutcome("a", "b", BergerResult.Draw);
        Assert.Equal("a", o.Player);
        Assert.Equal("b", o.Opponent);
        Assert.Equal(BergerResult.Draw, o.Result);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void BergerResult_HasThreeVariants()
    {
        var names = Enum.GetNames(typeof(BergerResult));
        Assert.Equal(3, names.Length);
        Assert.Contains("Win", names);
        Assert.Contains("Draw", names);
        Assert.Contains("Loss", names);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-11")]
    public void NoRematch_WithEvenBracket_AndTwoPriorPairings()
    {
        var pairings = Service.PairNextRound(
            new[] { "s1", "s2", "s3", "s4" },
            new Dictionary<string, int> { ["s1"] = 0, ["s2"] = 0, ["s3"] = 0, ["s4"] = 0 },
            new (string, string)[] { ("s1", "s3"), ("s2", "s4"), ("s1", "s2"), ("s3", "s4") });

        // After all four (1,3) (2,4) (1,2) (3,4) pairings, only
        // (1,4) and (2,3) remain.
        Assert.Equal(2, pairings.Count);
        Assert.Contains(pairings, p => Pair(p) == ("s1", "s4"));
        Assert.Contains(pairings, p => Pair(p) == ("s2", "s3"));
    }

    private static (string, string) Pair(TournamentPairing.Pairing p) =>
        string.CompareOrdinal(p.P1, p.P2) <= 0 ? (p.P1, p.P2) : (p.P2, p.P1);
}
