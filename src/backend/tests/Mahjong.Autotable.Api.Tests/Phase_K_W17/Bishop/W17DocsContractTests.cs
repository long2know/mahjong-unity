namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Bishop;

/// <summary>
/// Phase K Wave 17 — Bishop. Verifies that the operator-facing
/// documents shipped this wave are present and contain the
/// minimum required structural anchors (linked from alerts +
/// inbox memos).
/// </summary>
public sealed class W17DocsContractTests
{
    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void TournamentQueryRunbook_Present()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root!.FullName, "docs",
            "tournament-query-duration-runbook.md");
        Assert.True(File.Exists(path));
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void TournamentQueryRunbook_HasTitle()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root!.FullName, "docs",
            "tournament-query-duration-runbook.md");
        var text = File.ReadAllText(path);
        Assert.Contains("tournament query duration", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runbook", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void TournamentQueryRunbook_HasBothAlertSections()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root!.FullName, "docs",
            "tournament-query-duration-runbook.md");
        var text = File.ReadAllText(path);
        Assert.Contains("p99-page", text);
        Assert.Contains("p95-ticket", text);
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void TournamentQueryRunbook_NotEmpty()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root!.FullName, "docs",
            "tournament-query-duration-runbook.md");
        var len = new FileInfo(path).Length;
        Assert.True(len > 400,
            $"Runbook should be at least 400 bytes (was {len})");
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void AlertsFile_LivesInBackendLane()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root!.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Observability", "Alerts",
            "tournament-query-duration.yaml");
        Assert.True(File.Exists(path),
            "Alerts YAML must stay under the Bishop backend lane");
    }

    private static DirectoryInfo? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                && Directory.Exists(Path.Combine(dir.FullName, ".squad")))
            {
                return dir;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
