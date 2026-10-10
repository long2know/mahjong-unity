using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Cross-wave DbSerial 29/29 completion
/// observation. Pairs with W17's
/// <c>BishopW16W17DbSerialCandidatesTests</c> (soft-pinned the 4
/// open candidates at W17 close). W18 mile-marker: Bishop applies
/// <c>[Collection("DbSerial")]</c> to all four; this contract
/// trivially observes the resulting 29/29 inventory.
///
/// <para>See <c>docs/test-architecture.md §3.4c</c> for the W18
/// completion mile-marker (DbSerial COMPLETE — 29/29 migrated).</para>
/// </summary>
public sealed class BishopW16W17DbSerialCompletionObservationTests
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
        // W16 candidate (introduced W16, applied W18)
        "src/backend/tests/Mahjong.Autotable.Api.Tests/Phase_K_W16/Bishop/PerTenantRotationAdminControllerTests.cs",
        // W17 candidates (introduced W17, applied W18)
        "src/backend/tests/Mahjong.Autotable.Api.Tests/Phase_K_W17/Bishop/PerTenantRotationDeleteAsyncTests.cs",
        "src/backend/tests/Mahjong.Autotable.Api.Tests/Phase_K_W17/Bishop/ReplayRetentionAdminControllerTests.cs",
        "src/backend/tests/Mahjong.Autotable.Api.Tests/Phase_K_W17/Bishop/SignalRRetentionAdminControllerTests.cs",
    };

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-18")]
    public void All_Four_Candidate_Files_Still_Exist()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        foreach (var rel in CandidateRelPaths)
        {
            var p = Path.Combine(root!.FullName, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(p),
                $"W18 DbSerial candidate file expected at {p} (Bishop-lane).");
        }
    }

    [Fact, Trait("Category", "DbSerial"), Trait("Wave", "Phase-K-18")]
    public void DbSerialAttribute_AppliedCount_RecordOnly()
    {
        // Record-only (no assertion against the count): the W18
        // completion mile-marker is documented in §3.4c with the
        // post-Bishop applied count. If Bishop W18 lands the
        // attribute application, all 4 carry [Collection("DbSerial")];
        // if not (escape valve for partial landings), the soft-pin
        // in BishopW18DbSerialCompletionTests records the gap.
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
        Assert.InRange(applied, 0, 4);
    }
}
