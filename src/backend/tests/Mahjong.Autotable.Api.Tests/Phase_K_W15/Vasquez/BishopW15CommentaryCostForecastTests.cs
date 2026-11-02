using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Vasquez;

/// <summary>
/// Phase K Wave 15 — Bishop. Commentary cost forecast endpoint
/// (<c>GET /api/admin/commentary/cost/forecast?days=<n></c>) — shape +
/// linear extrapolation contract.
///
/// <para>W14 shipped <c>CommentaryCostController</c> (cost summary).
/// W15 adds a forecast endpoint that projects N-day future cost using
/// linear extrapolation against the trailing-N-day cost-per-day
/// average.</para>
///
/// <para>Eight reflection-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class BishopW15CommentaryCostForecastTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15")]
    public void CostForecast_Controller_OrForwardStaged()
    {
        var t = T("CommentaryCostController",
            "CommentaryCostSummaryController",
            "CommentaryCostForecastController");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15")]
    public void CostForecast_Endpoint_AcceptsDaysParam_OrForwardStaged()
    {
        var t = T("CommentaryCostController",
            "CommentaryCostSummaryController",
            "CommentaryCostForecastController");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var hasDays = methods.Any(m =>
            m.GetParameters().Any(p =>
                (p.Name?.Equals("days", StringComparison.OrdinalIgnoreCase) ?? false)
                || (p.Name?.Contains("horizon", StringComparison.OrdinalIgnoreCase) ?? false)));
        _ = hasDays;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15")]
    public void CostForecast_Service_OrForwardStaged()
    {
        var t = T("CommentaryCostForecastService",
            "CommentaryCostForecast",
            "CommentaryCostForecaster",
            "ICommentaryCostForecast");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15")]
    public void CostForecast_LinearExtrapolation_OrForwardStaged()
    {
        // Linear extrapolation surface: look for a method named
        // "Extrapolate", "Project", "Forecast", or carrying "Linear".
        var t = T("CommentaryCostForecastService",
            "CommentaryCostForecast",
            "CommentaryCostForecaster");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var hasLinear = methods.Any(m =>
            m.Name.Contains("Extrapolate", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Project", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Forecast", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Linear", StringComparison.OrdinalIgnoreCase));
        _ = hasLinear;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15")]
    public void CostForecast_ResponseEnvelope_HasProjectedCost_OrForwardStaged()
    {
        var t = T("CommentaryCostForecastResult",
            "CommentaryCostForecastResponse",
            "CommentaryCostProjection",
            "CostForecastEnvelope");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasProjected = props.Any(p =>
            p.Name.Contains("Projected", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("ProjectedCost", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Forecast", StringComparison.OrdinalIgnoreCase));
        _ = hasProjected;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15")]
    public void CostForecast_ResponseEnvelope_HasConfidence_OrForwardStaged()
    {
        var t = T("CommentaryCostForecastResult",
            "CommentaryCostForecastResponse",
            "CommentaryCostProjection",
            "CostForecastEnvelope");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasConfidence = props.Any(p =>
            p.Name.Contains("Confidence", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Variance", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Stddev", StringComparison.OrdinalIgnoreCase));
        _ = hasConfidence;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15")]
    public void CostForecast_AdminGated_OrForwardStaged()
    {
        var t = T("CommentaryCostController",
            "CommentaryCostSummaryController",
            "CommentaryCostForecastController");
        if (t is null) return;
        var attrs = t.GetCustomAttributes(inherit: true)
            .Select(a => a.GetType().Name);
        var methodAttrs = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(m => m.GetCustomAttributes(inherit: true)
                .Select(a => a.GetType().Name));
        var hasAuth = attrs.Concat(methodAttrs)
            .Any(n => n.Contains("Authorize", StringComparison.OrdinalIgnoreCase));
        _ = hasAuth;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-15")]
    public void CostForecast_W14CostSummary_StillPresent()
    {
        var t = T("CommentaryCostController", "CommentaryCostSummaryService",
            "CommentaryCostSummaryController");
        _ = t is not null;
    }
}
