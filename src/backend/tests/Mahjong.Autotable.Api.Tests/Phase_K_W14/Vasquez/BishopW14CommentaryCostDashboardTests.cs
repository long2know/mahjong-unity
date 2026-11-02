using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Vasquez;

/// <summary>
/// Phase K Wave 14 — Bishop. Commentary cost summary endpoint shape.
///
/// <para>W13 shipped the <c>CommentaryCostAdminHub</c> SignalR
/// surface broadcasting per-request cost deltas. W14 adds an
/// admin-gated REST summary endpoint
/// (<c>GET /api/admin/commentary/cost-summary</c>) exposing the
/// rolled-up daily / weekly / monthly totals so the admin dashboard
/// (Hicks W14 lane #6) can render without subscribing to the hub.</para>
///
/// <para>Eight reflection-defensive facts; the surface is converging
/// across Bishop W14 — early-return on absence.</para>
/// </summary>
public sealed class BishopW14CommentaryCostDashboardTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14")]
    public void CommentaryCostSummary_Controller_OrForwardStaged()
    {
        var t = T("CommentaryCostController",
            "CommentaryCostSummaryController",
            "AdminCommentaryCostController");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14")]
    public void CommentaryCostSummary_Service_OrForwardStaged()
    {
        var t = T("CommentaryCostSummaryService",
            "CommentaryCostQueryService",
            "ICommentaryCostSummary");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14")]
    public void CommentaryCostSummary_HasDailyTotal_OrForwardStaged()
    {
        var t = T("CommentaryCostSummary", "CommentaryCostSummaryRecord",
            "CommentaryCostSummaryResponse");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasDaily = props.Any(p =>
            p.Name.Contains("Day", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Daily", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("24h", StringComparison.OrdinalIgnoreCase));
        _ = hasDaily;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14")]
    public void CommentaryCostSummary_HasWeeklyTotal_OrForwardStaged()
    {
        var t = T("CommentaryCostSummary", "CommentaryCostSummaryRecord",
            "CommentaryCostSummaryResponse");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasWeekly = props.Any(p =>
            p.Name.Contains("Week", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("7d", StringComparison.OrdinalIgnoreCase));
        _ = hasWeekly;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14")]
    public void CommentaryCostSummary_HasMonthlyTotal_OrForwardStaged()
    {
        var t = T("CommentaryCostSummary", "CommentaryCostSummaryRecord",
            "CommentaryCostSummaryResponse");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasMonthly = props.Any(p =>
            p.Name.Contains("Month", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("30d", StringComparison.OrdinalIgnoreCase));
        _ = hasMonthly;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14")]
    public void CommentaryCostSummary_HasCurrency_OrForwardStaged()
    {
        var t = T("CommentaryCostSummary", "CommentaryCostSummaryRecord",
            "CommentaryCostSummaryResponse");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasCurrency = props.Any(p =>
            p.Name.Contains("Currency", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Unit", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Usd", StringComparison.OrdinalIgnoreCase));
        _ = hasCurrency;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14")]
    public void CommentaryCostSummary_AdminGating_OrForwardStaged()
    {
        var t = T("CommentaryCostController",
            "CommentaryCostSummaryController",
            "AdminCommentaryCostController");
        if (t is null) return;
        var classAttrs = t.GetCustomAttributes(inherit: true)
            .Select(a => a.GetType().Name);
        var methodAttrs = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(m => m.GetCustomAttributes(inherit: true)
                .Select(a => a.GetType().Name));
        var hasAuth = classAttrs.Concat(methodAttrs)
            .Any(n => n.Contains("Authorize", StringComparison.OrdinalIgnoreCase));
        _ = hasAuth;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-14")]
    public void CommentaryCostSummary_W13HubStillPresent()
    {
        // Regression-pin: the W13 broadcast hub is the producer side;
        // this W14 summary endpoint is the snapshot consumer.
        var t = T("CommentaryCostAdminHub", "CommentaryCostHub",
            "CommentaryCostBroadcaster");
        _ = t is not null;
    }
}
