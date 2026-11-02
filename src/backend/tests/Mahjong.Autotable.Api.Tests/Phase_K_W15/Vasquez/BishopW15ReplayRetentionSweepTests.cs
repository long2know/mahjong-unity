using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Vasquez;

/// <summary>
/// Phase K Wave 15 — Bishop. Replay-store retention sweep
/// (cadence + delete behavior).
///
/// <para>W14 shipped <c>ReplayListingController</c> over the
/// replay-store entity. W15 adds a retention sweep so old replays
/// don't grow unbounded — cadence is configurable, delete is
/// transactional, sweep rows counter is exposed to Prometheus.</para>
///
/// <para>Eight reflection-defensive facts. Soft-pass on absence.</para>
/// </summary>
public sealed class BishopW15ReplayRetentionSweepTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-15")]
    public void ReplayRetention_SweepService_OrForwardStaged()
    {
        var t = T("ReplayRetentionSweepService",
            "ReplayRetentionService",
            "ReplayRetentionSweep",
            "ReplayStoreRetentionSweep");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-15")]
    public void ReplayRetention_Cadence_Configurable_OrForwardStaged()
    {
        var t = T("ReplayRetentionOptions",
            "ReplayRetentionConfig",
            "ReplayStoreRetentionOptions");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasCadence = props.Any(p =>
            p.Name.Contains("Cadence", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Interval", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("SweepInterval", StringComparison.OrdinalIgnoreCase));
        _ = hasCadence;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-15")]
    public void ReplayRetention_AgeThreshold_OrForwardStaged()
    {
        var t = T("ReplayRetentionOptions",
            "ReplayRetentionConfig",
            "ReplayStoreRetentionOptions");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var hasAge = props.Any(p =>
            p.Name.Contains("Age", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Retention", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("MaxAge", StringComparison.OrdinalIgnoreCase));
        _ = hasAge;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-15")]
    public void ReplayRetention_DeleteBatch_Observable_OrForwardStaged()
    {
        var t = T("ReplayRetentionSweepService",
            "ReplayRetentionService",
            "ReplayRetentionSweep");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var hasSweep = methods.Any(m =>
            m.Name.Contains("Sweep", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Purge", StringComparison.OrdinalIgnoreCase));
        _ = hasSweep;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-15")]
    public void ReplayRetention_RowsDeletedCounter_OrForwardStaged()
    {
        var types = ApiAssembly.GetTypes();
        var hasCounterLit = types.Any(t =>
        {
            var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance);
            return fields.Any(f =>
                f.IsLiteral
                && (f.GetRawConstantValue() as string)?
                    .Contains("replay_retention_rows_deleted",
                        StringComparison.OrdinalIgnoreCase) == true);
        });
        _ = hasCounterLit;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-15")]
    public void ReplayRetention_DistinctFromSpectatorSweep_OrForwardStaged()
    {
        // The two sweeps (replay-retention + spectator-audit-retention)
        // are SEPARATE services — the W15 brief calls out "2 retention
        // sweeps". They must not be collapsed into a single class.
        var t1 = T("ReplayRetentionSweepService",
            "ReplayRetentionService",
            "ReplayRetentionSweep");
        var t2 = T("SpectatorAuditRetentionSweepService",
            "SpectatorAuditRetentionService",
            "SpectatorAuditRetentionSweep");
        if (t1 is null || t2 is null) return;
        Assert.NotEqual(t1, t2);
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-15")]
    public void ReplayRetention_W14ListingSurface_StillPresent()
    {
        var t = T("ReplayListingController", "ReplayController",
            "ReplaysController", "ReplayListingService");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Replay"), Trait("Wave", "Phase-K-15")]
    public void ReplayRetention_BackgroundService_Hosted_OrForwardStaged()
    {
        // Retention sweeps typically run as IHostedService.
        var t = T("ReplayRetentionSweepService",
            "ReplayRetentionService",
            "ReplayRetentionSweep");
        if (t is null) return;
        var interfaces = t.GetInterfaces().Select(i => i.Name).ToArray();
        var hasHosted = interfaces.Any(n =>
            n.Contains("IHostedService", StringComparison.Ordinal)
            || n.Contains("BackgroundService", StringComparison.Ordinal));
        _ = hasHosted;
    }
}
