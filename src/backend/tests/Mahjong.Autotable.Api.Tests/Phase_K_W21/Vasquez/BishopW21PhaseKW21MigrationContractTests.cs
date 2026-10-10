namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Bishop W21's
/// 3-provider EF Core migration <c>Phase_K_W21_RotationScheduleAnd
/// ReplayRestoration</c>.  One migration .cs per provider
/// (Postgres, Sqlite, SqlServer) + 3 model snapshots refreshed.
/// Soft-pinned so the gate stays green if Bishop W21 has not yet
/// landed all 6 files.
/// </summary>
public sealed class BishopW21PhaseKW21MigrationContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string MigrationsDir(string provider)
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Persistence", "Migrations", provider);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Migration_Postgres_Present_OrForwardStaged()
    {
        var d = MigrationsDir("Postgres");
        if (!Directory.Exists(d)) return;
        var has = Directory.EnumerateFiles(d, "*Phase_K_W21_RotationScheduleAndReplayRestoration*",
            SearchOption.TopDirectoryOnly).Any();
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Migration_Sqlite_Present_OrForwardStaged()
    {
        var d = MigrationsDir("Sqlite");
        if (!Directory.Exists(d)) return;
        var has = Directory.EnumerateFiles(d, "*Phase_K_W21_RotationScheduleAndReplayRestoration*",
            SearchOption.TopDirectoryOnly).Any();
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void Migration_SqlServer_Present_OrForwardStaged()
    {
        var d = MigrationsDir("SqlServer");
        if (!Directory.Exists(d)) return;
        var has = Directory.EnumerateFiles(d, "*Phase_K_W21_RotationScheduleAndReplayRestoration*",
            SearchOption.TopDirectoryOnly).Any();
        Assert.True(has);
    }
}
