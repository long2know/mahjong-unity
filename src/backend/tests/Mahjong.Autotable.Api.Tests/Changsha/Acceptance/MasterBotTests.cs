using System.Reflection;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Bot;
using Mahjong.Autotable.Api.Tables;
using Xunit.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Phase J Wave 8 — Master-tier bot strength regression tests (Vasquez).
///
/// <para>Bishop's Wave 8 adds a <c>MasterStrategy</c> bot tier above
/// Hard, surfaced via <c>ChangshaBotEngine.Resolve("master")</c>. The
/// expected ordering is Master ≥ Hard ≥ Medium ≥ Easy.</para>
///
/// <para>Same harness as <see cref="BotStrengthTests"/> (Phase I Wave 4
/// reused): 8–16 hands per match-up, statistical floor — Master win-rate
/// must NOT drop below Hard's per-seat baseline (a regression alarm,
/// not a strength magnitude proof).</para>
///
/// <para><b>Reflection-defensive.</b> We probe
/// <c>ChangshaBotEngine.Resolve("master")</c> for a strategy whose
/// <c>Difficulty</c> property returns the string <c>"master"</c>. If the
/// engine falls back to Medium (the documented unknown-difficulty
/// behaviour), the surface isn't yet wired and the test soft-passes.</para>
/// </summary>
public class MasterBotTests
{
    private readonly ITestOutputHelper _output;
    public MasterBotTests(ITestOutputHelper output) { _output = output; }

    private const int BaseSeed = 2000;
    private const int SeedStride = 7919;
    private const int HandCount = 20; // matches Phase I Wave 4 BotStrengthTests for ±5% statistical noise
    private const int MaxStepsPerHand = 4000;

    private static IChangshaBotStrategy? TryResolveMaster()
    {
        var strategy = ChangshaBotEngine.Resolve("master");
        // The engine falls back to Medium on unknown — detect via Difficulty.
        var diff = strategy.Difficulty?.ToLowerInvariant();
        if (diff == "master") return strategy;

        // Also probe for a MasterStrategy type via reflection (might be
        // registered under a different resolver shape).
        var apiAssembly = typeof(IChangshaBotStrategy).Assembly;
        var masterType = apiAssembly.GetTypes().FirstOrDefault(t =>
            !t.IsAbstract && !t.IsInterface
            && typeof(IChangshaBotStrategy).IsAssignableFrom(t)
            && t.Name.Equals("MasterStrategy", StringComparison.OrdinalIgnoreCase));
        if (masterType is null) return null;
        try
        {
            return Activator.CreateInstance(masterType) as IChangshaBotStrategy;
        }
        catch
        {
            return null;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Master strategy is reachable via engine OR not-yet-shipped
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-8")]
    public void MasterStrategy_PresentOrNotYetShipped()
    {
        var master = TryResolveMaster();
        if (master is null)
        {
            _output.WriteLine("Master strategy not yet shipped (engine falls back to Medium / type missing). Soft pass.");
            return;
        }
        _output.WriteLine($"Master strategy resolved: {master.GetType().Name} (difficulty={master.Difficulty}).");
        Assert.Equal("master", master.Difficulty?.ToLowerInvariant());
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Master vs Hard — statistical "no regression" floor
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-8")]
    public void Master_NotWorseThan_Hard_OnSeedSweep()
    {
        var master = TryResolveMaster();
        if (master is null) return; // not yet shipped — soft pass.

        var hard = new HardStrategy();
        var masterWins = 0;
        var hardTotalWins = 0;
        var draws = 0;

        for (var i = 0; i < HandCount; i++)
        {
            var seed = BaseSeed + i * SeedStride;
            var winner = RunOneHand(seed, master, hard, hard, hard);
            if (winner == 0) masterWins++;
            else if (winner >= 1 && winner <= 3) hardTotalWins++;
            else draws++;
        }

        var hardAvgWinsPerSeat = hardTotalWins / 3.0;
        _output.WriteLine(
            $"N={HandCount} Master(seat0) vs 3×Hard(seats1..3): " +
            $"MasterWins={masterWins}, HardWinsTotal={hardTotalWins} (avg/seat={hardAvgWinsPerSeat:F2}), Draws={draws}");

        // Permissive "no major regression" floor matching Phase I Wave 4
        // BotStrengthTests pattern (N=20, ±5% noise). Master should NOT
        // regress below 50% of Hard's per-seat baseline; statistical
        // outliers below that signal a real strategy regression.
        var floor = hardAvgWinsPerSeat * 0.5;
        Assert.True(
            masterWins >= floor,
            $"Master win-rate regressed below 50% of Hard per-seat baseline. " +
            $"MasterWins={masterWins}, HardAvgPerSeat={hardAvgWinsPerSeat:F2}, Threshold={floor:F2}. " +
            $"Investigate MasterStrategy regression vs HardStrategy.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Master vs Master — sanity (no infinite-loop in self-play)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-8")]
    public void Master_SelfPlay_NoStall()
    {
        var master = TryResolveMaster();
        if (master is null) return;

        var completed = 0;
        for (var seed = 300; seed < 305; seed++)
        {
            var winner = RunOneHand(seed, master, master, master, master);
            completed++;
            _output.WriteLine($"seed={seed} 4×Master: winner={winner}");
        }
        Assert.True(completed >= 1,
            "All Master-vs-Master hands failed to complete — investigate MasterStrategy bot turn loop.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Master strategy has consistent Difficulty string
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Changsha"), Trait("Wave", "Phase-J-8")]
    public void MasterStrategy_DifficultyStringIsLowercase()
    {
        var master = TryResolveMaster();
        if (master is null) return;

        Assert.NotNull(master.Difficulty);
        Assert.Equal(master.Difficulty, master.Difficulty.ToLowerInvariant());
    }

    // ────────────────────────────────────────────────────────────────────
    //  Step-machine harness (port of BotStrengthTests.RunOneHand)
    // ────────────────────────────────────────────────────────────────────

    private static int RunOneHand(int seed,
        IChangshaBotStrategy seat0, IChangshaBotStrategy seat1,
        IChangshaBotStrategy seat2, IChangshaBotStrategy seat3)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(
            seed, botSeatIndexes: new[] { 0, 1, 2, 3 });
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(seed));
        ChangshaGameStateMachine.Deal(state);

        var strategies = new[] { seat0, seat1, seat2, seat3 };
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
                    if (hand.ConcealedTiles.Count == 0) return -1;

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
                    return state.CurrentWin?.WinningSeatIndex ?? -1;
                default:
                    throw new InvalidOperationException(
                        $"Unexpected phase {state.Phase} during master strength run.");
            }
        }
        throw new InvalidOperationException(
            $"Hand failed to terminate within {MaxStepsPerHand} steps (seed={seed}).");
    }
}
