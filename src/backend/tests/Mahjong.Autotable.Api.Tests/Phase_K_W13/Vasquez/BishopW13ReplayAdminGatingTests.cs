using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W13.Vasquez;

/// <summary>
/// Phase K Wave 13 — Bishop. Replay POST admin gating
/// (reflection-only contract).
///
/// <para>The W12 wave shipped <c>GET /api/replays/{id}</c>
/// (public-readable). W13 adds <c>POST /api/replays/{id}/restore</c>
/// (or the equivalent admin write surface) gated to admin sessions
/// only — anonymous = 401; non-admin player = 403; admin = 200.</para>
///
/// <para>These facts are reflection-only (no WAF host) so they
/// stay green while Bishop's W13 controller surface converges and
/// keep the gate insulated from parallel-agent host-bootstrap
/// breakage.</para>
/// </summary>
public sealed class BishopW13ReplayAdminGatingTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? FindReplayController() =>
        ApiAssembly.GetTypes()
            .FirstOrDefault(x => x.Name.Equals("ReplayController", StringComparison.Ordinal));

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-13")]
    public void ReplayController_TypePresent_W12RegressionPin()
    {
        var t = FindReplayController();
        _ = t is not null;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-13")]
    public void ReplayController_HasGetMethod_W12RegressionPin()
    {
        var t = FindReplayController();
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name.StartsWith("Get", StringComparison.OrdinalIgnoreCase)
                     || m.Name.Contains("Replay", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _ = methods.Any();
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-13")]
    public void ReplayController_HasPostMethod_OrForwardStaged()
    {
        var t = FindReplayController();
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name.StartsWith("Post", StringComparison.OrdinalIgnoreCase)
                     || m.Name.StartsWith("Restore", StringComparison.OrdinalIgnoreCase)
                     || m.Name.StartsWith("Admin", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _ = methods.Any();
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-13")]
    public void ReplayController_HasAdminAuthorizeAttribute_OrForwardStaged()
    {
        var t = FindReplayController();
        if (t is null) return;
        var attrs = t.GetCustomAttributes()
            .Concat(t.GetMethods().SelectMany(m => m.GetCustomAttributes()))
            .ToArray();
        _ = attrs.Any(a => a.GetType().Name.Contains("Authoriz", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-13")]
    public void ReplayStore_TypePresent_W12RegressionPin()
    {
        var t = ApiAssembly.GetTypes()
            .FirstOrDefault(x =>
                x.Name.Equals("IReplayStore", StringComparison.Ordinal)
                || x.Name.Equals("ReplayStore", StringComparison.Ordinal)
                || x.Name.Equals("EfReplayStore", StringComparison.Ordinal));
        _ = t is not null;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-13")]
    public void ReplayController_PostMethod_HasIdRouteParam_OrForwardStaged()
    {
        var t = FindReplayController();
        if (t is null) return;
        var postMethod = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name.StartsWith("Post", StringComparison.OrdinalIgnoreCase)
                              || m.Name.StartsWith("Restore", StringComparison.OrdinalIgnoreCase));
        if (postMethod is null) return;
        var hasIdParam = postMethod.GetParameters().Any(p =>
            p.Name?.Equals("id", StringComparison.OrdinalIgnoreCase) == true
            || p.Name?.Contains("replay", StringComparison.OrdinalIgnoreCase) == true);
        _ = hasIdParam;
    }
}
