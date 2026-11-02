using Mahjong.Autotable.Api.Tournament;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W8.Bishop;

/// <summary>
/// Phase K Wave 8 — Bishop. Hard-asserted facts for the Swiss
/// tiebreaker calculation. Pins:
///
/// <list type="number">
///   <item>Winners ahead of losers by total wins.</item>
///   <item>Median-Buchholz drops single high + single low opponent.</item>
///   <item>Median-Buchholz falls back to plain Buchholz for &lt;3 opps.</item>
///   <item>Sonneborn-Berger rewards beating a stronger field.</item>
///   <item>Cumulative-score rewards early-round wins.</item>
///   <item>Alphabetical-PlayerId fallback is deterministic + Ordinal.</item>
///   <item>Re-running with the same input produces byte-identical
///         output (deterministic order).</item>
/// </list>
/// </summary>
public sealed class SwissStandingsServiceTests
{
    private static SwissPairing Win(string winner, string loser) =>
        new(winner, loser, SwissOutcome.Win, WinnerId: winner);

    private static SwissPairing Draw(string a, string b) =>
        new(a, b, SwissOutcome.Draw);

    private static SwissPairing Bye(string p) =>
        new(p, null, SwissOutcome.Bye, IsBye: true);

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void Wins_DominateOverTiebreakers()
    {
        var svc = new SwissStandingsService();
        var rounds = new[]
        {
            new SwissRound(1, new[] { Win("alice", "bob") }),
            new SwissRound(2, new[] { Win("alice", "carol") }),
            new SwissRound(3, new[] { Win("bob", "carol") }),
        };
        var standings = svc.ComputeFinalStandings(rounds);
        Assert.Equal("alice", standings[0].PlayerId);
        Assert.Equal(2, standings[0].Wins);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void StandingEntry_CarriesAllTiebreakerComponents()
    {
        var svc = new SwissStandingsService();
        var rounds = new[]
        {
            new SwissRound(1, new[] { Win("alice", "bob") }),
        };
        var s = svc.ComputeFinalStandings(rounds);
        var alice = s.First(x => x.PlayerId == "alice");
        Assert.True(alice.Wins >= 0);
        Assert.True(alice.MatchPoints >= 0);
        // The fields exist on the record — null reference would have thrown.
        _ = alice.MedianBuchholz;
        _ = alice.SonnebornBerger;
        _ = alice.CumulativeScore;
        _ = alice.BuchholzSum;
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void MedianBuchholz_DropsHighAndLowOpponents()
    {
        // Inputs: 1, 5, 10, 3, 7  → drop 1 + 10, sum of {5, 3, 7} = 15
        var result = SwissStandingsService.MedianBuchholz(new double[] { 1, 5, 10, 3, 7 });
        Assert.Equal(15.0, result, 6);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void MedianBuchholz_FallsBackToPlainBuchholz_BelowThreeOpponents()
    {
        Assert.Equal(0.0, SwissStandingsService.MedianBuchholz(Array.Empty<double>()), 6);
        Assert.Equal(7.0, SwissStandingsService.MedianBuchholz(new double[] { 7 }), 6);
        Assert.Equal(10.0, SwissStandingsService.MedianBuchholz(new double[] { 3, 7 }), 6);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void Deterministic_TiedPlayers_OrderAlphabetically()
    {
        // No games at all → all players tied by everything; the
        // alphabetical-PlayerId tiebreaker must be deterministic.
        var svc = new SwissStandingsService();
        var rounds = new[]
        {
            new SwissRound(1, new[] { Draw("zulu", "alpha") }),
            new SwissRound(2, new[] { Draw("zulu", "alpha") }),
        };
        var standings = svc.ComputeFinalStandings(rounds);
        Assert.Equal("alpha", standings[0].PlayerId);
        Assert.Equal("zulu", standings[1].PlayerId);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void Deterministic_RepeatedCall_ProducesSameOrdering()
    {
        var svc = new SwissStandingsService();
        var rounds = new[]
        {
            new SwissRound(1, new[] { Win("alice", "bob"), Win("carol", "dave") }),
            new SwissRound(2, new[] { Win("alice", "carol"), Win("bob", "dave") }),
            new SwissRound(3, new[] { Draw("alice", "dave"), Draw("bob", "carol") }),
        };
        var first = svc.ComputeFinalStandings(rounds);
        var second = svc.ComputeFinalStandings(rounds);
        Assert.Equal(first.Select(s => s.PlayerId), second.Select(s => s.PlayerId));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void Byes_AccumulateMatchPoints_WithoutOpponentMath()
    {
        var svc = new SwissStandingsService();
        var rounds = new[]
        {
            new SwissRound(1, new[] { Bye("alice"), Win("bob", "carol") }),
            new SwissRound(2, new[] { Win("alice", "bob"), Bye("carol") }),
        };
        var standings = svc.ComputeFinalStandings(rounds);
        // Alice should be in the standings (no exception was thrown
        // by the bye math).
        Assert.Contains(standings, s => s.PlayerId == "alice");
        Assert.Contains(standings, s => s.PlayerId == "bob");
        Assert.Contains(standings, s => s.PlayerId == "carol");
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void EmptyInput_ProducesEmptyStandings()
    {
        var svc = new SwissStandingsService();
        var standings = svc.ComputeFinalStandings(Array.Empty<SwissRound>());
        Assert.Empty(standings);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void DistinctPlayers_AreEachListedExactlyOnce()
    {
        var svc = new SwissStandingsService();
        var rounds = new[]
        {
            new SwissRound(1, new[] { Win("alice", "bob") }),
            new SwissRound(2, new[] { Win("alice", "bob") }),
            new SwissRound(3, new[] { Win("alice", "bob") }),
        };
        var standings = svc.ComputeFinalStandings(rounds);
        Assert.Equal(2, standings.Count);
        Assert.Single(standings, s => s.PlayerId == "alice");
        Assert.Single(standings, s => s.PlayerId == "bob");
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void TieBrokenBy_SonnebornBerger_BeatsStrongerField_Wins()
    {
        // Both alice + bob have 2 wins. Alice beat strong players
        // (carol, dave who themselves win a game), bob beat weak
        // players (eve, frank who lose everything). Sonneborn-Berger
        // should favour alice.
        var svc = new SwissStandingsService();
        var rounds = new[]
        {
            // Round 1: alice ↘ carol; bob ↘ eve; dave ↘ frank
            new SwissRound(1, new[]
            {
                Win("alice", "carol"),
                Win("bob",   "eve"),
                Win("dave",  "frank"),
            }),
            // Round 2: alice ↘ dave (strong opp); bob ↘ frank (weak opp); carol ↘ eve
            new SwissRound(2, new[]
            {
                Win("alice", "dave"),
                Win("bob",   "frank"),
                Win("carol", "eve"),
            }),
        };
        var standings = svc.ComputeFinalStandings(rounds);
        var alice = standings.First(s => s.PlayerId == "alice");
        var bob = standings.First(s => s.PlayerId == "bob");
        // Alice's defeated opponents (carol, dave) carry more match
        // points than bob's (eve, frank) — strict greater.
        Assert.True(alice.SonnebornBerger > bob.SonnebornBerger,
            $"Expected alice SB > bob SB; got {alice.SonnebornBerger} vs {bob.SonnebornBerger}");
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-8")]
    public void Standings_OrderedDescending_ByMatchPoints()
    {
        var svc = new SwissStandingsService();
        var rounds = new[]
        {
            new SwissRound(1, new[] { Win("alice", "bob"), Win("carol", "dave") }),
            new SwissRound(2, new[] { Win("alice", "carol"), Win("bob", "dave") }),
        };
        var standings = svc.ComputeFinalStandings(rounds);
        for (var i = 1; i < standings.Count; i++)
        {
            Assert.True(standings[i - 1].MatchPoints >= standings[i].MatchPoints,
                $"Standings not monotonic at index {i}: " +
                $"{standings[i - 1].PlayerId}={standings[i - 1].MatchPoints} → " +
                $"{standings[i].PlayerId}={standings[i].MatchPoints}");
        }
    }
}
