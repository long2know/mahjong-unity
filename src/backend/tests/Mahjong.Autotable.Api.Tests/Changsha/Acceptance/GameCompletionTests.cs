using System.Reflection;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Bot;
using Mahjong.Autotable.Api.Tables;
using Xunit.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Phase J Wave 2 — game completion tests (Vasquez).
///
/// <para>Bishop's Phase J Wave 2 task adds explicit "game over" semantics to the
/// Changsha state machine. The pre-Wave-2 baseline only flagged
/// <see cref="ChangshaPhase.EndGame"/> after 16 hands (4 hands × 4 rounds), too
/// long for solo play and decoupled from the autotable surface. Bishop's contract:
/// <list type="bullet">
///   <item>A new <c>MaxHands</c> setting on <see cref="ChangshaGameState"/> that
///     caps the game length. Default is 4 (one round) — the standard solo /
///     bot-match length surfaced through the autotable.</item>
///   <item>A new <c>ChangshaPhase.GameComplete</c> enum value (distinct from
///     <c>EndGame</c>) that <see cref="ChangshaGameStateMachine.RotateBanker"/>
///     sets when <see cref="ChangshaGameState.HandNumber"/> exceeds
///     <c>MaxHands</c>.</item>
///   <item>A new <c>IsGameComplete</c> read-only flag (or equivalent surface)
///     for callers that want a non-phase-bound predicate.</item>
///   <item>Subsequent attempts to start a new hand after <c>GameComplete</c>
///     are a no-op (or throw a recognisable exception): the state stays in
///     <c>GameComplete</c> and no further mutation occurs.</item>
/// </list>
/// </para>
///
/// <para><b>Bot harness.</b> Tests reuse the canonical step-machine pattern from
/// <see cref="BotStrengthTests.RunOneHand"/> with the same
/// <c>MaxStepsPerHand=4000</c> budget. Each hand finishes within budget; the
/// outer harness drives <see cref="ChangshaGameStateMachine.RotateBanker"/>
/// between hands until either <c>GameComplete</c> is reached or a per-test cap
/// on hands played is exhausted.</para>
///
/// <para><b>Reflection-defensive contract probes.</b> The new symbols
/// (<c>MaxHands</c> property setter, <c>IsGameComplete</c> property,
/// <c>ChangshaPhase.GameComplete</c>) are resolved via reflection so this test
/// assembly compiles before Bishop's commit lands. Tests fail RED with a clear
/// message naming the contract owed.</para>
/// </summary>
public class GameCompletionTests(ITestOutputHelper output)
{
    private const int MaxStepsPerHand = 4000;
    private const int MaxHandsBudget = 32; // upper bound on hands played by the harness

    // ────────────────────────────────────────────────────────────────────
    //  1. Default MaxHands → completion after 4 hands
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-2")]
    public void GameCompletes_AfterDefaultMaxHands()
    {
        var gameComplete = ResolveGameCompletePhase();

        var (state, _) = ChangshaGameStateMachine.CreateGame(
            seed: 42, botSeatIndexes: new[] { 0, 1, 2, 3 });

        var defaultMaxHands = GetMaxHands(state)
            ?? throw new InvalidOperationException(
                "ChangshaGameState.MaxHands property not defined — Bishop owes the " +
                "Phase J Wave 2 contract. Expected default value: 4 (one full round).");
        Assert.True(defaultMaxHands > 0,
            $"Default MaxHands must be positive — got {defaultMaxHands}.");
        Assert.True(defaultMaxHands <= MaxHandsBudget,
            $"Default MaxHands={defaultMaxHands} exceeds harness budget MaxHandsBudget={MaxHandsBudget}; " +
            "either Bishop's default is wrong or this test needs a larger cap.");

        var handsPlayed = PlayUntilGameComplete(state, gameComplete);

        output.WriteLine(
            $"Default MaxHands={defaultMaxHands}: completed after {handsPlayed} hands " +
            $"(final HandNumber={state.HandNumber}, Phase={state.Phase}).");

        Assert.Equal(gameComplete, state.Phase);
        Assert.True(GetIsGameComplete(state),
            "After reaching the GameComplete phase, ChangshaGameState.IsGameComplete " +
            "(or equivalent flag) must read true. Bishop owes this contract.");
        Assert.Equal(defaultMaxHands, handsPlayed);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Custom MaxHands → completion at the configured count
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-2")]
    public void GameCompletes_AfterCustomMaxHands()
    {
        var gameComplete = ResolveGameCompletePhase();

        var (state, _) = ChangshaGameStateMachine.CreateGame(
            seed: 1337, botSeatIndexes: new[] { 0, 1, 2, 3 });

        // Override MaxHands to 2 before any hand runs. If Bishop ships MaxHands as
        // an init-only or otherwise read-only property the test fails RED with a
        // clear message — the wave brief says "Start with MaxHands=2", so callers
        // must have a path to set it.
        const int customMaxHands = 2;
        TrySetMaxHands(state, customMaxHands);

        var actualMaxHands = GetMaxHands(state)
            ?? throw new InvalidOperationException(
                "ChangshaGameState.MaxHands property not defined — Bishop owes the " +
                "Phase J Wave 2 contract (custom-cap path).");
        Assert.Equal(customMaxHands, actualMaxHands);

        var handsPlayed = PlayUntilGameComplete(state, gameComplete);

        output.WriteLine(
            $"Custom MaxHands={customMaxHands}: completed after {handsPlayed} hands " +
            $"(final HandNumber={state.HandNumber}, Phase={state.Phase}).");

        Assert.Equal(gameComplete, state.Phase);
        Assert.True(GetIsGameComplete(state),
            "GameComplete phase must coincide with IsGameComplete==true.");
        Assert.Equal(customMaxHands, handsPlayed);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. No new hands start after GameComplete
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-2")]
    public void AfterGameComplete_NoNewHandsStart()
    {
        var gameComplete = ResolveGameCompletePhase();

        var (state, _) = ChangshaGameStateMachine.CreateGame(
            seed: 9001, botSeatIndexes: new[] { 0, 1, 2, 3 });
        TrySetMaxHands(state, 1);

        // Play the single hand to completion.
        var handsPlayed = PlayUntilGameComplete(state, gameComplete);
        Assert.Equal(1, handsPlayed);
        Assert.Equal(gameComplete, state.Phase);

        var preHandNumber = state.HandNumber;
        var preWallSize = state.Wall.Count;
        var preDealer = state.DealerSeatIndex;

        // Attempt to start a new hand via the canonical dealer entry-point.
        // Per Bishop's contract this must be a no-op OR throw a recognisable
        // exception (InvalidOperationException is the canonical "wrong phase"
        // surface used by other state-machine guards). Either way, the state
        // stays in GameComplete with the same hand counters.
        var startedAnotherHand = false;
        Exception? thrown = null;
        try
        {
            ChangshaGameStateMachine.RollDice(state, new DiceService(state.Seed));
            // If RollDice succeeds it implies the SM did not block on GameComplete;
            // record so the assertion fails with a precise message.
            startedAnotherHand = state.Phase != gameComplete;
        }
        catch (Exception ex)
        {
            // Recognised "wrong phase" / "game complete" guards throw an
            // InvalidOperationException (or subclass). Anything else surfaces
            // verbatim to fail the test with the original message.
            thrown = ex;
        }

        Assert.False(startedAnotherHand,
            "Calling RollDice while in GameComplete must NOT advance the state machine " +
            $"into a new hand. Observed Phase={state.Phase}, HandNumber={state.HandNumber}.");
        if (thrown is not null)
        {
            // Any exception is acceptable; require it to be an
            // InvalidOperationException so we don't catch CLR-fatal bugs
            // (NRE / OOM) and falsely pass the test.
            Assert.IsAssignableFrom<InvalidOperationException>(thrown);
        }

        // State is byte-for-byte unchanged on the relevant invariants.
        Assert.Equal(gameComplete, state.Phase);
        Assert.Equal(preHandNumber, state.HandNumber);
        Assert.Equal(preWallSize, state.Wall.Count);
        Assert.Equal(preDealer, state.DealerSeatIndex);
        Assert.True(GetIsGameComplete(state),
            "IsGameComplete must remain true after the rebuffed RollDice attempt.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  Step-machine harness — play hands until GameComplete or budget
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives <paramref name="state"/> through complete hands until the phase
    /// reaches <paramref name="gameComplete"/> or the harness budget is hit.
    /// Returns the number of hands actually played. Throws if the budget is
    /// exhausted (a real test alarm — the state machine should always
    /// terminate within <see cref="MaxHandsBudget"/> hands).
    /// </summary>
    private static int PlayUntilGameComplete(ChangshaGameState state, ChangshaPhase gameComplete)
    {
        var bot = new HardStrategy();
        var seatStrategies = new IChangshaBotStrategy[] { bot, bot, bot, bot };

        ChangshaGameStateMachine.StartGame(state);

        var handsPlayed = 0;
        while (handsPlayed < MaxHandsBudget && state.Phase != gameComplete)
        {
            // Run one hand: RollDice → Deal → bot loop → EndHand → RotateBanker.
            var seed = state.Seed + state.HandNumber * 1_000_003;
            ChangshaGameStateMachine.RollDice(state, new DiceService(seed));
            ChangshaGameStateMachine.Deal(state);

            RunHandUntilEnded(state, seatStrategies);

            // After Score / HandleWallExhausted the SM lands in EndHand. RotateBanker
            // advances counters and, when HandNumber > MaxHands (Bishop's gate),
            // sets Phase=GameComplete; otherwise sets Phase=RollingDice for the
            // next hand.
            ChangshaGameStateMachine.RotateBanker(state);
            handsPlayed++;
        }

        if (state.Phase != gameComplete)
        {
            throw new InvalidOperationException(
                $"GameCompletionTests harness budget exhausted ({MaxHandsBudget} hands played) " +
                $"without reaching {gameComplete}. Final Phase={state.Phase}, " +
                $"HandNumber={state.HandNumber}, RoundNumber={state.RoundNumber}.");
        }

        return handsPlayed;
    }

    /// <summary>
    /// Run one hand from AwaitingDiscard / claim-window phases through to
    /// EndHand using the per-seat strategy chain. Mirrors
    /// <see cref="BotStrengthTests"/>'s RunOneHand step machine — same step
    /// budget, same case branches — but does NOT terminate on EndHand; the
    /// caller drives RotateBanker explicitly.
    /// </summary>
    private static void RunHandUntilEnded(ChangshaGameState state, IChangshaBotStrategy[] strategies)
    {
        var steps = 0;
        while (steps < MaxStepsPerHand)
        {
            steps++;
            switch (state.Phase)
            {
                case ChangshaPhase.AwaitingDiscard:
                {
                    var seat = state.ActiveSeatIndex;
                    var hand = state.Hands[seat];
                    var totalTiles = hand.ConcealedTiles.Count + hand.Melds.Sum(m => m.TileIds.Count);
                    // Kong-aware draw gate (BotTurnHarness.PreDrawTileCount): a K-kong seat sits
                    // at 13+K; the old flat `== 13` spun the harness to its step guard.
                    if (totalTiles == _TestHarness.BotTurnHarness.PreDrawTileCount(hand))
                    {
                        ChangshaGameStateMachine.DrawTile(state);
                        if (state.Phase == ChangshaPhase.WallExhausted) break;
                        continue;
                    }
                    // Terminate the hand (not `break`: a bare break only exits the switch and
                    // re-enters the while, spinning to the step guard). Unreachable post-gate.
                    if (hand.ConcealedTiles.Count == 0) return;

                    var action = strategies[seat].DecideAction(state, seat);
                    switch (action.Type)
                    {
                        case BotActionType.DeclareWin:
                            ChangshaGameStateMachine.DeclareSelfDrawWin(state, seat);
                            break;
                        case BotActionType.DeclareConcealedKong:
                            ChangshaGameStateMachine.DeclareConcealedKong(state, seat, action.LogicalTile!.Value);
                            break;
                        case BotActionType.DeclareAddedKong:
                            ChangshaGameStateMachine.DeclareAddedKong(state, seat, action.TileId!.Value);
                            break;
                        case BotActionType.Discard:
                            ChangshaGameStateMachine.Discard(state, seat, action.TileId!.Value);
                            break;
                        default:
                            ChangshaGameStateMachine.Discard(state, seat, hand.ConcealedTiles[^1]);
                            break;
                    }
                    break;
                }
                case ChangshaPhase.AwaitingClaim:
                {
                    var window = state.ClaimWindow!;
                    var claimerSeat = -1;
                    TableClaimType? claimType = null;

                    foreach (var opp in window.Opportunities.OrderByDescending(o => o.Priority))
                    {
                        var decision = strategies[opp.SeatIndex].DecideAction(state, opp.SeatIndex);
                        if (decision.Type == BotActionType.Claim && decision.ClaimType.HasValue)
                        {
                            claimerSeat = opp.SeatIndex;
                            claimType = decision.ClaimType.Value;
                            break;
                        }
                    }

                    if (claimerSeat >= 0 && claimType.HasValue)
                        ChangshaGameStateMachine.ResolveClaim(state, claimerSeat, claimType.Value);
                    else
                        ChangshaGameStateMachine.PassClaim(state);
                    break;
                }
                case ChangshaPhase.WallExhausted:
                    ChangshaGameStateMachine.HandleWallExhausted(state);
                    break;
                case ChangshaPhase.Scoring:
                    ChangshaGameStateMachine.Score(state);
                    break;
                case ChangshaPhase.EndHand:
                    return;
                default:
                    throw new InvalidOperationException(
                        $"Unexpected phase {state.Phase} during GameCompletionTests hand run.");
            }
        }

        throw new InvalidOperationException(
            $"GameCompletionTests hand did not terminate within {MaxStepsPerHand} steps " +
            $"(Phase={state.Phase}). Possible infinite loop in the bot strategy chain.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  Reflection probes for Bishop's Phase J Wave 2 contract
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve the <see cref="ChangshaPhase.GameComplete"/> enum value at
    /// runtime. Fails RED with a contract message when Bishop hasn't yet
    /// added the value.
    /// </summary>
    internal static ChangshaPhase ResolveGameCompletePhase()
    {
        var names = Enum.GetNames(typeof(ChangshaPhase));
        if (!names.Contains("GameComplete"))
        {
            throw new InvalidOperationException(
                "ChangshaPhase.GameComplete enum value not defined — Bishop owes the " +
                "Phase J Wave 2 contract. The new phase indicates the configured MaxHands " +
                "cap has been reached and no further hands will start. " +
                $"Current ChangshaPhase values: [{string.Join(",", names)}].");
        }
        return (ChangshaPhase)Enum.Parse(typeof(ChangshaPhase), "GameComplete");
    }

    /// <summary>
    /// Read <c>ChangshaGameState.MaxHands</c> by reflection. Returns null when
    /// the property is missing so the caller can fail RED with a precise
    /// contract message.
    /// </summary>
    internal static int? GetMaxHands(ChangshaGameState state)
    {
        var prop = typeof(ChangshaGameState).GetProperty("MaxHands",
            BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || prop.PropertyType != typeof(int)) return null;
        return (int?)prop.GetValue(state);
    }

    /// <summary>
    /// Write <c>ChangshaGameState.MaxHands</c> by reflection. Throws with a
    /// precise contract message when the property is missing or read-only,
    /// so the test fails RED at the line that owns the gap.
    /// </summary>
    internal static void TrySetMaxHands(ChangshaGameState state, int value)
    {
        var prop = typeof(ChangshaGameState).GetProperty("MaxHands",
            BindingFlags.Public | BindingFlags.Instance);
        if (prop is null)
        {
            throw new InvalidOperationException(
                "ChangshaGameState.MaxHands property not defined — Bishop owes the " +
                "Phase J Wave 2 contract. Custom-cap callers (autotable, tests) need a " +
                "writable surface to override the default before the first hand starts.");
        }
        if (!prop.CanWrite)
        {
            throw new InvalidOperationException(
                "ChangshaGameState.MaxHands exists but is read-only — Bishop's Phase J Wave 2 " +
                "contract must include a write path (or factory init) so callers can override " +
                "the default. The wave brief asks for 'Start with MaxHands=2'.");
        }
        prop.SetValue(state, value);
    }

    /// <summary>
    /// Read <c>ChangshaGameState.IsGameComplete</c> by reflection. Defaults to
    /// checking <see cref="ChangshaGameState.Phase"/> against the resolved
    /// GameComplete phase when the flag itself isn't surfaced — minimises
    /// noisy contract messages when Bishop ships the phase but not the
    /// derived bool.
    /// </summary>
    internal static bool GetIsGameComplete(ChangshaGameState state)
    {
        var prop = typeof(ChangshaGameState).GetProperty("IsGameComplete",
            BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || prop.PropertyType != typeof(bool))
        {
            // Fall back to phase-equality semantics.
            return state.Phase == ResolveGameCompletePhase();
        }
        return (bool)(prop.GetValue(state) ?? false);
    }
}
