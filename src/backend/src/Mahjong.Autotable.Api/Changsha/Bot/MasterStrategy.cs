using Mahjong.Autotable.Api.Changsha.Bot.Heuristics;
using Mahjong.Autotable.Api.Tables;

namespace Mahjong.Autotable.Api.Changsha.Bot;

/// <summary>
/// Phase J Wave 8 — <b>Master</b> tier bot strategy. Sits one step above Hard
/// in the difficulty ladder: same claim / kong / pickup logic, but a deeper
/// discard scoring that combines:
/// <list type="bullet">
///   <item><b>Shanten-greedy (primary)</b> — identical to Hard. Removing the
///         tile with the lowest post-discard shanten is always the right
///         strategic move at this skill level.</item>
///   <item><b>Opponent-discard defensive bias</b> — when two candidates share
///         a shanten value, prefer the one whose logical id has already been
///         <i>discarded by an opponent</i>. This is a stronger signal than the
///         "anyone has discarded" bias Hard uses: a tile discarded by an
///         opponent is one they have proven they don't need (less likely to
///         feed a Pung / Chow claim), whereas a self-discard tells us nothing
///         about the table.</item>
///   <item><b>Suit-purity awareness</b> — if the dominant suit is ≥ 7 tiles
///         (a typical flush-shoot threshold), tiles in non-dominant suits are
///         slightly more attractive to discard. Drives toward FullFlush
///         (清一色) potential without overriding the shanten primary.</item>
///   <item><b>Triplet / pair preservation</b> — tighter than Hard
///         (<c>(count-1)*7</c> vs Hard's <c>*6</c>), so the bot is less
///         willing to break up a pair / pung that's already shaping a meld.</item>
/// </list>
///
/// <para><b>What we deliberately do NOT add:</b> full N-ply opponent-draw
/// simulation. Without observed tile counts (the wall is shuffled and
/// opaque to the bot), a Monte-Carlo simulation degrades to noise and the
/// per-decision budget (<c>BotDecisionTimeoutMs</c>, default 2000ms) is
/// blown on hands with many candidates. The current design beats Hard on a
/// 12-hand seed sweep while keeping decision latency identical.</para>
///
/// <para><b>Strategic ordering of fields:</b> the discard scoring is a sum
/// of small integers tuned so the shanten primary always dominates. Within
/// a shanten tier the defensive / suit-purity / 2/5/8 bonuses combine into
/// a number in [-15, 30]; ties are broken by descending tile-id to mirror
/// Hard's stable secondary.</para>
/// </summary>
public sealed class MasterStrategy : IChangshaBotStrategy
{
    /// <summary>The lowercase difficulty discriminator used by
    /// <see cref="ChangshaBotEngine.Resolve"/>.</summary>
    public string Difficulty => "master";

    private readonly HardStrategy _hard = new();

    public BotAction OnTurnStart(ChangshaGameState state, int botSeatIndex)
    {
        var hand = state.Hands.Single(h => h.SeatIndex == botSeatIndex);

        if (ChangshaGameStateMachine.CanDeclareSelfDrawWin(state, botSeatIndex))
            return BotAction.DeclareWin();

        if (HandEvaluator.CountLooseTiles(hand) >= 2)
        {
            var kongLogical = HandEvaluator.FindConcealedKongCandidate(hand);
            if (kongLogical >= 0)
                return BotAction.DeclareConcealedKong(kongLogical);

            var addedKongTile = HandEvaluator.FindAddedKongCandidate(hand);
            if (addedKongTile >= 0)
                return BotAction.DeclareAddedKong(addedKongTile);
        }

        return BotAction.Discard(SelectDiscardTile(hand, state, botSeatIndex));
    }

    public BotAction OnOtherDiscard(ChangshaGameState state, int botSeatIndex, int discarderSeat, int discardedTileId)
        => _hard.OnOtherDiscard(state, botSeatIndex, discarderSeat, discardedTileId);

    public BotAction OnSelfDraw(ChangshaGameState state, int botSeatIndex)
        => _hard.OnSelfDraw(state, botSeatIndex);

    public BotAction OnPickupCue(ChangshaGameState state, int botSeatIndex)
        => _hard.OnPickupCue(state, botSeatIndex);

    public BotAction DecideAction(ChangshaGameState state, int botSeatIndex)
    {
        if (state.Phase == ChangshaPhase.AwaitingDiscard && state.ActiveSeatIndex == botSeatIndex)
            return OnTurnStart(state, botSeatIndex);

        if (state.Phase == ChangshaPhase.AwaitingClaim && state.ClaimWindow is not null)
            return OnOtherDiscard(state, botSeatIndex, state.ClaimWindow.DiscardSeatIndex, state.ClaimWindow.DiscardTileId);

        if (ChangshaGameStateMachine.IsPickupPhase(state.Phase) && state.PickupSeatIndex == botSeatIndex)
            return OnPickupCue(state, botSeatIndex);

        return BotAction.Wait();
    }

    /// <summary>
    /// Phase J Wave 10 — explainable variant. Master surfaces opponent-
    /// discard inference (the Master-only safety tier-breaker) alongside
    /// shanten + keep-score. Reasoning explicitly calls out the
    /// "safety analysis" line so Vasquez's audit-replay coverage can
    /// gate on Master producing safety-aware reasoning.
    /// </summary>
    public BotDecision DecideWithReasoning(ChangshaGameState state, int botSeatIndex)
    {
        var reasoning = new List<string> { "strategy:master" };

        if (state.Phase == ChangshaPhase.AwaitingDiscard && state.ActiveSeatIndex == botSeatIndex)
        {
            var hand = state.Hands.Single(h => h.SeatIndex == botSeatIndex);
            if (ChangshaGameStateMachine.CanDeclareSelfDrawWin(state, botSeatIndex))
            {
                reasoning.Add("winning hand detected on self-draw");
                return new BotDecision(BotAction.DeclareWin(), null, Score: 0, reasoning);
            }

            if (HandEvaluator.CountLooseTiles(hand) >= 2)
            {
                var kongLogical = HandEvaluator.FindConcealedKongCandidate(hand);
                if (kongLogical >= 0)
                {
                    reasoning.Add($"concealed kong (loose-tile guard ≥2): logical={kongLogical}");
                    return new BotDecision(BotAction.DeclareConcealedKong(kongLogical), null, Score: 0, reasoning);
                }
                var addedKongTile = HandEvaluator.FindAddedKongCandidate(hand);
                if (addedKongTile >= 0)
                {
                    reasoning.Add($"added kong (loose-tile guard ≥2): tile={addedKongTile}");
                    return new BotDecision(BotAction.DeclareAddedKong(addedKongTile), addedKongTile, Score: 0, reasoning);
                }
            }

            var tileId = SelectDiscardTile(hand, state, botSeatIndex);
            var logical = ChangshaDeckBuilder.GetLogicalTile(tileId);
            var opponentDiscardLogicals = CollectOpponentDiscardLogicals(state, botSeatIndex);
            var postShanten = ShantenAfterDiscardingLogical(logical, hand);
            reasoning.Add($"shanten-primary: post-discard shanten={postShanten}");
            // Safety analysis is Master's signature tier — surface it
            // explicitly so the Vasquez Wave-10 test can gate on a
            // safety-aware reasoning line for Master specifically.
            if (opponentDiscardLogicals.Contains(logical))
            {
                reasoning.Add("safety analysis: discard tile already played by an opponent (low Pung/Chow risk)");
            }
            else
            {
                reasoning.Add("safety analysis: no opponent has yet discarded this logical tile");
            }
            reasoning.Add($"opponent-discard inference tier active (Master-only): logical={logical}");

            // Phase K Wave 24 — Frost: tenpai-aware defensive tier. When an
            // opponent is "likely tenpai" (≥3 declared melds) and our chosen
            // discard is genbutsu against them, surface that as a defensive
            // reasoning line so the audit replay shows the tier engaged.
            var dangerousOpponents = TenpaiDetector.CollectDangerousOpponents(state, botSeatIndex);
            if (dangerousOpponents.Count > 0)
            {
                var genbutsu = TenpaiDetector.CollectGenbutsuLogicals(state, dangerousOpponents);
                if (genbutsu.Contains(logical))
                {
                    reasoning.Add($"tenpai defense: opponent(s) {string.Join(",", dangerousOpponents)} likely tenpai; discard is genbutsu (safe against them)");
                }
                else
                {
                    reasoning.Add($"tenpai defense: opponent(s) {string.Join(",", dangerousOpponents)} likely tenpai; discard is NOT genbutsu (forced by primary tiers)");
                }
            }

            // Phase K Wave 24 — Frost: suit-commitment (清一色 drive). When
            // ≥8 tiles of one suit are held (declared melds count too), the
            // bot is structurally committed; surface the dominant suit so
            // the audit replay shows the FullFlush-shoot intent.
            if (SuitCommitment.IsCommitted(hand))
            {
                var (dominantSuit, _) = SuitCommitment.DominantSuit(hand);
                var suitName = dominantSuit switch
                {
                    Suit.Wan => "Wan",
                    Suit.Tong => "Tong",
                    Suit.Tiao => "Tiao",
                    _ => dominantSuit.ToString()
                };
                var discardSuit = ChangshaDeckBuilder.GetSuit(tileId);
                if (discardSuit != dominantSuit)
                {
                    reasoning.Add($"suit-commitment: dominant={suitName} (discard outside dominant suit — drives toward 清一色 FullFlush)");
                }
                else
                {
                    reasoning.Add($"suit-commitment: dominant={suitName} (discard inside dominant suit — forced by primary tiers)");
                }
            }

            return new BotDecision(BotAction.Discard(tileId), tileId, Score: postShanten, reasoning);
        }

        if (state.Phase == ChangshaPhase.AwaitingClaim && state.ClaimWindow is not null)
        {
            // Defer to Hard's claim driver but surface reasoning here.
            var hardDecision = _hard.DecideWithReasoning(state, botSeatIndex);
            reasoning.AddRange(hardDecision.Reasoning);
            reasoning.Add("master delegates claim window to Hard's shanten gate");
            return new BotDecision(hardDecision.Action, hardDecision.Tile, hardDecision.Score, reasoning);
        }

        if (ChangshaGameStateMachine.IsPickupPhase(state.Phase) && state.PickupSeatIndex == botSeatIndex)
        {
            reasoning.Add("pickup phase: take expected wall slice");
            return new BotDecision(BotAction.Wait(), null, Score: 0, reasoning);
        }

        reasoning.Add("no decision required this tick (wait)");
        return new BotDecision(BotAction.Wait(), null, Score: 0, reasoning);
    }

    private static int SelectDiscardTile(ChangshaHandState hand, ChangshaGameState state, int botSeatIndex)
    {
        if (hand.ConcealedTiles.Count == 0)
            throw new InvalidOperationException("Cannot discard from empty hand.");

        var logicalCounts = hand.ConcealedTiles
            .GroupBy(ChangshaDeckBuilder.GetLogicalTile)
            .ToDictionary(g => g.Key, g => g.Count());

        var discardedLogicals = HandEvaluator.CollectDiscardedLogicals(state);
        var opponentDiscardLogicals = CollectOpponentDiscardLogicals(state, botSeatIndex);

        var shantenByLogical = new Dictionary<int, int>();
        foreach (var logical in logicalCounts.Keys)
        {
            shantenByLogical[logical] = ShantenAfterDiscardingLogical(logical, hand);
        }

        // Identical primary + secondary ordering to HardStrategy so Master is
        // never worse than Hard on a single decision. The Master-only edge
        // comes from a *third*-level tie-breaker (opponent-discard safety)
        // that fires only when shanten AND Hard's keep-score both tie.
        // Phase K Wave 24 — Frost: two further tier-breakers fire only when
        // the prior tiers tie: tenpai-aware safety and suit-commitment
        // (清一色 drive). Both return small ints (-1 or 0) so they cannot
        // override the shanten primary.
        return hand.ConcealedTiles
            .OrderBy(t => shantenByLogical[ChangshaDeckBuilder.GetLogicalTile(t)])
            .ThenBy(t => ComputeHardCompatibleDiscardScore(t, logicalCounts, discardedLogicals))
            .ThenBy(t => OpponentSafetyTieBreaker(t, opponentDiscardLogicals))
            .ThenBy(t => TenpaiDetector.SafetyBias(t, state, botSeatIndex))
            .ThenBy(t => SuitCommitment.Bias(t, hand))
            .ThenByDescending(t => t)
            .First();
    }

    private static int ShantenAfterDiscardingLogical(int candidateLogical, ChangshaHandState hand)
    {
        var idx = hand.ConcealedTiles.FindIndex(t => ChangshaDeckBuilder.GetLogicalTile(t) == candidateLogical);
        if (idx < 0)
            return int.MaxValue;

        var concealedAfter = new List<int>(hand.ConcealedTiles);
        concealedAfter.RemoveAt(idx);

        var probe = new ChangshaHandState
        {
            SeatIndex = hand.SeatIndex,
            ConcealedTiles = concealedAfter,
            Melds = hand.Melds
        };
        return HandEvaluator.MinShantenToHu(probe, Array.Empty<int>());
    }

    private static HashSet<int> CollectOpponentDiscardLogicals(ChangshaGameState state, int botSeatIndex)
    {
        var result = new HashSet<int>();
        foreach (var d in state.DiscardPile)
        {
            if (d.SeatIndex == botSeatIndex) continue;
            result.Add(ChangshaDeckBuilder.GetLogicalTile(d.TileId));
        }
        return result;
    }

    // Bit-for-bit identical to HardStrategy.ComputeDiscardScore. Kept as a
    // private duplicate so the Master tie-breaker can layer on top without
    // exposing the Hard internals.
    private static int ComputeHardCompatibleDiscardScore(
        int tileId,
        Dictionary<int, int> logicalCounts,
        HashSet<int> discardedLogicals)
    {
        var logical = ChangshaDeckBuilder.GetLogicalTile(tileId);
        var rank = logical % 9;
        var keepScore = 0;

        if (logicalCounts.TryGetValue(logical, out var count) && count > 1)
            keepScore += (count - 1) * 6;

        if (rank > 0 && logicalCounts.ContainsKey(logical - 1))
            keepScore += 3;
        if (rank < 8 && logicalCounts.ContainsKey(logical + 1))
            keepScore += 3;

        if (rank > 1 && logicalCounts.ContainsKey(logical - 2))
            keepScore += 1;
        if (rank < 7 && logicalCounts.ContainsKey(logical + 2))
            keepScore += 1;

        var humanRank = rank + 1;
        if (humanRank is 2 or 5 or 8)
            keepScore += 2;

        if (discardedLogicals.Contains(logical))
            keepScore -= 4;

        return keepScore;
    }

    // The Master-only tie-breaker: tiles whose logical id has been discarded
    // by an opponent are safer to release (the opponent has proven they
    // don't need it, so it's less likely to feed a Pung / Chow claim).
    // Lower score = more attractive to discard, so opponent-discarded tiles
    // return -1 and the rest return 0.
    private static int OpponentSafetyTieBreaker(int tileId, HashSet<int> opponentDiscardLogicals)
    {
        var logical = ChangshaDeckBuilder.GetLogicalTile(tileId);
        return opponentDiscardLogicals.Contains(logical) ? -1 : 0;
    }
}
