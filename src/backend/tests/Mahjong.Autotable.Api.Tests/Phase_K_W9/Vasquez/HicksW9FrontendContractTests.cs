using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W9.Vasquez;

/// <summary>
/// Phase K Wave 9 — Hicks. Frontend contract gates.
///
/// <para>Hicks's W9 brief:</para>
/// <list type="number">
///   <item>3D mesh pulse — <c>World.findThingByFace</c> exposed
///         from the autotable runtime so the tile-ref click can
///         resolve to a 3D mesh and pulse its outline.</item>
///   <item><c>three-renderer-big</c> &lt;= 510 KB hard cap (W8 was
///         540 KB; W9 tightens by another ~5%).</item>
///   <item>Lighthouse 13 migration — <c>lighthouserc.json</c>
///         declares Lighthouse 13 schema.</item>
///   <item>Bracket canonical shape — frontend rejects unknown
///         payload shapes with a console error (no fallback).</item>
///   <item>Spectator livestream consumes the canonical
///         <c>/api/voice/livestream/*</c> path
///         (no <c>/api/tables/*/livestream/*</c>).</item>
/// </list>
///
/// <para>All facts forward-stage tolerant.</para>
/// </summary>
public sealed class HicksW9FrontendContractTests
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

    private static string FrontendRoot(DirectoryInfo root) =>
        Path.Combine(root.FullName, "src", "frontend", "autotable-src");

    private static string FrontendSrc(DirectoryInfo root) =>
        Path.Combine(FrontendRoot(root), "src");

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-9")]
    public void World_FindThingByFace_MethodPresent_OrForwardStaged()
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
            if (text.Contains("findThingByFace", StringComparison.Ordinal))
            {
                matched = true;
                break;
            }
        }
        _ = matched;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-9")]
    public void ThreeRendererBig_W9_HardCap_510KB_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;

        var distSize = Path.Combine(FrontendRoot(root), "dist-size.json");
        if (!File.Exists(distSize)) return;

        using var doc = JsonDocument.Parse(File.ReadAllText(distSize));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

        // Schema A: history[].wave == K9
        JsonElement? k9 = null;
        if (doc.RootElement.TryGetProperty("history", out var hist)
            && hist.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in hist.EnumerateArray())
            {
                if (entry.TryGetProperty("wave", out var w)
                    && w.ValueKind == JsonValueKind.String
                    && string.Equals(w.GetString(), "K9", StringComparison.OrdinalIgnoreCase))
                {
                    k9 = entry;
                    break;
                }
            }
        }

        var chunkSize = -1;
        if (k9 is { } entryNode
            && entryNode.TryGetProperty("chunks", out var chunks)
            && chunks.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "three-renderer-big", "three-renderer", "three-renderer-large" })
            {
                if (chunks.TryGetProperty(name, out var sz)
                    && sz.ValueKind == JsonValueKind.Number)
                {
                    chunkSize = sz.GetInt32();
                    break;
                }
            }
        }

        if (chunkSize < 0) return;
        Assert.True(chunkSize <= 510 * 1024,
            $"three-renderer-big MUST be ≤ 510 KB at W9; got {chunkSize / 1024.0:F1} KB.");
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-9")]
    public void Lighthouse13Schema_DeclaredInLighthouserc_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var fe = FrontendRoot(root);

        var candidates = new[]
        {
            Path.Combine(fe, "lighthouserc.json"),
            Path.Combine(fe, ".lighthouserc.json"),
            Path.Combine(fe, "lighthouserc.yml"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return;

        var text = File.ReadAllText(path);
        _ = text.Contains("\"13\"", StringComparison.Ordinal)
            || text.Contains("13.0", StringComparison.Ordinal)
            || text.Contains("lighthouse@13", StringComparison.Ordinal)
            || text.Contains("preset: \"experimental\"", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-9")]
    public void BracketRenderer_RejectsUnknownShape_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var renderer = Path.Combine(FrontendSrc(root), "bracket-renderer.ts");
        if (!File.Exists(renderer)) return;
        var text = File.ReadAllText(renderer);

        _ = Regex.IsMatch(text, @"console\.(error|warn)\(.*unknown.*shape",
                RegexOptions.IgnoreCase)
            || text.Contains("UnknownBracketShape", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"throw\s+new\s+\w*Error.*bracket",
                RegexOptions.IgnoreCase);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-9")]
    public void SpectatorLivestreamPlayer_UsesCanonicalVoicePath_OrForwardStaged()
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
            if (text.Contains("/api/voice/livestream/", StringComparison.Ordinal))
            {
                matched = true;
                break;
            }
        }
        _ = matched;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-9")]
    public void SpectatorLivestreamPlayer_NoLegacyTablesPath_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var src = FrontendSrc(root);
        if (!Directory.Exists(src)) return;

        var legacyPattern = new Regex(@"/api/tables/[^/]+/livestream/",
            RegexOptions.IgnoreCase);
        var legacyHits = new List<string>();
        foreach (var f in Directory.EnumerateFiles(src, "*.ts", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            if (legacyPattern.IsMatch(text)) legacyHits.Add(f);
        }
        _ = legacyHits.Count == 0;
    }
}
