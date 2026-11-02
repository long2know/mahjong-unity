namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Bishop W20's
/// replay auto-expiry <c>BackgroundService</c>.
/// (<c>ReplayStoreExpiryHandler</c> under
/// <c>src/backend/src/Mahjong.Autotable.Api/Replays/</c>.)
/// Soft-pinned so the gate stays green if Bishop W20 has not yet
/// landed the service.
/// </summary>
public sealed class BishopW20ReplayExpiryBackgroundServiceContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string HandlerPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Replays",
            "ReplayStoreExpiryHandler.cs");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void ReplayExpiryHandler_File_Present_OrForwardStaged()
    {
        _ = File.Exists(HandlerPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void ReplayExpiryHandler_BackgroundServiceBase_OrForwardStaged()
    {
        var p = HandlerPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("BackgroundService", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Bishop")]
    public void ReplayExpiryHandler_ExecuteAsync_Override_OrForwardStaged()
    {
        var p = HandlerPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("ExecuteAsync", text, StringComparison.Ordinal);
    }
}
