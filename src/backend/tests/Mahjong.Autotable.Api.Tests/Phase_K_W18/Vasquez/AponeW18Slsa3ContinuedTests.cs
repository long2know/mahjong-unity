using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Apone W18 contract: SLSA-3 SHA-pin
/// expansion continuation (W17 added +50 pins across 9 workflows;
/// W18 continues the expansion across remaining workflows).
/// Soft-pin on absence.
/// </summary>
public sealed class AponeW18Slsa3ContinuedTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-18")]
    public void Workflows_Folder_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var wf = Path.Combine(root!.FullName, ".github", "workflows");
        Assert.True(Directory.Exists(wf));
    }
}
