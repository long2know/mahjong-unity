using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W18.Vasquez;

/// <summary>
/// Phase K Wave 18 — Vasquez. Apone W18 / Vasquez W18 hard-assert:
/// the <c>.github/workflows/pwa-audit.yml</c> Lighthouse 13
/// invocation must include both <c>--form-factor=desktop</c> AND
/// <c>--screenEmulation.mobile=false</c> (the W18 root-cause fix
/// for the LH13 cron deadlock identified in W17 §6.7).
///
/// <para>This file is the Vasquez-lane hard-assert paired with the
/// Apone-lane fix (<see cref="AponeW18Lh13FormFactorFixTests"/>
/// soft-pins the same surface).</para>
///
/// <para>Per the W18 brief: if 3+ successful schedule-event cron
/// runs accrue post-fix, <c>docs/agent-handoff-protocol.md §6.7</c>
/// flips to GREEN and calibration data hands off to Hicks for §6.8
/// LH13 HARD-PIN; if still failing, the new failure mode is
/// documented in §6.8 (RED if persistent).</para>
/// </summary>
public sealed class PwaAuditWorkflowGateW18Tests
{
    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !File.Exists(Path.Combine(d.FullName, "Dockerfile")))
            d = d.Parent;
        return d;
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void PwaAuditWorkflow_File_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var wf = Path.Combine(root!.FullName, ".github", "workflows", "pwa-audit.yml");
        Assert.True(File.Exists(wf));
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void PwaAuditWorkflow_FormFactorDesktop_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var wf = Path.Combine(root!.FullName, ".github", "workflows", "pwa-audit.yml");
        Assert.True(File.Exists(wf));
        var text = File.ReadAllText(wf);
        Assert.Contains("--form-factor=desktop", text, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "LaneDiscipline"), Trait("Wave", "Phase-K-18")]
    public void PwaAuditWorkflow_LighthouseInvocation_Present()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var wf = Path.Combine(root!.FullName, ".github", "workflows", "pwa-audit.yml");
        Assert.True(File.Exists(wf));
        var text = File.ReadAllText(wf);
        Assert.Contains("lighthouse", text, StringComparison.OrdinalIgnoreCase);
    }
}
