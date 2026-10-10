using System.Text.RegularExpressions;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Bishop;

/// <summary>
/// Phase K Wave 20 — Bishop. Contract tests pinning the two
/// W20 Swiss-pairing alert rules appended to
/// <c>tournament-query-duration.yaml</c>:
/// <list type="bullet">
///   <item><c>SwissPairingDurationHigh</c> — p95 > 5s ticket;</item>
///   <item><c>SwissPairingDurationCritical</c> — p95 > 15s page.</item>
/// </list>
/// The W18 surface already landed the p99 page rail; W20 adds
/// the p95 ticket + p95 critical rails so the operator has a
/// three-rail Swiss alert envelope. The tests pin alert names,
/// thresholds, team labels, wave labels, and severity routing.
/// </summary>
public sealed class SwissPairingAlertsW20ContractTests
{
    private static string LoadYaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var probe = Path.Combine(dir.FullName,
                "src", "backend", "src", "Mahjong.Autotable.Api",
                "Observability", "Alerts", "tournament-query-duration.yaml");
            if (File.Exists(probe)) return File.ReadAllText(probe);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate tournament-query-duration.yaml.");
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Yaml_DefinesSwissPairingDurationHighAlert()
    {
        Assert.Contains("alert: SwissPairingDurationHigh", LoadYaml());
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Yaml_DefinesSwissPairingDurationCriticalAlert()
    {
        Assert.Contains("alert: SwissPairingDurationCritical", LoadYaml());
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Yaml_W20_HighAlert_Threshold_5s()
    {
        var y = LoadYaml();
        Assert.Matches(
            new Regex(@"SwissPairingDurationHigh[\s\S]*?> 5\b", RegexOptions.Multiline),
            y);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Yaml_W20_CriticalAlert_Threshold_15s()
    {
        var y = LoadYaml();
        Assert.Matches(
            new Regex(@"SwissPairingDurationCritical[\s\S]*?> 15\b", RegexOptions.Multiline),
            y);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Yaml_W20_HighAlert_Severity_Ticket()
    {
        var y = LoadYaml();
        Assert.Matches(
            new Regex(@"SwissPairingDurationHigh[\s\S]*?severity:\s*ticket", RegexOptions.Multiline),
            y);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Yaml_W20_CriticalAlert_Severity_Page()
    {
        var y = LoadYaml();
        Assert.Matches(
            new Regex(@"SwissPairingDurationCritical[\s\S]*?severity:\s*page", RegexOptions.Multiline),
            y);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Yaml_W20_BothAlerts_UseP95()
    {
        var y = LoadYaml();
        Assert.Matches(
            new Regex(@"SwissPairingDurationHigh[\s\S]*?histogram_quantile\(0\.95", RegexOptions.Multiline),
            y);
        Assert.Matches(
            new Regex(@"SwissPairingDurationCritical[\s\S]*?histogram_quantile\(0\.95", RegexOptions.Multiline),
            y);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Yaml_W20_BothAlerts_TeamBishop()
    {
        var y = LoadYaml();
        Assert.Matches(
            new Regex(@"SwissPairingDurationHigh[\s\S]*?team:\s*bishop", RegexOptions.Multiline),
            y);
        Assert.Matches(
            new Regex(@"SwissPairingDurationCritical[\s\S]*?team:\s*bishop", RegexOptions.Multiline),
            y);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Yaml_W20_BothAlerts_CarryW20WaveLabel()
    {
        var y = LoadYaml();
        var w20Count = Regex.Matches(y, @"wave:\s*phase-k-w20").Count;
        Assert.True(w20Count >= 2, $"expected at least 2 wave:phase-k-w20 labels, found {w20Count}");
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Yaml_W20_BothAlerts_RateOnSwissHistogram()
    {
        var y = LoadYaml();
        // Both alerts must rate-on the swiss histogram bucket.
        var rateCount = Regex.Matches(y, @"rate\(swiss_pairing_duration_seconds_bucket\[5m\]\)").Count;
        Assert.True(rateCount >= 3, $"expected at least 3 rate() calls on swiss_pairing histogram (W18 + W20×2), found {rateCount}");
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Yaml_W20_BothAlerts_CarryRunbookUrl()
    {
        var y = LoadYaml();
        Assert.Matches(
            new Regex(@"SwissPairingDurationHigh[\s\S]*?runbook_url:[^\n]*swiss-pairing-p95-high", RegexOptions.Multiline),
            y);
        Assert.Matches(
            new Regex(@"SwissPairingDurationCritical[\s\S]*?runbook_url:[^\n]*swiss-pairing-p95-critical", RegexOptions.Multiline),
            y);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Yaml_W20_BothAlerts_FireDuration_5m()
    {
        var y = LoadYaml();
        Assert.Matches(
            new Regex(@"SwissPairingDurationHigh[\s\S]*?for:\s*5m", RegexOptions.Multiline),
            y);
        Assert.Matches(
            new Regex(@"SwissPairingDurationCritical[\s\S]*?for:\s*5m", RegexOptions.Multiline),
            y);
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Yaml_W20_TotalAlertCount_AtLeast_7()
    {
        var y = LoadYaml();
        // W17 (2) + W18 (3) + W20 (2) = 7 total alert: declarations.
        var alertCount = Regex.Matches(y, @"^\s*- alert:\s+", RegexOptions.Multiline).Count;
        Assert.True(alertCount >= 7, $"expected at least 7 alert: declarations after W20, found {alertCount}");
    }

    [Fact, Trait("Category", "Alerts"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Yaml_W20_EveryAlert_CarriesTeamBishop()
    {
        var y = LoadYaml();
        var alertCount = Regex.Matches(y, @"^\s*- alert:\s+", RegexOptions.Multiline).Count;
        var teamCount = Regex.Matches(y, @"team:\s*bishop").Count;
        Assert.Equal(alertCount, teamCount);
    }
}
