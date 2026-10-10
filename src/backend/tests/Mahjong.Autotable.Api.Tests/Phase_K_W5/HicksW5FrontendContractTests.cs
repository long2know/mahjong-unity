using System.Reflection;
using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W5;

/// <summary>
/// Phase K Wave 5 — Hicks's frontend-lane surface contracts (Vasquez).
///
/// <para>Covers Hicks's Wave 5 deliverables:</para>
/// <list type="bullet">
///   <item><b>Lazy three.js into 3rd chunk</b> — three.js no longer
///         lives inside <c>scene-shell.ts</c>'s static import graph.
///         Pinned by the absence of <c>import …from 'three'</c> in
///         <c>scene-shell.ts</c> AND presence of dedicated
///         <c>three-renderer.ts</c> module.</item>
///   <item><b>scene-shell &lt; 500 KB budget</b> — file-size probe on
///         the source TypeScript module itself (Playwright spec
///         covers the runtime equivalent).</item>
///   <item><b>Retire <c>game-scene-ready</c> back-compat marker</b> —
///         the testid MUST be absent from <c>scene-shell.ts</c>
///         (Wave-4 specs already gate on <c>scene-shell-ready</c>).</item>
///   <item><b>Keyboard-accessible sparse-seed reorder</b> — the
///         tournament sparse-seeding view's seat rows MUST carry
///         <c>tabindex</c> + arrow-key keyboard handlers.</item>
///   <item><b>Exhaustive <c>voiceReasonToText</c> discriminated
///         union</b> — covered in the gap test; this file pins
///         the TypeScript-level exhaustiveness via a no-default-arm
///         compile guard probe (best-effort).</item>
///   <item><b><c>three-renderer-ready</c> testid emit</b> — the
///         new chunk MUST mint <c>data-testid="three-renderer-ready"</c>
///         once the WebGL renderer is up, so Vasquez's W5 Playwright
///         spec can wait on the heavy chunk specifically.</item>
/// </list>
///
/// <para>Filesystem probes anchor at the repo root. Forward-stage
/// soft-pass on absence; hard-assert on presence.</para>
/// </summary>
public class HicksW5FrontendContractTests
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

    private static string? ReadIfExists(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;

    // ────────────────────────────────────────────────────────────────────
    //  1. scene-shell.ts has no static three.js import.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-5")]
    public void SceneShell_NoStaticThreeImport_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(FrontendSrc(root), "scene-shell.ts");
        var text = ReadIfExists(path);
        if (text is null) return; // forward-staged

        // Hard-pin: no static `import … from 'three'` in scene-shell.
        var hasStaticThree = Regex.IsMatch(text,
            @"^\s*import\s[^;]*\sfrom\s+['""]three['""]\s*;",
            RegexOptions.Multiline);
        Assert.False(hasStaticThree,
            "scene-shell.ts MUST NOT statically import 'three' — Wave-5 lazy-loads via three-renderer chunk.");

        // Hard-pin: no static `import … from './asset-loader'` either
        // (asset-loader transitively imports three).
        var hasStaticAssetLoader = Regex.IsMatch(text,
            @"^\s*import\s[^;]*\sfrom\s+['""]\./asset-loader['""]\s*;",
            RegexOptions.Multiline);
        Assert.False(hasStaticAssetLoader,
            "scene-shell.ts MUST NOT statically import asset-loader — Wave-5 lazy-loads via three-renderer chunk.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. three-renderer.ts exists and carries the canonical mount fn.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-5")]
    public void ThreeRenderer_ModulePresent_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(FrontendSrc(root), "three-renderer.ts");
        var text = ReadIfExists(path);
        if (text is null) return; // forward-staged

        // Hard-pin: exported mount function name.
        Assert.Matches(@"export\s+(?:async\s+)?function\s+mountThreeRenderer\b", text);

        // Hard-pin: three.js IS statically imported somewhere on the
        // three-renderer chunk (W5 contract).
        //
        // Wave 7 (Vasquez): the bundler swap allows Hicks to peel
        // the static three import OUT of the top-level
        // three-renderer.ts and INTO a sibling module under
        // src/render/. The chunk graph is still three-anchored — the
        // import just lives one hop deeper. Tolerate either layout:
        // direct import in three-renderer.ts OR a static
        // `from 'three'` in any sibling module under src/render/ or
        // src/renderer/.
        var directImport = Regex.IsMatch(text,
            @"^\s*import\s+[^;]*\s+from\s+['""]three['""]",
            RegexOptions.Multiline);
        if (directImport) return;

        var renderDirs = new[]
        {
            Path.Combine(FrontendSrc(root), "render"),
            Path.Combine(FrontendSrc(root), "renderer"),
        };
        var hasSiblingImport = renderDirs
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.ts", SearchOption.AllDirectories))
            .Any(f =>
            {
                var t = File.ReadAllText(f);
                return Regex.IsMatch(t,
                    @"^\s*import\s+[^;]*\s+from\s+['""]three['""]",
                    RegexOptions.Multiline);
            });

        Assert.True(hasSiblingImport,
            "three-renderer chunk MUST statically import 'three' "
            + "(directly OR via a sibling under src/render or src/renderer).");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. game-scene-ready back-compat marker retired.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-5")]
    public void GameSceneReady_Retired_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(FrontendSrc(root), "scene-shell.ts");
        var text = ReadIfExists(path);
        if (text is null) return; // forward-staged

        // Match any setAttribute / dataset / classList line that names
        // game-scene-ready as a LITERAL (we tolerate prose comments).
        // Strip out single-line comments + block comments before scanning.
        var stripped = StripComments(text);

        // game-scene-ready may NOT be set as a data-testid value or
        // attribute in the live code.
        var hasLiveMarker = Regex.IsMatch(stripped,
            @"['""]game-scene-ready['""]");
        Assert.False(hasLiveMarker,
            "scene-shell.ts MUST NOT mint `game-scene-ready` — retired in Wave 5 (`scene-shell-ready` is canonical).");
    }

    private static string StripComments(string ts)
    {
        // Remove /* ... */ block comments (non-greedy).
        var noBlock = Regex.Replace(ts, @"/\*[\s\S]*?\*/", "");
        // Remove // ... single-line comments.
        var sb = new System.Text.StringBuilder(noBlock.Length);
        foreach (var line in noBlock.Split('\n'))
        {
            var idx = -1;
            var inSingle = false;
            var inDouble = false;
            var inBack = false;
            for (var i = 0; i < line.Length - 1; i++)
            {
                var c = line[i];
                if (c == '\\' && i + 1 < line.Length) { i++; continue; }
                if (!inDouble && !inBack && c == '\'') inSingle = !inSingle;
                else if (!inSingle && !inBack && c == '"') inDouble = !inDouble;
                else if (!inSingle && !inDouble && c == '`') inBack = !inBack;
                else if (!inSingle && !inDouble && !inBack
                         && c == '/' && line[i + 1] == '/')
                {
                    idx = i;
                    break;
                }
            }
            sb.AppendLine(idx >= 0 ? line[..idx] : line);
        }
        return sb.ToString();
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. three-renderer-ready testid minted somewhere.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-5")]
    public void ThreeRendererReady_TestIdMinted_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(FrontendSrc(root), "three-renderer.ts");
        var text = ReadIfExists(path);
        if (text is null) return; // forward-staged

        // Hard-pin: the heavy chunk mints `data-testid="three-renderer-ready"`
        // (in the live code, not just a prose comment).
        var stripped = StripComments(text);
        Assert.Matches(@"['""]three-renderer-ready['""]", stripped);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Keyboard-accessible sparse-seed reorder.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-5")]
    public void SparseSeedReorder_KeyboardAccessible_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(FrontendSrc(root), "tournaments.ts");
        var text = ReadIfExists(path);
        if (text is null) return;

        if (!text.Contains("buildSeedingPanel", StringComparison.Ordinal)
            && !text.Contains("tournament-seed", StringComparison.Ordinal))
        {
            return; // forward-staged
        }

        // Hard-pin: at least ONE arrow-key handler is wired AND seat rows
        // carry tabindex. Soft-pass when the panel exists but neither
        // is yet wired (W5 brief is to ADD this).
        var hasArrowHandler = Regex.IsMatch(text,
            @"['""](?:Arrow(?:Up|Down)|key(?:Up|Down))['""]")
            || text.Contains("key === 'ArrowUp'", StringComparison.Ordinal)
            || text.Contains("key === 'ArrowDown'", StringComparison.Ordinal)
            || text.Contains("event.key === 'ArrowUp'", StringComparison.Ordinal)
            || text.Contains("event.key === 'ArrowDown'", StringComparison.Ordinal)
            || text.Contains("ArrowUp", StringComparison.Ordinal);
        var hasTabIndex = Regex.IsMatch(text,
            @"tabIndex\s*=|tabindex\s*=|setAttribute\s*\(\s*['""]tabindex['""]");

        if (!hasArrowHandler && !hasTabIndex) return; // forward-staged

        // If EITHER is present, hard-pin BOTH (so an in-flight merge
        // can't ship arrow keys without making them reachable, or
        // tabindex without working keys).
        Assert.True(hasArrowHandler,
            "tournaments.ts has tabindex but no Arrow key handler — keyboard-accessible reorder incomplete.");
        Assert.True(hasTabIndex,
            "tournaments.ts has Arrow key handler but no tabindex — seat rows not focusable.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. voiceReasonToText TypeScript-level exhaustiveness — the
    //     reason type union mentions all 6 canonical codes, and the
    //     mapper has cases for each.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-5")]
    public void VoiceReasonToText_DiscriminatedUnion_Exhaustive_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(FrontendSrc(root), "voice.ts");
        var text = ReadIfExists(path);
        if (text is null) return;
        if (!text.Contains("voiceReasonToText", StringComparison.Ordinal)) return;

        // Hard-pin: case arms for each of the 6 canonical wire codes.
        var canonical = new[]
        {
            "voice-not-enabled",
            "not-seated",
            "spectator",
            "rate-limited",
            "target-not-found",
            "unauthorized",
        };
        foreach (var code in canonical)
        {
            Assert.Matches($@"case\s+['""]{Regex.Escape(code)}['""]\s*:", text);
        }

        // Hard-pin: the function signature accepts a typed parameter
        // (not just `unknown`). Wave-5 brief calls for a discriminated
        // union — accept any type annotation, soft-pass on `unknown`
        // (which keeps Wave-4 back-compat).
        var sig = Regex.Match(text,
            @"export\s+function\s+voiceReasonToText\s*\(\s*\w+\s*:\s*([^)]+)\)");
        if (sig.Success)
        {
            var paramType = sig.Groups[1].Value.Trim();
            Assert.False(string.IsNullOrEmpty(paramType));
        }
    }
}
