namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Bishop;

/// <summary>
/// Phase K Wave 17 — Bishop. Contract tests for the tournament-
/// query-duration Prometheus alert rules. Asserts shape +
/// canonical thresholds so a future refactor can't silently
/// loosen the operator wake-up envelope.
/// </summary>
public sealed class TournamentAlertsContractTests
{
    private static string LoadAlertsYaml()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName,
            "src", "backend", "src", "Mahjong.Autotable.Api",
            "Observability", "Alerts", "tournament-query-duration.yaml");
        Assert.True(File.Exists(path));
        return File.ReadAllText(path);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Yaml_Present_InRepo()
    {
        Assert.NotEmpty(LoadAlertsYaml());
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Yaml_HasGroupBlock()
    {
        Assert.Contains("groups:", LoadAlertsYaml());
        Assert.Contains("- name: tournament-query-duration", LoadAlertsYaml());
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Yaml_DefinesP99PageAlert()
    {
        Assert.Contains("alert: TournamentQueryDurationP99HighPage", LoadAlertsYaml());
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Yaml_DefinesP95TicketAlert()
    {
        Assert.Contains("alert: TournamentQueryDurationP95HighTicket", LoadAlertsYaml());
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Yaml_PageAlertThreshold_Is500ms()
    {
        var text = LoadAlertsYaml();
        // p99 threshold 0.5 seconds.
        Assert.Contains("> 0.5", text);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Yaml_TicketAlertThreshold_Is250ms()
    {
        var text = LoadAlertsYaml();
        // p95 threshold 0.25 seconds.
        Assert.Contains("> 0.25", text);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Yaml_PageAlertWindow_Is5min()
    {
        var text = LoadAlertsYaml();
        Assert.Contains("rate(tournament_query_duration_seconds_bucket[5m])", text);
        // PAGE rail uses 5m for: window.
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"P99HighPage[\s\S]*?for:\s*5m",
                System.Text.RegularExpressions.RegexOptions.Multiline),
            text);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Yaml_TicketAlertWindow_Is15min()
    {
        var text = LoadAlertsYaml();
        Assert.Contains("rate(tournament_query_duration_seconds_bucket[15m])", text);
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"P95HighTicket[\s\S]*?for:\s*15m",
                System.Text.RegularExpressions.RegexOptions.Multiline),
            text);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Yaml_PageAlertSeverity_Is_Page()
    {
        var text = LoadAlertsYaml();
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"P99HighPage[\s\S]*?severity:\s*page",
                System.Text.RegularExpressions.RegexOptions.Multiline),
            text);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Yaml_TicketAlertSeverity_Is_Ticket()
    {
        var text = LoadAlertsYaml();
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"P95HighTicket[\s\S]*?severity:\s*ticket",
                System.Text.RegularExpressions.RegexOptions.Multiline),
            text);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Yaml_BothAlertsCarry_TeamBishop()
    {
        var text = LoadAlertsYaml();
        // W17 shipped two alerts; W18 added three more. The
        // contract is that every alert in the file carries the
        // bishop team label — assert that the team-label count
        // matches the alert count and that we have at least the
        // W17 baseline.
        var alertCount = System.Text.RegularExpressions.Regex.Matches(text, @"^\s*- alert:\s+", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        var teamBishopCount = System.Text.RegularExpressions.Regex.Matches(text, @"team:\s*bishop").Count;
        Assert.True(alertCount >= 2, $"expected at least the W17 baseline of 2 alerts, found {alertCount}");
        Assert.Equal(alertCount, teamBishopCount);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Yaml_BothAlertsCarry_RunbookUrl()
    {
        var text = LoadAlertsYaml();
        Assert.Contains("runbook_url:", text);
        Assert.Contains("tournament-query-duration-runbook.md", text);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Runbook_Present_InRepo()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs",
            "tournament-query-duration-runbook.md");
        Assert.True(File.Exists(path));
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void Runbook_Covers_BothAnchors()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root!.FullName, "docs",
            "tournament-query-duration-runbook.md");
        var text = File.ReadAllText(path);
        // The alerts link to #p99-page and #p95-ticket fragments.
        Assert.Contains("p99-page", text);
        Assert.Contains("p95-ticket", text);
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
