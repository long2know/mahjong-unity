namespace Mahjong.Autotable.Api.Tests.Phase_K_W20.Vasquez;

/// <summary>
/// Phase K Wave 20 — Vasquez self-lane.  SLSA-3 vasquez-lane SHA
/// pinning sweep — confirm the 9 unpinned refs identified in
/// <c>docs/slsa-pinning-w20-sweep.md</c> have been propagated to
/// the canonical <c>@&lt;sha40&gt; # v&lt;semver&gt;</c> shape in
/// the 4 vasquez-lane workflows.
///
/// <para>The 9 refs (per Apone's W20 hand-off doc):</para>
/// <list type="number">
///   <item><c>lane-discipline.yml:42</c>      — actions/checkout@v4</item>
///   <item><c>lane-discipline-nightly.yml:37</c> — actions/checkout@v4</item>
///   <item><c>lane-discipline-status.yml:35</c> — actions/checkout@v4</item>
///   <item><c>playwright-visual-regression.yml:68</c>  — actions/checkout@v4</item>
///   <item><c>playwright-visual-regression.yml:74</c>  — actions/setup-node@v4</item>
///   <item><c>playwright-visual-regression.yml:81</c>  — actions/cache@v4</item>
///   <item><c>playwright-visual-regression.yml:135</c> — actions/upload-artifact@v4</item>
///   <item><c>playwright-visual-regression.yml:147</c> — actions/upload-artifact@v4</item>
///   <item><c>playwright-visual-regression.yml:196</c> — marocchino/sticky-pull-request-comment@v2</item>
/// </list>
/// </summary>
public sealed class Slsa3VasquezLaneSweepW20Tests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    private static readonly string[] VasquezLaneWorkflows =
    {
        ".github/workflows/lane-discipline.yml",
        ".github/workflows/lane-discipline-nightly.yml",
        ".github/workflows/lane-discipline-status.yml",
        ".github/workflows/playwright-visual-regression.yml",
    };

    private static string ReadWorkflow(string relativePath)
    {
        var root = FindRepoRoot();
        var p = Path.Combine(root!.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(p) ? File.ReadAllText(p) : string.Empty;
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void Slsa3W20Sweep_NoUnpinned_ActionsCheckoutV4()
    {
        foreach (var wf in VasquezLaneWorkflows)
        {
            var text = ReadWorkflow(wf);
            if (string.IsNullOrEmpty(text)) continue;
            Assert.DoesNotContain("uses: actions/checkout@v4", text, StringComparison.Ordinal);
        }
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void Slsa3W20Sweep_NoUnpinned_ActionsSetupNodeV4()
    {
        foreach (var wf in VasquezLaneWorkflows)
        {
            var text = ReadWorkflow(wf);
            if (string.IsNullOrEmpty(text)) continue;
            Assert.DoesNotContain("uses: actions/setup-node@v4", text, StringComparison.Ordinal);
        }
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void Slsa3W20Sweep_NoUnpinned_ActionsCacheV4()
    {
        foreach (var wf in VasquezLaneWorkflows)
        {
            var text = ReadWorkflow(wf);
            if (string.IsNullOrEmpty(text)) continue;
            Assert.DoesNotContain("uses: actions/cache@v4", text, StringComparison.Ordinal);
        }
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void Slsa3W20Sweep_NoUnpinned_ActionsUploadArtifactV4()
    {
        foreach (var wf in VasquezLaneWorkflows)
        {
            var text = ReadWorkflow(wf);
            if (string.IsNullOrEmpty(text)) continue;
            Assert.DoesNotContain("uses: actions/upload-artifact@v4", text, StringComparison.Ordinal);
        }
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void Slsa3W20Sweep_NoUnpinned_StickyPullRequestCommentV2()
    {
        foreach (var wf in VasquezLaneWorkflows)
        {
            var text = ReadWorkflow(wf);
            if (string.IsNullOrEmpty(text)) continue;
            Assert.DoesNotContain("uses: marocchino/sticky-pull-request-comment@v2",
                text, StringComparison.Ordinal);
        }
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-20"), Trait("Lane", "Vasquez")]
    public void Slsa3W20Sweep_ActionsCheckout_CanonicalSha_AtLeastOnce()
    {
        // The canonical SHA from Apone's W20 doc is reused across all
        // lane-discipline workflows.  At least one ref must remain
        // present (sanity that the rewrite produced the pinned shape,
        // not a deletion).
        var anyPresent = VasquezLaneWorkflows
            .Select(ReadWorkflow)
            .Any(t => t.Contains(
                "actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683",
                StringComparison.Ordinal));
        Assert.True(anyPresent);
    }
}
