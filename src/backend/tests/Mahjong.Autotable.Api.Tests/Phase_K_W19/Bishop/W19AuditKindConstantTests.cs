using Mahjong.Autotable.Api.Data.Entities;
using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Bishop;

/// <summary>
/// Phase K Wave 19 — Bishop. Pins the wire-stable Kind
/// classifiers added to <see cref="ReconnectAuditEntry"/> in
/// W19. Each constant is the documented "dotted event name"
/// that downstream dashboards / alerts filter on. A future
/// refactor that renames any of these constants without
/// updating dashboards must fail loudly here.
/// </summary>
public sealed class W19AuditKindConstantTests
{
    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Kind_AuthJwksPerTenantBulkApplied_HasStableWireName()
    {
        Assert.Equal("auth.jwks.per-tenant.bulk-applied",
            ReconnectAuditEntry.KindAuthJwksPerTenantBulkApplied);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Kind_ReplayIntegrityAudit_HasStableWireName()
    {
        Assert.Equal("replays.integrity-audit",
            ReconnectAuditEntry.KindReplayIntegrityAudit);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Kind_TournamentSwissPairingAuditRead_HasStableWireName()
    {
        Assert.Equal("tournament.swiss-pairing.audit.read",
            ReconnectAuditEntry.KindTournamentSwissPairingAuditRead);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Kinds_AreUniqueAcrossW19Surface()
    {
        var names = new[]
        {
            ReconnectAuditEntry.KindAuthJwksPerTenantBulkApplied,
            ReconnectAuditEntry.KindReplayIntegrityAudit,
            ReconnectAuditEntry.KindTournamentSwissPairingAuditRead,
        };
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Kinds_FollowDotNotation()
    {
        var names = new[]
        {
            ReconnectAuditEntry.KindAuthJwksPerTenantBulkApplied,
            ReconnectAuditEntry.KindReplayIntegrityAudit,
            ReconnectAuditEntry.KindTournamentSwissPairingAuditRead,
        };
        foreach (var name in names)
        {
            Assert.Contains(".", name);
            Assert.Equal(name.ToLowerInvariant(), name);
        }
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    public void Kinds_DoNotOverlapWithW18ExistingNames()
    {
        // Drop a defensive sanity check so a future bump
        // doesn't silently rename a W18 constant onto a W19
        // value.
        var w19Names = new[]
        {
            ReconnectAuditEntry.KindAuthJwksPerTenantBulkApplied,
            ReconnectAuditEntry.KindReplayIntegrityAudit,
            ReconnectAuditEntry.KindTournamentSwissPairingAuditRead,
        };
        var w18ish = new[]
        {
            ReconnectAuditEntry.KindReconnectTokenRotated,
            ReconnectAuditEntry.KindTournamentForfeit,
            ReconnectAuditEntry.KindTournamentMatchComplete,
            ReconnectAuditEntry.KindTournamentSeeded,
        };
        foreach (var w19 in w19Names)
        {
            Assert.DoesNotContain(w19, w18ish);
        }
    }
}
