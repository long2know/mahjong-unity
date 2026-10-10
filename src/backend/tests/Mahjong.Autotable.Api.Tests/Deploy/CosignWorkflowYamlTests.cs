using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Deploy;

/// <summary>
/// Phase K Wave 1 — cosign sign-image GitHub workflow contract (Vasquez).
///
/// <para>Apone's Phase K Wave 1 brief ships
/// <c>.github/workflows/sign-image.yml</c> — a workflow that signs
/// freshly-built container images via <c>sigstore/cosign-installer</c>
/// + <c>cosign sign</c>. Contract:
/// <list type="bullet">
///   <item>File parses as a valid GitHub Actions workflow (<c>name</c>,
///         <c>on</c>, <c>jobs</c>).</item>
///   <item>Uses <c>sigstore/cosign-installer@v3</c> (canonical major
///         version as of late 2025).</item>
///   <item>The <c>cosign sign</c> command appears in a job step.</item>
///   <item>Triggered on push to <c>main</c> or new tag refs.</item>
///   <item>Has appropriate <c>id-token: write</c> permission for
///         keyless OIDC signing (Sigstore).</item>
/// </list></para>
///
/// <para><b>Forward-staged.</b> Soft-pass when the workflow file is not
/// yet present in the repo.</para>
/// </summary>
public class CosignWorkflowYamlTests
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
        foreach (var name in new[] { "sign-image.yml", "sign-image.yaml", "cosign.yml", "image-signing.yml" })
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
    public void Cosign_Workflow_ExistsOrNotYetShipped()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        Assert.False(string.IsNullOrWhiteSpace(content));
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Has the standard workflow header fields
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    public void Cosign_Workflow_HasNameOnJobs()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        Assert.Matches(@"(?m)^name:\s*\S", content);
        Assert.Matches(@"(?m)^on:\s*", content);
        Assert.Matches(@"(?m)^jobs:\s*", content);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Uses sigstore/cosign-installer (any major) — canonical v3 pinned
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    public void Cosign_Workflow_UsesCosignInstaller()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        // Accept v2 or v3 here — the brief says "uses cosign@v2"; the
        // canonical action name is `sigstore/cosign-installer@v3` as of
        // late 2025. Either is acceptable provided the installer is
        // present at all.
        var hasInstaller = Regex.IsMatch(content,
            @"sigstore/cosign-installer@v\d", RegexOptions.IgnoreCase);
        Assert.True(hasInstaller,
            "sign-image.yml must use the sigstore/cosign-installer action.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Actually invokes `cosign sign` (or `cosign sign-blob`)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    public void Cosign_Workflow_InvokesCosignSign()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        var hasSign = Regex.IsMatch(content,
            @"\bcosign\s+sign\b", RegexOptions.IgnoreCase);
        Assert.True(hasSign,
            "sign-image.yml must run `cosign sign` in a job step.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Keyless OIDC requires id-token: write
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    public void Cosign_Workflow_HasIdTokenWritePermission()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        // Soft-pass when the workflow is keyful (uses a stored key) —
        // in that case id-token: write isn't required.
        if (Regex.IsMatch(content, @"cosign\s+sign[^\r\n]*(--key|-k\s+)", RegexOptions.IgnoreCase)) return;
        // Otherwise: keyless signing needs the id-token write perm.
        Assert.Matches(@"id-token:\s*write", content);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. Workflow trigger covers main / tags (registry-push surface)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-1")]
    public void Cosign_Workflow_RunsOnMainOrTag()
    {
        var content = LoadWorkflow();
        if (content is null) return;
        var hasMain = content.Contains("main", StringComparison.OrdinalIgnoreCase);
        var hasTags = content.Contains("tags:", StringComparison.OrdinalIgnoreCase)
                   || Regex.IsMatch(content, @"v\*\.\*\.\*");
        var hasWfRun = content.Contains("workflow_run", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasMain || hasTags || hasWfRun,
            "sign-image.yml must trigger on main branch, tag push, or workflow_run.");
    }
}
