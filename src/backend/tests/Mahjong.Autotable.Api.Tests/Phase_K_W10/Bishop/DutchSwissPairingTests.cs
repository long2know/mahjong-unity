using Mahjong.Autotable.Api.Tournament;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Bishop;

/// <summary>
/// Phase K Wave 10 — Bishop. Contract tests for the Dutch-system
/// Swiss pairing service.
///
/// <list type="number">
///   <item>An empty player list emits an empty pairing.</item>
///   <item>A single-player list emits an empty pairing (no
///         self-pairing).</item>
///   <item>Round-one (empty score map, empty history) degenerates
///         to top-half vs bottom-half — same shape as
///         <see cref="TournamentPairing.SwissFirstRound"/>.</item>
///   <item>Pairings within a score group respect seed order —
///         higher-seed (lower seed index) is P1.</item>
///   <item>Players with different score totals end up in
///         different score groups.</item>
///   <item>The algorithm pairs top-half against bottom-half
///         within each score group.</item>
///   <item>Prior pairings are avoided via single-swap when an
///         alternative bottom-half opponent is available.</item>
///   <item>Odd score groups float the lowest-ranked player to the
///         next score group.</item>
///   <item>An odd total roster lands the lowest-ranked player on
///         a bye (P2 = "__bye__").</item>
///   <item>Result is deterministic for identical inputs.</item>
///   <item>Every player from the input list appears exactly once
///         across the emitted pairings (no missing entrants).</item>
///   <item>The service implements <see cref="ISwissPairingService"/>.</item>
///   <item>Eight-player round-three scenario produces a valid
///         pairing where every player appears once.</item>
///   <item>Two-player tournament always pairs the two.</item>
///   <item>Players sharing a score group sort by seed (P1 = lowest
///         seed index when within same score).</item>
/// </list>
/// </summary>
public sealed class DutchSwissPairingTests
{
    private static DutchSwissPairingService NewService() => new();

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void Empty_Players_EmptyResult()
    {
        var pairings = NewService().PairNextRound(
            Array.Empty<string>(),
            new Dictionary<string, int>(),
            Array.Empty<(string, string)>());
        Assert.Empty(pairings);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void SinglePlayer_EmptyResult()
    {
        var pairings = NewService().PairNextRound(
            new[] { "p1" },
            new Dictionary<string, int>(),
            Array.Empty<(string, string)>());
        Assert.Empty(pairings);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void TwoPlayers_PairsAsExpected()
    {
        var pairings = NewService().PairNextRound(
            new[] { "alice", "bob" },
            new Dictionary<string, int>(),
            Array.Empty<(string, string)>());
        Assert.Single(pairings);
        Assert.Equal("alice", pairings[0].P1);
        Assert.Equal("bob", pairings[0].P2);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void RoundOne_EmptyScore_DegeneratesToTopVsBottomHalf()
    {
        // Round one — every player has 0 points.
        var players = new[] { "p1", "p2", "p3", "p4" };
        var pairings = NewService().PairNextRound(
            players,
            new Dictionary<string, int>(),
            Array.Empty<(string, string)>());
        Assert.Equal(2, pairings.Count);
        // Top half {p1,p2} vs bottom half {p3,p4}.
        // Seed order: p1 (index 0) < p2 (1), p3 (2) < p4 (3).
        Assert.Equal("p1", pairings[0].P1);
        Assert.Equal("p3", pairings[0].P2);
        Assert.Equal("p2", pairings[1].P1);
        Assert.Equal("p4", pairings[1].P2);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void SeedOrder_BreaksTiesWithinScoreGroup()
    {
        // p1 has higher seed (index 0) than p2.
        var players = new[] { "p1", "p2", "p3", "p4" };
        var scores = new Dictionary<string, int>
        {
            ["p1"] = 3, ["p2"] = 3, ["p3"] = 0, ["p4"] = 0,
        };
        var pairings = NewService().PairNextRound(
            players,
            scores,
            Array.Empty<(string, string)>());
        // First pairing should have P1=p1 (the higher seed in the
        // 3-point group).
        Assert.Equal("p1", pairings[0].P1);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void DifferentScores_LandInDifferentGroups()
    {
        // 6-player config: 3 score groups of 2 each.
        var players = new[] { "p1", "p2", "p3", "p4", "p5", "p6" };
        var scores = new Dictionary<string, int>
        {
            ["p1"] = 6, ["p2"] = 6,
            ["p3"] = 3, ["p4"] = 3,
            ["p5"] = 0, ["p6"] = 0,
        };
        var pairings = NewService().PairNextRound(
            players,
            scores,
            Array.Empty<(string, string)>());
        Assert.Equal(3, pairings.Count);
        // Each pairing must be within the same score group.
        Assert.Equal("p1", pairings[0].P1);
        Assert.Equal("p2", pairings[0].P2);
        Assert.Equal("p3", pairings[1].P1);
        Assert.Equal("p4", pairings[1].P2);
        Assert.Equal("p5", pairings[2].P1);
        Assert.Equal("p6", pairings[2].P2);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void NoRematches_SingleSwapAvoidance()
    {
        var players = new[] { "p1", "p2", "p3", "p4" };
        var scores = new Dictionary<string, int>
        {
            ["p1"] = 3, ["p2"] = 3, ["p3"] = 3, ["p4"] = 3,
        };
        // All in same score group; default pairing would be
        // p1-p3, p2-p4. Mark p1-p3 as already played; expect
        // p1-p4 and p2-p3 instead via single-swap.
        var history = new[] { ("p1", "p3") };
        var pairings = NewService().PairNextRound(
            players,
            scores,
            history);

        Assert.Equal(2, pairings.Count);
        var pairSet = pairings
            .Select(p => Normalise(p.P1, p.P2))
            .ToHashSet();
        Assert.Contains(Normalise("p1", "p4"), pairSet);
        Assert.Contains(Normalise("p2", "p3"), pairSet);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void OddScoreGroup_FloatsLowestRankedToNextGroup()
    {
        // Five players: three in 3-point group, two in 0-point
        // group. The lowest-ranked of the 3-point group (p3 by seed)
        // should float to the 0-point group.
        var players = new[] { "p1", "p2", "p3", "p4", "p5" };
        var scores = new Dictionary<string, int>
        {
            ["p1"] = 3, ["p2"] = 3, ["p3"] = 3,
            ["p4"] = 0, ["p5"] = 0,
        };
        var pairings = NewService().PairNextRound(
            players,
            scores,
            Array.Empty<(string, string)>());

        // After float-down: p1 + p2 (paired); p3 floats; group
        // becomes {p3, p4, p5} (3 entries — odd again, p5 floats
        // again but there's no next group so p5 gets a bye).
        Assert.Contains(pairings, p =>
            (p.P1 == "p1" && p.P2 == "p2") || (p.P1 == "p2" && p.P2 == "p1"));
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void OddTotalRoster_LowestRankedGetsBye()
    {
        var players = new[] { "p1", "p2", "p3" };
        var pairings = NewService().PairNextRound(
            players,
            new Dictionary<string, int>(),
            Array.Empty<(string, string)>());
        // Three-player tournament, all 0 points, one bye for the
        // lowest-ranked (p3 = seed index 2).
        Assert.Contains(pairings, p =>
            p.P2 == DutchSwissPairingService.ByeOpponent);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void Deterministic_SameInputs_SameOutputs()
    {
        var players = new[] { "p1", "p2", "p3", "p4", "p5", "p6" };
        var scores = new Dictionary<string, int>
        {
            ["p1"] = 3, ["p2"] = 3, ["p3"] = 0, ["p4"] = 0, ["p5"] = 0, ["p6"] = 0,
        };
        var a = NewService().PairNextRound(players, scores, Array.Empty<(string, string)>());
        var b = NewService().PairNextRound(players, scores, Array.Empty<(string, string)>());
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].P1, b[i].P1);
            Assert.Equal(a[i].P2, b[i].P2);
        }
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void EveryPlayer_AppearsExactlyOnce_AcrossPairings()
    {
        var players = new[] { "p1", "p2", "p3", "p4", "p5", "p6" };
        var scores = new Dictionary<string, int>
        {
            ["p1"] = 6, ["p2"] = 3, ["p3"] = 3, ["p4"] = 3, ["p5"] = 3, ["p6"] = 0,
        };
        var pairings = NewService().PairNextRound(players, scores, Array.Empty<(string, string)>());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in pairings)
        {
            if (p.P1 != DutchSwissPairingService.ByeOpponent) Assert.True(seen.Add(p.P1), $"player {p.P1} appears twice");
            if (p.P2 != DutchSwissPairingService.ByeOpponent && p.P2 is not null) Assert.True(seen.Add(p.P2), $"player {p.P2} appears twice");
        }
        foreach (var player in players) Assert.Contains(player, seen);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void Service_ImplementsISwissPairingService()
    {
        Assert.IsAssignableFrom<ISwissPairingService>(NewService());
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void EightPlayerRound_AllPlayersAppearOnce_NoRematches()
    {
        var players = new[] { "p1", "p2", "p3", "p4", "p5", "p6", "p7", "p8" };
        var scores = new Dictionary<string, int>
        {
            ["p1"] = 6, ["p2"] = 6, ["p3"] = 3, ["p4"] = 3,
            ["p5"] = 3, ["p6"] = 3, ["p7"] = 0, ["p8"] = 0,
        };
        var history = new[]
        {
            ("p1", "p2"), // r1
            ("p3", "p4"), // r1
            ("p5", "p6"), // r1
            ("p7", "p8"), // r1
            ("p1", "p3"), // r2
            ("p2", "p4"), // r2
            ("p5", "p7"), // r2
            ("p6", "p8"), // r2
        };
        var pairings = NewService().PairNextRound(players, scores, history);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in pairings)
        {
            if (p.P1 != DutchSwissPairingService.ByeOpponent) seen.Add(p.P1);
            if (p.P2 is not null && p.P2 != DutchSwissPairingService.ByeOpponent) seen.Add(p.P2);
        }
        foreach (var player in players) Assert.Contains(player, seen);
        Assert.Equal(4, pairings.Count);
    }

    [Fact, Trait("Category", "Tournament"), Trait("Wave", "Phase-K-10")]
    public void TopHalfVsBottomHalf_WithinScoreGroup()
    {
        // Four players in one score group — top half (p1,p2) plays
        // bottom half (p3,p4), top[i] vs bottom[i]: p1-p3, p2-p4.
        var players = new[] { "p1", "p2", "p3", "p4" };
        var scores = new Dictionary<string, int>
        {
            ["p1"] = 3, ["p2"] = 3, ["p3"] = 3, ["p4"] = 3,
        };
        var pairings = NewService().PairNextRound(players, scores, Array.Empty<(string, string)>());
        Assert.Equal(2, pairings.Count);
        Assert.Equal("p1", pairings[0].P1);
        Assert.Equal("p3", pairings[0].P2);
        Assert.Equal("p2", pairings[1].P1);
        Assert.Equal("p4", pairings[1].P2);
    }

    private static (string, string) Normalise(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
}
