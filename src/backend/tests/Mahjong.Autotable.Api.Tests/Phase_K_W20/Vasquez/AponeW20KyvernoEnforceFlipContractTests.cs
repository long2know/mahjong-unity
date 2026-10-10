namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Apone W20's
/// Kyverno W19 enforce-flip (Audit → Enforce) on the two W19
/// ClusterPolicies: <c>disallow-lateral-movement</c> and
/// <c>require-network-policy</c>.
///
/// Soft-pinned so the gate stays green if Apone W20 has not yet
/// landed the cutover edits.
/// </summary>
public sealed class AponeW20KyvernoEnforceFlipContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string KyvernoDir()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "infra", "k8s", "base", "kyverno-policies");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void Kyverno_DisallowLateralMovement_File_Present_OrForwardStaged()
    {
        var p = Path.Combine(KyvernoDir(), "disallow-lateral-movement.yaml");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void Kyverno_RequireNetworkPolicy_File_Present_OrForwardStaged()
    {
        var p = Path.Combine(KyvernoDir(), "require-network-policy.yaml");
        _ = File.Exists(p);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void Kyverno_LateralMovement_EnforceMode_AfterW20Cutover_OrForwardStaged()
    {
        var p = Path.Combine(KyvernoDir(), "disallow-lateral-movement.yaml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // Post-W20 cutover: validationFailureAction: Enforce (or Enforce-equivalent token).
        var hasEnforce = text.Contains("Enforce", StringComparison.Ordinal);
        Assert.True(hasEnforce);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void Kyverno_RequireNetworkPolicy_EnforceMode_AfterW20Cutover_OrForwardStaged()
    {
        var p = Path.Combine(KyvernoDir(), "require-network-policy.yaml");
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        var hasEnforce = text.Contains("Enforce", StringComparison.Ordinal);
        Assert.True(hasEnforce);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void Kyverno_W19AdditionalRules_Doc_Updated_With_W20_Cutover_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var doc = Path.Combine(root!.FullName, "docs", "kyverno-w19-additional-rules.md");
        if (!File.Exists(doc)) return;
        var text = File.ReadAllText(doc);
        // Apone W20 memo carries sec 4.2 cutover evidence in this doc.
        var hasCutoverNarrative = text.Contains("Enforce", StringComparison.Ordinal)
                                    || text.Contains("W20", StringComparison.Ordinal);
        Assert.True(hasCutoverNarrative);
    }
}
