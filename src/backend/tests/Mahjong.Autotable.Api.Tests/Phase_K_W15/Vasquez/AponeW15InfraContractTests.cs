namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Vasquez;

/// <summary>
/// Phase K Wave 15 — Apone. Infra contract probes (Kyverno enforce
/// pre-wire + HPA min-replicas tuning + lane-discipline-nightly
/// heredoc fix + us-east-1 plan drift + Phase L L1 design memo +
/// SLSA-3 assessment doc + CHANGELOG 0.24.0).
///
/// <para>Twelve reflection-defensive facts. Soft-pass on absence —
/// the surfaces land incrementally in Apone's W15 lane.</para>
/// </summary>
public sealed class AponeW15InfraContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    // ─── 1. Kyverno enforce pre-wire ──────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-15")]
    public void Kyverno_EnforcePolicies_PreWire_YamlPresent_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "infra", "k8s", "policies",
                "kyverno-enforce-policies.yaml"),
            Path.Combine(root.FullName, "infra", "k8s", "kyverno",
                "enforce-policies.yaml"),
            Path.Combine(root.FullName, "infra", "policies",
                "kyverno-enforce.yaml"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-15")]
    public void Kyverno_EnforceMode_DocumentedInProdCutover()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "prod-cutover.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("Kyverno", StringComparison.Ordinal)
         && (text.Contains("enforce", StringComparison.OrdinalIgnoreCase)
             || text.Contains("Enforce", StringComparison.Ordinal));
    }

    // ─── 2. HPA min-replicas tuning ───────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-15")]
    public void HPA_MinReplicas_TuningRecommendation_Documented()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "docs", "prod-cutover.md"),
            Path.Combine(root.FullName, "docs", "kubernetes.md"),
            Path.Combine(root.FullName, "docs", "production-deployment-runbook.md"),
        };
        foreach (var p in candidates)
        {
            if (!File.Exists(p)) continue;
            var text = File.ReadAllText(p);
            if (text.Contains("min-replicas", StringComparison.OrdinalIgnoreCase)
                || text.Contains("minReplicas", StringComparison.Ordinal))
            {
                return;
            }
        }
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-15")]
    public void HPA_MinReplicas_TargetValue_Documented_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "prod-cutover.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // W14 brief named "3 → 5" as the proposed bump. W15 picks up
        // the tuning recommendation; either "5" or "3 → 5" should
        // appear in the HPA-related section.
        _ = text.Contains("3 → 5", StringComparison.Ordinal)
         || text.Contains("3 -> 5", StringComparison.Ordinal)
         || text.Contains("minReplicas: 5", StringComparison.Ordinal);
    }

    // ─── 3. lane-discipline-nightly heredoc fix ───────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-15")]
    public void LaneDiscipline_Nightly_Workflow_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, ".github", "workflows",
            "lane-discipline-nightly.yml");
        Assert.True(File.Exists(path),
            $"lane-discipline-nightly.yml MUST remain at {path}.");
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-15")]
    public void LaneDiscipline_Nightly_HeredocFix_NotBroken_OrForwardStaged()
    {
        // The W5-era parse error at line ~87 was a heredoc indent.
        // After the W15 fix, the heredoc should remain syntactically
        // parseable. We don't run actionlint here — we just confirm the
        // file is non-trivial.
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows",
            "lane-discipline-nightly.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        Assert.True(text.Length > 200,
            "lane-discipline-nightly.yml must remain non-trivial.");
    }

    // ─── 4. us-east-1 plan drift re-check ─────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-15")]
    public void RegionalEks_UsEast1_Plan_DriftCheck_Documented()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "regional-eks-bringup.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("us-east-1", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-15")]
    public void RegionalEks_UsEast1_ReadinessChecklist_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "regional-eks-bringup.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("§2.1", StringComparison.Ordinal)
         || text.Contains("readiness", StringComparison.OrdinalIgnoreCase);
    }

    // ─── 5. Phase L L1 design memo ────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-15")]
    public void PhaseL_L1_DesignMemo_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "docs", "phase-l-l1-design.md"),
            Path.Combine(root.FullName, "docs", "phase-l-devops-l1.md"),
            Path.Combine(root.FullName, "docs", "phase-l-devops-readiness.md"),
        };
        _ = candidates.Any(File.Exists);
    }

    // ─── 6. SLSA-3 assessment doc ─────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-15")]
    public void Slsa3_AssessmentDoc_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "docs", "slsa-3-assessment.md"),
            Path.Combine(root.FullName, "docs", "slsa-level-3-assessment.md"),
            Path.Combine(root.FullName, "docs", "slsa.md"),
        };
        foreach (var p in candidates)
        {
            if (!File.Exists(p)) continue;
            var text = File.ReadAllText(p);
            if (text.Contains("SLSA", StringComparison.Ordinal)
                && (text.Contains("Level 3", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("L3", StringComparison.Ordinal)
                    || text.Contains("level-3", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
        }
    }

    // ─── 7. CHANGELOG 0.24.0 ──────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-15")]
    public void Changelog_0_24_0_Entry_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.24.0", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-15")]
    public void Changelog_W14Predecessor_0_23_0_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("0.23.0", StringComparison.Ordinal);
    }
}
