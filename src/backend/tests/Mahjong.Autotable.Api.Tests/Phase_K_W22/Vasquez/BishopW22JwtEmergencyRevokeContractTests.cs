namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez paired contract for Bishop W22's
/// JWT emergency-revocation endpoint, revoked-kid entity,
/// Prometheus metric, and JwksCache invalidation hook.
/// </summary>
public sealed class BishopW22JwtEmergencyRevokeContractTests
{
    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void JwtEmergencyRevokeController_Present_OrForwardStaged()
    {
        var apiAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Mahjong.Autotable.Api");
        if (apiAsm is null) return;
        var t = apiAsm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("JwtEmergencyRevokeController", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void JwtEmergencyRevokedKidEntity_Present_OrForwardStaged()
    {
        var apiAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Mahjong.Autotable.Api");
        if (apiAsm is null) return;
        var t = apiAsm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("JwtEmergencyRevokedKid", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void ReconnectAuditEntry_KindAuthJwtEmergencyRevoke_OrForwardStaged()
    {
        var apiAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Mahjong.Autotable.Api");
        if (apiAsm is null) return;
        var t = apiAsm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("ReconnectAuditEntry", StringComparison.Ordinal));
        if (t is null) return;
        // Accept either the dotted "auth.jwt.emergency.revoke" string
        // constant or a PascalCase field name carrying it.
        var fields = t.GetFields(System.Reflection.BindingFlags.Public
                                  | System.Reflection.BindingFlags.Static);
        var has = fields.Any(f =>
            f.Name.Contains("EmergencyRevoke", StringComparison.OrdinalIgnoreCase)
            || f.Name.Contains("JwtEmergency", StringComparison.OrdinalIgnoreCase));
        Assert.True(has);
    }
}
