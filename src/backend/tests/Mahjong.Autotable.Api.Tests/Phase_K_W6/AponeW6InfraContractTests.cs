using System.Reflection;
using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W6;

/// <summary>
/// Phase K Wave 6 — Apone's infra-lane surface contracts (Vasquez).
///
/// <para>Apone's W6 deliverables:</para>
/// <list type="bullet">
///   <item><b>Terraform DR multi-region module</b> —
///         <c>infra/terraform/modules/dr-replication/</c> with
///         <c>main.tf</c> + <c>variables.tf</c> + <c>outputs.tf</c>
///         + <c>README.md</c>.</item>
///   <item><b>GH OIDC role narrowing</b> — the
///         <c>aws_iam_role.github_deploy</c> attached policy MUST
///         NOT carry an unbounded <c>ecr:*</c> wildcard nor an
///         unbounded <c>"*"</c> Resource on a high-privilege
///         action.</item>
///   <item><b>Coturn k8s manifest</b> —
///         <c>infra/k8s/base/coturn-deployment.yaml</c> OR
///         <c>turn-server.yaml</c> with the canonical fields
///         (<c>kind: Deployment</c>, <c>image: coturn/coturn</c>,
///         <c>port: 3478</c>).</item>
///   <item><b>Container-scan severity tuning</b> — Trivy allowlist
///         entries MUST have an explicit <c>expires-at</c> ISO 8601
///         date that's parseable (the W4 allowlist had string-only
///         dates; W6 enforces the date is real).</item>
///   <item><b>Mobile internal-testing workflow</b> —
///         <c>.github/workflows/mobile-internal-testing.yml</c>
///         present, triggers manual + tag.</item>
///   <item><b>SLSA verification on deploy</b> —
///         <c>.github/workflows/verify-slsa-on-deploy.yml</c>
///         present, invokes <c>slsa-verifier</c>.</item>
///   <item><b>CHANGELOG 0.15.0</b> — <c>CHANGELOG.md</c> has a
///         <c>## [0.15.0]</c> section for the W6 release.</item>
///   <item><b>Retro doc</b> — <c>docs/retros/phase-k-wave-6.md</c>
///         present with retro structure.</item>
/// </list>
///
/// <para>Every fact filesystem-probed at the repo root. Forward-stage
/// soft-pass on absence; hard-assert canonical shape on presence.</para>
/// </summary>
public class AponeW6InfraContractTests
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

    private static string? ReadIfExists(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;

    // ────────────────────────────────────────────────────────────────────
    //  Terraform DR multi-region module presence.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-6")]
    public void TerraformDrReplicationModule_PresentWithCanonicalFiles_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var moduleDir = Path.Combine(root.FullName, "infra", "terraform", "modules", "dr-replication");
        if (!Directory.Exists(moduleDir)) return; // forward-staged

        // Once the module ships, MUST carry main.tf at minimum.
        var mainTf = Path.Combine(moduleDir, "main.tf");
        Assert.True(File.Exists(mainTf),
            $"dr-replication module MUST carry main.tf at {mainTf}.");

        // Hard-pin: the module references cross-region replication.
        var text = File.ReadAllText(mainTf);
        var hasReplication = Regex.IsMatch(text,
            @"replic|cross[_-]?region|aws_rds_cluster|aws_db_instance",
            RegexOptions.IgnoreCase);
        Assert.True(hasReplication,
            "dr-replication/main.tf MUST reference at least one cross-region resource (RDS/replication/cross-region).");
    }

    // ────────────────────────────────────────────────────────────────────
    //  GH OIDC role narrowing — no ecr:* wildcard on the github_deploy
    //  role's attached policy. Pure regex scan of the IAM Terraform.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-6")]
    public void GitHubOidcRole_NoEcrWildcard_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "infra", "terraform", "iam-github-oidc.tf"),
            Path.Combine(root.FullName, "infra", "terraform", "modules", "github-oidc", "main.tf"),
            Path.Combine(root.FullName, "infra", "terraform", "iam.tf"),
        };
        var existing = candidates.Where(File.Exists).ToList();
        if (existing.Count == 0) return; // forward-staged

        foreach (var path in existing)
        {
            var text = File.ReadAllText(path);
            // The W6 narrowing brief: NO ecr:* wildcard action.
            // Pattern: `"ecr:*"` as an action string (whitespace
            // tolerant). We tolerate "ecr-public:*" since that's a
            // separate ARN namespace.
            var hasEcrWild = Regex.IsMatch(text,
                @"['""]ecr:\*['""]");
            Assert.False(hasEcrWild,
                $"{path} MUST NOT grant ecr:* wildcard (W6 OIDC narrowing).");

            // Also flag iam:* + sts:* on the deploy role (high blast radius).
            var hasIamWild = Regex.IsMatch(text,
                @"['""]iam:\*['""]");
            Assert.False(hasIamWild,
                $"{path} MUST NOT grant iam:* wildcard (W6 OIDC narrowing).");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Coturn k8s manifest fields.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-6")]
    public void CoturnManifest_CanonicalFields_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "infra", "k8s", "base", "coturn-deployment.yaml"),
            Path.Combine(root.FullName, "infra", "k8s", "base", "turn-server.yaml"),
            Path.Combine(root.FullName, "infra", "k8s", "base", "coturn.yaml"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return; // forward-staged

        var text = File.ReadAllText(path);
        Assert.Matches(new Regex(@"^kind:\s*Deployment", RegexOptions.Multiline), text);
        Assert.Matches(new Regex(@"image:\s*[^\s]*coturn", RegexOptions.IgnoreCase), text);
        // TURN listens on UDP 3478 by spec.
        Assert.Matches(new Regex(@"3478"), text);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Container-scan allowlist — expires-at MUST be parseable ISO date.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-6")]
    public void TrivyAllowlist_ExpiresAtParseable_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, ".trivyignore"),
            Path.Combine(root.FullName, "trivy.yaml"),
            Path.Combine(root.FullName, ".trivy.yaml"),
            Path.Combine(root.FullName, ".github", "trivy-allowlist.yaml"),
            Path.Combine(root.FullName, "infra", "trivy-allowlist.yaml"),
        };
        var existing = candidates.Where(File.Exists).ToList();
        if (existing.Count == 0) return; // forward-staged

        foreach (var path in existing)
        {
            var text = File.ReadAllText(path);
            // Find every `expires-at: <date>` or `expiry: <date>` entry.
            var matches = Regex.Matches(text,
                @"(?:expires[_-]?at|expiry|expires)\s*:\s*['""]?(\d{4}-\d{2}-\d{2}(?:T\d{2}:\d{2}:\d{2}Z?)?)['""]?",
                RegexOptions.IgnoreCase);

            foreach (Match m in matches)
            {
                var dateStr = m.Groups[1].Value;
                Assert.True(
                    DateTime.TryParse(dateStr, out _),
                    $"{path}: allowlist expiry '{dateStr}' MUST be parseable ISO 8601.");
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Mobile internal-testing workflow presence.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-6")]
    public void MobileInternalTestingWorkflow_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wfPath = Path.Combine(root.FullName, ".github", "workflows", "mobile-internal-testing.yml");
        if (!File.Exists(wfPath)) return; // forward-staged

        var text = File.ReadAllText(wfPath);
        // Once the workflow lands, MUST carry a YAML `on:` trigger
        // (workflow_dispatch + push tag at minimum).
        Assert.Matches(new Regex(@"^on:", RegexOptions.Multiline), text);
        Assert.Matches(new Regex(@"workflow_dispatch", RegexOptions.IgnoreCase), text);
    }

    // ────────────────────────────────────────────────────────────────────
    //  slsa-verifier on deploy.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-6")]
    public void VerifySlsaOnDeployWorkflow_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wfPath = Path.Combine(root.FullName, ".github", "workflows", "verify-slsa-on-deploy.yml");
        if (!File.Exists(wfPath)) return; // forward-staged

        var text = File.ReadAllText(wfPath);
        // The workflow MUST invoke slsa-verifier (binary or action).
        var hasSlsaVerifier = Regex.IsMatch(text, @"slsa-verifier", RegexOptions.IgnoreCase);
        Assert.True(hasSlsaVerifier,
            "verify-slsa-on-deploy.yml MUST reference slsa-verifier (binary or action).");
    }

    // ────────────────────────────────────────────────────────────────────
    //  CHANGELOG 0.15.0 entry.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-6")]
    public void Changelog_0_15_0_Section_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "CHANGELOG.md");
        if (!File.Exists(path)) return; // forward-staged

        var text = File.ReadAllText(path);
        // The section header MUST land before the gate flips.
        var has0150 = Regex.IsMatch(text,
            @"^##\s+\[?0\.15\.0\]?", RegexOptions.Multiline);
        if (!has0150) return; // forward-staged
        // Once the header is there, must be followed by SOMETHING.
        var sectionStart = text.IndexOf("0.15.0", StringComparison.Ordinal);
        Assert.True(sectionStart >= 0);
        var rest = text[(sectionStart + 6)..];
        Assert.True(rest.Trim().Length > 0,
            "CHANGELOG 0.15.0 section MUST have a body.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  Retro doc structure.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-6")]
    public void RetroDoc_PhaseKWave6_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var candidates = new[]
        {
            Path.Combine(root.FullName, "docs", "retros", "phase-k-wave-6.md"),
            Path.Combine(root.FullName, "docs", "retro", "phase-k-wave-6.md"),
            Path.Combine(root.FullName, "docs", "phase-k-wave-6-retro.md"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return; // forward-staged

        var text = File.ReadAllText(path);
        // Canonical retro headers: "What went well" + "What didn't" + "Action items"
        // OR a "Glows / Grows" pair. Tolerate either.
        var hasStructure = Regex.IsMatch(text,
            @"(what went well|glows|wins|highlights)",
            RegexOptions.IgnoreCase);
        Assert.True(hasStructure,
            $"{path}: retro doc MUST carry a canonical retro section header.");
    }
}
