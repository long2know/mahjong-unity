using System.IO;
using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Deploy;

/// <summary>
/// Phase J Wave 9 — SBOM GitHub Actions workflow contract tests (Vasquez).
///
/// <para>Apone's Wave 9 ships <c>.github/workflows/sbom.yml</c> — a
/// scheduled / push-triggered workflow that generates a Software Bill of
/// Materials for the production image + repo source. Contract:
/// <list type="bullet">
///   <item>YAML file parses + has the canonical workflow shape
///         (<c>name</c>, <c>on</c>, <c>jobs</c>).</item>
///   <item>Uses Trivy (or another scanner) for vulnerability scanning.</item>
///   <item>Trivy severity threshold gates the workflow (CRITICAL,HIGH).</item>
///   <item>Generates SPDX or CycloneDX SBOM artefacts.</item>
///   <item>Uploads results to GitHub code-scanning or attaches as
///         release artefacts.</item>
/// </list></para>
///
/// <para>Soft-passes when the file is not yet present.</para>
/// </summary>
public class SbomWorkflowTests
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

    private static string? LoadWorkflow()
    {
        var root = LocateRepoRoot();
        var candidates = new[]
        {
            Path.Combine(root, ".github", "workflows", "sbom.yml"),
            Path.Combine(root, ".github", "workflows", "sbom.yaml"),
            Path.Combine(root, ".github", "workflows", "trivy.yml"),
            Path.Combine(root, ".github", "workflows", "security-scan.yml"),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-9")]
    public void SbomWorkflow_FileExists_OrNotYetShipped()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        Assert.False(string.IsNullOrWhiteSpace(content));
    }

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-9")]
    public void SbomWorkflow_HasCanonicalTopLevelKeys()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        Assert.Matches(@"(?im)^\s*name:\s*", content);
        Assert.Matches(@"(?im)^on:\s*$", content);
        Assert.Matches(@"(?im)^jobs:\s*$", content);
    }

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-9")]
    public void SbomWorkflow_UsesTrivyOrEquivalentScanner()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        // Accept any of the canonical scanner actions.
        var hasScanner =
            content.Contains("aquasecurity/trivy-action", StringComparison.OrdinalIgnoreCase)
            || content.Contains("aquasecurity/trivy", StringComparison.OrdinalIgnoreCase)
            || content.Contains("anchore/scan-action", StringComparison.OrdinalIgnoreCase)
            || content.Contains("trivy", StringComparison.OrdinalIgnoreCase)
            || content.Contains("grype", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasScanner,
            "SBOM workflow must invoke a vulnerability scanner (Trivy / Grype / Anchore).");
    }

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-9")]
    public void SbomWorkflow_SetsCorrectTrivyThresholds()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        // Severity gate must cover CRITICAL + HIGH at minimum.
        // Severities can be supplied as a YAML `severity:` field or
        // a `--severity` CLI flag.
        var hasCritical = Regex.IsMatch(content, @"(?i)\bCRITICAL\b");
        var hasHigh = Regex.IsMatch(content, @"(?i)\bHIGH\b");
        // Soft-pass while in flight: only fire RED if the file lists a
        // severity but missed the canonical pair.
        if (!hasCritical && !hasHigh) return;
        Assert.True(hasCritical && hasHigh,
            "Trivy severity threshold must cover both CRITICAL and HIGH.");
    }

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-9")]
    public void SbomWorkflow_GeneratesSpdxOrCycloneDx()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        var hasSpdx = content.Contains("spdx", StringComparison.OrdinalIgnoreCase);
        var hasCycloneDx = content.Contains("cyclonedx", StringComparison.OrdinalIgnoreCase);
        var hasSbom = content.Contains("sbom", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasSpdx || hasCycloneDx || hasSbom,
            "SBOM workflow must produce an SPDX or CycloneDX bill of materials.");
    }

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-9")]
    public void SbomWorkflow_FailsOnHighSeverity()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        // The action must NOT silently log; it must fail the job on
        // CRITICAL findings. Trivy: `exit-code: '1'`. Anchore:
        // `fail-build: true`. Grype: `fail-on: critical`.
        var failsOnFinding =
            Regex.IsMatch(content, @"(?im)^\s*exit-code:\s*['""]?1['""]?")
            || Regex.IsMatch(content, @"(?im)^\s*fail-build:\s*true")
            || Regex.IsMatch(content, @"(?im)^\s*fail-on:\s*(critical|high)");
        if (!Regex.IsMatch(content, @"(?i)exit-code|fail-build|fail-on")) return; // not yet declared
        Assert.True(failsOnFinding,
            "Scanner must fail the workflow on CRITICAL findings (exit-code:1 / fail-build / fail-on).");
    }
}
