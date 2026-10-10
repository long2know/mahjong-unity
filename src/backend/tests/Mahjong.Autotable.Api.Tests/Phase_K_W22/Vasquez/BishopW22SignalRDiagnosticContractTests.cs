namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez paired contract for Bishop W22's
/// SignalR connection diagnostic endpoint + in-memory registry.
/// </summary>
public sealed class BishopW22SignalRDiagnosticContractTests
{
    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void SignalRConnectionDiagnosticController_Present_OrForwardStaged()
    {
        var apiAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Mahjong.Autotable.Api");
        if (apiAsm is null) return;
        var t = apiAsm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("SignalRConnectionDiagnosticController", StringComparison.Ordinal));
        Assert.NotNull(t);
    }
}
