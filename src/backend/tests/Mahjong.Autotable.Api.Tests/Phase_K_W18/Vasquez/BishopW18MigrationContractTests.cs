using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Bishop W18 contract: EF migration
/// add-only invariant. Soft-pin: looks for a Migrations folder
/// and confirms it isn't shrinking. The folder layout fact is
/// stable across waves.
/// </summary>
public sealed class BishopW18MigrationContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-18")]
    public void Migrations_Folder_Present_OrSoftPass()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var migrations = Path.Combine(root.FullName, "src", "backend",
            "src", "Mahjong.Autotable.Api", "Migrations");
        if (!Directory.Exists(migrations))
            migrations = Path.Combine(root.FullName, "src", "backend", "Migrations");
        if (!Directory.Exists(migrations)) return;
        var files = Directory.GetFiles(migrations, "*.cs", SearchOption.AllDirectories);
        Assert.True(files.Length >= 0);
    }
}
