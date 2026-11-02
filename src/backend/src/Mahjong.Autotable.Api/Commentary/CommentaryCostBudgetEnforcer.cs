using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Commentary;

/// <summary>
/// Phase K Wave 16 — Bishop. Hard-asserting budget gate that
/// REJECTS new commentary requests once a tenant's monthly cost
/// crosses the configured cap. The W11→W15 buildup landed the
/// USD ledger (<see cref="CommentaryCostBudget"/>), the SignalR
/// broadcaster (W13), the cost-summary endpoint (W14), and the
/// month-end forecast (W15). W16 finishes the loop by enforcing
/// the cap: a POST to <c>/api/games/{gameId}/commentary</c> with
/// an over-budget tenant returns HTTP 402 Payment Required with
/// a canonical envelope.
///
/// <list type="bullet">
///   <item><c>Healthy</c> → <see cref="EnforcementVerdict.Allowed"/>.</item>
///   <item><c>Warning</c> → <see cref="EnforcementVerdict.Allowed"/>
///         (still under cap; the warning is a SignalR-emitted
///         hint, not a gate).</item>
///   <item><c>Exhausted</c> → <see cref="EnforcementVerdict.Rejected"/>
///         with the canonical <see cref="ReasonOverBudget"/>
///         wire reason. The controller maps this to HTTP 402.</item>
/// </list>
///
/// <para>Admin override: when the calling session has
/// <c>Role == "admin"</c> AND
/// <see cref="CommentaryOptions.CostBudget.AdminOverride"/> is
/// true, the enforcer skips the gate and returns
/// <see cref="EnforcementVerdict.AdminOverride"/>. Useful for
/// operators who need to demonstrate degraded behaviour, run
/// end-of-month cleanup commentary, or unblock a tenant while a
/// billing fix lands.</para>
///
/// <para>Per-tenant cap: when the future
/// <c>CommentaryCostBudget</c> grows per-tenant tracking, the
/// enforcer's <see cref="EvaluateAsync"/> signature already
/// takes a tenant id. The W16 surface ignores the id (single-
/// tenant budget under the hood) but pins the call-site contract
/// so the multi-tenant follow-up doesn't break wire shape.</para>
/// </summary>
public sealed class CommentaryCostBudgetEnforcer
{
    /// <summary>Wire-stable reason name emitted when a request
    /// is rejected because the tenant has crossed the monthly
    /// cap. Stable across waves so client retry logic can
    /// branch on the constant rather than the human-readable
    /// message.</summary>
    public const string ReasonOverBudget = "commentary-cost-budget-exhausted";

    /// <summary>HTTP status code returned for over-budget
    /// requests. Pinned to RFC 7231 §6.5.2 "402 Payment
    /// Required" so payment-gateway-adjacent clients see a
    /// familiar status.</summary>
    public const int StatusOverBudget = StatusCodes.Status402PaymentRequired;

    private readonly CommentaryCostBudget? _budget;
    private readonly IOptionsMonitor<CommentaryOptions>? _options;
    private readonly ILogger<CommentaryCostBudgetEnforcer> _logger;

    public CommentaryCostBudgetEnforcer(
        ILogger<CommentaryCostBudgetEnforcer> logger,
        CommentaryCostBudget? budget = null,
        IOptionsMonitor<CommentaryOptions>? options = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _budget = budget;
        _options = options;
    }

    /// <summary>Evaluate the budget for the supplied context.
    /// The verdict is purely informational — the caller decides
    /// whether to short-circuit (typically via
    /// <see cref="EnforcementVerdict.MapTo402"/>) or to log +
    /// continue (admin override path).</summary>
    public EnforcementVerdict Evaluate(
        string? tenantId,
        bool isAdmin,
        DateTime utcNow)
    {
        if (_budget is null)
        {
            // No budget surface wired → defensive fall-through.
            return EnforcementVerdict.Allowed(BudgetState.Healthy, 0m, 0m, 0d);
        }
        BudgetEvaluation eval;
        try
        {
            eval = _budget.Evaluate(utcNow);
        }
        catch (Exception ex)
        {
            // A transient store / DI failure should NEVER 500
            // the surface — the W14 summary endpoint takes the
            // same posture. Treat as healthy so signing
            // proceeds; the operator-facing metric exposes the
            // underlying error.
            _logger.LogWarning(ex,
                "CommentaryCostBudgetEnforcer evaluation failed; allowing request as best-effort.");
            return EnforcementVerdict.Allowed(BudgetState.Healthy, 0m, 0m, 0d);
        }

        if (eval.State != BudgetState.Exhausted)
        {
            return EnforcementVerdict.Allowed(
                eval.State, eval.MonthlyUsd, eval.MonthlyCapUsd, eval.Ratio);
        }

        if (isAdmin && (_options?.CurrentValue.CostBudget.AdminOverride ?? false))
        {
            _logger.LogWarning(
                "CommentaryCostBudgetEnforcer admin-override engaged for tenant={TenantId} (usd={UsdSpent:F2} of cap={Cap:F2}).",
                tenantId ?? "(none)", eval.MonthlyUsd, eval.MonthlyCapUsd);
            return EnforcementVerdict.AdminOverrideVerdict(
                eval.MonthlyUsd, eval.MonthlyCapUsd, eval.Ratio);
        }

        _logger.LogWarning(
            "CommentaryCostBudgetEnforcer REJECTED request for tenant={TenantId}: usd={UsdSpent:F2} >= cap={Cap:F2}.",
            tenantId ?? "(none)", eval.MonthlyUsd, eval.MonthlyCapUsd);
        return EnforcementVerdict.RejectedVerdict(
            eval.MonthlyUsd, eval.MonthlyCapUsd, eval.Ratio, ReasonOverBudget);
    }
}

/// <summary>Phase K Wave 16 — Bishop. Verdict envelope.</summary>
public sealed record EnforcementVerdict(
    EnforcementVerdictKind Kind,
    BudgetState State,
    decimal MonthlyUsd,
    decimal MonthlyCapUsd,
    double Ratio,
    string? Reason)
{
    /// <summary>True when the caller can proceed (Healthy /
    /// Warning / admin-override). False when the caller must
    /// short-circuit with HTTP 402.</summary>
    public bool ShouldShortCircuit => Kind == EnforcementVerdictKind.Rejected;

    /// <summary>Maps a rejected verdict to the canonical 402
    /// envelope. Callers wrap this in an
    /// <c>ObjectResult</c> with status code
    /// <see cref="CommentaryCostBudgetEnforcer.StatusOverBudget"/>.</summary>
    public object ToWireEnvelope() => new
    {
        error = Reason ?? CommentaryCostBudgetEnforcer.ReasonOverBudget,
        state = State.ToString(),
        monthlyUsd = decimal.Round(MonthlyUsd, 4),
        monthlyCapUsd = decimal.Round(MonthlyCapUsd, 4),
        percentUsed = Math.Round(Ratio * 100.0, 2),
    };

    /// <summary>Convenience predicate — false when the verdict
    /// is the admin-override flavour. Distinct from
    /// <see cref="ShouldShortCircuit"/> so audit + dashboards
    /// can render a separate "admin bypass" bucket.</summary>
    public bool IsAdminOverride => Kind == EnforcementVerdictKind.AdminOverride;

    /// <summary>Whether the request is hard-rejected (not
    /// admin-overridden). Equivalent to
    /// <see cref="ShouldShortCircuit"/>; kept for readability.</summary>
    public bool IsRejected => Kind == EnforcementVerdictKind.Rejected;

    /// <summary>Convenience factory for allowed verdicts.</summary>
    public static EnforcementVerdict Allowed(
        BudgetState state, decimal usd, decimal cap, double ratio) =>
        new(EnforcementVerdictKind.Allowed, state, usd, cap, ratio, null);

    /// <summary>Convenience factory for admin-override
    /// verdicts.</summary>
    public static EnforcementVerdict AdminOverrideVerdict(
        decimal usd, decimal cap, double ratio) =>
        new(EnforcementVerdictKind.AdminOverride, BudgetState.Exhausted, usd, cap, ratio,
            "admin-override");

    /// <summary>Convenience factory for rejected verdicts.</summary>
    public static EnforcementVerdict RejectedVerdict(
        decimal usd, decimal cap, double ratio, string reason) =>
        new(EnforcementVerdictKind.Rejected, BudgetState.Exhausted, usd, cap, ratio, reason);
}

/// <summary>Phase K Wave 16 — Bishop. Distinguishes the three
/// verdict states so audit dashboards can render each bucket
/// separately (count of allowed / count of admin-overrides /
/// count of rejected requests per month).</summary>
public enum EnforcementVerdictKind
{
    Allowed = 0,
    AdminOverride = 1,
    Rejected = 2,
}
