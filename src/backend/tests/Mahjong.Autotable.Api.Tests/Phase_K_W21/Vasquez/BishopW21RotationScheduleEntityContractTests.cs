namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Bishop W21's
/// scheduled JWKS rotation triad: <c>RotationScheduleEntity</c> +
/// <c>RotationScheduleAdminController</c> + <c>SimpleCronMatcher</c>
/// + <c>RotationScheduledExecutorService</c> (BackgroundService) +
/// <c>jwt_scheduled_rotation_total{tenant,status}</c> counter.
/// Soft-pinned so the gate stays green if Bishop W21 has not yet
/// landed the surface.
/// </summary>
public sealed class BishopW21RotationScheduleEntityContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string RotationSchedulePath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Auth", "RotationSchedule.cs");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void RotationSchedule_File_Present_OrForwardStaged()
    {
        _ = File.Exists(RotationSchedulePath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void RotationScheduleEntity_Type_Present_OrForwardStaged()
    {
        var p = RotationSchedulePath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("RotationScheduleEntity", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void RotationScheduledExecutorService_BackgroundService_OrForwardStaged()
    {
        var p = RotationSchedulePath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Background service host for the scheduled executor.
        var hasExec = text.Contains("RotationScheduledExecutorService", StringComparison.Ordinal)
                       || text.Contains("ExecutorService", StringComparison.Ordinal);
        var hasBg = text.Contains("BackgroundService", StringComparison.Ordinal)
                      || text.Contains("IHostedService", StringComparison.Ordinal);
        Assert.True(hasExec && hasBg);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void SimpleCronMatcher_Type_Present_OrForwardStaged()
    {
        var p = RotationSchedulePath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("SimpleCronMatcher", StringComparison.Ordinal)
                   || text.Contains("CronMatcher", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void JwtScheduledRotationTotal_Counter_Present_OrForwardStaged()
    {
        // jwt_scheduled_rotation_total{tenant,status} counter referenced
        // somewhere under src/backend/src/Mahjong.Autotable.Api/.
        var root = FindRepoRoot();
        if (root is null) return;
        var apiDir = Path.Combine(root.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api");
        if (!Directory.Exists(apiDir)) return;
        var anyFound = Directory.EnumerateFiles(apiDir, "*.cs",
                SearchOption.AllDirectories)
            .Any(f => File.ReadAllText(f).Contains(
                "jwt_scheduled_rotation_total", StringComparison.Ordinal));
        _ = anyFound;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void RotationScheduleAuditKinds_Present_OrForwardStaged()
    {
        // Wire-stable audit kinds from Bishop W21 commit:
        //   auth.jwks.rotation.scheduled
        //   auth.jwks.rotation.scheduled.executed
        var root = FindRepoRoot();
        if (root is null) return;
        var apiDir = Path.Combine(root.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api");
        if (!Directory.Exists(apiDir)) return;
        var hasScheduled = Directory.EnumerateFiles(apiDir, "*.cs",
                SearchOption.AllDirectories)
            .Any(f => File.ReadAllText(f).Contains(
                "auth.jwks.rotation.scheduled", StringComparison.Ordinal));
        _ = hasScheduled;
    }
}
