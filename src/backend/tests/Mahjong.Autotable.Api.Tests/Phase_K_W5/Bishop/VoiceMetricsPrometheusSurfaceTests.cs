using System.Reflection;
using Mahjong.Autotable.Api.Voice;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W5.Bishop;

/// <summary>
/// Phase K Wave 5 — Bishop. Pin the labeled Prometheus surface added
/// in Wave 5 — <see cref="VoiceHubMetricsService.Snapshot"/> returns
/// labeled samples; the labeled overloads of <c>RecordRelay</c>,
/// <c>RecordRateLimitRejection</c>, and <c>RecordJoinUnauthorized</c>
/// all exist; <see cref="VoiceHubMetrics.ReasonUnknown"/> +
/// <see cref="VoiceHubMetrics.ReasonRateLimited"/> string constants
/// land for stable reason-label cardinality.
/// </summary>
public sealed class VoiceMetricsPrometheusSurfaceTests
{
    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-5"), Trait("Lane", "Bishop")]
    public void VoiceHubMetrics_HasReasonUnknown_AndReasonRateLimited()
    {
        Assert.Equal("unknown", VoiceHubMetrics.ReasonUnknown);
        Assert.Equal("rate-limited", VoiceHubMetrics.ReasonRateLimited);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-5"), Trait("Lane", "Bishop")]
    public void VoiceHubMetricsService_Exposes_LabeledOverloads_And_Snapshot()
    {
        var t = typeof(VoiceHubMetricsService);
        var relayLabeled = t.GetMethod(nameof(VoiceHubMetricsService.RecordRelay),
            new[] { typeof(string), typeof(string) });
        Assert.NotNull(relayLabeled);

        var rejectionLabeled = t.GetMethod(nameof(VoiceHubMetricsService.RecordRateLimitRejection),
            new[] { typeof(string), typeof(string) });
        Assert.NotNull(rejectionLabeled);

        var joinLabeled = t.GetMethod(nameof(VoiceHubMetricsService.RecordJoinUnauthorized),
            new[] { typeof(string), typeof(string) });
        Assert.NotNull(joinLabeled);

        var snapshot = t.GetMethod(nameof(VoiceHubMetricsService.Snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(
            typeof(System.Collections.Generic.IReadOnlyList<LabeledMetricSample>),
            snapshot!.ReturnType);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-5"), Trait("Lane", "Bishop")]
    public void Snapshot_AccumulatesByTableAndReason_MonotonicCounters()
    {
        var svc = new VoiceHubMetricsService();

        // Relay surface — relay-count only carries the table label.
        svc.RecordRelay("conn-1", "table-A");
        svc.RecordRelay("conn-1", "table-A");
        svc.RecordRelay("conn-2", "table-B");

        // Rate-limit-rejection — carries both table + reason.
        svc.RecordRateLimitRejection("table-A", VoiceHubMetrics.ReasonRateLimited);
        svc.RecordRateLimitRejection("table-A", VoiceHubMetrics.ReasonRateLimited);

        // Join-unauthorized — carries both table + reason.
        svc.RecordJoinUnauthorized("table-A", VoiceHubResult.ReasonSpectator);
        svc.RecordJoinUnauthorized("table-A", VoiceHubResult.ReasonNotSeated);
        svc.RecordJoinUnauthorized("table-A", VoiceHubResult.ReasonSpectator);

        var snap = svc.Snapshot();
        Assert.NotEmpty(snap);

        var relayA = snap.FirstOrDefault(s =>
            s.Metric == VoiceHubMetrics.MetricRelayCount && s.Table == "table-A");
        Assert.NotNull(relayA);
        Assert.Equal(2L, relayA!.Value);
        Assert.Null(relayA.Reason);

        var relayB = snap.FirstOrDefault(s =>
            s.Metric == VoiceHubMetrics.MetricRelayCount && s.Table == "table-B");
        Assert.NotNull(relayB);
        Assert.Equal(1L, relayB!.Value);

        var rejectA = snap.FirstOrDefault(s =>
            s.Metric == VoiceHubMetrics.MetricRateLimitRejection
            && s.Table == "table-A"
            && s.Reason == VoiceHubMetrics.ReasonRateLimited);
        Assert.NotNull(rejectA);
        Assert.Equal(2L, rejectA!.Value);

        var spectatorA = snap.FirstOrDefault(s =>
            s.Metric == VoiceHubMetrics.MetricJoinUnauthorized
            && s.Table == "table-A"
            && s.Reason == VoiceHubResult.ReasonSpectator);
        Assert.NotNull(spectatorA);
        Assert.Equal(2L, spectatorA!.Value);

        var notSeatedA = snap.FirstOrDefault(s =>
            s.Metric == VoiceHubMetrics.MetricJoinUnauthorized
            && s.Table == "table-A"
            && s.Reason == VoiceHubResult.ReasonNotSeated);
        Assert.NotNull(notSeatedA);
        Assert.Equal(1L, notSeatedA!.Value);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-5"), Trait("Lane", "Bishop")]
    public void Snapshot_NullOrEmptyTable_NormalizesToUnknown()
    {
        var svc = new VoiceHubMetricsService();
        svc.RecordRelay("conn-x", null);
        svc.RecordRelay("conn-y", string.Empty);
        svc.RecordRelay("conn-z", "  ");

        var snap = svc.Snapshot();
        var unknown = snap.FirstOrDefault(s =>
            s.Metric == VoiceHubMetrics.MetricRelayCount && s.Table == "unknown");
        Assert.NotNull(unknown);
        Assert.Equal(3L, unknown!.Value);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-5"), Trait("Lane", "Bishop")]
    public void Snapshot_NullReason_NormalizesToReasonUnknown()
    {
        var svc = new VoiceHubMetricsService();
        svc.RecordJoinUnauthorized("table-A", null);

        var snap = svc.Snapshot();
        var unknown = snap.FirstOrDefault(s =>
            s.Metric == VoiceHubMetrics.MetricJoinUnauthorized
            && s.Reason == VoiceHubMetrics.ReasonUnknown);
        Assert.NotNull(unknown);
        Assert.Equal(1L, unknown!.Value);
    }
}
