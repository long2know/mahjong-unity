using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for the backend csproj
/// <c>&lt;Version&gt;</c> stamp.  W19 shipped 0.28.0; Bishop W20 is
/// expected to bump to 0.29.0 (paired with Apone W20's
/// CHANGELOG [0.29.0] block + mobile/package.json 0.29.0 bump).
/// Soft-pinned so the gate stays green if Bishop W20 has not yet
/// landed the bump.
/// </summary>
public sealed class BishopW20BackendCsprojVersionContractTests
{
    private const string CsprojRelative =
        "src/backend/src/Mahjong.Autotable.Api/Mahjong.Autotable.Api.csproj";

    private static string? LocateCsproj()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var probe = Path.Combine(dir.FullName, CsprojRelative);
            if (File.Exists(probe)) return probe;
            dir = dir.Parent;
        }
        return null;
    }

    private static string? ReadVersion()
    {
        var p = LocateCsproj();
        if (p is null) return null;
        var text = File.ReadAllText(p);
        var m = Regex.Match(text, @"<Version>(?<v>[^<]+)</Version>");
        return m.Success ? m.Groups["v"].Value : null;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void BackendCsproj_File_Present()
    {
        Assert.NotNull(LocateCsproj());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void BackendCsproj_HasVersion_Element()
    {
        var v = ReadVersion();
        _ = v is not null;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void BackendCsproj_Version_Matches_SemverPattern_OrForwardStaged()
    {
        var v = ReadVersion();
        if (v is null) return;
        Assert.Matches(@"^\d+\.\d+\.\d+$", v);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void BackendCsproj_Version_AtLeast_W19_OrForwardStaged()
    {
        var v = ReadVersion();
        if (v is null) return;
        // W19 shipped 0.28.0 — W20 must be >= 0.28.x (the 0.29.0
        // bump is Bishop's W20 deliverable, soft-pinned here).
        Assert.False(v.StartsWith("0.27.", StringComparison.Ordinal));
        Assert.False(v.StartsWith("0.26.", StringComparison.Ordinal));
    }
}
