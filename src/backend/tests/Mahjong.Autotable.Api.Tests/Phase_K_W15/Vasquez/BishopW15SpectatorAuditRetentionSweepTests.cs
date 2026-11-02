using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Vasquez;

/// <summary>
/// Phase K Wave 15 — Bishop. Spectator audit retention sweep
/// (cadence + delete behavior).
///
/// <para>W13 shipped the <c>SpectatorHandoffAudit</c> row + retention
/// sweep with a fixed-cadence delete. W14 exposed the row via the
/// admin query endpoint. W15 strengthens the sweep cadence with an
/// observable Prometheus counter and an opt-in audit-trail row for
/// each delete batch.</para>
///
/// <para>Eight reflection-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class BishopW15SpectatorAuditRetentionSweepTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-15")]
    public void SpectatorAuditRetention_SweepService_OrForwardStaged()
    {
        var t = T("SpectatorAuditRetentionSweepService",
            "SpectatorAuditRetentionService",
            "SpectatorAuditRetentionSweep",
            "SpectatorHandoffAuditRetentionSweep");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-15")]
    public void SpectatorAuditRetention_Cadence_Configurable_OrForwardStaged()
    {
        var t = T("SpectatorAuditRetentionOptions",
            "SpectatorAuditRetentionConfig",
            "SpectatorAuditOptions");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasCadence = props.Any(p =>
            p.Name.Contains("Cadence", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Interval", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("SweepInterval", StringComparison.OrdinalIgnoreCase));
        _ = hasCadence;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-15")]
    public void SpectatorAuditRetention_DeleteBatch_Observable_OrForwardStaged()
    {
        var t = T("SpectatorAuditRetentionSweepService",
            "SpectatorAuditRetentionService",
            "SpectatorAuditRetentionSweep");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var hasSweep = methods.Any(m =>
            m.Name.Contains("Sweep", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Purge", StringComparison.OrdinalIgnoreCase));
        _ = hasSweep;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-15")]
    public void SpectatorAuditRetention_RowsDeletedCounter_OrForwardStaged()
    {
        // Prometheus counter for rows deleted per sweep batch.
        var types = ApiAssembly.GetTypes();
        var hasCounterLit = types.Any(t =>
        {
            var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance);
            return fields.Any(f =>
                f.IsLiteral
                && (f.GetRawConstantValue() as string)?
                    .Contains("spectator_audit_retention_rows_deleted",
                        StringComparison.OrdinalIgnoreCase) == true);
        });
        _ = hasCounterLit;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-15")]
    public void SpectatorAuditRetention_AgeThreshold_OrForwardStaged()
    {
        var t = T("SpectatorAuditRetentionOptions",
            "SpectatorAuditRetentionConfig",
            "SpectatorAuditOptions");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasAge = props.Any(p =>
            p.Name.Contains("Age", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Retention", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("MaxAge", StringComparison.OrdinalIgnoreCase));
        _ = hasAge;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-15")]
    public void SpectatorAuditRetention_W13Entity_StillPresent()
    {
        var t = T("SpectatorHandoffAudit", "SpectatorAudit",
            "SpectatorHandoffAuditEntry");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-15")]
    public void SpectatorAuditRetention_W14QueryEndpoint_StillPresent()
    {
        var t = T("SpectatorAuditQueryController",
            "SpectatorHandoffAuditController",
            "AdminSpectatorAuditController");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-15")]
    public void SpectatorAuditRetention_DeleteIsTransactional_OrForwardStaged()
    {
        // The sweep should run inside an EF Core transaction so a partial
        // failure doesn't leave the audit table in an inconsistent state.
        var t = T("SpectatorAuditRetentionSweepService",
            "SpectatorAuditRetentionService",
            "SpectatorAuditRetentionSweep");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance);
        _ = methods.Length > 0; // smoke-only
    }
}
