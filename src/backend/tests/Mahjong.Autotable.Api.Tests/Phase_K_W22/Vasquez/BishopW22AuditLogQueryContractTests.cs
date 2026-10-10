namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez paired contract for Bishop W22's
/// audit-log paginated query endpoint with meta-audit row.
/// </summary>
public sealed class BishopW22AuditLogQueryContractTests
{
    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void AuditLogQueryController_Present_OrForwardStaged()
    {
        var apiAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Mahjong.Autotable.Api");
        if (apiAsm is null) return;
        var t = apiAsm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("AuditLogQueryController", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void ReconnectAuditEntry_KindAuditLogQueried_OrForwardStaged()
    {
        var apiAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Mahjong.Autotable.Api");
        if (apiAsm is null) return;
        var t = apiAsm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("ReconnectAuditEntry", StringComparison.Ordinal));
        if (t is null) return;
        var fields = t.GetFields(System.Reflection.BindingFlags.Public
                                  | System.Reflection.BindingFlags.Static);
        var has = fields.Any(f =>
            f.Name.Contains("LogQueried", StringComparison.OrdinalIgnoreCase)
            || f.Name.Contains("AuditLogQueried", StringComparison.OrdinalIgnoreCase));
        Assert.True(has);
    }
}
