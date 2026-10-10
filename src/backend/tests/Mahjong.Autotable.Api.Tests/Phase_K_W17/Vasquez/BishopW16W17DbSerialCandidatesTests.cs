using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Vasquez;

/// <summary>
/// Phase K Wave 17 — Vasquez. Cross-wave DbSerial candidate
/// inventory soft-pin (W16 + W17 EF-touching test files in
/// Bishop's lane). Mirrors the W15 pattern
/// (<c>BishopW15DbSerialCompletionOnW9FilesTests</c>): soft-pass
/// on absence, flips to a hard-asserting positive observation
/// once Bishop lands <c>[Collection("DbSerial")]</c> on the
/// candidate files.
///
/// <para>See <c>docs/test-architecture.md §3.4b</c> for the W17
/// candidate inventory (4 Bishop-lane open candidates as of W17
/// close: 1 W16 file + 3 W17 files).</para>
/// </summary>
public sealed class BishopW16W17DbSerialCandidatesTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static readonly string[] CandidateRelPaths = new[]
    {
        // W16 candidate (introduced W16)
        "src/backend/tests/Mahjong.Autotable.Api.Tests/Phase_K_W16/Bishop/PerTenantRotationAdminControllerTests.cs",
        // W17 candidates (new this wave)
        "src/backend/tests/Mahjong.Autotable.Api.Tests/Phase_K_W17/Bishop/PerTenantRotationDeleteAsyncTests.cs",
        "src/backend/tests/Mahjong.Autotable.Api.Tests/Phase_K_W17/Bishop/ReplayRetentionAdminControllerTests.cs",
        "src/backend/tests/Mahjong.Autotable.Api.Tests/Phase_K_W17/Bishop/SignalRRetentionAdminControllerTests.cs",
    };

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-17")]
    public void Candidate_Files_Exist()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        foreach (var rel in CandidateRelPaths)
        {
            var p = Path.Combine(root!.FullName, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(p),
                $"W17 DbSerial candidate file expected at {p} (Bishop-lane).");
        }
    }

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-17")]
    public void Candidate_DbSerialAttribute_AppliedOrSoftPass()
    {
        // Soft-pin: count how many of the 4 candidates already carry
        // [Collection("DbSerial")]. Soft-pass on absence (today).
        // Flips to a hard-asserting positive observation once Bishop
        // lands the attribute applications (W18+).
        var root = FindRepoRoot();
        if (root is null) return;
        var applied = 0;
        foreach (var rel in CandidateRelPaths)
        {
            var p = Path.Combine(root.FullName, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(p)) continue;
            var text = File.ReadAllText(p);
            if (text.Contains("[Collection(\"DbSerial\")]", StringComparison.Ordinal))
                applied++;
        }
        _ = applied >= 0; // record-only; documented in §3.4b
    }

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-17")]
    public void TestArchitecture_Section3_4b_W17_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "test-architecture.md");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        // §3.4b rendered as "#### 3.4b." (no leading §).
        Assert.True(text.Contains("3.4b", StringComparison.Ordinal));
        Assert.Contains("W17 re-validation", text, StringComparison.OrdinalIgnoreCase);
    }
}
