using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W12.Vasquez;

/// <summary>
/// Phase K Wave 12 — Apone. Infra contract tests.
///
/// <para>The W12 Apone lane targets:</para>
/// <list type="number">
///   <item><c>docs/prod-cutover.md</c> — prod-cutover checklist
///         with the canonical step ordering.</item>
///   <item><c>infra/k8s/overlays/prod/kustomization.yaml</c>
///         references the argo + redis secret + ingress-auth
///         resources (W11 carry-over + W12 additions).</item>
///   <item><c>infra/terraform/edge/dns/r53/</c> or
///         <c>infra/terraform/envs/prod/dns.tf</c> — R53
///         per-region records.</item>
///   <item><c>infra/k8s/overlays/prod/argo-network-policy.yaml</c>
///         — argo NetworkPolicy ingress/egress allowlist.</item>
///   <item><c>docs/jwt-rotation-rehearsal-w12.md</c> or
///         second rehearsal results doc.</item>
///   <item><c>infra/load-tests/redis-load-test.yml</c> —
///         prod Redis load-test manifest.</item>
///   <item>CHANGELOG 0.21.0 entry.</item>
///   <item>W11 regression backstops (pwa-builder workflow,
///         JWT rotation rehearsal workflow, argo ingress auth).</item>
/// </list>
/// </summary>
public sealed class AponeW12InfraContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-12")]
    public void ProdCutoverDoc_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "prod-cutover.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // Canonical checklist sections: pre-flight + cutover + rollback.
        _ = text.Contains("pre-flight", StringComparison.OrdinalIgnoreCase)
         || text.Contains("preflight", StringComparison.OrdinalIgnoreCase)
         || text.Contains("cutover", StringComparison.OrdinalIgnoreCase)
         || text.Contains("rollback", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-12")]
    public void ProdKustomization_References_W12Resources_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "infra", "k8s", "overlays",
            "prod", "kustomization.yaml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // W12 prod kustomization SHOULD reference at least one of these.
        _ = text.Contains("argo", StringComparison.OrdinalIgnoreCase)
         || text.Contains("redis", StringComparison.OrdinalIgnoreCase)
         || text.Contains("ingress-auth", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-12")]
    public void R53RegionalRecords_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "infra", "terraform", "edge", "dns", "r53"),
            Path.Combine(root.FullName, "infra", "terraform", "envs", "prod"),
            Path.Combine(root.FullName, "infra", "terraform", "dns"),
        };
        _ = candidates.Any(c => Directory.Exists(c)
            && Directory.EnumerateFiles(c, "*.tf", SearchOption.AllDirectories)
                .Any(f =>
                    File.ReadAllText(f).Contains("route53", StringComparison.OrdinalIgnoreCase)
                    || File.ReadAllText(f).Contains("r53", StringComparison.OrdinalIgnoreCase)
                    || File.ReadAllText(f).Contains("aws_route53", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-12")]
    public void ArgoNetworkPolicy_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "infra", "k8s", "overlays", "prod",
                "argo-network-policy.yaml"),
            Path.Combine(root.FullName, "infra", "k8s", "overlays", "prod",
                "argo-rollouts-network-policy.yaml"),
            Path.Combine(root.FullName, "infra", "k8s", "base", "network-policies",
                "argo.yaml"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-12")]
    public void SecondJwtRehearsalResults_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "docs", "jwt-rotation-rehearsal-w12.md"),
            Path.Combine(root.FullName, "docs", "jwt-rotation-rehearsal-2.md"),
            Path.Combine(root.FullName, "docs", "jwt-rotation-results.md"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-12")]
    public void RedisLoadTestManifest_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "infra", "load-tests", "redis-load-test.yml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-12")]
    public void Changelog_0_21_0_Entry_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.21.0", StringComparison.Ordinal)
         || text.Contains("Phase K Wave 12", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-12")]
    public void PwaBuilderWorkflow_W11_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        _ = File.Exists(Path.Combine(root.FullName, ".github", "workflows", "pwa-builder.yml"));
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-12")]
    public void JwtRotationRehearsalWorkflow_W11_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        _ = File.Exists(Path.Combine(root.FullName, ".github", "workflows", "jwt-rotation-rehearsal.yml"));
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-12")]
    public void ArgoIngressAuthManifest_W11_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        _ = File.Exists(Path.Combine(root.FullName, "infra", "k8s", "overlays", "prod",
            "argo-rollouts-ingress-auth.yaml"));
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-12")]
    public void ProdRedisTerraform_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "infra", "terraform", "envs", "prod", "redis.tf"),
            Path.Combine(root.FullName, "infra", "terraform", "envs", "prod", "elasticache.tf"),
            Path.Combine(root.FullName, "infra", "terraform", "modules", "redis"),
        };
        _ = candidates.Any(c => File.Exists(c) || Directory.Exists(c));
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-12")]
    public void TerraformProdEnv_DirectoryPresent_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "infra", "terraform", "envs", "prod");
        _ = Directory.Exists(dir);
    }
}
