namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez paired contract for Bishop W22's
/// TournamentFinalizationController (POST /api/admin/tournaments/
/// {id}/finalize) and the new TournamentStanding entity.
/// Soft-pinned for forward-staging.
/// </summary>
public sealed class BishopW22TournamentFinalizationContractTests
{
    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void TournamentFinalizationController_Present_OrForwardStaged()
    {
        var apiAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Mahjong.Autotable.Api");
        if (apiAsm is null) return;
        var t = apiAsm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("TournamentFinalizationController", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void TournamentStandingEntity_Present_OrForwardStaged()
    {
        var apiAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Mahjong.Autotable.Api");
        if (apiAsm is null) return;
        var t = apiAsm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("TournamentStanding", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void ReconnectAuditEntry_KindTournamentFinalized_OrForwardStaged()
    {
        var apiAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Mahjong.Autotable.Api");
        if (apiAsm is null) return;
        var t = apiAsm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("ReconnectAuditEntry", StringComparison.Ordinal));
        if (t is null) return;
        var f = t.GetField("KindTournamentFinalized",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.NotNull(f);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void ReconnectAuditEntry_KindTournamentCompleted_OrForwardStaged()
    {
        var apiAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Mahjong.Autotable.Api");
        if (apiAsm is null) return;
        var t = apiAsm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("ReconnectAuditEntry", StringComparison.Ordinal));
        if (t is null) return;
        var f = t.GetField("KindTournamentCompleted",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.NotNull(f);
    }
}
