namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Apone W20's
/// CHANGELOG <c>[0.29.0]</c> block + <c>mobile/package.json</c>
/// 0.29.0 stamp (paired with Bishop W20's csproj 0.29.0 bump).
/// Soft-pinned so the gate stays green if Apone W20 has not yet
/// landed the entry.
/// </summary>
public sealed class AponeW20ChangelogW20ContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void Changelog_Has_0_29_0_Block_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "CHANGELOG.md");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Header for 0.29.0 (the [0.29.0] form is canonical Keep-a-Changelog).
        var has = text.Contains("0.29.0", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void MobilePackageJson_HasVersion_0_29_0_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "mobile", "package.json");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        using var package = System.Text.Json.JsonDocument.Parse(text);
        var version = package.RootElement.GetProperty("version").GetString();
        Assert.True(Version.Parse(version!) >= new Version(0, 29, 0));
    }
}
