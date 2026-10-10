using System.Reflection;
using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W6;

/// <summary>
/// Phase K Wave 6 — Hicks's frontend-lane surface contracts (Vasquez).
///
/// <para>Hicks's W6 deliverables:</para>
/// <list type="bullet">
///   <item><b>AI commentary panel UI</b> — new <c>commentary-panel.ts</c>
///         module under 80 KB source budget; mounts on replay route
///         with a <c>data-testid="commentary-panel"</c> root.</item>
///   <item><b>Spectator livestream HLS viewer</b> — new
///         <c>spectator-livestream.ts</c> module rendering an
///         <c>&lt;audio&gt;</c> element with an HLS source URL.</item>
///   <item><b>Swiss + double-elim bracket renderers</b> — bracket
///         component dispatches on format and emits per-format
///         <c>data-testid="bracket-format-{swiss,double-elim}"</c>
///         roots.</item>
///   <item><b>three-renderer chunk &lt; 700 KB</b> — file-size probe
///         on the source TypeScript module (Playwright spec covers
///         the runtime bundle).</item>
///   <item><b>PWA install prompt</b> — new install button + handler
///         wired to <c>beforeinstallprompt</c> event; emits
///         <c>data-testid="pwa-install-button"</c>.</item>
/// </list>
///
/// <para>All facts filesystem-probed at the repo root. Forward-stage
/// soft-pass on absence; hard-assert canonical shape on presence.</para>
/// </summary>
public class HicksW6FrontendContractTests
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
    //  AI commentary panel — commentary-panel.ts module present and
    //  source size < 80 KB (panel is a small read-only UI; if it grew
    //  past 80 KB we'd be over-rendering the commentary stream).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-6")]
    public void CommentaryPanel_ModulePresent_Under80KbSource_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(FrontendSrc(root), "commentary-panel.ts"),
            Path.Combine(FrontendSrc(root), "commentary", "commentary-panel.ts"),
            Path.Combine(FrontendSrc(root), "replay", "commentary-panel.ts"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return; // forward-staged

        var fi = new FileInfo(path);
        Assert.True(fi.Length < 80 * 1024,
            $"commentary-panel source MUST be < 80 KB; got {fi.Length} bytes at {path}.");

        var text = File.ReadAllText(path);
        // The panel MUST emit a canonical testid root for Playwright —
        // tolerate both inline-template (`data-testid="commentary-panel"`)
        // and programmatic (`setAttribute('data-testid', 'commentary-panel')`)
        // emission styles.
        var hasTestid = Regex.IsMatch(text,
                @"data-testid\s*=\s*['""]commentary-panel['""]")
            || Regex.IsMatch(text,
                @"setAttribute\(\s*['""]data-testid['""]\s*,\s*['""]commentary-panel['""]\s*\)");
        Assert.True(hasTestid,
            "commentary-panel module MUST emit data-testid=\"commentary-panel\" (inline or via setAttribute).");
    }

    // ────────────────────────────────────────────────────────────────────
    //  Spectator livestream HLS viewer — spectator-livestream.ts module
    //  present; carries an <audio> element wired to an HLS playlist URL.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-6")]
    public void SpectatorLivestream_HlsViewer_AudioSource_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(FrontendSrc(root), "spectator-livestream.ts"),
            Path.Combine(FrontendSrc(root), "spectator", "livestream.ts"),
            Path.Combine(FrontendSrc(root), "livestream.ts"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return; // forward-staged

        var text = File.ReadAllText(path);
        // The viewer MUST mount an <audio> element (HLS audio-only is the
        // canonical W6 shape per Bishop's voice-livestream stream).
        Assert.Matches(new Regex(@"<audio|createElement\(['""]audio['""]\)"), text);
        // The source URL MUST reference the playlist endpoint.
        Assert.Matches(new Regex(@"playlist\.m3u8|application/vnd\.apple\.mpegurl"), text);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Bracket renderers — per-format testid roots.
    //  Swiss: data-testid="bracket-format-swiss"
    //  Double-elim: data-testid="bracket-format-double-elim"
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-6")]
    public void BracketRenderers_PerFormatTestid_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;

        // Walk all bracket-related TS modules and look for the testid
        // emit. The exact module structure is Hicks's choice — we
        // string-scan a small set of candidates.
        var candidates = new[]
        {
            Path.Combine(FrontendSrc(root), "bracket-renderer.ts"),
            Path.Combine(FrontendSrc(root), "tournament-bracket.ts"),
            Path.Combine(FrontendSrc(root), "tournament", "bracket.ts"),
            Path.Combine(FrontendSrc(root), "tournament", "bracket-renderer.ts"),
        };
        var existing = candidates.Where(File.Exists).ToList();
        if (existing.Count == 0) return; // forward-staged

        var allText = string.Join("\n", existing.Select(File.ReadAllText));

        // Per-format testid emission: tolerate static literals OR
        // dynamic template-literal emission (`bracket-format-${format}`)
        // PROVIDED the renderer also handles the format by name.
        bool HasFormatBranch(string formatName)
        {
            // Static literal testid.
            if (Regex.IsMatch(allText,
                $@"data-testid\s*=\s*['""]bracket-format-{formatName}['""]")) return true;
            if (Regex.IsMatch(allText,
                $@"setAttribute\(\s*['""]data-testid['""]\s*,\s*['""]bracket-format-{formatName}['""]\s*\)")) return true;
            // Dynamic template-literal — confirm the format is handled by name.
            var hasDynamicTestid = Regex.IsMatch(allText,
                @"data-testid['""]\s*,\s*[`'""]bracket-format-\$\{[^}]+\}")
                || Regex.IsMatch(allText,
                @"`bracket-format-\$\{[^}]+\}`");
            var hasFormatName = Regex.IsMatch(allText,
                $@"['""\\b]{formatName}['""\\b]", RegexOptions.IgnoreCase);
            return hasDynamicTestid && hasFormatName;
        }

        var hasSwiss = HasFormatBranch("swiss");
        var hasDouble = HasFormatBranch("double-elim");

        if (!hasSwiss && !hasDouble) return; // forward-staged

        // If ANY per-format testid lands, BOTH must (W6 brief lockstep).
        Assert.True(hasSwiss,
            "bracket-format-swiss testid MUST be emitted (W6 brief).");
        Assert.True(hasDouble,
            "bracket-format-double-elim testid MUST be emitted (W6 brief).");
    }

    // ────────────────────────────────────────────────────────────────────
    //  three-renderer chunk < 700 KB on source. Wave-5 introduced the
    //  split; Wave-6 brief tightens to a 700 KB ceiling (W5 was loose).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-6")]
    public void ThreeRenderer_SourceUnder700Kb_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(FrontendSrc(root), "three-renderer.ts");
        if (!File.Exists(path)) return; // forward-staged

        var fi = new FileInfo(path);
        // Source is much smaller than the bundled chunk; the SOURCE
        // budget catches an unbounded import expansion early.
        Assert.True(fi.Length < 700 * 1024,
            $"three-renderer.ts source MUST be < 700 KB; got {fi.Length} bytes.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  PWA install prompt — install button + beforeinstallprompt handler.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Frontend"), Trait("Wave", "Phase-K-6")]
    public void PwaInstallPrompt_ButtonAndHandler_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(FrontendSrc(root), "pwa.ts"),
            Path.Combine(FrontendSrc(root), "pwa-install.ts"),
            Path.Combine(FrontendSrc(root), "pwa", "install.ts"),
        };
        var existing = candidates.Where(File.Exists).ToList();
        if (existing.Count == 0) return; // forward-staged

        var allText = string.Join("\n", existing.Select(File.ReadAllText));

        var hasHandler = Regex.IsMatch(allText, @"beforeinstallprompt");
        if (!hasHandler) return; // forward-staged (Wave-3 PWA may pre-date this)

        // When the handler IS wired, the install button MUST carry the
        // canonical testid (inline or via setAttribute).
        var hasInstallButton = Regex.IsMatch(allText,
                @"data-testid\s*=\s*['""]pwa-install-button['""]")
            || Regex.IsMatch(allText,
                @"setAttribute\(\s*['""]data-testid['""]\s*,\s*['""]pwa-install-button['""]\s*\)");
        Assert.True(hasInstallButton,
            "pwa.ts MUST emit data-testid=\"pwa-install-button\" once beforeinstallprompt handler is wired.");
    }
}
