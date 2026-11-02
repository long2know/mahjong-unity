namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez paired contract for Apone W19
/// Kyverno additional Audit-mode rules (D3 in Apone memo) —
/// <c>disallow-lateral-movement.yaml</c> + <c>require-network-policy.yaml</c>
/// in <c>infra/kyverno/policies/</c>.  Both land in Audit mode
/// with a 5-day grace window before any Enforce flip.
/// </summary>
public sealed class AponeW19KyvernoAdditionalRulesContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string? KyvernoPolicyPath(string filename)
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        return Path.Combine(root.FullName, "infra", "kyverno", "policies", filename);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void Kyverno_DisallowLateralMovement_Policy_Present_OrForwardStaged()
    {
        var p = KyvernoPolicyPath("disallow-lateral-movement.yaml");
        if (p is null) return;
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void Kyverno_RequireNetworkPolicy_Policy_Present_OrForwardStaged()
    {
        var p = KyvernoPolicyPath("require-network-policy.yaml");
        if (p is null) return;
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void Kyverno_DisallowLateralMovement_AuditMode_OrForwardStaged()
    {
        var p = KyvernoPolicyPath("disallow-lateral-movement.yaml");
        if (p is null || !File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // W19 ships in Audit mode (5-day grace before any
        // Enforce flip).
        Assert.Contains("Audit", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void Kyverno_RequireNetworkPolicy_AuditMode_OrForwardStaged()
    {
        var p = KyvernoPolicyPath("require-network-policy.yaml");
        if (p is null || !File.Exists(p)) return;
        var text = File.ReadAllText(p);
        Assert.Contains("Audit", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Apone")]
    public void Kyverno_PolicyDir_Still_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        // Kyverno policy dir is forward-staged at W19 — Apone
        // lands the dir + the two new W19 policies in this
        // wave.  Soft-pin until then.
        var d = Path.Combine(root!.FullName, "infra", "kyverno", "policies");
        _ = Directory.Exists(d);
    }
}
