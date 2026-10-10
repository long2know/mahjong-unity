namespace Mahjong.Autotable.Api.Changsha.Bot;

using Microsoft.Extensions.Logging;

/// <summary>
/// Phase F resolver for <see cref="IChangshaBotStrategy"/>. The runtime asks
/// <c>ChangshaBotEngine.Resolve("easy"|"medium"|"hard")</c> on every decision point
/// and gets back a singleton strategy instance. Unknown values fall back to Medium —
/// matches Stephen's UX rule that an unrecognised <c>?bots=hard</c> shouldn't break
/// the game.
/// </summary>
/// <remarks>
/// Strategies are stateless across hands, so a single instance per difficulty is
/// reused for the lifetime of the process. This keeps allocations zero on the hot
/// path during a bot's turn.
/// </remarks>
public static class ChangshaBotEngine
{
    private static readonly IChangshaBotStrategy EasyInstance = new EasyStrategy();
    private static readonly IChangshaBotStrategy MediumInstance = new MediumStrategy();
    private static readonly IChangshaBotStrategy HardInstance = new HardStrategy();
    private static readonly IChangshaBotStrategy MasterInstance = new MasterStrategy();

    /// <summary>
    /// Resolves a difficulty string to its strategy. Case-insensitive; whitespace
    /// trimmed. Empty / null / unrecognised strings → Medium (the default).
    /// </summary>
    public static IChangshaBotStrategy Resolve(string? difficulty)
    {
        if (string.IsNullOrWhiteSpace(difficulty))
            return MediumInstance;

        return difficulty.Trim().ToLowerInvariant() switch
        {
            "easy" => EasyInstance,
            "medium" => MediumInstance,
            "hard" => HardInstance,
            "master" => MasterInstance,
            _ => MediumInstance
        };
    }

    /// <summary>The default strategy (Medium) — exposed for unit testing.</summary>
    public static IChangshaBotStrategy Default => MediumInstance;

    /// <summary>
    /// Phase H Wave 1 — race a synchronous bot decision against a timeout. If the
    /// decision returns within <paramref name="timeoutMs"/> the result is used;
    /// otherwise <paramref name="safeDefault"/> is invoked and the slow task is
    /// allowed to run to completion in the background (its result is discarded —
    /// strategies are pure / side-effect free).
    /// </summary>
    /// <param name="decision">
    /// The bot decision to invoke (typically a closure over
    /// <see cref="IChangshaBotStrategy.DecideAction"/>). Executed on the thread pool.
    /// </param>
    /// <param name="timeoutMs">
    /// Per-decision budget. <c>0</c> or negative disables the timeout and invokes
    /// <paramref name="decision"/> inline on the calling thread (legacy behaviour).
    /// </param>
    /// <param name="safeDefault">
    /// Factory for the fallback action — supplied by the caller because the safe
    /// default depends on the call site (turn → discard, claim window → pass).
    /// Invoked only on timeout; never throws.
    /// </param>
    /// <param name="logger">Optional logger; a warning is emitted on timeout.</param>
    /// <param name="ct">
    /// Lifecycle cancellation. If cancelled, the decision task is abandoned and
    /// <paramref name="safeDefault"/> is invoked.
    /// </param>
    public static async Task<BotAction> DecideActionWithTimeoutAsync(
        Func<BotAction> decision,
        int timeoutMs,
        Func<BotAction> safeDefault,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(safeDefault);

        if (timeoutMs <= 0)
        {
            return decision();
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var decisionTask = Task.Run(decision, ct);
        await Task.WhenAny(decisionTask, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);

        // #124 — the strategy's outcome is honoured ONLY when it demonstrably arrived within the
        // wall-clock budget. Task.WhenAny ordering is NOT authoritative under thread-pool
        // starvation: a synchronous (Thread.Sleep-style) strategy hogs a pool thread while the
        // Task.Delay timeout's own completion is starved past the (slower) strategy, so WhenAny
        // can surface a late strategy result as if it were in-budget — leaking e.g. a false Hu
        // where the safe-default is required. The Stopwatch is immune to that scheduling inversion.
        if (stopwatch.ElapsedMilliseconds <= timeoutMs)
        {
            if (decisionTask.IsCompletedSuccessfully)
            {
                return decisionTask.Result;
            }

            if (decisionTask.IsCompleted && !decisionTask.IsCompletedSuccessfully)
            {
                return decisionTask.GetAwaiter().GetResult();
            }
        }

        logger?.LogWarning(
            "Bot decision timed out after {TimeoutMs}ms; using safe-default action.",
            timeoutMs);

        // Let the slow task drain in the background; observe its exception so the
        // task is not flagged as unhandled. Strategies are pure so the result is
        // safely discardable.
        _ = decisionTask.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return safeDefault();
    }

    /// <summary>
    /// Phase J Wave 10 — explainable variant of
    /// <see cref="DecideActionWithTimeoutAsync"/>. Same timeout / safe-default
    /// semantics, but threads a <see cref="BotDecision"/> all the way through
    /// so the runtime can capture reasoning + score for the audit replay.
    /// On timeout the caller's <paramref name="safeDefault"/> factory is
    /// invoked; the safe default typically wraps a stable
    /// <see cref="BotAction"/> with empty reasoning + score 0.
    /// </summary>
    public static async Task<BotDecision> DecideWithReasoningWithTimeoutAsync(
        Func<BotDecision> decision,
        int timeoutMs,
        Func<BotDecision> safeDefault,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(safeDefault);

        if (timeoutMs <= 0)
        {
            return decision();
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var decisionTask = Task.Run(decision, ct);
        await Task.WhenAny(decisionTask, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);

        // #124 — see DecideActionWithTimeoutAsync: honour the strategy's outcome ONLY when it
        // demonstrably arrived within the wall-clock budget, so a late (thread-pool-starved)
        // strategy answer can never be surfaced in place of the safe-default (the Postgres-cell
        // false-Hu flake). The Stopwatch is authoritative where Task.WhenAny ordering is not.
        if (stopwatch.ElapsedMilliseconds <= timeoutMs)
        {
            if (decisionTask.IsCompletedSuccessfully)
            {
                return decisionTask.Result;
            }

            if (decisionTask.IsCompleted && !decisionTask.IsCompletedSuccessfully)
            {
                return decisionTask.GetAwaiter().GetResult();
            }
        }

        logger?.LogWarning(
            "Bot decision timed out after {TimeoutMs}ms; using safe-default decision.",
            timeoutMs);

        _ = decisionTask.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return safeDefault();
    }
}
