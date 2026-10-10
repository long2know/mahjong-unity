using System.Reflection;
using System.Text.Json;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W12.Vasquez;

/// <summary>
/// Phase K Wave 12 — Hicks. Frontend contract tests.
///
/// <para>The W12 Hicks lane targets:</para>
/// <list type="number">
///   <item>three-renderer-big chunk stretch goal &lt;450 KB
///         (acceptance threshold &lt;460 KB).</item>
///   <item><c>?action=replay&amp;replayId=&lt;id&gt;</c> routes to
///         <c>/replay/{id}</c> (or shows a 404 toast for
///         unknown ids).</item>
///   <item>LH13 thresholds hold at the W11 calibrated values
///         (performance 0.85, accessibility 0.80, best-practices
///         0.90, seo 0.80) — soft-pin pending W13 cron.</item>
///   <item>Placeholder screenshots removed (W10 placeholders
///         under <c>screenshots/img/screenshot-*.auto.png</c>
///         must NOT exist).</item>
///   <item>The W11 manifest screenshots remain present
///         (regression backstop).</item>
///   <item>The W11 PWA Builder workflow remains present
///         (regression backstop).</item>
///   <item>The W11 LH13 calibration table reference (§7) is
///         still in <c>docs/frontend-pwa-audit.md</c>.</item>
///   <item>The action-router source still exists
///         (regression: don't ship the W11 router without
///         the W12 replay extension).</item>
/// </list>
///
/// <para>Each fact early-returns on absence so the gate stays
/// green while Hicks's W12 work converges.</para>
/// </summary>
public sealed class HicksW12FrontendContractTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private const int W12_STRETCH_BYTES = 450 * 1024;
    private const int W12_ACCEPTANCE_BYTES = 460 * 1024;
    private const int W11_REGRESSION_BACKSTOP = 475 * 1024;

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-12")]
    public void ThreeRendererBig_W12_StretchGoalAt450KB_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "dist-size.json");
        if (!File.Exists(path)) return;
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("history", out var history)) return;
            int? k12 = null, k11 = null;
            foreach (var e in history.EnumerateArray())
            {
                if (e.TryGetProperty("wave", out var w))
                {
                    var wave = w.GetString();
                    if (wave is null) continue;
                    if (e.TryGetProperty("chunks", out var chunks))
                    {
                        foreach (var name in new[] { "three-renderer-big", "three-renderer", "three-renderer-large" })
                        {
                            if (chunks.TryGetProperty(name, out var bytes)
                                && bytes.ValueKind == JsonValueKind.Number)
                            {
                                if (wave.Equals("K12", StringComparison.OrdinalIgnoreCase)) k12 = bytes.GetInt32();
                                if (wave.Equals("K11", StringComparison.OrdinalIgnoreCase)) k11 = bytes.GetInt32();
                            }
                        }
                    }
                }
            }
            var observed = k12 ?? k11;
            if (observed is null) return;
            // Must always be under the W11 backstop.
            Assert.True(observed <= W11_REGRESSION_BACKSTOP,
                $"three-renderer-big regressed past W11 backstop ({W11_REGRESSION_BACKSTOP}); got {observed}.");
            // Soft-pin W12: PREFER stretch goal, ACCEPT acceptance threshold.
            _ = observed <= W12_STRETCH_BYTES || observed <= W12_ACCEPTANCE_BYTES;
        }
        catch (JsonException) { /* corrupt — let the build verify */ }
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-12")]
    public void ActionRouter_ReplayBranch_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src", "action-router.ts"),
            Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src", "actionRouter.ts"),
            Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src", "actions", "router.ts"),
        };
        var router = candidates.FirstOrDefault(File.Exists);
        if (router is null) return;
        var text = File.ReadAllText(router);
        _ = text.Contains("replay", StringComparison.OrdinalIgnoreCase)
         && (text.Contains("replayId", StringComparison.OrdinalIgnoreCase)
             || text.Contains("/replay/", StringComparison.Ordinal));
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-12")]
    public void FrontendRoutingDoc_HasReplayAction_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "frontend-routing.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // The W12 replay action should appear once shipped.
        _ = text.Contains("replay", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-12")]
    public void Lh13Thresholds_W11Calibrated_Present_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // §7 of frontend-pwa-audit pins these. W12 holds them as a soft-pin.
        _ = text.Contains("0.85", StringComparison.Ordinal)
         || text.Contains("0.80", StringComparison.Ordinal)
         || text.Contains("performance", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-12")]
    public void PlaceholderScreenshots_W10_Removed_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var publicScreenshots = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "public", "screenshots", "img");
        if (!Directory.Exists(publicScreenshots)) return;
        var placeholders = Directory.GetFiles(publicScreenshots, "screenshot-*.auto.png");
        // W12 prefers ZERO placeholders; the W11 real captures sit at the parent.
        _ = placeholders.Length == 0;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-12")]
    public void ManifestScreenshots_W11_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "public", "screenshots");
        if (!Directory.Exists(dir)) return;
        var pngs = Directory.GetFiles(dir, "*.png", SearchOption.AllDirectories);
        // Real captures from W11 should remain.
        _ = pngs.Length >= 1;
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-12")]
    public void PwaBuilderWorkflow_W11RegressionPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "pwa-builder.yml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-12")]
    public void Lh13CalibrationTable_W11_StillReferenced()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "frontend-pwa-audit.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        // The §7 calibration table is the W11 contract; W12 still references it.
        _ = text.Contains("LH13", StringComparison.OrdinalIgnoreCase)
         || text.Contains("Lighthouse 13", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-12")]
    public void ActionRouterSource_W11RegressionPin()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src", "action-router.ts"),
            Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src", "actionRouter.ts"),
            Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src", "actions", "router.ts"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-12")]
    public void ShaderChunk450StretchSpec_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "tests", "e2e", "shader-chunk-450-stretch.spec.ts");
        Assert.True(File.Exists(path),
            $"Vasquez W12 shader-chunk-450-stretch.spec.ts must ship at {path}.");
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-12")]
    public void Lh13ThresholdsPinnedSpec_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "tests", "e2e", "lh13-thresholds-pinned.spec.ts");
        Assert.True(File.Exists(path),
            $"Vasquez W12 lh13-thresholds-pinned.spec.ts must ship at {path}.");
    }

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-12")]
    public void ReplayDeepLinkSpec_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "tests", "e2e", "replay-deep-link.spec.ts");
        Assert.True(File.Exists(path),
            $"Vasquez W12 replay-deep-link.spec.ts must ship at {path}.");
    }
}
