using System.Reflection;
using Mahjong.Autotable.Api.Persistence.Migrations.Sqlite;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Bishop;

/// <summary>
/// Phase K Wave 17 — Bishop. Sanity tests around the W17
/// migration (auto-generated, identical for all three providers
/// modulo column types).
/// </summary>
public sealed class MigrationW17Tests
{
    [Fact, Trait("Category", "Migrations"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Sqlite_Migration_Type_Present()
    {
        var t = typeof(Phase_K_W17_AdminCrudAndPerTenantRetention);
        Assert.NotNull(t);
        Assert.Equal("Mahjong.Autotable.Api.Persistence.Migrations.Sqlite", t.Namespace);
    }

    [Fact, Trait("Category", "Migrations"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Postgres_Migration_Type_Present()
    {
        var t = Type.GetType(
            "Mahjong.Autotable.Api.Persistence.Migrations.Postgres.Phase_K_W17_AdminCrudAndPerTenantRetention, Mahjong.Autotable.Api");
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "Migrations"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void SqlServer_Migration_Type_Present()
    {
        var t = Type.GetType(
            "Mahjong.Autotable.Api.Persistence.Migrations.SqlServer.Phase_K_W17_AdminCrudAndPerTenantRetention, Mahjong.Autotable.Api");
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "Migrations"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Sqlite_Migration_OverridesUpAndDown()
    {
        var t = typeof(Phase_K_W17_AdminCrudAndPerTenantRetention);
        var up = t.GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic);
        var dn = t.GetMethod("Down", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(up);
        Assert.NotNull(dn);
        Assert.Equal(t, up!.DeclaringType);
        Assert.Equal(t, dn!.DeclaringType);
    }

    [Fact, Trait("Category", "Migrations"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Sqlite_Migration_File_AddsSignalRRetentionPolicies()
    {
        var path = LocateMigration("Sqlite");
        var text = File.ReadAllText(path);
        Assert.Contains("SignalRRetentionPolicies", text);
    }

    [Fact, Trait("Category", "Migrations"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Sqlite_Migration_File_AddsReplayRetentionPolicies()
    {
        var path = LocateMigration("Sqlite");
        var text = File.ReadAllText(path);
        Assert.Contains("ReplayRetentionPolicies", text);
    }

    [Fact, Trait("Category", "Migrations"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Sqlite_Migration_File_AddsTenantIdOnSignalRSequenceEntries()
    {
        var path = LocateMigration("Sqlite");
        var text = File.ReadAllText(path);
        Assert.Contains("name: \"TenantId\"", text);
        Assert.Contains("table: \"SignalRSequenceEntries\"", text);
    }

    [Fact, Trait("Category", "Migrations"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Sqlite_Migration_File_AddsOverlapWindowDays()
    {
        var path = LocateMigration("Sqlite");
        var text = File.ReadAllText(path);
        Assert.Contains("OverlapWindowDays", text);
    }

    [Fact, Trait("Category", "Migrations"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Postgres_Migration_File_Present()
    {
        Assert.True(File.Exists(LocateMigration("Postgres")));
    }

    [Fact, Trait("Category", "Migrations"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void SqlServer_Migration_File_Present()
    {
        Assert.True(File.Exists(LocateMigration("SqlServer")));
    }

    [Fact, Trait("Category", "Migrations"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void All3Providers_Migration_File_Present()
    {
        foreach (var provider in new[] { "Sqlite", "Postgres", "SqlServer" })
        {
            Assert.True(File.Exists(LocateMigration(provider)),
                $"Migration file missing for provider {provider}");
        }
    }

    private static string LocateMigration(string provider)
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var dir = Path.Combine(root!.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Persistence", "Migrations", provider);
        var file = Directory.EnumerateFiles(dir, "*Phase_K_W17_AdminCrudAndPerTenantRetention.cs")
            .FirstOrDefault(p => !p.EndsWith(".Designer.cs", StringComparison.Ordinal));
        Assert.NotNull(file);
        return file!;
    }

    private static DirectoryInfo? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                && Directory.Exists(Path.Combine(dir.FullName, ".squad")))
            {
                return dir;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
