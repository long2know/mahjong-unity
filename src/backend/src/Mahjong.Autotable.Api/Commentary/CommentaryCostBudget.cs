using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Commentary;

/// <summary>
/// Phase K Wave 12 — Bishop. LLM cost-budget gate.
///
/// <para>Layers a USD ledger on top of the existing
/// <see cref="ICommentaryUsageMeter"/> monthly-token counter.
/// Computes <c>usdSpent = MonthlyTokens / TokensPerDollar</c>
/// and reports three states:</para>
/// <list type="bullet">
///   <item><c>BudgetState.Healthy</c> — under the warning
///         threshold. Generation proceeds normally.</item>
///   <item><c>BudgetState.Warning</c> — between warn-threshold
///         (default 80%) and the cap. Generation proceeds but
///         the surface SHOULD emit a "near-cap" event so
///         operators can react.</item>
///   <item><c>BudgetState.Exhausted</c> — at or over the cap.
///         The runtime is expected to swap the active generator
///         for the stub (see <see cref="StubCommentaryGenerator"/>)
///         until the next month rolls over.</item>
/// </list>
///
/// <para>The class is intentionally a thin computation seam — it
/// exposes <see cref="Evaluate"/> + a couple of pure helpers so
/// callers can decide whether to act. It does NOT mutate the
/// usage meter or call into SignalR / audit; those are the
/// responsibility of whichever surface invokes
/// <see cref="Evaluate"/> (currently <see cref="CommentaryController"/>
/// and the future scheduler).</para>
/// </summary>
public sealed class CommentaryCostBudget
{
    private readonly IOptionsMonitor<CommentaryOptions> _options;
    private readonly ICommentaryUsageMeter _meter;
    private readonly ILogger<CommentaryCostBudget> _logger;
    private readonly CommentaryCostBroadcaster? _broadcaster;
    private long _warningEmittedForMonth;
    private long _exhaustedEmittedForMonth;

    public CommentaryCostBudget(
        IOptionsMonitor<CommentaryOptions> options,
        ICommentaryUsageMeter meter,
        ILogger<CommentaryCostBudget> logger,
        CommentaryCostBroadcaster? broadcaster = null)
    {
        _options = options;
        _meter = meter;
        _logger = logger;
        _broadcaster = broadcaster;
    }

    /// <summary>
    /// Computes the current budget state. <paramref name="utcNow"/>
    /// is the clock the meter consults; tests pin it via the
    /// optional argument.
    /// </summary>
    public BudgetEvaluation Evaluate(DateTime utcNow)
    {
        var opts = _options.CurrentValue.CostBudget;
        var monthlyTokens = _meter.MonthlyTokens(utcNow);
        var tokensPerDollar = opts.TokensPerDollar > 0
            ? opts.TokensPerDollar
            : 200_000L;
        var usdSpent = (decimal)monthlyTokens / tokensPerDollar;
        var cap = opts.MonthlyCapUsd;
        if (cap <= 0m)
        {
            return new BudgetEvaluation(
                BudgetState.Healthy,
                usdSpent,
                0m,
                0d,
                monthlyTokens,
                tokensPerDollar);
        }
        var ratio = (double)(usdSpent / cap);
        var state = ratio switch
        {
            >= 1.0 => BudgetState.Exhausted,
            _ when ratio >= opts.WarnThreshold => BudgetState.Warning,
            _ => BudgetState.Healthy,
        };
        // Phase K Wave 12 — Bishop. One-shot per-month log so the
        // operator knows when the surface flips, but we don't spam
        // every Evaluate() call. The "month key" pins the message
        // to a single calendar month; the next month resets.
        //
        // Phase K Wave 13 — Bishop. The same one-shot gate now also
        // fans out a SignalR envelope to the admin commentary-cost
        // hub so operator dashboards react in realtime. The
        // broadcaster is optional — unit-test harnesses that
        // construct the budget without DI skip it cleanly.
        var monthKey = (utcNow.Year * 100L) + utcNow.Month;
        var evaluation = new BudgetEvaluation(state, usdSpent, cap, ratio, monthlyTokens, tokensPerDollar);
        if (state == BudgetState.Warning
            && System.Threading.Interlocked.Exchange(ref _warningEmittedForMonth, monthKey) != monthKey)
        {
            _logger.LogWarning(
                "Commentary cost budget WARNING: monthlyUsd={UsdSpent:F2} of cap={Cap:F2} ({Ratio:P0}); model={Model}.",
                usdSpent, cap, ratio, _options.CurrentValue.Model);
            FireBroadcast(_broadcaster?.BroadcastWarningAsync(evaluation, _options.CurrentValue.Model));
        }
        if (state == BudgetState.Exhausted
            && System.Threading.Interlocked.Exchange(ref _exhaustedEmittedForMonth, monthKey) != monthKey)
        {
            _logger.LogError(
                "Commentary cost budget EXHAUSTED: monthlyUsd={UsdSpent:F2} reached cap={Cap:F2}; switching to stub generator for the remainder of the month.",
                usdSpent, cap);
            FireBroadcast(_broadcaster?.BroadcastCapReachedAsync(evaluation, _options.CurrentValue.Model));
        }
        return evaluation;
    }

    /// <summary>
    /// Phase K Wave 13 — Bishop. Fire-and-forget the broadcaster
    /// task so the synchronous <see cref="Evaluate"/> path doesn't
    /// block on the SignalR send. Failures are already swallowed
    /// inside the broadcaster; this wrapper observes the task to
    /// avoid an unobserved-task exception finalizer hit.
    /// </summary>
    private static void FireBroadcast(Task? task)
    {
        if (task is null) return;
        _ = task.ContinueWith(static t =>
        {
            // Touch the exception so the GC unobserved-task hook
            // doesn't elevate it. The broadcaster already logs.
            _ = t.Exception;
        }, TaskScheduler.Default);
    }
}

/// <summary>Phase K Wave 12 — Bishop. Cost-budget verdict.</summary>
public readonly record struct BudgetEvaluation(
    BudgetState State,
    decimal MonthlyUsd,
    decimal MonthlyCapUsd,
    double Ratio,
    long MonthlyTokens,
    long TokensPerDollar);

/// <summary>Phase K Wave 12 — Bishop. Three-valued state.</summary>
public enum BudgetState
{
    /// <summary>Under the warning threshold. Generation proceeds.</summary>
    Healthy = 0,
    /// <summary>Between warn-threshold and the cap. Surface SHOULD
    /// emit a near-cap event but generation still proceeds.</summary>
    Warning = 1,
    /// <summary>At or over the cap. Runtime is expected to swap to
    /// the stub generator until the next month rolls over.</summary>
    Exhausted = 2,
}
