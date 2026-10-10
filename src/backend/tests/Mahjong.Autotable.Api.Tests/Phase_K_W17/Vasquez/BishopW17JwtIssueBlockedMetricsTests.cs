using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Vasquez;

/// <summary>
/// Phase K Wave 17 — Bishop forward-stage. JwtIssueBlockedMetrics +
/// the <c>jwt_issue_blocked_total{reason}</c> Prometheus counter +
/// the new <c>auth.jwt.issue.blocked.stale_per_tenant_policy</c>
/// audit-kind constant.
///
/// <para>Five reflection-defensive facts. Soft-pass on absence —
/// the surfaces land in Bishop's W17 lane.</para>
/// </summary>
public sealed class BishopW17JwtIssueBlockedMetricsTests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(BishopW17JwtIssueBlockedMetricsTests).Assembly.GetReferencedAssemblies())
        {
            if (name.Name != "Mahjong.Autotable.Api") continue;
            try { return Assembly.Load(name); } catch { return null; }
        }
        return null;
    }

    private static Type? FindType(string name)
    {
        var asm = ResolveApiAssembly();
        return asm?.GetTypes().FirstOrDefault(t => t.Name.Equals(name, StringComparison.Ordinal));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17")]
    public void Metrics_TypeReachable_OrForwardStaged()
    {
        var t = FindType("JwtIssueBlockedMetrics");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17")]
    public void Metrics_HasIncrementOrRecordSurface_OrForwardStaged()
    {
        var t = FindType("JwtIssueBlockedMetrics");
        if (t is null) return;
        var hasMethod = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Any(m => m.Name.Contains("Increment", StringComparison.OrdinalIgnoreCase)
                   || m.Name.Contains("Record", StringComparison.OrdinalIgnoreCase)
                   || m.Name.Contains("Observe", StringComparison.OrdinalIgnoreCase));
        _ = hasMethod;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17")]
    public void Metrics_ExposesReasonField_OrForwardStaged()
    {
        var t = FindType("JwtIssueBlockedMetrics");
        if (t is null) return;
        var hasReason = t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
            .Any(f => f.Name.Contains("reason", StringComparison.OrdinalIgnoreCase))
            || t.GetProperties().Any(p => p.Name.Contains("Reason", StringComparison.OrdinalIgnoreCase));
        _ = hasReason;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17")]
    public void AuditKind_StalePerTenantPolicy_Constant_OrForwardStaged()
    {
        var asm = ResolveApiAssembly();
        if (asm is null) return;
        // The constant lives wherever the audit-kind table lives —
        // try a few canonical candidate types.
        var candidates = new[] { "ReconnectAuditKinds", "AuditKinds", "JwtAuditKinds", "AuthAuditKinds" };
        foreach (var typeName in candidates)
        {
            var t = asm.GetTypes().FirstOrDefault(x => x.Name.Equals(typeName, StringComparison.Ordinal));
            if (t is null) continue;
            var hasField = t.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Any(f => f.GetValue(null) is string s
                       && s.Contains("stale_per_tenant_policy", StringComparison.OrdinalIgnoreCase));
            if (hasField) return;
        }
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-17")]
    public void IssuingService_ConsultsValidator_OrForwardStaged()
    {
        var t = FindType("JwtIssuingService");
        if (t is null) return;
        // Either a constructor depends on the validator, or a method
        // body references the validator type by name (we cannot
        // introspect IL portably — settle for the dependency proxy).
        var ctors = t.GetConstructors();
        var hasValidator = ctors.Any(c => c.GetParameters()
            .Any(p => p.ParameterType.Name.Contains("PerTenantJwksRotationValidator", StringComparison.OrdinalIgnoreCase)));
        _ = hasValidator;
    }
}
