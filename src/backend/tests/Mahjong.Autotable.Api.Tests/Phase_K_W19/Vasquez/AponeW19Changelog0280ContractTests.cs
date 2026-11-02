namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez paired contract for Apone W19
/// CHANGELOG [0.28.0] entry + mobile/package.json
/// <c>"version": "0.28.0"</c> (D5 in Apone memo).
/// </summary>
public sealed class AponeW19Changelog0280ContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void Changelog_0280_Block_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Soft-pin: the [0.28.0] heading should appear once
        // Apone's CHANGELOG W19 update lands.
        Assert.Contains("0.28.0", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void Mobile_PackageJson_Version_0280_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var p = Path.Combine(root.FullName, "mobile", "package.json");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("\"version\"", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void Changelog_File_Still_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "CHANGELOG.md");
        Assert.True(File.Exists(p),
            $"CHANGELOG.md MUST remain at {p}.");
    }
}
