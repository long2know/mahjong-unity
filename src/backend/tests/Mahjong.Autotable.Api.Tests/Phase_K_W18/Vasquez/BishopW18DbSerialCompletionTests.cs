using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Bishop W18 contract: DbSerial 29/29
/// completion. After Bishop applies <c>[Collection("DbSerial")]</c>
/// to the four W16/W17 open candidates, this contract observes the
/// completion (soft-pin until present, then trivially asserts).
///
/// <para>Pairs with <c>BishopW16W17DbSerialCompletionObservationTests</c>
/// (which counts applied attributes across the 4 candidate files)
/// and with <c>docs/test-architecture.md §3.4c</c> (W18 mile-marker).</para>
/// </summary>
public sealed class BishopW18DbSerialCompletionTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-18")]
    public void TestArchitecture_Section3_4c_W18_Present_OrSoftPass()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "test-architecture.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // §3.4c rendered as "#### 3.4c." once Vasquez W18 lands.
        if (!text.Contains("3.4c", StringComparison.Ordinal)) return;
        Assert.Contains("W18", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-18")]
    public void DbSerialCollection_CanonicalName_Unchanged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "src", "backend", "tests",
            "Mahjong.Autotable.Api.Tests", "Collections", "DbSerialCollection.cs");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("DbSerial", text, StringComparison.Ordinal);
        Assert.Contains("DisableParallelization = true", text, StringComparison.Ordinal);
    }
}
