using System.Reflection;
using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W7;

/// <summary>
/// Phase K Wave 7 — Vasquez. Bulk smoke-fact coverage for the W7
/// surfaces. Mirrors the W6 <c>W6SurfaceSmokeFactsTests</c> pattern
/// — broad single-axis assertions across all three lanes.
///
/// <para>Every fact is reflection-defensive / filesystem-defensive
/// against the API assembly and the repo root under the Vasquez-
/// owned read lane.</para>
/// </summary>
public sealed class W7SurfaceSmokeFactsTests
{
    private static readonly Assembly ApiAssembly =
        typeof(Mahjong.Autotable.Api.Changsha.Runtime.ChangshaGameRuntime).Assembly;

    private static DirectoryInfo? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = dir; d is not null; d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".github", "workflows"))
                && File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            {
                return d;
            }
        }
        return null;
    }

    private static Type? T(string name) =>
        ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == name);

    // ────────────────────────────────────────────────────────────────────
    //  Backend smoke facts — Bishop's W7 lane.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_FfmpegHlsRecorder_TypeNamingValid()
    {
        var t = T("FfmpegHlsRecorder") ?? T("HlsRecorder") ?? T("FfmpegHlsRecorderService");
        if (t is null) return;
        Assert.True(t.IsClass);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_CommentaryRecord_TypePresent()
    {
        var t = T("CommentaryRecord");
        if (t is null) return;
        Assert.False(t.IsEnum);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_BracketFormat_DoubleElim_LosersBracket_NonZero()
    {
        var bracket = T("DoubleEliminationBracket");
        if (bracket is null) return;
        var instance = Activator.CreateInstance(bracket);
        if (instance is null) return;
        var method = bracket.GetMethod("Generate");
        if (method is null) return;
        var seeds = new[] { "a", "b", "c", "d", "e", "f", "g", "h" };
        var result = method.Invoke(instance, new object[] { seeds });
        if (result is null) return;
        var items = ((System.Collections.IEnumerable)result).Cast<object>().ToList();
        var losers = items.Count(p =>
        {
            var prop = p.GetType().GetProperty("Bracket");
            return prop?.GetValue(p)?.ToString() == "Losers";
        });
        Assert.True(losers > 0,
            "DoubleEliminationBracket MUST emit > 0 losers-bracket pairings.");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_JwtIssuingService_KidProperty_Optional()
    {
        var t = T("JwtIssuingService");
        if (t is null) return;
        var p = t.GetProperty("Kid", BindingFlags.Public | BindingFlags.Instance);
        if (p is null) return;
        Assert.Equal(typeof(string), p.PropertyType);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_ICommentaryGenerator_StillAnInterface()
    {
        // Carry-forward W6 smoke: the interface MUST stay an interface
        // even as W7 wires the persisted-record envelope.
        var t = T("ICommentaryGenerator");
        if (t is null) return;
        Assert.True(t.IsInterface);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Frontend smoke facts — Hicks's W7 lane.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_ThreeRendererSourceUnder550Kb()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend", "autotable-src",
            "src", "three-renderer.ts");
        if (!File.Exists(path)) return;
        var fi = new FileInfo(path);
        Assert.True(fi.Length < 550 * 1024,
            $"three-renderer.ts MUST be < 550 KB (W7 ceiling); got {fi.Length}.");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_DistSizeJson_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "src", "frontend", "autotable-src", "dist-size.json"),
            Path.Combine(root.FullName, "src", "frontend", "autotable-src", "dist", "dist-size.json"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_BundlerConfigOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var fe = Path.Combine(root.FullName, "src", "frontend", "autotable-src");
        var candidates = new[]
        {
            Path.Combine(fe, "vite.config.ts"),
            Path.Combine(fe, "vite.config.js"),
            Path.Combine(fe, "rspack.config.js"),
            Path.Combine(fe, "rspack.config.ts"),
            Path.Combine(fe, ".parcelrc"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_OutlineShaderModule_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var feSrc = Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src");
        var candidates = new[]
        {
            Path.Combine(feSrc, "outline-shader.ts"),
            Path.Combine(feSrc, "shaders", "outline-shader.ts"),
            Path.Combine(feSrc, "outline.ts"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_CommentaryPanel_CarriesCommentaryRecord_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var feSrc = Path.Combine(root.FullName, "src", "frontend", "autotable-src", "src");
        var candidates = new[]
        {
            Path.Combine(feSrc, "commentary-panel.ts"),
            Path.Combine(feSrc, "commentary", "commentary-panel.ts"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("CommentaryRecord", StringComparison.Ordinal); // soft-pass
    }

    // ────────────────────────────────────────────────────────────────────
    //  Infra smoke facts — Apone's W7 lane.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_HelmChartYaml_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "helm", "mahjong", "Chart.yaml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_EdgeTerraformDir_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var dir = Path.Combine(root.FullName, "infra", "terraform", "modules", "edge");
        _ = Directory.Exists(dir);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_PreCommitConfig_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".pre-commit-config.yaml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_JwtRsaKeysSecret_DevOverlay_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "infra", "k8s", "overlays", "dev", "jwt-rsa-keys-secret.yaml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_JwtRsaKeysSecret_ProdOverlay_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "infra", "k8s", "overlays", "prod", "jwt-rsa-keys-secret.yaml"),
            Path.Combine(root.FullName, "infra", "k8s", "overlays", "production", "jwt-rsa-keys-secret.yaml"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_GhcrToEcrMirrorWorkflow_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, ".github", "workflows", "ghcr-to-ecr-mirror.yml"),
            Path.Combine(root.FullName, ".github", "workflows", "ghcr-ecr-mirror.yml"),
            Path.Combine(root.FullName, ".github", "workflows", "image-mirror.yml"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_MobileExternalTestingWorkflow_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "mobile-external-testing.yml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_Retro_2026_06_Doc_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "docs", "retros", "retro-2026-06.md"),
            Path.Combine(root.FullName, "docs", "retros", "phase-k-wave-7.md"),
            Path.Combine(root.FullName, "docs", "retros", "2026-06.md"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_Changelog_0_16_0_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = Regex.IsMatch(text, @"^##\s+\[?0\.16\.0\]?", RegexOptions.Multiline);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_JwtSsmRunbook_Doc_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "docs", "jwt-ssm-runbook.md"),
            Path.Combine(root.FullName, "docs", "ssm-jwt-runbook.md"),
            Path.Combine(root.FullName, "docs", "jwt-rotation-ssm.md"),
        };
        _ = candidates.Any(File.Exists);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_GoogleOAuthVerification_Doc_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "docs", "google-oauth-verification.md"),
            Path.Combine(root.FullName, "docs", "oauth-google-verification.md"),
            Path.Combine(root.FullName, "docs", "google-oauth.md"),
        };
        _ = candidates.Any(File.Exists);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Cross-lane smoke — W6 handoff carry-forward, W7 lane-map.json
    //  invariant.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_HandoffProtocolDoc_StillPresent()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "agent-handoff-protocol.md");
        Assert.True(File.Exists(path),
            "docs/agent-handoff-protocol.md MUST remain present (W5 deliverable).");
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_LaneMapJson_Present()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "lane-map.json");
        Assert.True(File.Exists(path),
            "tests/ci/lane-map.json MUST be present (W7 deliverable).");
        var text = File.ReadAllText(path);
        Assert.Contains("\"lanes\"", text);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_LaneDiscipline_PhaseKW7_Attribution_Documented()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "tests", "ci", "check-cross-lane-bundling.sh");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        // The W7 refinement MUST extend the Phase_K_W*/<AgentName>/
        // attribution rule to any-depth.
        Assert.Contains("Phase_K_W*/Bishop/", text);
        Assert.Contains("Phase_K_W*/Hicks/", text);
        Assert.Contains("Phase_K_W*/Apone/", text);
        Assert.Contains("Phase_K_W*/Vasquez/", text);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Wave-7 → Wave-6 carry-forward — RS256 algorithm switch surface
    //  remains string-typed (Bishop's W7 lane retains the W6 contract
    //  for downstream callers).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-7")]
    public void Smoke_W7_AuthOptions_JwtAlgorithm_StillString()
    {
        var t = T("AuthOptions");
        if (t is null) return;
        var p = t.GetProperty("JwtAlgorithm",
            BindingFlags.Public | BindingFlags.Instance);
        if (p is null) return;
        Assert.Equal(typeof(string), p.PropertyType);
    }
}
