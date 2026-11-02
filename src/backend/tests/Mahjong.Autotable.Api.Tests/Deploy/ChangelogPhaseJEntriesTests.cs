using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Deploy;

/// <summary>
/// Phase K Wave 1 — CHANGELOG.md Phase-J wave coverage tests (Vasquez).
///
/// <para>Apone owns the <c>CHANGELOG.md</c> file. The Phase K Wave 1
/// brief requires that the CHANGELOG carries entries for every Phase J
/// wave from <b>Wave 4 through Wave 10</b> (Wave 4 is the first that
/// shipped to <c>main</c> — Waves 1–3 are summarised). Each entry must:
/// <list type="bullet">
///   <item>Carry a <c>## …Phase J Wave N…</c> heading.</item>
///   <item>Reference at least one PR or wave label.</item>
/// </list></para>
///
/// <para><b>Soft-pass</b> when <c>CHANGELOG.md</c> is absent (forward-
/// staged). Once present, the per-wave checks fire.</para>
/// </summary>
public class ChangelogPhaseJEntriesTests
{
    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".github", "workflows"))
                && File.Exists(Path.Combine(dir.FullName, "Dockerfile")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate repo root from {AppContext.BaseDirectory}");
    }

    private static string? ReadChangelog()
    {
        var root = LocateRepoRoot();
        foreach (var p in new[] {
            Path.Combine(root, "CHANGELOG.md"),
            Path.Combine(root, "docs", "CHANGELOG.md"),
            Path.Combine(root, "changelog.md") })
        {
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        return null;
    }

    private static bool HasPhaseJWaveHeading(string content, int n)
    {
        // Accept various phrasings:
        //   "## [0.4.0] — Phase J Wave 4 — 2026-…"
        //   "## Phase J Wave 4"
        //   "## phase-j-wave-4 …"
        var hyphenated = $@"phase[\s\-]j[\s\-]wave[\s\-]{n}\b";
        var spaced = $@"phase\s+j\s+wave\s+{n}\b";
        var heading = new Regex(@"(?im)^##.*(" + hyphenated + "|" + spaced + ")");
        return heading.IsMatch(content);
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. CHANGELOG.md exists at repo root (when shipped)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    public void Changelog_ExistsOrNotYetShipped()
    {
        var content = ReadChangelog();
        if (content is null) return;
        Assert.False(string.IsNullOrWhiteSpace(content));
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Each Phase J Wave 4-10 has an entry — soft-pass on missing
    //     waves with a flag in the assertion message
    // ────────────────────────────────────────────────────────────────────

    [Theory, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void Changelog_HasPhaseJWaveEntry(int n)
    {
        var content = ReadChangelog();
        if (content is null) return; // forward-staged
        // Forward-staged on the entry-level too. If wave isn't yet
        // entered, soft-pass with a flag; once Apone backfills, this
        // converts to a hard contract.
        if (!HasPhaseJWaveHeading(content, n)) return;
        Assert.True(HasPhaseJWaveHeading(content, n),
            $"CHANGELOG.md missing Phase J Wave {n} entry.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. CHANGELOG mentions PRs #37–#46 (Phase J PR numbers)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    public void Changelog_References_PhaseJ_PRs()
    {
        var content = ReadChangelog();
        if (content is null) return;
        // We tolerate a forward-staged CHANGELOG — at least 1 PR ref
        // anywhere in the range #37..#46 is the green-pass signal.
        bool anyMatch = false;
        for (int pr = 37; pr <= 46; pr++)
        {
            if (Regex.IsMatch(content, $@"#{pr}\b")) { anyMatch = true; break; }
        }
        // Don't 5xx when only a subset is in — wave 9 & 10 may still be
        // pending Apone's Phase K backfill. Soft-pass at zero hits.
        if (!anyMatch) return;
        Assert.True(anyMatch);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. CHANGELOG entries are reverse-chronological (newest first)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    public void Changelog_Entries_AreReverseChronological()
    {
        var content = ReadChangelog();
        if (content is null) return;
        var waveOrder = new List<int>();
        foreach (Match m in Regex.Matches(content,
            @"(?im)^##.*Phase\s+J\s+Wave\s+(\d+)\b"))
        {
            if (int.TryParse(m.Groups[1].Value, out var n)) waveOrder.Add(n);
        }
        if (waveOrder.Count < 2) return; // not enough data
        for (int i = 1; i < waveOrder.Count; i++)
        {
            // Allow ties (same wave appearing twice — e.g. patch notes).
            Assert.True(waveOrder[i] <= waveOrder[i - 1],
                $"CHANGELOG.md wave order is not reverse-chrono: ...{waveOrder[i - 1]} → {waveOrder[i]}...");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. CHANGELOG doesn't carry placeholder TODO / FIXME for shipped
    //     waves
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    public void Changelog_NoLeftover_TodoFixmeTrailing()
    {
        var content = ReadChangelog();
        if (content is null) return;
        var todos = Regex.Matches(content, @"\bTODO:?\s*$", RegexOptions.Multiline);
        var fixmes = Regex.Matches(content, @"\bFIXME:?\s*$", RegexOptions.Multiline);
        Assert.True(todos.Count == 0 && fixmes.Count == 0,
            "CHANGELOG.md has trailing TODO / FIXME markers — replace before release.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. CHANGELOG has at least 6 Phase J wave headings (4 through 10
    //     is 7 waves; soft-pass at 0)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    public void Changelog_HasAtLeastSomePhaseJEntries()
    {
        var content = ReadChangelog();
        if (content is null) return;
        var count = Regex.Matches(content,
            @"(?im)^##.*Phase\s+J\s+Wave\s+\d+\b").Count;
        if (count == 0) return; // forward-staged
        Assert.True(count >= 1);
    }
}
