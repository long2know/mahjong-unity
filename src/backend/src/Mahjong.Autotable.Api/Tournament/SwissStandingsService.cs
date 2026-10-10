namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 8 — Bishop. Finalised Swiss tiebreaker rules for
/// Mahjong-Autotable's Swiss-system tournaments. Wave 6 shipped the
/// pairing algorithm + the live <see cref="TournamentPairing.BuchholzScore"/>
/// helper; this service finalises the standings calculation that
/// runs at tournament-completion time to produce the published
/// final order.
///
/// <para><b>Tiebreaker stack</b> — applied in order. Two players tied
/// on the higher tiebreaker are compared on the next; ties carry
/// through to the next rule. If all tiebreakers are exhausted with
/// remaining ties, the players are ordered alphabetically by
/// <see cref="StandingEntry.PlayerId"/> (deterministic, never random):</para>
///
/// <list type="number">
///   <item><b>Wins</b> (primary score) — number of rounds won.</item>
///   <item><b>Median-Buchholz</b> — sum of every opponent's final
///         match-point score, with the highest and lowest opponent
///         dropped. Mitigates the "easy draw vs. tough draw" effect.
///         For fewer than 3 opponents the median collapses to the
///         standard Buchholz (sum of all opponent points).</item>
///   <item><b>Sonneborn-Berger</b> — sum of defeated opponents'
///         scores plus half the sum of drawn opponents' scores.
///         Rewards players who beat the strong field.</item>
///   <item><b>Cumulative-score</b> — sum of a player's own running
///         score per round (W=1, D=0.5, L=0). Higher means the
///         player won the early rounds (when the field was still
///         settling) so they faced harder opponents later.</item>
///   <item><b>PlayerId</b> — alphabetical, <see cref="StringComparer.Ordinal"/>.
///         Deterministic; tests pin this exact comparator so a
///         re-run produces the same final ordering byte-for-byte.</item>
/// </list>
///
/// <para>The service is pure (no DI dependencies) — callers pass the
/// completed Swiss rounds as a list, the service returns the final
/// ranking. <see cref="TournamentService"/> wires it into the
/// tournament-complete path; tests construct rounds directly.</para>
/// </summary>
public sealed class SwissStandingsService
{
    /// <summary>
    /// Computes the final standings for a Swiss tournament. Returns
    /// one <see cref="StandingEntry"/> per distinct player observed
    /// across the supplied <paramref name="rounds"/>, ordered by the
    /// full tiebreaker stack.
    /// </summary>
    public IReadOnlyList<StandingEntry> ComputeFinalStandings(IEnumerable<SwissRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        var rs = rounds.ToList();

        // ── Phase 1: discover every player + their per-round results.
        var perPlayerResults = new Dictionary<string, List<SwissPairingResult>>(StringComparer.Ordinal);
        foreach (var round in rs)
        {
            foreach (var pairing in round.Pairings)
            {
                AddResult(perPlayerResults, pairing.PlayerAId, pairing, isPlayerA: true);
                AddResult(perPlayerResults, pairing.PlayerBId, pairing, isPlayerA: false);
            }
        }

        // ── Phase 2: total match points per player (W=1, D=0.5, L=0).
        var totalPoints = perPlayerResults.ToDictionary(
            kv => kv.Key,
            kv => SumPoints(kv.Value, focusPlayer: kv.Key),
            StringComparer.Ordinal);

        var wins = perPlayerResults.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Count(p => GetOutcomeForFocus(p, kv.Key) == SwissOutcome.Win),
            StringComparer.Ordinal);

        // ── Phase 3: tiebreaker components.
        var entries = perPlayerResults.Keys.Select(player =>
        {
            var games = perPlayerResults[player];
            var opponentPoints = games
                .Select(g => OpponentOf(g, player))
                .Where(o => !string.IsNullOrEmpty(o))
                .Select(o => totalPoints.GetValueOrDefault(o!, 0.0))
                .ToList();

            var buchholz = opponentPoints.Sum();
            var median = MedianBuchholz(opponentPoints);
            var sonneborn = SonnebornBerger(games, player, totalPoints);
            var cumulative = Cumulative(games, player);

            return new StandingEntry(
                PlayerId: player,
                Wins: wins[player],
                MatchPoints: totalPoints[player],
                MedianBuchholz: median,
                SonnebornBerger: sonneborn,
                CumulativeScore: cumulative,
                BuchholzSum: buchholz);
        }).ToList();

        // ── Phase 4: apply the tiebreaker stack.
        entries.Sort((a, b) =>
        {
            // Primary: more wins is better.
            var byWins = b.Wins.CompareTo(a.Wins);
            if (byWins != 0) return byWins;

            // Then match points (handles draws — wins-on-tie can still
            // diverge when a draw counts).
            var byPoints = b.MatchPoints.CompareTo(a.MatchPoints);
            if (byPoints != 0) return byPoints;

            var byMedian = b.MedianBuchholz.CompareTo(a.MedianBuchholz);
            if (byMedian != 0) return byMedian;

            var bySonneborn = b.SonnebornBerger.CompareTo(a.SonnebornBerger);
            if (bySonneborn != 0) return bySonneborn;

            var byCumulative = b.CumulativeScore.CompareTo(a.CumulativeScore);
            if (byCumulative != 0) return byCumulative;

            // Final fallback: alphabetical PlayerId (deterministic).
            return string.CompareOrdinal(a.PlayerId, b.PlayerId);
        });

        return entries;
    }

    private static void AddResult(
        IDictionary<string, List<SwissPairingResult>> map,
        string? playerId,
        SwissPairing pairing,
        bool isPlayerA)
    {
        if (string.IsNullOrEmpty(playerId)) return;
        if (!map.TryGetValue(playerId, out var list))
        {
            list = new List<SwissPairingResult>();
            map[playerId] = list;
        }
        list.Add(new SwissPairingResult(pairing, isPlayerA));
    }

    private static double SumPoints(IEnumerable<SwissPairingResult> games, string focusPlayer)
    {
        var total = 0.0;
        foreach (var g in games)
        {
            switch (GetOutcomeForFocus(g, focusPlayer))
            {
                case SwissOutcome.Win: total += 1.0; break;
                case SwissOutcome.Draw: total += 0.5; break;
                case SwissOutcome.Loss: total += 0.0; break;
                case SwissOutcome.Bye: total += 1.0; break; // Wave-8 spec: byes count as wins
            }
        }
        return total;
    }

    private static SwissOutcome GetOutcomeForFocus(SwissPairingResult game, string focusPlayer)
    {
        var p = game.Pairing;
        if (p.IsBye)
        {
            // A bye is recorded as PlayerA = focus, PlayerB = null.
            return SwissOutcome.Bye;
        }
        if (p.Outcome == SwissOutcome.Draw) return SwissOutcome.Draw;
        if (p.Outcome == SwissOutcome.Win)
        {
            return string.Equals(p.WinnerId, focusPlayer, StringComparison.Ordinal)
                ? SwissOutcome.Win
                : SwissOutcome.Loss;
        }
        return SwissOutcome.Loss;
    }

    private static string? OpponentOf(SwissPairingResult game, string focusPlayer)
    {
        var p = game.Pairing;
        if (p.IsBye) return null;
        if (string.Equals(p.PlayerAId, focusPlayer, StringComparison.Ordinal)) return p.PlayerBId;
        return p.PlayerAId;
    }

    /// <summary>
    /// Median-Buchholz: sum of opponent points after dropping the
    /// single highest and single lowest opponent score. For fewer
    /// than 3 opponents this collapses to a plain Buchholz sum.
    /// </summary>
    public static double MedianBuchholz(IReadOnlyList<double> opponentPoints)
    {
        if (opponentPoints.Count == 0) return 0.0;
        if (opponentPoints.Count < 3) return opponentPoints.Sum();

        var sorted = opponentPoints.OrderBy(x => x).ToList();
        // Drop first (lowest) and last (highest).
        var middleSum = 0.0;
        for (var i = 1; i < sorted.Count - 1; i++) middleSum += sorted[i];
        return middleSum;
    }

    /// <summary>
    /// Sonneborn-Berger: sum of defeated opponents' scores +
    /// half the sum of drawn opponents' scores. Byes do not
    /// contribute (no opponent to weight).
    /// </summary>
    private static double SonnebornBerger(
        IReadOnlyList<SwissPairingResult> games,
        string focusPlayer,
        IReadOnlyDictionary<string, double> totalPoints)
    {
        var sum = 0.0;
        foreach (var g in games)
        {
            var opp = OpponentOf(g, focusPlayer);
            if (string.IsNullOrEmpty(opp)) continue;
            var oppPoints = totalPoints.GetValueOrDefault(opp!, 0.0);
            switch (GetOutcomeForFocus(g, focusPlayer))
            {
                case SwissOutcome.Win: sum += oppPoints; break;
                case SwissOutcome.Draw: sum += oppPoints / 2.0; break;
            }
        }
        return sum;
    }

    /// <summary>
    /// Cumulative score: sum of the focus player's running score
    /// after each round in input order. Rewards early-round
    /// success because high early scores compound into later sums.
    /// </summary>
    private static double Cumulative(IReadOnlyList<SwissPairingResult> games, string focusPlayer)
    {
        var running = 0.0;
        var cumulative = 0.0;
        foreach (var g in games)
        {
            switch (GetOutcomeForFocus(g, focusPlayer))
            {
                case SwissOutcome.Win: running += 1.0; break;
                case SwissOutcome.Draw: running += 0.5; break;
                case SwissOutcome.Loss: running += 0.0; break;
                case SwissOutcome.Bye: running += 1.0; break;
            }
            cumulative += running;
        }
        return cumulative;
    }

    private readonly record struct SwissPairingResult(SwissPairing Pairing, bool IsPlayerA);
}

/// <summary>
/// Phase K Wave 8 — Bishop. Input shape for
/// <see cref="SwissStandingsService.ComputeFinalStandings"/>. One
/// round of a completed Swiss tournament.
/// </summary>
public sealed record SwissRound(int RoundNumber, IReadOnlyList<SwissPairing> Pairings);

/// <summary>
/// Phase K Wave 8 — Bishop. Single Swiss pairing result. For a bye,
/// set <see cref="PlayerBId"/> null + <see cref="IsBye"/> true.
/// </summary>
public sealed record SwissPairing(
    string PlayerAId,
    string? PlayerBId,
    SwissOutcome Outcome,
    string? WinnerId = null,
    bool IsBye = false);

public enum SwissOutcome
{
    Win,
    Loss,
    Draw,
    Bye,
}

/// <summary>
/// Phase K Wave 8 — Bishop. Output row from
/// <see cref="SwissStandingsService.ComputeFinalStandings"/>. Carries
/// every tiebreaker component as a separate field so downstream UIs
/// can render the full breakdown without re-computing.
/// </summary>
public sealed record StandingEntry(
    string PlayerId,
    int Wins,
    double MatchPoints,
    double MedianBuchholz,
    double SonnebornBerger,
    double CumulativeScore,
    double BuchholzSum);
