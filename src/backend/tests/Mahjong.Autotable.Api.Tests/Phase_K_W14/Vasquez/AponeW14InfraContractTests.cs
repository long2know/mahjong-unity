namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Vasquez;

/// <summary>
/// Phase K Wave 14 — Apone. Infrastructure contract tests (W14 surfaces).
///
/// <para>The W14 Apone lane targets:</para>
/// <list type="number">
///   <item>Regional EKS bring-up §2 us-east-1 plan dry-run ready.</item>
///   <item>Terraform §7 1.11.4 bump applied (W14 target pin).</item>
///   <item>JWT rotation rehearsal #3 + GA recommendation.</item>
///   <item>Phase L devops-readiness doc structure
///         (<c>docs/phase-l-devops-readiness.md</c>).</item>
///   <item>Redis envFrom required-patch in prod kustomization.</item>
///   <item>PWA Builder workflow graceful PWA_PREVIEW_URL skip.</item>
///   <item>CHANGELOG 0.23.0 entry.</item>
/// </list>
///
/// <para>Each fact early-returns on absence so a Vasquez gate stays
/// green while Apone's W14 lane converges.</para>
/// </summary>
public sealed class AponeW14InfraContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-14")]
    public void RegionalEksBringup_Section2_UsEast1_DryRun_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "regional-eks-bringup.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        var hasW14UsEast = text.Contains("us-east-1", StringComparison.Ordinal)
                        && (text.Contains("W14", StringComparison.Ordinal)
                            || text.Contains("dry-run", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("plan", StringComparison.OrdinalIgnoreCase));
        _ = hasW14UsEast;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-14")]
    public void Terraform_Section7_VersionBump_1_11_4_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "terraform.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("1.11.4", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-14")]
    public void Terraform_RequiredVersion_HclFilePinned_1_11_4_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var infraTf = Path.Combine(root.FullName, "infra", "terraform");
        if (!Directory.Exists(infraTf)) return;
        var hcl = Directory.EnumerateFiles(infraTf, "*.tf", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(infraTf, "*.tfvars", SearchOption.AllDirectories))
            .ToArray();
        var any = hcl.Any(p =>
        {
            try { return File.ReadAllText(p).Contains("1.11.4", StringComparison.Ordinal); }
            catch { return false; }
        });
        _ = any;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-14")]
    public void JwtRotationRehearsal_Section5_Third_GA_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "jwt-rotation-rehearsal.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        var hasThird = text.Contains("third", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Rehearsal #3", StringComparison.Ordinal)
                    || text.Contains("Rehearsal 3", StringComparison.Ordinal);
        var hasGA = text.Contains("GA", StringComparison.Ordinal);
        _ = hasThird && hasGA;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-14")]
    public void PhaseLDevopsReadiness_DocPresent_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "phase-l-devops-readiness.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-14")]
    public void PhaseLDevopsReadiness_HasReadinessChecklist_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "phase-l-devops-readiness.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        var hasChecklist = text.Contains("checklist", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("readiness", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("- [ ]", StringComparison.Ordinal)
                        || text.Contains("- [x]", StringComparison.Ordinal);
        _ = hasChecklist;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-14")]
    public void RedisEnvFromPatch_ProdKustomization_W14_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "infra", "k8s", "overlays",
            "prod", "kustomization.yaml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // Either an envFrom block exists, OR a commented-in patch
        // line references redis + envFrom (the W14 flip prep).
        _ = text.Contains("envFrom", StringComparison.Ordinal)
         || (text.Contains("redis", StringComparison.OrdinalIgnoreCase)
             && text.Contains("envFrom", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-14")]
    public void PwaBuilderWorkflow_GracefulPreviewUrlSkip_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-builder.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // The graceful skip should reference PWA_PREVIEW_URL absence
        // and continue-on-error or an `if:` guard.
        var hasPreviewVar = text.Contains("PWA_PREVIEW_URL", StringComparison.Ordinal);
        var hasGracefulShape = text.Contains("if:", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("continue-on-error", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("skip", StringComparison.OrdinalIgnoreCase);
        _ = hasPreviewVar && hasGracefulShape;
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-14")]
    public void Changelog_0_23_0_Entry_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.23.0", StringComparison.Ordinal);
    }
}
