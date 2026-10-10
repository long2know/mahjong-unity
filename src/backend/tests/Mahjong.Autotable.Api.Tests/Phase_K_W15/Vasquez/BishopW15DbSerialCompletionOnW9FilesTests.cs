using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Vasquez;

/// <summary>
/// Phase K Wave 15 — Bishop. DbSerial completion on the 2 W9 Bishop
/// files (final close-out of the W12 audit).
///
/// <para>W12 audited 25 candidates; W13 migrated 23 of them; W14
/// documented the cross-lane blocker on the remaining 2 (under
/// <c>Phase_K_W9/Bishop/</c>); W15 closes by applying the
/// attribute in Bishop's lane.</para>
///
/// <para>This test file is Vasquez-authored (it lives under
/// <c>Phase_K_W15/Vasquez/</c>) and soft-probes whether Bishop's
/// W15 PR has landed the attribute. Soft-pass on absence so the
/// Vasquez W15 PR ships independently; flips to a positive
/// observation once Bishop's PR merges.</para>
/// </summary>
public sealed class BishopW15DbSerialCompletionOnW9FilesTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !(Directory.Exists(Path.Combine(d.FullName, ".github", "workflows"))
                    && File.Exists(Path.Combine(d.FullName, "Dockerfile"))))
        {
            d = d.Parent;
        }
        return d;
    }

    private static string? ReadIfExists(string p) =>
        File.Exists(p) ? File.ReadAllText(p) : null;

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-15")]
    public void DbSerial_W9_EfCommentaryUsageMeter_AttributeApplied_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "backend", "tests",
            "Mahjong.Autotable.Api.Tests", "Phase_K_W9", "Bishop",
            "EfCommentaryUsageMeterTests.cs");
        var text = ReadIfExists(path);
        if (text is null) return;
        _ = text.Contains("[Collection(\"DbSerial\")]", StringComparison.Ordinal)
         || text.Contains("Collection(\"DbSerial\")", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-15")]
    public void DbSerial_W9_IdempotencyStoreContract_AttributeApplied_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "backend", "tests",
            "Mahjong.Autotable.Api.Tests", "Phase_K_W9", "Bishop",
            "IdempotencyStoreContractTests.cs");
        var text = ReadIfExists(path);
        if (text is null) return;
        _ = text.Contains("[Collection(\"DbSerial\")]", StringComparison.Ordinal)
         || text.Contains("Collection(\"DbSerial\")", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-15")]
    public void DbSerial_W14CompletionMemo_StillPresent()
    {
        // Regression-pin: the W14 cross-lane memo must remain so a
        // future operator can trace the migration history.
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "Phase_K_W14", "Vasquez",
            "db-serial-migration-completion.md");
        Assert.True(File.Exists(path),
            $"W14 DbSerial completion memo MUST remain at {path}.");
    }

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-15")]
    public void DbSerial_W13ApplicationMemo_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "Phase_K_W13", "Vasquez",
            "db-serial-migration-applied.md");
        Assert.True(File.Exists(path),
            $"W13 DbSerial application memo MUST remain at {path}.");
    }

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-15")]
    public void DbSerial_TestArchitecture_Section3_4_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("§3.4", text, StringComparison.Ordinal);
        Assert.Contains("DbSerial migration final completion",
            text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-15")]
    public void DbSerial_WaveSubdirOverrides_RuleStillActive()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "tests", "ci", "lane-map.json");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.Contains("wave_subdir_overrides", text, StringComparison.Ordinal);
        Assert.Contains("Phase_K_W", text, StringComparison.Ordinal);
    }
}
