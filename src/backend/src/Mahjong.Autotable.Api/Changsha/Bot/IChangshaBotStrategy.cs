namespace Mahjong.Autotable.Api.Changsha.Bot;

/// <summary>
/// Per-seat bot strategy interface for Phase F. A strategy is consulted on every
/// runtime tick that needs a bot decision: turn-start (its own turn to draw + discard),
/// claim-window resolution (someone else discarded — should we claim?), self-draw
/// after a wall tile is added (Hu? Kong?), and pickup cue (manual-deal pickup phase).
/// Implementations are stateless across hands; <see cref="ChangshaGameState"/> carries
/// everything they need to reason about the table.
/// </summary>
/// <remarks>
/// <para>
/// Difficulty tiers:
/// <list type="bullet">
///   <item><b>Easy</b> — discards the highest-rank unpaired tile; only ever claims Hu
///   and obvious Pungs. No defensive play, no shanten awareness.</item>
///   <item><b>Medium</b> — port of the legacy <see cref="ChangshaBotPolicy"/>: scores
///   each tile by "keepability" (pairs + adjacencies + 2/5/8 bias) and claims Hu,
///   Pung, Kong, plus Chow when below 3 melds.</item>
///   <item><b>Hard</b> — EV-aware: combines Medium's keep score with a defensive
///   penalty for tiles recently discarded by opponents (assumed safer). Claims Chow
///   only when it improves shanten; otherwise plays the long game.</item>
/// </list>
/// </para>
/// <para>
/// For backward compatibility (<see cref="ChangshaBotPolicy"/>, BotMatchHarness),
/// <see cref="DecideAction"/> remains as a unified entry point: it dispatches to
/// <see cref="OnTurnStart"/>, <see cref="OnOtherDiscard"/>, <see cref="OnSelfDraw"/>,
/// or <see cref="OnPickupCue"/> based on the current phase.
/// </para>
/// </remarks>
public interface IChangshaBotStrategy
{
    /// <summary>One of <c>"easy"</c>, <c>"medium"</c>, <c>"hard"</c>. Lowercase.</summary>
    string Difficulty { get; }

    /// <summary>
    /// Bot's turn-start hook (its own turn to discard). Returns a Discard /
    /// DeclareWin / DeclareKong action. Never Wait or Pass.
    /// </summary>
    BotAction OnTurnStart(ChangshaGameState state, int botSeatIndex);

    /// <summary>
    /// Claim-window hook (some other seat discarded). Returns Claim or Pass.
    /// When the bot has no offered opportunity, returns Pass.
    /// </summary>
    BotAction OnOtherDiscard(ChangshaGameState state, int botSeatIndex, int discarderSeat, int discardedTileId);

    /// <summary>
    /// Self-draw hook (called after a wall tile is added to the bot's hand). Returns
    /// DeclareWin / DeclareConcealedKong / DeclareAddedKong / Wait. Discard is handled
    /// by the following <see cref="OnTurnStart"/> call.
    /// </summary>
    BotAction OnSelfDraw(ChangshaGameState state, int botSeatIndex);

    /// <summary>
    /// Phase F §3 — pickup cue. Bots always take the expected wall slice; the
    /// strategy hook exists so future difficulty tiers can choose to break the wall
    /// differently. Always returns <see cref="BotAction.Wait"/>, which the runtime
    /// translates into a <see cref="ChangshaGameStateMachine.TakeTilesFromWall"/>
    /// call with the expected count.
    /// </summary>
    BotAction OnPickupCue(ChangshaGameState state, int botSeatIndex);

    /// <summary>
    /// Unified entry point preserved for backward compatibility. Routes to the
    /// appropriate phase hook above based on <paramref name="state"/>.<see cref="ChangshaGameState.Phase"/>.
    /// </summary>
    BotAction DecideAction(ChangshaGameState state, int botSeatIndex);

    /// <summary>
    /// Phase J Wave 10 — explainable variant of <see cref="DecideAction"/>.
    /// Returns the same action wrapped in a <see cref="BotDecision"/> that
    /// also carries a numeric strategy score and an ordered list of
    /// human-readable reasoning lines. Strategies that want to surface
    /// tier-specific reasoning (shanten value, safety score, opponent-
    /// discard inference) override this method directly; legacy callers
    /// (hub claim driver) keep calling <see cref="DecideAction"/>.
    ///
    /// <para>Default implementation wraps <see cref="DecideAction"/> with
    /// empty reasoning so external strategies (none in-tree today) keep
    /// working without modification.</para>
    /// </summary>
    BotDecision DecideWithReasoning(ChangshaGameState state, int botSeatIndex)
        => BotDecision.FromAction(DecideAction(state, botSeatIndex));
}
