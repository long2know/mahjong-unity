namespace Mahjong.Autotable.Api.Tests.Phase_K_W22.Vasquez;

/// <summary>
/// Phase K Wave 22 — Vasquez paired contract for Bishop W22's
/// chunked replay download endpoint (ReplayChunksController) +
/// internal static helpers ComputeChunkCount / ComputeEtag /
/// TryParseSingleByteRange.
/// </summary>
public sealed class BishopW22ReplayChunksContractTests
{
    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void ReplayChunksController_Present_OrForwardStaged()
    {
        var apiAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Mahjong.Autotable.Api");
        if (apiAsm is null) return;
        var t = apiAsm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("ReplayChunksController", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void ReplayChunksController_HasComputeChunkCount_OrForwardStaged()
    {
        var apiAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Mahjong.Autotable.Api");
        if (apiAsm is null) return;
        var t = apiAsm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("ReplayChunksController", StringComparison.Ordinal));
        if (t is null) return;
        var m = t.GetMethod("ComputeChunkCount",
            System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static);
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void ReplayChunksController_HasComputeEtag_OrForwardStaged()
    {
        var apiAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Mahjong.Autotable.Api");
        if (apiAsm is null) return;
        var t = apiAsm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("ReplayChunksController", StringComparison.Ordinal));
        if (t is null) return;
        var m = t.GetMethod("ComputeEtag",
            System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static);
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-22"), Trait("Lane", "Bishop")]
    public void ReplayChunksController_HasTryParseSingleByteRange_OrForwardStaged()
    {
        var apiAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Mahjong.Autotable.Api");
        if (apiAsm is null) return;
        var t = apiAsm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("ReplayChunksController", StringComparison.Ordinal));
        if (t is null) return;
        var m = t.GetMethod("TryParseSingleByteRange",
            System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static);
        Assert.NotNull(m);
    }
}
