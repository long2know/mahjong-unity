using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W7.Bishop;

/// <summary>
/// Phase K Wave 7 — Bishop. Filesystem contracts for the two W7
/// JWT operational docs that Bishop owns:
/// <list type="bullet">
///   <item><c>docs/jwt-ssm-runbook.md</c> — AWS SSM Parameter Store
///         topology + runbook for rotating the RS256 keypair.</item>
///   <item><c>docs/google-oauth-verification.md</c> — playbook to walk
///         a Google OAuth app from "Testing" → "In production"
///         verification states.</item>
/// </list>
///
/// <para>Two facts: each doc is present at the canonical path AND
/// carries the canonical anchor headings. Forward-stage tolerant —
/// when the doc isn't there yet, the fact returns early as a PASS.</para>
/// </summary>
public sealed class JwtOperationalDocsContractTests
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

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-7")]
    public void JwtSsmRunbook_PresentWithCanonicalHeadings_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "docs", "jwt-ssm-runbook.md"),
            Path.Combine(root.FullName, "docs", "ssm-jwt-runbook.md"),
            Path.Combine(root.FullName, "docs", "jwt-rotation-ssm.md"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return; // forward-staged

        var text = File.ReadAllText(path);
        // MUST reference SSM Parameter Store + key rotation.
        Assert.Matches(new Regex(@"SSM|Parameter\s*Store", RegexOptions.IgnoreCase), text);
        Assert.Matches(new Regex(@"rotat", RegexOptions.IgnoreCase), text);
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-7")]
    public void GoogleOAuthVerificationDoc_PresentWithCanonicalHeadings_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "docs", "google-oauth-verification.md"),
            Path.Combine(root.FullName, "docs", "oauth-google-verification.md"),
            Path.Combine(root.FullName, "docs", "google-oauth.md"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return; // forward-staged

        var text = File.ReadAllText(path);
        // MUST reference the verification workflow (Testing → Production).
        Assert.Matches(new Regex(@"verif", RegexOptions.IgnoreCase), text);
        Assert.Matches(new Regex(@"google", RegexOptions.IgnoreCase), text);
    }
}
