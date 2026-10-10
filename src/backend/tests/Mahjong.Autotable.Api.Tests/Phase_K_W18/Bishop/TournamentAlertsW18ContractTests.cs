using Mahjong.Autotable.Api.Observability;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Bishop;

/// <summary>
/// Phase K Wave 18 — Bishop. Contract tests for the expanded
/// tournament-query alert YAML + the runtime
/// <see cref="TournamentQueryAlertThresholds"/> constants. Pins
/// the W18 alert rules (bracket p99, swiss-pairing p99,
/// heartbeat) + the threshold constants against the YAML
/// literals so the two halves can't drift silently.
/// </summary>
public sealed class TournamentAlertsW18ContractTests
{
    private static string LoadYaml()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName,
            "src", "backend", "src", "Mahjong.Autotable.Api",
            "Observability", "Alerts", "tournament-query-duration.yaml");
        Assert.True(File.Exists(path));
        return File.ReadAllText(path);
    }

    private static string LoadRunbook()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root!.FullName, "docs", "tournament-query-duration-runbook.md");
        return File.ReadAllText(path);
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

    // ─── new W18 alert presence ────────────────────────────────

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Yaml_DefinesBracketP99PageAlert()
    {
        Assert.Contains("alert: BracketQueryDurationP99HighPage", LoadYaml());
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Yaml_DefinesSwissPairingP99PageAlert()
    {
        Assert.Contains("alert: SwissPairingDurationP99HighPage", LoadYaml());
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Yaml_DefinesHeartbeatAlert()
    {
        Assert.Contains("alert: TournamentQueryNoTrafficHeartbeat", LoadYaml());
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Yaml_BracketAlert_Threshold_1s()
    {
        var y = LoadYaml();
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"BracketQueryDurationP99HighPage[\s\S]*?> 1\.0",
                System.Text.RegularExpressions.RegexOptions.Multiline),
            y);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Yaml_SwissAlert_Threshold_1s()
    {
        var y = LoadYaml();
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"SwissPairingDurationP99HighPage[\s\S]*?> 1\.0",
                System.Text.RegularExpressions.RegexOptions.Multiline),
            y);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Yaml_HeartbeatAlert_NoTraffic_10m()
    {
        var y = LoadYaml();
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"TournamentQueryNoTrafficHeartbeat[\s\S]*?for:\s*10m",
                System.Text.RegularExpressions.RegexOptions.Multiline),
            y);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Yaml_BracketAlert_Severity_Page()
    {
        var y = LoadYaml();
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"BracketQueryDurationP99HighPage[\s\S]*?severity:\s*page",
                System.Text.RegularExpressions.RegexOptions.Multiline),
            y);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Yaml_SwissAlert_Severity_Page()
    {
        var y = LoadYaml();
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"SwissPairingDurationP99HighPage[\s\S]*?severity:\s*page",
                System.Text.RegularExpressions.RegexOptions.Multiline),
            y);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Yaml_HeartbeatAlert_Severity_Ticket()
    {
        var y = LoadYaml();
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"TournamentQueryNoTrafficHeartbeat[\s\S]*?severity:\s*ticket",
                System.Text.RegularExpressions.RegexOptions.Multiline),
            y);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Yaml_BracketAlert_RatesOn_BracketHistogram()
    {
        Assert.Contains("rate(bracket_query_duration_seconds_bucket[5m])", LoadYaml());
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Yaml_SwissAlert_RatesOn_SwissHistogram()
    {
        Assert.Contains("rate(swiss_pairing_duration_seconds_bucket[5m])", LoadYaml());
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Yaml_HeartbeatAlert_Uses_CountSeries()
    {
        Assert.Contains("rate(tournament_query_duration_seconds_count[10m])", LoadYaml());
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Yaml_W18_AlertsCarry_TeamBishop()
    {
        var y = LoadYaml();
        var teamCount = System.Text.RegularExpressions.Regex.Matches(y, @"team:\s*bishop").Count;
        // Phase K Wave 20 — Bishop. Relaxed from exact-5 pin to
        // strict-AT-OR-ABOVE so future-wave alert additions don't
        // trip this contract. The W20 test
        // (Phase_K_W20/Bishop/SwissPairingAlertsW20ContractTests)
        // takes over the exact-count pin for the W20 surface.
        Assert.True(teamCount >= 5,
            $"expected at least the W18 baseline of 5 team:bishop labels, found {teamCount}");
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Yaml_W18_AlertsCarry_WaveLabel()
    {
        var y = LoadYaml();
        var w18Count = System.Text.RegularExpressions.Regex.Matches(y, @"wave:\s*phase-k-w18").Count;
        Assert.Equal(3, w18Count);
    }

    // ─── threshold constants pin against YAML ──────────────────

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Thresholds_TournamentP99_Is500ms()
    {
        Assert.Equal(0.5, TournamentQueryAlertThresholds.TournamentP99PageSeconds);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Thresholds_TournamentP95_Is250ms()
    {
        Assert.Equal(0.25, TournamentQueryAlertThresholds.TournamentP95TicketSeconds);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Thresholds_BracketP99_Is1s()
    {
        Assert.Equal(1.0, TournamentQueryAlertThresholds.BracketP99PageSeconds);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Thresholds_SwissP99_Is1s()
    {
        Assert.Equal(1.0, TournamentQueryAlertThresholds.SwissPairingP99PageSeconds);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Thresholds_Heartbeat_Is10m()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), TournamentQueryAlertThresholds.HeartbeatNoTrafficWindow);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Thresholds_PageWindow_Is5m()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), TournamentQueryAlertThresholds.PageRateWindow);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Thresholds_TicketWindow_Is15m()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), TournamentQueryAlertThresholds.TicketRateWindow);
    }

    // ─── runbook anchors for new alerts ────────────────────────

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Runbook_Covers_BracketP99()
    {
        Assert.Contains("bracket-p99-page", LoadRunbook());
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Runbook_Covers_SwissPairing()
    {
        Assert.Contains("swiss-pairing-p99", LoadRunbook());
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Runbook_Covers_Heartbeat()
    {
        Assert.Contains("### heartbeat", LoadRunbook());
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-18"), Trait("Lane", "Bishop")]
    public void Runbook_Mentions_W18_Extension()
    {
        Assert.Contains("W18", LoadRunbook());
    }
}
