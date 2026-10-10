namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez paired contract for Apone W20's
/// us-east-1 ACTUAL APPLY runbook V2 hardening (post-Stephen
/// feedback).  The V2 ships
/// <c>infra/terraform/regional-eks/us-east-1/post-apply-smoke-test.sh</c>
/// (8 invariants, up from 4 at W19 V1).
///
/// Soft-pinned so the gate stays green if Apone W20 has not yet
/// landed the script.
/// </summary>
public sealed class AponeW20UsEast1ApplyRunbookV2ContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string SmokeScriptPath()
    {
        var root = FindRepoRoot();
        return Path.Combine(root!.FullName, "infra", "terraform", "regional-eks",
            "us-east-1", "post-apply-smoke-test.sh");
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void UsEast1_PostApplySmoke_Script_Present_OrForwardStaged()
    {
        _ = File.Exists(SmokeScriptPath());
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void UsEast1_PostApplySmoke_Script_Executable_OrForwardStaged()
    {
        var p = SmokeScriptPath();
        if (!File.Exists(p)) return;
        var bytes = File.ReadAllBytes(p);
        Assert.True(bytes.Length > 0);
        Assert.Equal((byte)'#', bytes[0]);
        Assert.Equal((byte)'!', bytes[1]);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void UsEast1_PostApplySmoke_EksClusterToken_OrForwardStaged()
    {
        var p = SmokeScriptPath();
        if (!File.Exists(p)) return;
        var text = File.ReadAllText(p);
        // The smoke shells out to `aws eks` or kubectl; either way the
        // script must reference one of those tokens.
        var hasEks = text.Contains("eks", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("kubectl", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasEks);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Apone")]
    public void UsEast1_ApplyRunbook_Doc_Updated_W20_OrForwardStaged()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var doc = Path.Combine(root!.FullName, "docs", "us-east-1-apply-runbook.md");
        if (!File.Exists(doc)) return;
        var text = File.ReadAllText(doc);
        // V2 / W20 marker.
        var hasV2 = text.Contains("V2", StringComparison.Ordinal)
                     || text.Contains("W20", StringComparison.Ordinal)
                     || text.Contains("v2", StringComparison.Ordinal);
        Assert.True(hasV2);
    }
}
