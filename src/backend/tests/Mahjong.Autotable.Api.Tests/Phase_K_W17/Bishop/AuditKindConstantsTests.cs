using Mahjong.Autotable.Api.Data.Entities;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Bishop;

/// <summary>
/// Phase K Wave 17 — Bishop. Wire-stability pinning for every
/// new audit-kind constant introduced this wave. A future
/// maintainer who renames one without updating this test will
/// see a red CI run, signalling that downstream dashboards +
/// log-search expressions need updating in lockstep.
/// </summary>
public sealed class AuditKindConstantsTests
{
    [Fact, Trait("Category", "AuditKinds"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void KindAuthJwtIssueBlockedStale_IsExact()
    {
        Assert.Equal("auth.jwt.issue.blocked.stale_per_tenant_policy",
            ReconnectAuditEntry.KindAuthJwtIssueBlockedStale);
    }

    [Fact, Trait("Category", "AuditKinds"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void KindAuthJwksPerTenantHardDeleted_IsExact()
    {
        Assert.Equal("auth.jwks.per-tenant.hard-deleted",
            ReconnectAuditEntry.KindAuthJwksPerTenantHardDeleted);
    }

    [Fact, Trait("Category", "AuditKinds"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void KindCommentaryAdminOverride_IsExact()
    {
        Assert.Equal("commentary.admin.override",
            ReconnectAuditEntry.KindCommentaryAdminOverride);
    }

    [Fact, Trait("Category", "AuditKinds"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void KindReplayRetentionCreated_IsExact()
    {
        Assert.Equal("replays.retention.created",
            ReconnectAuditEntry.KindReplayRetentionCreated);
    }

    [Fact, Trait("Category", "AuditKinds"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void KindReplayRetentionUpdated_IsExact()
    {
        Assert.Equal("replays.retention.updated",
            ReconnectAuditEntry.KindReplayRetentionUpdated);
    }

    [Fact, Trait("Category", "AuditKinds"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void KindReplayRetentionDeleted_IsExact()
    {
        Assert.Equal("replays.retention.deleted",
            ReconnectAuditEntry.KindReplayRetentionDeleted);
    }

    [Fact, Trait("Category", "AuditKinds"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void KindSignalRRetentionCreated_IsExact()
    {
        Assert.Equal("signalr.retention.created",
            ReconnectAuditEntry.KindSignalRRetentionCreated);
    }

    [Fact, Trait("Category", "AuditKinds"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void KindSignalRRetentionUpdated_IsExact()
    {
        Assert.Equal("signalr.retention.updated",
            ReconnectAuditEntry.KindSignalRRetentionUpdated);
    }

    [Fact, Trait("Category", "AuditKinds"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void KindSignalRRetentionDeleted_IsExact()
    {
        Assert.Equal("signalr.retention.deleted",
            ReconnectAuditEntry.KindSignalRRetentionDeleted);
    }

    [Fact, Trait("Category", "AuditKinds"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void AllW17Kinds_AreLowercase()
    {
        var w17 = new[]
        {
            ReconnectAuditEntry.KindAuthJwtIssueBlockedStale,
            ReconnectAuditEntry.KindAuthJwksPerTenantHardDeleted,
            ReconnectAuditEntry.KindCommentaryAdminOverride,
            ReconnectAuditEntry.KindReplayRetentionCreated,
            ReconnectAuditEntry.KindReplayRetentionUpdated,
            ReconnectAuditEntry.KindReplayRetentionDeleted,
            ReconnectAuditEntry.KindSignalRRetentionCreated,
            ReconnectAuditEntry.KindSignalRRetentionUpdated,
            ReconnectAuditEntry.KindSignalRRetentionDeleted,
        };
        foreach (var k in w17)
        {
            Assert.Equal(k.ToLowerInvariant(), k);
        }
    }

    [Fact, Trait("Category", "AuditKinds"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void AllW17Kinds_AreUnique()
    {
        var w17 = new[]
        {
            ReconnectAuditEntry.KindAuthJwtIssueBlockedStale,
            ReconnectAuditEntry.KindAuthJwksPerTenantHardDeleted,
            ReconnectAuditEntry.KindCommentaryAdminOverride,
            ReconnectAuditEntry.KindReplayRetentionCreated,
            ReconnectAuditEntry.KindReplayRetentionUpdated,
            ReconnectAuditEntry.KindReplayRetentionDeleted,
            ReconnectAuditEntry.KindSignalRRetentionCreated,
            ReconnectAuditEntry.KindSignalRRetentionUpdated,
            ReconnectAuditEntry.KindSignalRRetentionDeleted,
        };
        Assert.Equal(w17.Length, w17.Distinct().Count());
    }
}
