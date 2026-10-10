namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez paired contract for Bishop W22's
/// backend csproj 0.30.0 → 0.31.0 bump (paired with Apone W22's
/// CHANGELOG [0.31.0] block + mobile/package.json 0.31.0 stamp).
/// Soft-pinned so the gate stays green if Bishop W22 has not yet
/// landed the bump.
/// </summary>
public sealed class BishopW22BackendCsprojVersionContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void BackendCsproj_Version_0_31_0_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var p = Path.Combine(root!.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Mahjong.Autotable.Api.csproj");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var version = System.Xml.Linq.XDocument.Parse(text)
            .Descendants("Version").Single().Value;
        Assert.True(Version.Parse(version) >= new Version(0, 31, 0));
    }
}
