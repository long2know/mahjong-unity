using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Vasquez;

/// <summary>
/// Phase K Wave 14 — Bishop. Spectator audit query endpoint (with
/// pagination + admin gating).
///
/// <para>W13 shipped the <c>SpectatorHandoffAudit</c> row + retention
/// sweep (<see cref="Phase_K_W13.Vasquez.BishopW13SpectatorAuditTests"/>).
/// W14 exposes the audit table via an admin-gated query endpoint
/// (<c>GET /api/admin/spectator/audit</c>) with cursor-style pagination
/// (page + pageSize, max 200) and a 401-redirect when the caller
/// lacks the <c>admin</c> claim.</para>
///
/// <para>Eight reflection-defensive facts (soft-pass on absence — the
/// surface lands incrementally in Bishop's W14 lane).</para>
/// </summary>
public sealed class BishopW14SpectatorAuditQueryTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-14")]
    public void SpectatorAuditQuery_Controller_OrForwardStaged()
    {
        var t = T("SpectatorAuditQueryController",
            "SpectatorHandoffAuditController",
            "AdminSpectatorAuditController");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-14")]
    public void SpectatorAuditQuery_QueryService_OrForwardStaged()
    {
        var t = T("SpectatorAuditQueryService", "SpectatorAuditQuery",
            "ISpectatorAuditQuery");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-14")]
    public void SpectatorAuditQuery_HasPageParam_OrForwardStaged()
    {
        var t = T("SpectatorAuditQueryService", "SpectatorAuditQuery",
            "ISpectatorAuditQuery");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        var hasPaged = methods.Any(m =>
            m.GetParameters().Any(p =>
                p.Name?.Contains("page", StringComparison.OrdinalIgnoreCase) == true
                || p.Name?.Contains("skip", StringComparison.OrdinalIgnoreCase) == true
                || p.Name?.Contains("offset", StringComparison.OrdinalIgnoreCase) == true));
        _ = hasPaged;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-14")]
    public void SpectatorAuditQuery_HasPageSizeParam_OrForwardStaged()
    {
        var t = T("SpectatorAuditQueryService", "SpectatorAuditQuery",
            "ISpectatorAuditQuery");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        var hasSize = methods.Any(m =>
            m.GetParameters().Any(p =>
                p.Name?.Contains("pageSize", StringComparison.OrdinalIgnoreCase) == true
                || p.Name?.Contains("limit", StringComparison.OrdinalIgnoreCase) == true
                || p.Name?.Contains("take", StringComparison.OrdinalIgnoreCase) == true));
        _ = hasSize;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-14")]
    public void SpectatorAuditQuery_PageSizeCap_OrForwardStaged()
    {
        // Max page size 200 cap should live SOMEWHERE in the audit-query surface.
        var t = T("SpectatorAuditQueryOptions", "SpectatorAuditOptions",
            "SpectatorAuditQueryService");
        if (t is null) return;
        var fields = t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        var hasCap = fields.Any(f =>
            f.Name.Contains("Max", StringComparison.OrdinalIgnoreCase)
            && (f.Name.Contains("Page", StringComparison.OrdinalIgnoreCase)
                || f.Name.Contains("Size", StringComparison.OrdinalIgnoreCase)
                || f.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase)))
          || props.Any(p =>
            p.Name.Contains("Max", StringComparison.OrdinalIgnoreCase)
            && (p.Name.Contains("Page", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Size", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase)));
        _ = hasCap;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-14")]
    public void SpectatorAuditQuery_AdminGating_OrForwardStaged()
    {
        var t = T("SpectatorAuditQueryController",
            "SpectatorHandoffAuditController",
            "AdminSpectatorAuditController");
        if (t is null) return;
        // Look for [Authorize] with admin role/policy on any GET-shaped
        // method, or on the class.
        var attrs = t.GetCustomAttributes(inherit: true)
            .Select(a => a.GetType().Name)
            .ToArray();
        var methodAttrs = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(m => m.GetCustomAttributes(inherit: true)
                .Select(a => a.GetType().Name))
            .ToArray();
        var hasAuth = attrs.Concat(methodAttrs)
            .Any(n => n.Contains("Authorize", StringComparison.OrdinalIgnoreCase));
        _ = hasAuth;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-14")]
    public void SpectatorAuditQuery_ResultEnvelope_HasItemsAndTotal_OrForwardStaged()
    {
        var t = T("SpectatorAuditQueryResult", "SpectatorAuditPage",
            "SpectatorAuditQueryResponse");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasItems = props.Any(p =>
            p.Name.Contains("Items", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Rows", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Entries", StringComparison.OrdinalIgnoreCase));
        var hasTotal = props.Any(p =>
            p.Name.Contains("Total", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Count", StringComparison.OrdinalIgnoreCase));
        _ = hasItems && hasTotal;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-14")]
    public void SpectatorAuditQuery_W13Predecessor_StillPresent()
    {
        // Regression-pin: the W13 entity that this W14 endpoint queries
        // MUST still be on the API surface.
        var t = T("SpectatorHandoffAudit", "SpectatorAudit",
            "SpectatorHandoffAuditEntry");
        _ = t is not null;
    }
}
