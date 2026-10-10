using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Deploy;

/// <summary>
/// Phase J Wave 8 — CHANGELOG.md shape tests (Vasquez).
///
/// <para>Apone's Wave 8 ships a top-level <c>CHANGELOG.md</c> capturing
/// human-readable release notes per phase / wave. Expected shape:
/// <list type="bullet">
///   <item>One <c>## [version-or-tag]</c> header per release.</item>
///   <item>Reverse-chronological order (newest first).</item>
///   <item>Each entry references at least one PR by number or short
///         description.</item>
/// </list></para>
///
/// <para>This test soft-passes when <c>CHANGELOG.md</c> is absent (the
/// surface isn't yet shipped). Once present, the parse / structure checks
/// fire.</para>
/// </summary>
public class ChangelogShapeTests
{
    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static string? FindChangelog()
    {
        var root = LocateRepoRoot();
        string[] candidates =
        {
            Path.Combine(root, "CHANGELOG.md"),
            Path.Combine(root, "CHANGELOG"),
            Path.Combine(root, "docs", "CHANGELOG.md"),
            Path.Combine(root, "changelog.md"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. CHANGELOG is present OR not-yet-shipped
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-8")]
    public void Changelog_PresentOrNotYetShipped()
    {
        var path = FindChangelog();
        if (path is null) return; // not yet shipped — soft pass.
        Assert.True(File.Exists(path));
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. CHANGELOG carries at least one ## section header
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-8")]
    public void Changelog_HasAtLeastOneSectionHeader()
    {
        var path = FindChangelog();
        if (path is null) return;

        var text = File.ReadAllText(path);
        var sections = Regex.Matches(text, @"^##\s+.+$", RegexOptions.Multiline);
        Assert.True(sections.Count >= 1,
            "CHANGELOG.md must carry at least one ## release section header.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. CHANGELOG mentions Phase J at least once
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-8")]
    public void Changelog_MentionsPhaseJ()
    {
        var path = FindChangelog();
        if (path is null) return;

        var text = File.ReadAllText(path);
        Assert.True(
            text.Contains("Phase J", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Phase-J", StringComparison.OrdinalIgnoreCase),
            "CHANGELOG.md must reference at least one Phase J wave entry.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. CHANGELOG has entries referring to PRs / waves
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-8")]
    public void Changelog_EntriesReferencePullRequestsOrWaves()
    {
        var path = FindChangelog();
        if (path is null) return;

        var text = File.ReadAllText(path);
        // Match `#37`-style PR refs OR `Wave N`-style references.
        var prRefs = Regex.Matches(text, @"#\d{1,4}\b");
        var waveRefs = Regex.Matches(text, @"Wave\s+\d+", RegexOptions.IgnoreCase);
        Assert.True(prRefs.Count + waveRefs.Count >= 1,
            "CHANGELOG.md entries must reference at least one PR (#NN) or Wave label.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Section headers parse as valid CommonMark headings
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-8")]
    public void Changelog_SectionHeaders_AreCommonMarkH2()
    {
        var path = FindChangelog();
        if (path is null) return;

        var lines = File.ReadAllLines(path);
        var sectionLines = lines.Where(l => l.TrimStart().StartsWith("##")).ToList();
        if (sectionLines.Count == 0) return;

        // Each header must have a space after the `##`.
        foreach (var line in sectionLines)
        {
            Assert.True(
                Regex.IsMatch(line, @"^#{2,}\s+\S"),
                $"Malformed CHANGELOG heading: '{line}' — must be `## <title>`.");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. No trailing leftover TODO / FIXME markers
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-8")]
    public void Changelog_NoLeftoverTodoMarkers()
    {
        var path = FindChangelog();
        if (path is null) return;

        var text = File.ReadAllText(path);
        // Soft check — `TODO`/`FIXME` outside code blocks indicates an
        // unfinished release note. Empty CHANGELOG is fine. The check
        // tolerates `TBD` / `TBC` which are conventional placeholders.
        var todoMatches = Regex.Matches(text, @"\bTODO:?\s*$", RegexOptions.Multiline);
        var fixmeMatches = Regex.Matches(text, @"\bFIXME:?\s*$", RegexOptions.Multiline);
        Assert.True(todoMatches.Count == 0 && fixmeMatches.Count == 0,
            "CHANGELOG.md has trailing TODO / FIXME markers — replace before release.");
    }
}
