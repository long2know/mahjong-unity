using Microsoft.AspNetCore.SignalR;

namespace Mahjong.Autotable.Api.Commentary;

/// <summary>
/// Phase K Wave 13 — Bishop. Admin-only SignalR hub that surfaces
/// commentary LLM cost-budget state changes to operator dashboards.
/// Clients join via <see cref="JoinAdminChannel"/> after a normal
/// SignalR handshake; the join is admin-gated upstream by the cookie
/// resolver on the negotiate request (see Program.cs).
///
/// <para>The hub emits two envelopes:
/// <list type="bullet">
///   <item><c>CommentaryCostWarning</c> — fired the first time the
///         cost evaluation flips to <see cref="BudgetState.Warning"/>
///         (default 80% of cap) within a calendar month.</item>
///   <item><c>CommentaryCostCapReached</c> — fired the first time
///         the evaluation flips to <see cref="BudgetState.Exhausted"/>
///         within a calendar month. Subsequent calls within the
///         same month are suppressed by the one-shot gate inside
///         <see cref="CommentaryCostBudget"/>.</item>
/// </list></para>
///
/// <para>Mapped at <c>/hubs/admin/commentary-cost</c>. See
/// <c>docs/commentary-llm.md §5 "Realtime warnings"</c>.</para>
/// </summary>
public sealed class CommentaryCostAdminHub : Hub
{
    /// <summary>Group name for the admin cost channel — every admin
    /// client subscribes to this single broadcast group.</summary>
    public const string AdminGroup = "commentary:cost:admin";

    /// <summary>SignalR method invoked when the cost evaluation
    /// crosses the warning threshold for the first time in a month.</summary>
    public const string WarningEvent = "CommentaryCostWarning";

    /// <summary>SignalR method invoked when the cost evaluation
    /// reaches the monthly cap for the first time in a month.</summary>
    public const string CapReachedEvent = "CommentaryCostCapReached";

    public Task JoinAdminChannel() =>
        Groups.AddToGroupAsync(Context.ConnectionId, AdminGroup);

    public Task LeaveAdminChannel() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, AdminGroup);
}

/// <summary>
/// Phase K Wave 13 — Bishop. Broadcaster utility that
/// <see cref="CommentaryCostBudget"/> calls when the cost evaluation
/// flips to Warning or Exhausted. Wraps the
/// <see cref="IHubContext{THub}"/> abstraction so the budget surface
/// stays mockable in unit tests.
///
/// <para>The broadcaster is best-effort — failures are caught + logged
/// so a transient SignalR fault never bubbles up to the LLM
/// generation path. See <c>docs/commentary-llm.md §5</c>.</para>
/// </summary>
public sealed class CommentaryCostBroadcaster
{
    private readonly IHubContext<CommentaryCostAdminHub> _hub;
    private readonly ILogger<CommentaryCostBroadcaster> _logger;

    public CommentaryCostBroadcaster(
        IHubContext<CommentaryCostAdminHub> hub,
        ILogger<CommentaryCostBroadcaster> logger)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Broadcast the 80% warning envelope. <paramref name="evaluation"/>
    /// is the current cost snapshot; the wire shape exposes
    /// <c>currentCost</c>, <c>budget</c>, <c>percentUsed</c> + the
    /// active LLM model identifier so dashboards can show context
    /// without re-querying the metrics endpoint.
    /// </summary>
    public Task BroadcastWarningAsync(
        BudgetEvaluation evaluation,
        string model,
        CancellationToken ct = default) =>
        BroadcastInternalAsync(
            CommentaryCostAdminHub.WarningEvent, evaluation, model, ct);

    /// <summary>
    /// Broadcast the cap-reached (100%) envelope.
    /// </summary>
    public Task BroadcastCapReachedAsync(
        BudgetEvaluation evaluation,
        string model,
        CancellationToken ct = default) =>
        BroadcastInternalAsync(
            CommentaryCostAdminHub.CapReachedEvent, evaluation, model, ct);

    private async Task BroadcastInternalAsync(
        string method,
        BudgetEvaluation evaluation,
        string model,
        CancellationToken ct)
    {
        try
        {
            await _hub.Clients
                .Group(CommentaryCostAdminHub.AdminGroup)
                .SendAsync(method, new
                {
                    currentCost = decimal.Round(evaluation.MonthlyUsd, 4),
                    budget = decimal.Round(evaluation.MonthlyCapUsd, 4),
                    percentUsed = Math.Round(evaluation.Ratio * 100.0, 2),
                    monthlyTokens = evaluation.MonthlyTokens,
                    model = model ?? "unknown",
                    state = evaluation.State.ToString(),
                    at = DateTimeOffset.UtcNow,
                }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "CommentaryCostBroadcaster send failed (non-fatal; method={Method}).",
                method);
        }
    }
}
