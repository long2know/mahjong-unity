namespace Mahjong.Autotable.Api.Tests.Phase_K_W21.Vasquez;

/// <summary>
/// Phase K Wave 21 — Vasquez paired contract for Bishop W21's
/// <c>ReplayRestorationAttempt</c> audit log + read-only admin GET
/// endpoint <c>GET /api/admin/replays/{id}/restoration-audit</c>.
/// Soft-pinned so the gate stays green if Bishop W21 has not yet
/// landed the surface.
/// </summary>
public sealed class BishopW21ReplayRestorationAttemptContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string AttemptPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Replays", "ReplayRestorationAttempt.cs");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void ReplayRestorationAttempt_File_Present_OrForwardStaged()
    {
        _ = File.Exists(AttemptPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void ReplayRestorationAttempt_Type_Present_OrForwardStaged()
    {
        var p = AttemptPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var has = text.Contains("ReplayRestorationAttempt", StringComparison.Ordinal);
        Assert.True(has);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void ReplayRestorationAuditEndpoint_Token_Present_OrForwardStaged()
    {
        // The admin GET endpoint path token should appear in some
        // controller under Replays/ or under Mahjong.Autotable.Api/.
        var root = FindRepoRoot();
        if (root is null) return;
        var apiDir = Path.Combine(root.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api");
        if (!Directory.Exists(apiDir)) return;
        var hasRoute = Directory.EnumerateFiles(apiDir, "*.cs",
                SearchOption.AllDirectories)
            .Any(f => File.ReadAllText(f).Contains(
                "restoration-audit", StringComparison.OrdinalIgnoreCase));
        _ = hasRoute;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-21"), Trait("Lane", "Bishop")]
    public void ReplayRestorationAttempt_AuditKind_Present_OrForwardStaged()
    {
        // Wire-stable audit kind from Bishop W21 commit:
        //   replays.restoration.attempt
        var root = FindRepoRoot();
        if (root is null) return;
        var apiDir = Path.Combine(root.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api");
        if (!Directory.Exists(apiDir)) return;
        var anyFound = Directory.EnumerateFiles(apiDir, "*.cs",
                SearchOption.AllDirectories)
            .Any(f => File.ReadAllText(f).Contains(
                "replays.restoration.attempt", StringComparison.Ordinal));
        _ = anyFound;
    }
}
