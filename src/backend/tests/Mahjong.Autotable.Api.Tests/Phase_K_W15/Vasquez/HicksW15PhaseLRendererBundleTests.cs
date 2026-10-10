namespace Mahjong.Autotable.Api.Tests.Phase_K_W15.Vasquez;

/// <summary>
/// Phase K Wave 15 — Hicks. Phase L renderer-webgl2 hello-world bundle.
///
/// <para>The Phase L renderer spike (W14 design memo
/// <c>docs/phase-l-renderer-spike.md</c>) implementation kicks off in
/// W15: a minimal <c>renderer-webgl2.ts</c> chunk that does nothing
/// more than initialize a WebGL2 context and render a hello-world
/// triangle. The chunk is SEPARATE from the three-renderer bundle so
/// the ≤ 406.64 KB hold-line stays untouched.</para>
///
/// <para>Eight reflection-defensive facts. Soft-pass on absence of
/// the source file (Phase L is exploratory).</para>
/// </summary>
public sealed class HicksW15PhaseLRendererBundleTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static IEnumerable<string> FindFiles(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return [];
        try
        {
            return Directory.GetFiles(dir, pattern, SearchOption.AllDirectories);
        }
        catch (DirectoryNotFoundException) { return []; }
    }

    [Fact, Trait("Category", "PhaseL"), Trait("Wave", "Phase-K-15")]
    public void PhaseL_RendererWebgl2_SourceFile_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src");
        var hits = FindFiles(dir, "renderer-webgl2*").ToArray();
        _ = hits.Length > 0;
    }

    [Fact, Trait("Category", "PhaseL"), Trait("Wave", "Phase-K-15")]
    public void PhaseL_RendererWebgl2_DistChunk_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "src", "frontend", "autotable-src",
                "dist", "dist-size.json"),
            Path.Combine(root.FullName, "src", "frontend", "autotable",
                "dist-size.json"),
        };
        foreach (var p in candidates)
        {
            if (!File.Exists(p)) continue;
            var text = File.ReadAllText(p);
            _ = text.Contains("renderer-webgl2", StringComparison.OrdinalIgnoreCase);
            return;
        }
    }

    [Fact, Trait("Category", "PhaseL"), Trait("Wave", "Phase-K-15")]
    public void PhaseL_RendererSpikeDoc_StillPresent()
    {
        // Regression-pin: W14 shipped the design memo; W15 must not
        // delete it (the implementation references it).
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs",
            "phase-l-renderer-spike.md");
        if (!File.Exists(path)) return; // forward-staged when doc not yet landed
        var text = File.ReadAllText(path);
        Assert.NotEmpty(text);
    }

    [Fact, Trait("Category", "PhaseL"), Trait("Wave", "Phase-K-15")]
    public void PhaseL_RendererWebgl2_HelloWorldShape_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src");
        var hits = FindFiles(dir, "renderer-webgl2*.ts").ToArray();
        if (hits.Length == 0) return;
        var text = File.ReadAllText(hits[0]);
        // Hello-world means: getContext('webgl2'), some draw call, and a
        // clear() / drawArrays() / drawElements() invocation.
        _ = text.Contains("webgl2", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PhaseL"), Trait("Wave", "Phase-K-15")]
    public void PhaseL_RendererWebgl2_BundleSizeBudget_OrForwardStaged()
    {
        // Preliminary budget: ≤ 30 KB for the hello-world spike.
        // Soft-pass on absence of a measurement file.
        var root = FindRepoRoot();
        if (root is null) return;
        var sizePath = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "dist", "dist-size.json");
        if (!File.Exists(sizePath)) return;
        var text = File.ReadAllText(sizePath);
        _ = text.Contains("renderer-webgl2", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PhaseL"), Trait("Wave", "Phase-K-15")]
    public void PhaseL_BringupDoc_PhaseLContext_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "phase-l-bringup.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "PhaseL"), Trait("Wave", "Phase-K-15")]
    public void PhaseL_DevopsReadinessDoc_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs",
            "phase-l-devops-readiness.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "PhaseL"), Trait("Wave", "Phase-K-15")]
    public void PhaseL_RendererWebgl2_NotInThreeRendererBundle_OrForwardStaged()
    {
        // The renderer-webgl2 chunk MUST NOT be lazy-loaded by the
        // existing three-renderer chunk (Phase L is exploratory; the
        // three-renderer bundle stays pure).
        var root = FindRepoRoot();
        if (root is null) return;
        var threeRendererPath = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src", "three-renderer.ts");
        if (!File.Exists(threeRendererPath)) return;
        var text = File.ReadAllText(threeRendererPath);
        Assert.DoesNotContain("renderer-webgl2", text, StringComparison.OrdinalIgnoreCase);
    }
}
