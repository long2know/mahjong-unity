using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Deploy;

/// <summary>
/// Phase K Wave 1 — multi-arch smoke workflow contract (Vasquez).
///
/// <para>Apone's Phase J Wave 10 added a multi-arch Docker build
/// (linux/amd64 + linux/arm64). Phase K Wave 1 adds a follow-up smoke
/// workflow <c>multi-arch-smoke.yml</c> that pulls each arch's image
/// from ghcr.io and runs a basic <c>/health</c> probe against it. The
/// contract:
/// <list type="bullet">
///   <item>File parses as a valid GitHub Actions workflow.</item>
///   <item>Has a matrix strategy that lists BOTH <c>linux/amd64</c>
///         AND <c>linux/arm64</c>.</item>
///   <item>Uses <c>docker run --platform=</c> or <c>--platform</c>
///         setup-buildx-action to pin per-arch.</item>
///   <item>Runs the existing <c>tests/smoke/</c> probes (or curl-style
///         /health hits) against the booted container.</item>
/// </list></para>
///
/// <para><b>Forward-staged.</b> Soft-pass when the workflow file
/// isn't yet present.</para>
/// </summary>
public class MultiArchSmokeYamlTests
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
        foreach (var name in new[]
            {
                "multi-arch-smoke.yml",
                "multi-arch-smoke.yaml",
                "multiarch-smoke.yml",
                "image-smoke-multi-arch.yml",
            })
        {
            var p = Path.Combine(root, ".github", "workflows", name);
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        return null;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. File present OR forward-staged
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    public void MultiArchSmoke_Workflow_ExistsOrNotYetShipped()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        Assert.False(string.IsNullOrWhiteSpace(content));
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Has the canonical workflow header
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    public void MultiArchSmoke_Workflow_HasShape()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        Assert.Matches(@"(?m)^name:\s*\S", content);
        Assert.Matches(@"(?m)^on:\s*", content);
        Assert.Matches(@"(?m)^jobs:\s*", content);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Exercises BOTH linux/amd64 AND linux/arm64
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    public void MultiArchSmoke_Workflow_ExercisesBothArches()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        Assert.Contains("linux/amd64", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("linux/arm64", content, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Uses a matrix strategy or per-arch jobs
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    public void MultiArchSmoke_Workflow_UsesMatrixOrPerArchJobs()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        // Matrix can appear directly under `strategy:` or after sibling keys
        // like `fail-fast:`; accept either layout.
        var hasMatrix = Regex.IsMatch(content, @"strategy:\s*\r?\n(\s+[A-Za-z_-]+:.*\r?\n)*\s+matrix:",
            RegexOptions.IgnoreCase);
        // Or two separate jobs each with `--platform=linux/<arch>`.
        var hasTwoPlatforms = Regex.Matches(content,
            @"--platform[= ]linux/(amd64|arm64)", RegexOptions.IgnoreCase).Count >= 2;
        Assert.True(hasMatrix || hasTwoPlatforms,
            "multi-arch-smoke.yml must use a matrix or two per-arch jobs.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Invokes the smoke harness (curl /health, smoke shell script,
    //     or e2e Playwright)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    public void MultiArchSmoke_Workflow_InvokesSmokeHarness()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        var hasSmoke =
            Regex.IsMatch(content, @"\bcurl\s+[^\r\n]*\/health", RegexOptions.IgnoreCase)
            || content.Contains("tests/smoke/", StringComparison.OrdinalIgnoreCase)
            || content.Contains("docker-build-smoke.sh", StringComparison.OrdinalIgnoreCase)
            || content.Contains("auth-flow-smoke.sh", StringComparison.OrdinalIgnoreCase)
            || content.Contains("npm run e2e", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasSmoke,
            "multi-arch-smoke.yml must invoke /health curl, tests/smoke/, or npm run e2e.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. References ghcr.io as the image pull origin
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    public void MultiArchSmoke_Workflow_PullsFromGhcr()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        Assert.Contains("ghcr.io", content, StringComparison.OrdinalIgnoreCase);
    }
}
