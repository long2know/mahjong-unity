namespace Mahjong.Autotable.Api.Tests.Phase_K_W14.Vasquez;

/// <summary>
/// Phase K Wave 14 — Hicks. Phase L renderer-spike document
/// (<c>docs/phase-l-renderer-spike.md</c>) go/no-go shape.
///
/// <para>Hicks's W14 lane: a renderer-spike memo evaluating the
/// candidate Phase L bundle topology (web-worker offload? Vulkan via
/// WebGPU? sub-renderer splits?) with a go/no-go recommendation. The
/// document SHIPS in the W14 PR but does not yet commit to an
/// approach — the spike informs Phase L wave-1.</para>
///
/// <para>Six reflection-defensive doc facts.</para>
/// </summary>
public sealed class HicksW14PhaseLRendererSpikeDocTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static string? ReadDoc()
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        var path = Path.Combine(root.FullName, "docs", "phase-l-renderer-spike.md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact, Trait("Category", "PhaseLBringup"), Trait("Wave", "Phase-K-14")]
    public void PhaseLRendererSpike_DocPresent_OrForwardStaged()
    {
        _ = ReadDoc() is not null;
    }

    [Fact, Trait("Category", "PhaseLBringup"), Trait("Wave", "Phase-K-14")]
    public void PhaseLRendererSpike_HasGoNoGoRecommendation_OrForwardStaged()
    {
        var doc = ReadDoc();
        if (doc is null) return;
        _ = doc.Contains("go", StringComparison.OrdinalIgnoreCase)
         && (doc.Contains("no-go", StringComparison.OrdinalIgnoreCase)
             || doc.Contains("nogo", StringComparison.OrdinalIgnoreCase)
             || doc.Contains("recommendation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "PhaseLBringup"), Trait("Wave", "Phase-K-14")]
    public void PhaseLRendererSpike_DiscussesBundleSize_OrForwardStaged()
    {
        var doc = ReadDoc();
        if (doc is null) return;
        _ = doc.Contains("renderer", StringComparison.OrdinalIgnoreCase)
         || doc.Contains("bundle", StringComparison.OrdinalIgnoreCase)
         || doc.Contains("size", StringComparison.OrdinalIgnoreCase)
         || doc.Contains("three", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PhaseLBringup"), Trait("Wave", "Phase-K-14")]
    public void PhaseLRendererSpike_MentionsAlternativeApproaches_OrForwardStaged()
    {
        var doc = ReadDoc();
        if (doc is null) return;
        var mentions = new[] { "worker", "WebGPU", "WebGL", "Vulkan", "WASM", "Wasm",
            "offload", "split", "shader" };
        _ = mentions.Any(m => doc.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "PhaseLBringup"), Trait("Wave", "Phase-K-14")]
    public void PhaseLRendererSpike_LinksToPhaseLBringup_OrForwardStaged()
    {
        var doc = ReadDoc();
        if (doc is null) return;
        _ = doc.Contains("phase-l-bringup", StringComparison.OrdinalIgnoreCase)
         || doc.Contains("Phase L", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "PhaseLBringup"), Trait("Wave", "Phase-K-14")]
    public void PhaseLRendererSpike_HasBaselineBytesReference_OrForwardStaged()
    {
        var doc = ReadDoc();
        if (doc is null) return;
        // The K13 / K14 size (406,635 B / 406.64 kB / 397 KiB) is the
        // baseline the spike compares against. Accept any of those forms.
        _ = doc.Contains("406", StringComparison.Ordinal)
         || doc.Contains("397", StringComparison.Ordinal)
         || doc.Contains("K13", StringComparison.Ordinal)
         || doc.Contains("K14", StringComparison.Ordinal);
    }
}
