using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Bot;
using Mahjong.Autotable.Api.Tables;

namespace Mahjong.Autotable.Api.Tests.Changsha.Bots;

/// <summary>
/// #124 — regression coverage for <see cref="ChangshaBotEngine.DecideWithReasoningWithTimeoutAsync"/>
/// (and its non-reasoning sibling). The bot-decision timeout is a safety net: when a strategy
/// exceeds <c>BotDecisionTimeoutMs</c> the runtime MUST use the caller's safe-default (Pass in a
/// claim window) and NEVER surface the strategy's late answer — otherwise a slow "Claim Hu" leaks
/// through as a false Hu.
///
/// <para>The prior implementation decided this purely from <see cref="Task.WhenAny(Task[])"/>
/// ordering (<c>winner == decisionTask</c>). That is not authoritative under thread-pool
/// starvation: a synchronous (<see cref="Thread.Sleep(int)"/>-style) strategy hogs a pool thread
/// while the <see cref="Task.Delay(int)"/> timeout's own completion is starved past the (slower)
/// strategy, so <c>WhenAny</c> surfaces the late strategy result as if it were in-budget. That is
/// exactly the <c>Test (Postgres)</c> matrix flake
/// (<c>BotBehaviorTests.Bot_Timeout_DuringClaim_PassesNotFalseHu</c>: 1/5337, rerun-green). The fix
/// makes the timeout authoritative on the wall clock (a <see cref="System.Diagnostics.Stopwatch"/>),
/// which these tests pin.</para>
/// </summary>
public sealed class BotDecisionTimeoutWrapperTests
{
    private static BotDecision Pass() => BotDecision.FromAction(BotAction.Pass());
    private static BotDecision ClaimHu() => BotDecision.FromAction(BotAction.Claim(TableClaimType.Hu));

    [Fact, Trait("Category", "Changsha")]
    public async Task SlowStrategy_ExceedingBudget_YieldsSafeDefault_NeverItsLateResult()
    {
        // 500ms strategy vs 30ms budget — the safe-default Pass must win; the strategy's Claim(Hu)
        // must never surface. Deterministic: the gap is far larger than any scheduling jitter.
        var result = await ChangshaBotEngine.DecideWithReasoningWithTimeoutAsync(
            () => { Thread.Sleep(500); return ClaimHu(); },
            timeoutMs: 30,
            Pass);

        Assert.Equal(BotActionType.Pass, result.Action.Type);
    }

    [Fact, Trait("Category", "Changsha")]
    public async Task FastStrategy_WithinBudget_ReturnsStrategyResult()
    {
        // Opposite guard: an in-budget answer IS honoured — the wall-clock guard must not
        // spuriously fall back for a strategy that answered well within the timeout.
        var result = await ChangshaBotEngine.DecideWithReasoningWithTimeoutAsync(
            ClaimHu, // no delay
            timeoutMs: 5_000,
            Pass);

        Assert.Equal(BotActionType.Claim, result.Action.Type);
        Assert.Equal(TableClaimType.Hu, result.Action.ClaimType);
    }

    [Fact, Trait("Category", "Changsha")]
    public async Task UnderThreadPoolStarvation_LateStrategyResult_NeverSurfaces()
    {
        // Reproduce the #124 flake mechanism deterministically-enough to guard against regressions:
        // fire many concurrent timeout calls whose synchronous strategy hogs a pool thread for far
        // longer than the tiny budget. The concurrent sleepers starve the Task.Delay timeout
        // continuations, so Task.WhenAny ordering ALONE would occasionally surface a late Claim(Hu).
        // The wall-clock guard makes the safe-default Pass win EVERY time — a single Hu is a leak.
        const int iterations = 300;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var calls = Enumerable.Range(0, iterations).Select(_ =>
            ChangshaBotEngine.DecideWithReasoningWithTimeoutAsync(
                () => { Thread.Sleep(60); return ClaimHu(); },
                timeoutMs: 10,
                Pass,
                logger: null,
                ct: cts.Token));

        var results = await Task.WhenAll(calls);

        var leaks = results.Count(r => r.Action.Type != BotActionType.Pass);
        Assert.True(leaks == 0,
            $"{leaks}/{iterations} timeout calls surfaced a late strategy result instead of the " +
            $"safe-default Pass — the #124 thread-pool-starvation false-Hu leak.");
    }
}
