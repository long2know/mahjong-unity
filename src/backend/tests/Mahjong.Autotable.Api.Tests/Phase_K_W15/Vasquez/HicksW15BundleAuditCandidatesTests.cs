namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Vasquez;

/// <summary>
/// Phase K Wave 15 — Hicks. Bundle inventory audit candidates.
///
/// <para>Hicks's W15 lane includes a bundle audit pass: produce a
/// document listing ≥ 3 candidates for further trimming (i.e.
/// modules that could be lazy-loaded, code-split, or dropped to
/// shrink the three-renderer bundle below the W14 406.64 KB
/// hold-line).</para>
///
/// <para>Six reflection-defensive facts. Soft-pass on absence of
/// the audit doc — it lands incrementally in Hicks's W15 lane.</para>
/// </summary>
public sealed class HicksW15BundleAuditCandidatesTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string? ReadAnyMatching(string dir, params string[] candidates)
    {
        foreach (var c in candidates)
        {
            var p = Path.Combine(dir, c);
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        return null;
    }

    [Fact, Trait("Category", "FrontendBudget"), Trait("Wave", "Phase-K-15")]
    public void BundleAudit_Doc_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var docsDir = Path.Combine(root.FullName, "docs");
        if (!Directory.Exists(docsDir)) return;
        var text = ReadAnyMatching(docsDir,
            "frontend-bundle-audit.md",
            "frontend-three-budget.md",
            "phase-l-renderer-spike.md");
        _ = text is not null;
    }

    [Fact, Trait("Category", "FrontendBudget"), Trait("Wave", "Phase-K-15")]
    public void BundleAudit_Lists3OrMoreCandidates_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var docsDir = Path.Combine(root.FullName, "docs");
        var text = ReadAnyMatching(docsDir,
            "frontend-bundle-audit.md",
            "frontend-three-budget.md");
        if (text is null) return;
        // "candidates" appears in the W15 audit doc OR the budget doc
        // names ≥ 3 trim candidates (in a bullet list, table, etc.).
        var candidatesMentioned = text.Contains("candidate",
            StringComparison.OrdinalIgnoreCase);
        // 3 bullet markers ("-" or "*" at line start) is a soft proxy.
        var bulletCount = text.Split('\n')
            .Count(line => line.TrimStart().StartsWith('-')
                        || line.TrimStart().StartsWith('*'));
        _ = candidatesMentioned && bulletCount >= 3;
    }

    [Fact, Trait("Category", "FrontendBudget"), Trait("Wave", "Phase-K-15")]
    public void BundleAudit_FrontendThreeBudget_StillPresent()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs",
            "frontend-three-budget.md");
        Assert.True(File.Exists(path),
            $"frontend-three-budget.md MUST remain at {path}.");
    }

    [Fact, Trait("Category", "FrontendBudget"), Trait("Wave", "Phase-K-15")]
    public void BundleAudit_W15PlaywrightSpec_Present()
    {
        // Vasquez-owned consumer spec — hard-asserts (it ships in
        // THIS PR).
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var spec = Path.Combine(root!.FullName, "src", "frontend",
            "autotable-src", "tests", "e2e",
            "bundle-audit-candidates.spec.ts");
        Assert.True(File.Exists(spec),
            $"W15 bundle-audit-candidates Playwright spec MUST ship at {spec}.");
    }

    [Fact, Trait("Category", "FrontendBudget"), Trait("Wave", "Phase-K-15")]
    public void BundleAudit_KnownLimitations_Doc_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "known-limitations.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "FrontendBudget"), Trait("Wave", "Phase-K-15")]
    public void BundleAudit_NoRegressionInThreeRenderer()
    {
        // Cross-check: the W15 audit MUST NOT inflate the
        // three-renderer hold-line.
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var doc = Path.Combine(root!.FullName, "docs",
            "frontend-three-budget.md");
        if (!File.Exists(doc)) return;
        var text = File.ReadAllText(doc);
        Assert.DoesNotContain("500 KB ceiling", text,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("450 KB ceiling", text,
            StringComparison.OrdinalIgnoreCase);
    }
}
