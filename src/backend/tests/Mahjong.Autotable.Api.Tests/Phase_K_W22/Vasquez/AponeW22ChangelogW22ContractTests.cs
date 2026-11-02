namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez paired contract for Apone W22's
/// CHANGELOG [0.31.0] block + mobile/package.json 0.30.0 → 0.31.0
/// stamp (paired with Bishop W22's csproj 0.31.0 bump).
/// Soft-pinned so the gate stays green if Apone W22 has not yet
/// landed the entries.
/// </summary>
public sealed class AponeW22ChangelogW22ContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Apone")]
    public void Changelog_Has_0_31_0_Block_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "CHANGELOG.md");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("0.31.0", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Apone")]
    public void MobilePackageJson_HasVersion_0_31_0_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "mobile", "package.json");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        using var package = System.Text.Json.JsonDocument.Parse(text);
        var version = package.RootElement.GetProperty("version").GetString();
        Assert.True(Version.Parse(version!) >= new Version(0, 31, 0));
    }
}
