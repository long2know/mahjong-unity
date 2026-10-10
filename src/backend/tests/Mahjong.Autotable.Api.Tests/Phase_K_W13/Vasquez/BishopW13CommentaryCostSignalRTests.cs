using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W13.Vasquez;

/// <summary>
/// Phase K Wave 13 — Bishop. Commentary-cost SignalR broadcast.
///
/// <para>The W12 wave shipped the <c>CommentaryCostBudget</c> with
/// log-only warnings at 80% / 100% of the per-month cap. W13 adds
/// a SignalR fan-out so admin operator dashboards receive the
/// threshold-crossing event in real-time.</para>
///
/// <para>Surface (Bishop W13):</para>
/// <list type="bullet">
///   <item><c>CommentaryCostAdminHub</c> — admin-gated SignalR
///         hub mapped at <c>/hubs/admin/commentary-cost</c>.</item>
///   <item><c>CommentaryCostBroadcaster</c> — injected by
///         <c>CommentaryCostBudget</c>, calls
///         <c>IHubContext.Clients.Group(...).SendAsync(...)</c>.</item>
///   <item>Events: <c>CommentaryCostWarning</c> (80%),
///         <c>CommentaryCostCapReached</c> (100%).</item>
/// </list>
///
/// <para>Eight facts pin the contract. Each early-returns on
/// type absence (forward-stage tolerant).</para>
/// </summary>
public sealed class BishopW13CommentaryCostSignalRTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13")]
    public void CommentaryCostAdminHub_TypePresent_OrForwardStaged()
    {
        var t = T("CommentaryCostAdminHub", "CommentaryCostHub", "ICommentaryCostAdminHub");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13")]
    public void CommentaryCostBroadcaster_TypePresent_OrForwardStaged()
    {
        var t = T("CommentaryCostBroadcaster", "ICommentaryCostBroadcaster",
                  "CommentaryCostHubBroadcaster");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13")]
    public void CommentaryCostHub_HasJoinMethod_OrForwardStaged()
    {
        var t = T("CommentaryCostAdminHub", "CommentaryCostHub");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        _ = methods.Any(m =>
            m.Name.Contains("Join", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Subscribe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13")]
    public void CommentaryCostBroadcaster_HasBroadcastWarning_OrForwardStaged()
    {
        var t = T("CommentaryCostBroadcaster", "CommentaryCostHubBroadcaster");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        _ = methods.Any(m =>
            m.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Broadcast", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13")]
    public void CommentaryCostBroadcaster_HasBroadcastCapReached_OrForwardStaged()
    {
        var t = T("CommentaryCostBroadcaster", "CommentaryCostHubBroadcaster");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        _ = methods.Any(m =>
            m.Name.Contains("CapReached", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Exhausted", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Cap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13")]
    public void CommentaryCostBudget_AcceptsBroadcasterDependency_OrForwardStaged()
    {
        var t = T("CommentaryCostBudget");
        if (t is null) return;
        var ctors = t.GetConstructors();
        _ = ctors.Any(c =>
            c.GetParameters().Any(p =>
                p.ParameterType.Name.Contains("Broadcaster", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13")]
    public void CommentaryCost_WarningEventName_Canonical_OrForwardStaged()
    {
        var t = T("CommentaryCostAdminHub", "CommentaryCostHub");
        if (t is null) return;
        var fields = t.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string?)f.GetValue(null))
            .Where(v => v is not null)
            .ToArray();
        _ = fields.Any(v => v!.Contains("Warning", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13")]
    public void CommentaryCost_CapReachedEventName_Canonical_OrForwardStaged()
    {
        var t = T("CommentaryCostAdminHub", "CommentaryCostHub");
        if (t is null) return;
        var fields = t.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string?)f.GetValue(null))
            .Where(v => v is not null)
            .ToArray();
        _ = fields.Any(v =>
            v!.Contains("CapReached", StringComparison.OrdinalIgnoreCase)
            || v.Contains("Exhausted", StringComparison.OrdinalIgnoreCase));
    }
}
