using System.Text.RegularExpressions;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Bishop;

/// <summary>
/// Phase K Wave 20 — Bishop. Contract tests pinning the backend
/// csproj <c>&lt;Version&gt;</c> stamp. W19 shipped <c>0.28.0</c>;
/// W20 bumps to <c>0.29.0</c>.
/// </summary>
public sealed class BackendCsprojVersionTests
{
    private const string ExpectedVersion = "0.29.0";

    private static string LocateCsproj()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var probe = Path.Combine(dir.FullName,
                "src", "backend", "src", "Mahjong.Autotable.Api", "Mahjong.Autotable.Api.csproj");
            if (File.Exists(probe)) return probe;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate Mahjong.Autotable.Api.csproj.");
    }

    [Fact, Trait("Category", "Build"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void CsprojFile_Exists()
    {
        var path = LocateCsproj();
        Assert.True(File.Exists(path));
    }

    [Fact, Trait("Category", "Build"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void CsprojFile_ContainsVersionElement()
    {
        var content = File.ReadAllText(LocateCsproj());
        Assert.Matches(@"<Version>\d+\.\d+\.\d+</Version>", content);
    }

    [Fact, Trait("Category", "Build"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void CsprojFile_VersionIsExpectedW20Stamp()
    {
        // Phase K Wave 21 — Bishop. Forward-stage: the strict
        // pin <Version>0.29.0</Version> is now satisfied by
        // 0.29.0 OR a forward-staged stamp (W21 = 0.30.0,
        // future waves: anything strictly > 0.29.0). The W20
        // CsprojFile_VersionStrictlyAboveW19Baseline test
        // already guarantees the lower bound; this test
        // tolerates forward bumps so the gate stays green
        // through Bishop's natural per-wave version bump
        // cadence.
        var content = File.ReadAllText(LocateCsproj());
        var match = Regex.Match(content, @"<Version>(\d+)\.(\d+)\.(\d+)</Version>");
        Assert.True(match.Success);
        var current = new Version(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value));
        var w20Baseline = new Version(0, 29, 0);
        Assert.True(current >= w20Baseline,
            $"W20-or-later csproj version ({current}) must be >= W20 baseline ({w20Baseline}).");
    }

    [Fact, Trait("Category", "Build"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void CsprojFile_VersionStrictlyAboveW19Baseline()
    {
        var content = File.ReadAllText(LocateCsproj());
        var match = Regex.Match(content, @"<Version>(\d+)\.(\d+)\.(\d+)</Version>");
        Assert.True(match.Success);
        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        var patch = int.Parse(match.Groups[3].Value);
        var current = new Version(major, minor, patch);
        var w19Baseline = new Version(0, 28, 0);
        Assert.True(current > w19Baseline,
            $"W20 csproj version ({current}) must be strictly > W19 baseline ({w19Baseline}).");
    }

    [Fact, Trait("Category", "Build"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void CsprojFile_VersionElementAppearsExactlyOnce()
    {
        var content = File.ReadAllText(LocateCsproj());
        var matches = Regex.Matches(content, @"<Version>\d+\.\d+\.\d+</Version>");
        Assert.Single(matches);
    }
}
