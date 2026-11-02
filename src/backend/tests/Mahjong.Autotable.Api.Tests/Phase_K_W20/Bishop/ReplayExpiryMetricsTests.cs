using System.Text;
using Mahjong.Autotable.Api.Replays;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Bishop;

/// <summary>
/// Phase K Wave 20 — Bishop. Tests for the per-tenant replay
/// auto-expiry counter (<see cref="ReplayExpiryMetrics"/>).
/// </summary>
public sealed class ReplayExpiryMetricsTests
{
    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void MetricName_IsReplayExpiredTotal()
    {
        Assert.Equal("replay_expired_total", ReplayExpiryMetrics.MetricName);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void TenantLabel_IsTenant()
    {
        Assert.Equal("tenant", ReplayExpiryMetrics.TenantLabel);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void UnknownBucket_IsUnderscoreUnknown()
    {
        Assert.Equal("_unknown", ReplayExpiryMetrics.UnknownTenantBucket);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Add_BumpsCounter()
    {
        var m = new ReplayExpiryMetrics();
        m.Add("tenant-a", 3);
        Assert.Equal(3, m.Get("tenant-a"));
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Add_AccumulatesAcrossCalls()
    {
        var m = new ReplayExpiryMetrics();
        m.Add("tenant-a", 1);
        m.Add("tenant-a", 4);
        m.Add("tenant-a", 7);
        Assert.Equal(12, m.Get("tenant-a"));
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Add_PerTenant_KeepsBucketsIndependent()
    {
        var m = new ReplayExpiryMetrics();
        m.Add("tenant-a", 5);
        m.Add("tenant-b", 9);
        Assert.Equal(5, m.Get("tenant-a"));
        Assert.Equal(9, m.Get("tenant-b"));
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Add_EmptyTenant_BucketsUnderUnderscore_Unknown()
    {
        var m = new ReplayExpiryMetrics();
        m.Add("", 4);
        Assert.Equal(4, m.Get("_unknown"));
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Add_NullTenant_BucketsUnderUnderscore_Unknown()
    {
        var m = new ReplayExpiryMetrics();
        m.Add(null, 2);
        m.Add(null, 3);
        Assert.Equal(5, m.Get("_unknown"));
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Add_ZeroOrNegativeDelta_NoOp()
    {
        var m = new ReplayExpiryMetrics();
        m.Add("tenant-a", 0);
        m.Add("tenant-a", -5);
        Assert.Equal(0, m.Get("tenant-a"));
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Get_UnknownTenant_ReturnsZero()
    {
        var m = new ReplayExpiryMetrics();
        Assert.Equal(0, m.Get("no-such-tenant"));
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Snapshot_IsDetachedCopy()
    {
        var m = new ReplayExpiryMetrics();
        m.Add("tenant-a", 1);
        var snap = m.Snapshot();
        Assert.Equal(1, snap["tenant-a"]);
        m.Add("tenant-a", 5);
        // Snapshot is detached.
        Assert.Equal(1, snap["tenant-a"]);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsHelpAndTypeHeaders()
    {
        var m = new ReplayExpiryMetrics();
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains("# HELP replay_expired_total", text);
        Assert.Contains("# TYPE replay_expired_total counter", text);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsZeroedUnknownWhenEmpty()
    {
        var m = new ReplayExpiryMetrics();
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        Assert.Contains("replay_expired_total{tenant=\"_unknown\"} 0", sb.ToString());
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EmitsPerTenantRows()
    {
        var m = new ReplayExpiryMetrics();
        m.Add("tenant-a", 4);
        m.Add("tenant-b", 7);
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        Assert.Contains("replay_expired_total{tenant=\"tenant-a\"} 4", text);
        Assert.Contains("replay_expired_total{tenant=\"tenant-b\"} 7", text);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_StableOrdinalSort()
    {
        var m = new ReplayExpiryMetrics();
        m.Add("zebra", 1);
        m.Add("alpha", 1);
        m.Add("mike", 1);
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        var text = sb.ToString();
        var iAlpha = text.IndexOf("\"alpha\"", StringComparison.Ordinal);
        var iMike = text.IndexOf("\"mike\"", StringComparison.Ordinal);
        var iZebra = text.IndexOf("\"zebra\"", StringComparison.Ordinal);
        Assert.True(iAlpha >= 0 && iMike > iAlpha && iZebra > iMike);
    }

    [Fact, Trait("Category", "Metrics"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void AppendPrometheus_EscapesLabelChars()
    {
        var m = new ReplayExpiryMetrics();
        m.Add("tenant\"with-quote", 1);
        var sb = new StringBuilder();
        m.AppendPrometheus(sb);
        Assert.Contains(@"tenant\""with-quote", sb.ToString());
    }
}
