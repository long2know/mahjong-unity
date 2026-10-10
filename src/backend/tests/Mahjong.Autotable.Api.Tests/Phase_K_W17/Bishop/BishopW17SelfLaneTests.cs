using System.Reflection;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Commentary;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Observability;
using Mahjong.Autotable.Api.Replays;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Bishop;

/// <summary>
/// Phase K Wave 17 — Bishop. Self-lane invariants — every W17
/// Bishop deliverable surfaces a hard reflection assertion here
/// so a future maintainer can't silently drop one without a red
/// test. Pattern mirrors <c>BishopW16SelfLaneTests</c>.
/// </summary>
public sealed class BishopW17SelfLaneTests
{
    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void JwtIssueBlockedMetrics_TypeExists()
    {
        Assert.NotNull(typeof(JwtIssueBlockedMetrics));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void JwtIssueBlockedMetrics_MetricName_IsWireStable()
    {
        Assert.Equal("jwt_issue_blocked_total", JwtIssueBlockedMetrics.MetricName);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void JwtIssueBlockedMetrics_ReasonStalePerTenantPolicy_IsWireStable()
    {
        Assert.Equal("stale_per_tenant_policy",
            JwtIssueBlockedMetrics.ReasonStalePerTenantPolicy);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void JwtIssueBlockedMetrics_ReasonPerTenantStoreMissing_IsWireStable()
    {
        Assert.Equal("per_tenant_store_missing",
            JwtIssueBlockedMetrics.ReasonPerTenantStoreMissing);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void JwtIssuingService_IssueForTenantAsync_Exists()
    {
        var m = typeof(JwtIssuingService).GetMethod("IssueForTenantAsync");
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void IPerTenantJwksRotationStore_DeleteAsync_Exists()
    {
        var m = typeof(IPerTenantJwksRotationStore).GetMethod("DeleteAsync");
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void PerTenantRotationAdminController_KindHardDeleted_IsWireStable()
    {
        Assert.Equal("auth.jwks.per-tenant.hard-deleted",
            PerTenantRotationAdminController.KindHardDeleted);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ReplayRetentionAdminController_TypeExists()
    {
        Assert.NotNull(typeof(ReplayRetentionAdminController));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ReplayRetentionAdminController_AdminReasonHeader_IsWireStable()
    {
        Assert.Equal("X-Admin-Reason", ReplayRetentionAdminController.AdminReasonHeader);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ReplayRetentionAdminController_MethodsExist()
    {
        var t = typeof(ReplayRetentionAdminController);
        Assert.NotNull(t.GetMethod("List"));
        Assert.NotNull(t.GetMethod("Get"));
        Assert.NotNull(t.GetMethod("Create"));
        Assert.NotNull(t.GetMethod("Update"));
        Assert.NotNull(t.GetMethod("Delete"));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void CommentaryController_CommentaryAdminReasonHeader_IsWireStable()
    {
        Assert.Equal("X-Admin-Reason", CommentaryController.CommentaryAdminReasonHeader);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void SignalRRetentionPolicy_TypeExists()
    {
        Assert.NotNull(typeof(SignalRRetentionPolicy));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void SignalRRetentionPolicy_DefaultRetentionMinutes_Is24Hours()
    {
        Assert.Equal(1440, SignalRRetentionPolicy.DefaultRetentionMinutes);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ISignalRRetentionPolicyStore_TypeExists()
    {
        Assert.NotNull(typeof(ISignalRRetentionPolicyStore));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void InMemorySignalRRetentionPolicyStore_TypeExists()
    {
        Assert.NotNull(typeof(InMemorySignalRRetentionPolicyStore));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void EfSignalRRetentionPolicyStore_TypeExists()
    {
        Assert.NotNull(typeof(EfSignalRRetentionPolicyStore));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ISignalRSequenceStore_SweepExpiredWithPerTenantPolicyAsync_Exists()
    {
        var m = typeof(ISignalRSequenceStore)
            .GetMethod("SweepExpiredWithPerTenantPolicyAsync");
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void SignalRSequenceEntry_TenantId_PropertyExists()
    {
        var p = typeof(SignalRSequenceEntry).GetProperty("TenantId");
        Assert.NotNull(p);
        Assert.Equal(typeof(string), p!.PropertyType);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void SignalRRetentionAdminController_TypeExists()
    {
        Assert.NotNull(typeof(SignalRRetentionAdminController));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void SignalRRetentionAdminController_MethodsExist()
    {
        var t = typeof(SignalRRetentionAdminController);
        Assert.NotNull(t.GetMethod("List"));
        Assert.NotNull(t.GetMethod("Get"));
        Assert.NotNull(t.GetMethod("Create"));
        Assert.NotNull(t.GetMethod("Update"));
        Assert.NotNull(t.GetMethod("Delete"));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void DateTimeOffsetWideningR2_TypeExists()
    {
        Assert.NotNull(typeof(DateTimeOffsetWideningR2));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void DateTimeOffsetWideningR2_WaveTag_IsWireStable()
    {
        Assert.Equal("phase-k-w17-r2", DateTimeOffsetWideningR2.WaveTag);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ReconnectAuditEntry_KindAuthJwtIssueBlockedStale_IsWireStable()
    {
        Assert.Equal("auth.jwt.issue.blocked.stale_per_tenant_policy",
            ReconnectAuditEntry.KindAuthJwtIssueBlockedStale);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ReconnectAuditEntry_KindAuthJwksPerTenantHardDeleted_IsWireStable()
    {
        Assert.Equal("auth.jwks.per-tenant.hard-deleted",
            ReconnectAuditEntry.KindAuthJwksPerTenantHardDeleted);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ReconnectAuditEntry_KindCommentaryAdminOverride_IsWireStable()
    {
        Assert.Equal("commentary.admin.override",
            ReconnectAuditEntry.KindCommentaryAdminOverride);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ReconnectAuditEntry_KindReplayRetentionCreated_IsWireStable()
    {
        Assert.Equal("replays.retention.created",
            ReconnectAuditEntry.KindReplayRetentionCreated);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ReconnectAuditEntry_KindReplayRetentionUpdated_IsWireStable()
    {
        Assert.Equal("replays.retention.updated",
            ReconnectAuditEntry.KindReplayRetentionUpdated);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ReconnectAuditEntry_KindReplayRetentionDeleted_IsWireStable()
    {
        Assert.Equal("replays.retention.deleted",
            ReconnectAuditEntry.KindReplayRetentionDeleted);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ReconnectAuditEntry_KindSignalRRetentionCreated_IsWireStable()
    {
        Assert.Equal("signalr.retention.created",
            ReconnectAuditEntry.KindSignalRRetentionCreated);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ReconnectAuditEntry_KindSignalRRetentionUpdated_IsWireStable()
    {
        Assert.Equal("signalr.retention.updated",
            ReconnectAuditEntry.KindSignalRRetentionUpdated);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ReconnectAuditEntry_KindSignalRRetentionDeleted_IsWireStable()
    {
        Assert.Equal("signalr.retention.deleted",
            ReconnectAuditEntry.KindSignalRRetentionDeleted);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void TournamentAlertsYaml_PresentInRepo()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName,
            "src", "backend", "src", "Mahjong.Autotable.Api",
            "Observability", "Alerts", "tournament-query-duration.yaml");
        Assert.True(File.Exists(path));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void TournamentRunbook_PresentInRepo()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName,
            "docs", "tournament-query-duration-runbook.md");
        Assert.True(File.Exists(path));
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
