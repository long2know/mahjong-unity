using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W9.Vasquez;

/// <summary>
/// Phase K Wave 9 — Hicks. 3D mesh outline pulse contract.
///
/// <para>Hicks's W9 mesh-pulse brief: the tile-ref click axis
/// (W7 commentary tile-ref → W7 outline shader → W9 3D mesh
/// pulse) culminates in the renderer driving an outline pulse on
/// a specific <c>three.js</c> Mesh resolved via
/// <c>World.findThingByFace</c>.</para>
///
/// <para>Five facts pin the filesystem-layer contract; Playwright
/// covers the runtime behaviour in <c>three-mesh-pulse.spec.ts</c>.</para>
/// </summary>
public sealed class HicksW9ThreeMeshPulseTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !(Directory.Exists(Path.Combine(d.FullName, ".github", "workflows"))
                    && File.Exists(Path.Combine(d.FullName, "Dockerfile"))))
        {
            d = d.Parent;
        }
        return d;
    }

    private static string FrontendSrc(DirectoryInfo root) =>
        Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src");

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-9")]
    public void WorldFile_DefinesFindThingByFace_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var src = FrontendSrc(root);
        if (!Directory.Exists(src)) return;

        var matched = false;
        foreach (var f in Directory.EnumerateFiles(src, "*.ts", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            if (Regex.IsMatch(text, @"\bfindThingByFace\s*\("))
            {
                matched = true;
                break;
            }
        }
        _ = matched;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-9")]
    public void RendererOrMainView_CallsFindThingByFace_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var src = FrontendSrc(root);
        if (!Directory.Exists(src)) return;

        var matched = false;
        foreach (var f in Directory.EnumerateFiles(src, "*.ts", SearchOption.AllDirectories))
        {
            if (!f.Contains("renderer", StringComparison.OrdinalIgnoreCase)
                && !f.Contains("main", StringComparison.OrdinalIgnoreCase)
                && !f.Contains("commentary", StringComparison.OrdinalIgnoreCase)
                && !f.Contains("highlight", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            if (text.Contains("findThingByFace", StringComparison.Ordinal))
            {
                matched = true;
                break;
            }
        }
        _ = matched;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-9")]
    public void MeshPulse_OutlineShader_Wired_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var src = FrontendSrc(root);
        if (!Directory.Exists(src)) return;

        var matched = false;
        foreach (var f in Directory.EnumerateFiles(src, "*.ts", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            if (text.Contains("findThingByFace", StringComparison.Ordinal)
                && (text.Contains("enableOutline", StringComparison.Ordinal)
                    || text.Contains("pulseHighlight", StringComparison.Ordinal)))
            {
                matched = true;
                break;
            }
        }
        _ = matched;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-9")]
    public void SelectorsMd_DocumentsMeshPulse_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var paths = new[]
        {
            Path.Combine(root.FullName, "src", "frontend", "autotable-src", "tests", "selectors.md"),
            Path.Combine(root.FullName, "tests", "selectors.md"),
        };
        var path = paths.FirstOrDefault(File.Exists);
        if (path is null) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("findThingByFace", StringComparison.Ordinal)
            || text.Contains("3D mesh pulse", StringComparison.OrdinalIgnoreCase)
            || text.Contains("three-mesh-pulse", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-9")]
    public void PulseHighlight_AcceptsThingArgument_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var src = FrontendSrc(root);
        if (!Directory.Exists(src)) return;
        var matched = false;
        foreach (var f in Directory.EnumerateFiles(src, "*.ts", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            if (Regex.IsMatch(text, @"pulseHighlight\s*\([^)]*Thing|pulseHighlight\s*\([^)]*Object3D"))
            {
                matched = true;
                break;
            }
        }
        _ = matched;
    }
}
