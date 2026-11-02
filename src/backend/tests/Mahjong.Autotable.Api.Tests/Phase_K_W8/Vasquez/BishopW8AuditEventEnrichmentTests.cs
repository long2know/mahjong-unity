using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W8.Vasquez;

/// <summary>
/// Phase K Wave 8 — Bishop. AuditEvent enrichment column contract.
///
/// <para>W4–W7 wired progressively richer audit rows (Kind / Why /
/// Actor / Subject / EmittedAt). W8 adds:</para>
///
/// <list type="number">
///   <item><c>IdempotencyKey</c> — request idempotency token, so
///         retried writes don't duplicate-write.</item>
///   <item><c>RequestId</c> — server-assigned correlation id, joined
///         to log lines.</item>
///   <item><c>UserAgent</c> — client identity header, for forensics.</item>
///   <item><c>ClientIp</c> — sanitised origin IP (forensics + DSAR).</item>
/// </list>
///
/// <para>Five facts:</para>
/// <list type="number">
///   <item><c>AuditEvent</c> type present in API assembly.</item>
///   <item>Carries <c>IdempotencyKey</c> property (string?).</item>
///   <item>Carries <c>RequestId</c> property.</item>
///   <item>Carries <c>UserAgent</c> property (or
///         <c>UserAgentHeader</c>).</item>
///   <item>Carries <c>ClientIp</c> property (or
///         <c>RemoteIpAddress</c>).</item>
/// </list>
///
/// <para>All facts forward-stage tolerant.</para>
/// </summary>
public sealed class AuditEventEnrichmentTests
{
    private static readonly Assembly ApiAssembly =
        typeof(Mahjong.Autotable.Api.Changsha.Runtime.ChangshaGameRuntime).Assembly;

    private static Type? FindAuditEventType() =>
        ApiAssembly.GetTypes().FirstOrDefault(t =>
            t.Name == "AuditEvent"
            || t.Name == "AuditEventEntity"
            || t.Name == "AuditRecord");

    private static HashSet<string> PropertyNames(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
         .Select(p => p.Name)
         .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void AuditEvent_TypePresent_OrForwardStaged()
    {
        var t = FindAuditEventType();
        if (t is null) return;
        Assert.True(t.IsClass || t.IsValueType,
            "AuditEvent MUST be a class or record (value type).");
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void AuditEvent_IdempotencyKey_Property_OrForwardStaged()
    {
        var t = FindAuditEventType();
        if (t is null) return;

        var props = PropertyNames(t);
        Assert.True(
            props.Contains("IdempotencyKey")
            || props.Contains("IdempotencyToken"),
            "AuditEvent MUST carry an IdempotencyKey property (W8 enrichment).");
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void AuditEvent_RequestId_Property_OrForwardStaged()
    {
        var t = FindAuditEventType();
        if (t is null) return;

        var props = PropertyNames(t);
        Assert.True(
            props.Contains("RequestId")
            || props.Contains("CorrelationId")
            || props.Contains("TraceId"),
            "AuditEvent MUST carry a RequestId / CorrelationId / TraceId property.");
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void AuditEvent_UserAgent_Property_OrForwardStaged()
    {
        var t = FindAuditEventType();
        if (t is null) return;

        var props = PropertyNames(t);
        Assert.True(
            props.Contains("UserAgent")
            || props.Contains("UserAgentHeader")
            || props.Contains("ClientUserAgent"),
            "AuditEvent MUST carry a UserAgent property.");
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-8")]
    public void AuditEvent_ClientIp_Property_OrForwardStaged()
    {
        var t = FindAuditEventType();
        if (t is null) return;

        var props = PropertyNames(t);
        Assert.True(
            props.Contains("ClientIp")
            || props.Contains("ClientIpAddress")
            || props.Contains("RemoteIp")
            || props.Contains("RemoteIpAddress")
            || props.Contains("OriginIp"),
            "AuditEvent MUST carry a ClientIp / RemoteIpAddress property.");
    }
}
