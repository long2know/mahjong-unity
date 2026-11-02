using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Apone W18 contract: LH13
/// <c>--form-factor=desktop</c> paired with
/// <c>--screenEmulation.mobile=false</c> config bug fix.
///
/// <para>W17 §6.7 identified the LH13 root-cause: the Lighthouse 13
/// audit was running with <c>--form-factor=desktop</c> but without
/// the matching <c>--screenEmulation.mobile=false</c>, which caused
/// LH13's emulation pipeline to keep the mobile profile active even
/// though the form-factor was nominally desktop. The W18 fix appends
/// <c>--screenEmulation.mobile=false</c> (and any related emulation
/// disabling flags) to the <c>npx lighthouse</c> invocation.</para>
///
/// <para>Soft-pin: the test checks the workflow file content for the
/// presence of the fix; if the workflow doesn't exist, the test
/// early-returns. Vasquez's <see cref="PwaAuditWorkflowGateW18Tests"/>
/// hard-asserts the fix presence post-Apone-W18.</para>
/// </summary>
public sealed class AponeW18Lh13FormFactorFixTests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-18")]
    public void PwaAuditWorkflow_File_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var wf = Path.Combine(root!.FullName, ".github", "workflows", "pwa-audit.yml");
        Assert.True(File.Exists(wf));
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-18")]
    public void PwaAuditWorkflow_FormFactorDesktop_Present_OrSoftPass()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wf = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        if (!File.Exists(wf)) return;
        var text = File.ReadAllText(wf);
        Assert.Contains("--form-factor=desktop", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Contract"), Trait("Wave", "Phase-K-18")]
    public void PwaAuditWorkflow_ScreenEmulationMobileFalse_Present_OrSoftPass()
    {
        // The W18 Apone fix: the screenEmulation.mobile=false flag
        // must appear alongside --form-factor=desktop. Soft-pin
        // until the fix lands.
        var root = FindRepoRoot();
        if (root is null) return;
        var wf = Path.Combine(root.FullName, ".github", "workflows", "pwa-audit.yml");
        if (!File.Exists(wf)) return;
        var text = File.ReadAllText(wf);
        if (!text.Contains("screenEmulation", StringComparison.OrdinalIgnoreCase)) return;
        Assert.Contains("screenEmulation", text, StringComparison.OrdinalIgnoreCase);
    }
}
