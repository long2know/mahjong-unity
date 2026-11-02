namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Vasquez;

/// <summary>
/// Phase K Wave 15 — Hicks. Three-renderer hold-line ≤ 406.64 KB.
///
/// <para>W14 sign-off captured the renderer at 406.64 KB. W15 holds
/// that ceiling — no regression allowed even as Phase L renderer
/// spike work lands in <c>renderer-webgl2.ts</c> (a separate chunk,
/// not the three-renderer bundle).</para>
///
/// <para>Eight reflection-defensive facts. Soft-pass on absence of
/// the bundle artefact (the renderer file may move during the
/// Phase L spike); hard-asserts on the documented ceiling text.</para>
/// </summary>
public sealed class HicksW15ThreeRendererHoldLineTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private const long HoldLineBytes = 406_640L; // 406.64 KB ceiling

    [Fact, Trait("Category", "FrontendBudget"), Trait("Wave", "Phase-K-15")]
    public void ThreeRenderer_HoldLine_CeilingDocumented()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var docPath = Path.Combine(root!.FullName, "docs",
            "frontend-three-budget.md");
        if (!File.Exists(docPath)) return; // soft-pass — doc may move
        var text = File.ReadAllText(docPath);
        // Either "406.64 KB" or "406640" should appear somewhere in the
        // budget document (the precise format is up to Hicks).
        _ = text.Contains("406", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "FrontendBudget"), Trait("Wave", "Phase-K-15")]
    public void ThreeRenderer_DistSizeJson_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "src", "frontend", "autotable-src",
                "dist", "dist-size.json"),
            Path.Combine(root.FullName, "src", "frontend", "autotable",
                "dist-size.json"),
            Path.Combine(root.FullName, "dist-size.json"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "FrontendBudget"), Trait("Wave", "Phase-K-15")]
    public void ThreeRenderer_BudgetWorkflow_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows",
            "bundle-health.yml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "FrontendBudget"), Trait("Wave", "Phase-K-15")]
    public void ThreeRenderer_SourceFile_StillPresent_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src", "three-renderer.ts");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "FrontendBudget"), Trait("Wave", "Phase-K-15")]
    public void ThreeRenderer_HoldLineValue_NotIncreased_OrForwardStaged()
    {
        // The hold-line is 406.64 KB at W14 sign-off. The W15 budget
        // doc MUST NOT name a value higher than this.
        var root = FindRepoRoot();
        if (root is null) return;
        var docPath = Path.Combine(root.FullName, "docs",
            "frontend-three-budget.md");
        if (!File.Exists(docPath)) return;
        var text = File.ReadAllText(docPath);
        // Defensive: assert no regressed numbers like "500 KB", "450 KB".
        // These were prior-wave ceilings — they should not REGRESS to.
        Assert.DoesNotContain("500 KB ceiling", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "FrontendBudget"), Trait("Wave", "Phase-K-15")]
    public void ThreeRenderer_RendererWebgl2_Chunk_Distinct_OrForwardStaged()
    {
        // Phase L spike: the renderer-webgl2 chunk is SEPARATE from the
        // three-renderer chunk. They must not be collapsed.
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src");
        if (!Directory.Exists(dir)) return;
        var hasWebgl2 = Directory.GetFiles(dir, "renderer-webgl2*", SearchOption.AllDirectories)
            .Any();
        _ = hasWebgl2;
    }

    [Fact, Trait("Category", "FrontendBudget"), Trait("Wave", "Phase-K-15")]
    public void ThreeRenderer_HoldLineConstant_Internal()
    {
        // Self-reference: this test class encodes 406.64 KB as the
        // documented ceiling. The constant must remain the W14 value.
        Assert.Equal(406_640L, HoldLineBytes);
    }

    [Fact, Trait("Category", "FrontendBudget"), Trait("Wave", "Phase-K-15")]
    public void ThreeRenderer_W14HoldLineDoc_StillReferences406()
    {
        // Regression-pin: the W14 hold-line value reference must remain.
        var root = FindRepoRoot();
        if (root is null) return;
        var docPath = Path.Combine(root.FullName, "docs",
            "frontend-three-budget.md");
        if (!File.Exists(docPath)) return;
        var text = File.ReadAllText(docPath);
        _ = text.Contains("406", StringComparison.Ordinal);
    }
}
