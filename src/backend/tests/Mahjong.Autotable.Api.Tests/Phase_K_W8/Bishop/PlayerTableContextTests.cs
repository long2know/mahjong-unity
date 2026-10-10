using Mahjong.Autotable.Api.Tables;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W8.Bishop;

/// <summary>
/// Phase K Wave 8 — Bishop. Hard-asserted facts for the
/// <see cref="PlayerTableAssociation"/> envelope + the
/// <see cref="PlayerTableRole"/> enum.
///
/// <para>The DB-touching <see cref="PlayerTableContext.ResolveAsync"/>
/// path is exercised via the controller integration spec in the
/// Vasquez lane; these unit tests pin the static factory contract.</para>
/// </summary>
public sealed class PlayerTableContextTests
{
    [Fact, Trait("Category", "Tables"), Trait("Wave", "Phase-K-8")]
    public void Anonymous_FactoryProducesAnonymousRole()
    {
        var a = PlayerTableAssociation.Anonymous();
        Assert.Equal(PlayerTableRole.Anonymous, a.Role);
        Assert.Null(a.PlayerId);
        Assert.Equal("no-session", a.Reason);
    }

    [Fact, Trait("Category", "Tables"), Trait("Wave", "Phase-K-8")]
    public void Unknown_FactoryProducesUnknownRole_WithReason()
    {
        var a = PlayerTableAssociation.Unknown("table-not-found");
        Assert.Equal(PlayerTableRole.Unknown, a.Role);
        Assert.Equal("table-not-found", a.Reason);
    }

    [Fact, Trait("Category", "Tables"), Trait("Wave", "Phase-K-8")]
    public void Seated_FactoryProducesSeatedRole_WithPlayerId()
    {
        var a = PlayerTableAssociation.Seated("alice");
        Assert.Equal(PlayerTableRole.Seated, a.Role);
        Assert.Equal("alice", a.PlayerId);
        Assert.Equal("seat-occupied", a.Reason);
    }

    [Fact, Trait("Category", "Tables"), Trait("Wave", "Phase-K-8")]
    public void Owner_FactoryProducesOwnerRole()
    {
        var a = PlayerTableAssociation.Owner("alice");
        Assert.Equal(PlayerTableRole.Owner, a.Role);
        Assert.Equal("alice", a.PlayerId);
        Assert.Equal("table-owner", a.Reason);
    }

    [Fact, Trait("Category", "Tables"), Trait("Wave", "Phase-K-8")]
    public void Spectator_FactoryProducesSpectatorRole()
    {
        var a = PlayerTableAssociation.Spectator("alice");
        Assert.Equal(PlayerTableRole.Spectator, a.Role);
        Assert.Equal("alice", a.PlayerId);
        Assert.Equal("spectator-snapshot-present", a.Reason);
    }

    [Fact, Trait("Category", "Tables"), Trait("Wave", "Phase-K-8")]
    public void Admin_FactoryProducesAdminRole()
    {
        var a = PlayerTableAssociation.Admin("alice");
        Assert.Equal(PlayerTableRole.Admin, a.Role);
        Assert.Equal("alice", a.PlayerId);
        Assert.Equal("admin-override", a.Reason);
    }

    [Fact, Trait("Category", "Tables"), Trait("Wave", "Phase-K-8")]
    public void PlayerTableRole_EnumContainsAllSixValues()
    {
        var names = Enum.GetNames<PlayerTableRole>();
        Assert.Contains("Anonymous", names);
        Assert.Contains("Unknown", names);
        Assert.Contains("Seated", names);
        Assert.Contains("Owner", names);
        Assert.Contains("Spectator", names);
        Assert.Contains("Admin", names);
        Assert.Equal(6, names.Length);
    }

    [Fact, Trait("Category", "Tables"), Trait("Wave", "Phase-K-8")]
    public void IPlayerTableContext_Interface_ExposesResolveAsync()
    {
        var method = typeof(IPlayerTableContext).GetMethod("ResolveAsync");
        Assert.NotNull(method);
        Assert.Equal(4, method!.GetParameters().Length);
    }

    [Fact, Trait("Category", "Tables"), Trait("Wave", "Phase-K-8")]
    public void PlayerTableContext_Implementation_ImplementsInterface()
    {
        Assert.True(typeof(IPlayerTableContext).IsAssignableFrom(typeof(PlayerTableContext)));
    }
}
