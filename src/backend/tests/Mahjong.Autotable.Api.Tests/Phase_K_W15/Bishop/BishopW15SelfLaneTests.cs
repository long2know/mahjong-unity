using System.Reflection;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Observability;
using Mahjong.Autotable.Api.Replays;
using Mahjong.Autotable.Api.Spectator;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Bishop;

/// <summary>
/// Phase K Wave 15 — Bishop. Self-lane invariants — assert each
/// new W15 Bishop surface exists at the documented type / method
/// shape, so a future maintainer can't silently drop a deliverable
/// without a test red.
///
/// <list type="number">
///   <item>ReplayController has a <c>GetBlob</c> action method.</item>
///   <item><c>IReplayStore</c> has <c>SweepByCompletedAtAsync</c>.</item>
///   <item><c>ReplayStoreRetentionSweep</c> hosted service exists.</item>
///   <item>InMemoryReplayStore implements <c>SweepByCompletedAtAsync</c>.</item>
///   <item><c>SpectatorHandoffAuditRetentionSweep</c> hosted service exists.</item>
///   <item><c>PerTenantJwksRotationPolicy</c> entity exists.</item>
///   <item><c>IPerTenantJwksRotationStore</c> seam exists.</item>
///   <item><c>InMemoryPerTenantJwksRotationStore</c> impl exists.</item>
///   <item><c>EfPerTenantJwksRotationStore</c> impl exists.</item>
///   <item><c>PerTenantJwksRotationOptions</c> exists.</item>
///   <item><c>TournamentQueryLatencyMetrics</c> collector exists.</item>
///   <item><c>CommentaryCostController</c> has a <c>Forecast</c> action.</item>
///   <item><c>ReplayOptions</c> has <c>StoreSweepIntervalMinutes</c>.</item>
///   <item><c>SpectatorHandoffAuditOptions</c> has <c>SweepIntervalMinutes</c>.</item>
/// </list>
/// </summary>
public sealed class BishopW15SelfLaneTests
{
    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void ReplayController_GetBlob_Exists()
    {
        var m = typeof(ReplayController).GetMethod("GetBlob");
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void IReplayStore_SweepByCompletedAtAsync_Exists()
    {
        var m = typeof(IReplayStore).GetMethod("SweepByCompletedAtAsync");
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void ReplayStoreRetentionSweep_HostedService_Exists()
    {
        var t = typeof(ReplayStoreRetentionSweep);
        Assert.True(typeof(Microsoft.Extensions.Hosting.BackgroundService).IsAssignableFrom(t));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void InMemoryReplayStore_ImplementsSweepByCompletedAt()
    {
        var m = typeof(InMemoryReplayStore).GetMethod("SweepByCompletedAtAsync",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void SpectatorHandoffAuditRetentionSweep_HostedService_Exists()
    {
        Assert.True(typeof(Microsoft.Extensions.Hosting.BackgroundService)
            .IsAssignableFrom(typeof(SpectatorHandoffAuditRetentionSweep)));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotationPolicy_EntityExists()
    {
        var t = typeof(PerTenantJwksRotationPolicy);
        Assert.NotNull(t.GetProperty(nameof(PerTenantJwksRotationPolicy.TenantId)));
        Assert.NotNull(t.GetProperty(nameof(PerTenantJwksRotationPolicy.ActiveKid)));
        Assert.NotNull(t.GetProperty(nameof(PerTenantJwksRotationPolicy.RotationStartUtc)));
        Assert.NotNull(t.GetProperty(nameof(PerTenantJwksRotationPolicy.RotationCompleteUtc)));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void IPerTenantJwksRotationStore_SeamExists()
    {
        var t = typeof(IPerTenantJwksRotationStore);
        Assert.NotNull(t.GetMethod("UpsertAsync"));
        Assert.NotNull(t.GetMethod("GetAsync"));
        Assert.NotNull(t.GetMethod("ListAsync"));
        Assert.NotNull(t.GetMethod("CountAsync"));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void InMemoryPerTenantJwksRotationStore_ImplExists()
    {
        Assert.True(typeof(IPerTenantJwksRotationStore).IsAssignableFrom(
            typeof(InMemoryPerTenantJwksRotationStore)));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void EfPerTenantJwksRotationStore_ImplExists()
    {
        Assert.True(typeof(IPerTenantJwksRotationStore).IsAssignableFrom(
            typeof(EfPerTenantJwksRotationStore)));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotationOptions_Exists()
    {
        var t = typeof(PerTenantJwksRotationOptions);
        Assert.NotNull(t.GetProperty(nameof(PerTenantJwksRotationOptions.Enabled)));
        Assert.NotNull(t.GetProperty(nameof(PerTenantJwksRotationOptions.StorageImpl)));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void TournamentQueryLatencyMetrics_CollectorExists()
    {
        var t = typeof(TournamentQueryLatencyMetrics);
        Assert.NotNull(t.GetMethod("ObserveDuration"));
        Assert.NotNull(t.GetMethod("AppendPrometheus"));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void CommentaryCostController_Forecast_Exists()
    {
        var t = typeof(Mahjong.Autotable.Api.Commentary.CommentaryCostController);
        var m = t.GetMethod("Forecast");
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void ReplayOptions_StoreSweepIntervalMinutes_Exists()
    {
        var p = typeof(ReplayOptions).GetProperty("StoreSweepIntervalMinutes");
        Assert.NotNull(p);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void SpectatorHandoffAuditOptions_SweepIntervalMinutes_Exists()
    {
        var p = typeof(SpectatorHandoffAuditOptions).GetProperty("SweepIntervalMinutes");
        Assert.NotNull(p);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void RotationStartUtc_IsDateTimeOffset()
    {
        // W15 widens the rotation edges from DateTime → DateTimeOffset
        // so a non-UTC operator timezone is preserved across persistence.
        var p = typeof(PerTenantJwksRotationPolicy).GetProperty(
            nameof(PerTenantJwksRotationPolicy.RotationStartUtc))!;
        Assert.Equal(typeof(DateTimeOffset), p.PropertyType);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-15"), Trait("Lane", "Bishop")]
    public void RotationCompleteUtc_IsDateTimeOffset()
    {
        var p = typeof(PerTenantJwksRotationPolicy).GetProperty(
            nameof(PerTenantJwksRotationPolicy.RotationCompleteUtc))!;
        Assert.Equal(typeof(DateTimeOffset), p.PropertyType);
    }
}
