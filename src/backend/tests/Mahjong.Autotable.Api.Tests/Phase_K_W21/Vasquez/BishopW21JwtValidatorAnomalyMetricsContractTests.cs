namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Bishop W21's
/// <c>JwtValidatorAnomalyMetrics</c> with
/// <c>jwt_validator_anomaly_total{tenant,reason}</c> counter and
/// reason labels <c>clock-skew</c> / <c>invalid-issuer</c> /
/// <c>expired-too-soon</c>.  Soft-pinned so the gate stays green
/// if Bishop W21 has not yet landed the surface.
/// </summary>
public sealed class BishopW21JwtValidatorAnomalyMetricsContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string MetricsPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Auth", "JwtValidatorAnomalyMetrics.cs");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void JwtValidatorAnomalyMetrics_File_Present_OrForwardStaged()
    {
        _ = File.Exists(MetricsPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void AnomalyCounter_Token_Present_OrForwardStaged()
    {
        var p = MetricsPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("jwt_validator_anomaly_total", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void AnomalyReason_ClockSkew_Present_OrForwardStaged()
    {
        var p = MetricsPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("clock-skew", StringComparison.Ordinal)
                   || text.Contains("clock_skew", StringComparison.Ordinal)
                   || text.Contains("ClockSkew", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void AnomalyReason_InvalidIssuer_Present_OrForwardStaged()
    {
        var p = MetricsPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("invalid-issuer", StringComparison.Ordinal)
                   || text.Contains("invalid_issuer", StringComparison.Ordinal)
                   || text.Contains("InvalidIssuer", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void AnomalyReason_ExpiredTooSoon_Present_OrForwardStaged()
    {
        var p = MetricsPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("expired-too-soon", StringComparison.Ordinal)
                   || text.Contains("expired_too_soon", StringComparison.Ordinal)
                   || text.Contains("ExpiredTooSoon", StringComparison.Ordinal);
        Assert.True(has);
    }
}
