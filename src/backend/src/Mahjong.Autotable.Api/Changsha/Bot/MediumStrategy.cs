using Mahjong.Autotable.Api.Tables;

namespace Mahjong.Autotable.Api.Changsha.Bot;

/// <summary>
/// Phase F "Medium" difficulty strategy — a direct port of the heuristics from the
/// legacy <see cref="ChangshaBotPolicy"/>. Scores each candidate discard by how much
/// it contributes to potential pairs, sequences, and the 2/5/8 bias common in
/// Changsha rule sets, and claims Hu/Kong/Pung greedily plus Chow when the hand has
/// fewer than 3 melds.
/// </summary>
/// <remarks>
/// This is the default difficulty when none is specified. The legacy
/// <see cref="ChangshaBotPolicy"/> still exists as a thin facade over this strategy
/// so that <c>BotMatchHarness</c> and existing acceptance tests keep working without
/// modification.
/// </remarks>
public sealed class MediumStrategy : IChangshaBotStrategy
{
    public string Difficulty => "medium";

    public BotAction OnTurnStart(ChangshaGameState state, int botSeatIndex)
    {
        var hand = state.Hands.Single(h => h.SeatIndex == botSeatIndex);
        return DecideDiscardPhase(state, hand);
    }

    public BotAction OnOtherDiscard(ChangshaGameState state, int botSeatIndex, int discarderSeat, int discardedTileId)
    {
        var hand = state.Hands.Single(h => h.SeatIndex == botSeatIndex);
        if (state.ClaimWindow is null) return BotAction.Pass();
        return DecideClaimPhase(state, hand, botSeatIndex);
    }

    public BotAction OnSelfDraw(ChangshaGameState state, int botSeatIndex)
    {
        var hand = state.Hands.Single(h => h.SeatIndex == botSeatIndex);

        if (ChangshaGameStateMachine.CanDeclareSelfDrawWin(state, botSeatIndex))
            return BotAction.DeclareWin();

        var kongLogical = HandEvaluator.FindConcealedKongCandidate(hand);
        if (kongLogical >= 0)
            return BotAction.DeclareConcealedKong(kongLogical);

        var addedKongTile = HandEvaluator.FindAddedKongCandidate(hand);
        if (addedKongTile >= 0)
            return BotAction.DeclareAddedKong(addedKongTile);

        return BotAction.Wait();
    }

    public BotAction OnPickupCue(ChangshaGameState state, int botSeatIndex)
    {
        // Phase F §3 — bots always take the expected wall slice; the runtime
        // translates Wait during a pickup phase into a TakeTilesFromWall call.
        return BotAction.Wait();
    }

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
    /// Phase J Wave 10 — explainable variant. Medium populates a reasoning
    /// list with the keep-score tier, kong/win detection, and claim-tier
    /// acceptance ordering so the audit replay surfaces "why" alongside
    /// "what". Score is the keep-score of the chosen tile (lower = better
    /// to discard), useful for ranking decisions across hands.
    /// </summary>
    public BotDecision DecideWithReasoning(ChangshaGameState state, int botSeatIndex)
    {
        var reasoning = new List<string> { "strategy:medium" };

        if (state.Phase == ChangshaPhase.AwaitingDiscard && state.ActiveSeatIndex == botSeatIndex)
        {
            var hand = state.Hands.Single(h => h.SeatIndex == botSeatIndex);
            if (ChangshaGameStateMachine.CanDeclareSelfDrawWin(state, botSeatIndex))
            {
                reasoning.Add("winning hand detected on self-draw");
                return new BotDecision(BotAction.DeclareWin(), null, Score: 0, reasoning);
            }

            var kongLogical = HandEvaluator.FindConcealedKongCandidate(hand);
            if (kongLogical >= 0)
            {
                reasoning.Add($"concealed kong candidate: logical={kongLogical}");
                return new BotDecision(BotAction.DeclareConcealedKong(kongLogical), null, Score: 0, reasoning);
            }
            var addedKongTile = HandEvaluator.FindAddedKongCandidate(hand);
            if (addedKongTile >= 0)
            {
                reasoning.Add($"added kong candidate: tile={addedKongTile}");
                return new BotDecision(BotAction.DeclareAddedKong(addedKongTile), addedKongTile, Score: 0, reasoning);
            }

            var tileId = SelectDiscardTile(hand);
            var logicalCounts = hand.ConcealedTiles
                .GroupBy(ChangshaDeckBuilder.GetLogicalTile)
                .ToDictionary(g => g.Key, g => g.Count());
            var score = ComputeKeepScore(tileId, logicalCounts);
            reasoning.Add($"keep-score primary: discard tile={tileId} score={score} (lower=better to discard)");
            reasoning.Add("tie-breaker: tile-id descending");
            return new BotDecision(BotAction.Discard(tileId), tileId, Score: score, reasoning);
        }

        if (state.Phase == ChangshaPhase.AwaitingClaim && state.ClaimWindow is not null)
        {
            var hand = state.Hands.Single(h => h.SeatIndex == botSeatIndex);
            var action = DecideClaimPhase(state, hand, botSeatIndex);
            if (action.Type == BotActionType.Claim)
            {
                reasoning.Add($"claim accepted: type={action.ClaimType} (Hu>Kong>Pung>Chow priority)");
            }
            else
            {
                reasoning.Add("no claim opportunity matched gate (chow requires <3 melds)");
            }
            return new BotDecision(action, action.TileId, Score: 0, reasoning);
        }

        if (ChangshaGameStateMachine.IsPickupPhase(state.Phase) && state.PickupSeatIndex == botSeatIndex)
        {
            reasoning.Add("pickup phase: take expected wall slice");
            return new BotDecision(BotAction.Wait(), null, Score: 0, reasoning);
        }

        reasoning.Add("no decision required this tick (wait)");
        return new BotDecision(BotAction.Wait(), null, Score: 0, reasoning);
    }

    private static BotAction DecideDiscardPhase(ChangshaGameState state, ChangshaHandState hand)
    {
        if (ChangshaGameStateMachine.CanDeclareSelfDrawWin(state, hand.SeatIndex))
            return BotAction.DeclareWin();

        var kongLogical = HandEvaluator.FindConcealedKongCandidate(hand);
        if (kongLogical >= 0)
            return BotAction.DeclareConcealedKong(kongLogical);

        var addedKongTile = HandEvaluator.FindAddedKongCandidate(hand);
        if (addedKongTile >= 0)
            return BotAction.DeclareAddedKong(addedKongTile);

        var tileId = SelectDiscardTile(hand);
        return BotAction.Discard(tileId);
    }

    private static BotAction DecideClaimPhase(ChangshaGameState state, ChangshaHandState hand, int botSeatIndex)
    {
        var claimWindow = state.ClaimWindow!;
        var opportunities = claimWindow.Opportunities
            .Where(o => o.SeatIndex == botSeatIndex)
            .OrderByDescending(o => o.Priority)
            .ToList();

        if (opportunities.Count == 0)
            return BotAction.Pass();

        foreach (var opp in opportunities)
        {
            if (opp.ClaimType == TableClaimType.Hu)
                return BotAction.Claim(TableClaimType.Hu);

            if (opp.ClaimType == TableClaimType.Kong)
                return BotAction.Claim(TableClaimType.Kong);

            if (opp.ClaimType == TableClaimType.Pung)
                return BotAction.Claim(TableClaimType.Pung);

            if (opp.ClaimType == TableClaimType.Chow && hand.Melds.Count < 3)
                return BotAction.Claim(TableClaimType.Chow);
        }

        return BotAction.Pass();
    }

    /// <summary>Public so the legacy <see cref="ChangshaBotPolicy.SelectDiscardTile"/>
    /// facade and harness tests can call into the same implementation.</summary>
    public static int SelectDiscardTile(ChangshaHandState hand)
    {
        if (hand.ConcealedTiles.Count == 0)
            throw new InvalidOperationException("Cannot discard from empty hand.");

        var logicalCounts = hand.ConcealedTiles
            .GroupBy(ChangshaDeckBuilder.GetLogicalTile)
            .ToDictionary(g => g.Key, g => g.Count());

        return hand.ConcealedTiles
            .OrderBy(t => ComputeKeepScore(t, logicalCounts))
            .ThenByDescending(t => t)
            .First();
    }

    private static int ComputeKeepScore(int tileId, Dictionary<int, int> logicalCounts)
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

        return keepScore;
    }
}
