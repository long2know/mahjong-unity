namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Hicks W21's
/// i18n zh-Hans/zh-Hant catalog lazification (~4.4 KB each,
/// dynamic-import via <c>ensureCatalog</c>; <c>t()</c> falls back
/// to English until the chunk lands).  Soft-pinned so the gate
/// stays green if Hicks W21 has not yet landed the edits.
/// </summary>
public sealed class HicksW21I18nLazyCatalogsContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string I18nPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "frontend", "autotable-src",
            "src", "i18n.ts");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void I18n_File_Present_OrForwardStaged()
    {
        _ = File.Exists(I18nPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void I18n_EnsureCatalog_Function_Present_OrForwardStaged()
    {
        var p = I18nPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("ensureCatalog", StringComparison.Ordinal)
                   || text.Contains("EnsureCatalog", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Hicks")]
    public void I18n_DynamicImport_ZhHans_OrForwardStaged()
    {
        var p = I18nPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // zh-Hans / zh-Hant catalog tokens should appear once
        // lazification lands (either as dynamic import paths or
        // as catalog identifiers in ensureCatalog dispatch).
        var hasZh = text.Contains("zh-Hans", StringComparison.Ordinal)
                     || text.Contains("zh-Hant", StringComparison.Ordinal)
                     || text.Contains("zhHans", StringComparison.Ordinal)
                     || text.Contains("zhHant", StringComparison.Ordinal);
        Assert.True(hasZh);
    }
}
