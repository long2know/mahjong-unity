using Mahjong.Autotable.Api.Data.Entities;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Bishop;

/// <summary>
/// Phase K Wave 20 — Bishop. Pins the wire-stable Kind
/// classifiers added to <see cref="ReconnectAuditEntry"/> in
/// W20. Each constant is the documented "dotted event name"
/// downstream dashboards / alerts filter on. A future
/// refactor renaming any of these constants without
/// updating dashboards must fail loudly here.
/// </summary>
public sealed class W20AuditKindConstantTests
{
    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Kind_TournamentSwissPairingComputed_HasStableWireName()
    {
        Assert.Equal("tournament.swiss-pairing.computed",
            ReconnectAuditEntry.KindTournamentSwissPairingComputed);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Kind_AuthJwksPerTenantBulkDeleted_HasStableWireName()
    {
        Assert.Equal("auth.jwks.per-tenant.bulk-deleted",
            ReconnectAuditEntry.KindAuthJwksPerTenantBulkDeleted);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Kind_AuthJwksPerTenantBulkEnabled_HasStableWireName()
    {
        Assert.Equal("auth.jwks.per-tenant.bulk-enabled",
            ReconnectAuditEntry.KindAuthJwksPerTenantBulkEnabled);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Kind_ReplayAutoExpiry_HasStableWireName()
    {
        Assert.Equal("replays.auto-expiry",
            ReconnectAuditEntry.KindReplayAutoExpiry);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Kind_JwtKeyRotationDrill_HasStableWireName()
    {
        Assert.Equal("auth.jwt.key-rotation-drill",
            ReconnectAuditEntry.KindJwtKeyRotationDrill);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Kinds_AreUniqueAcrossW20Surface()
    {
        var names = new[]
        {
            ReconnectAuditEntry.KindTournamentSwissPairingComputed,
            ReconnectAuditEntry.KindAuthJwksPerTenantBulkDeleted,
            ReconnectAuditEntry.KindAuthJwksPerTenantBulkEnabled,
            ReconnectAuditEntry.KindReplayAutoExpiry,
            ReconnectAuditEntry.KindJwtKeyRotationDrill,
        };
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Kinds_FollowDotNotation()
    {
        var names = new[]
        {
            ReconnectAuditEntry.KindTournamentSwissPairingComputed,
            ReconnectAuditEntry.KindAuthJwksPerTenantBulkDeleted,
            ReconnectAuditEntry.KindAuthJwksPerTenantBulkEnabled,
            ReconnectAuditEntry.KindReplayAutoExpiry,
            ReconnectAuditEntry.KindJwtKeyRotationDrill,
        };
        foreach (var name in names)
        {
            Assert.Contains(".", name);
            Assert.Equal(name.ToLowerInvariant(), name);
        }
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void Kinds_DoNotCollideWithW19Names()
    {
        var w20Names = new[]
        {
            ReconnectAuditEntry.KindTournamentSwissPairingComputed,
            ReconnectAuditEntry.KindAuthJwksPerTenantBulkDeleted,
            ReconnectAuditEntry.KindAuthJwksPerTenantBulkEnabled,
            ReconnectAuditEntry.KindReplayAutoExpiry,
            ReconnectAuditEntry.KindJwtKeyRotationDrill,
        };
        var w19Names = new[]
        {
            ReconnectAuditEntry.KindAuthJwksPerTenantBulkApplied,
            ReconnectAuditEntry.KindReplayIntegrityAudit,
            ReconnectAuditEntry.KindTournamentSwissPairingAuditRead,
        };
        foreach (var w20 in w20Names)
        {
            Assert.DoesNotContain(w20, w19Names);
        }
    }
}
