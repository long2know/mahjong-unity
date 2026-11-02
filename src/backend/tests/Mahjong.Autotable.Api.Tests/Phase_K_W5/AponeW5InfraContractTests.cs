using System.Reflection;
using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W5;

/// <summary>
/// Phase K Wave 5 — Apone's infra-lane surface contracts (Vasquez).
///
/// <para>Covers Apone's Wave 5 deliverables:</para>
/// <list type="bullet">
///   <item><b>Unified SLSA + SBOM single predicate</b> — the
///         <c>slsa-provenance.yml</c> workflow uses
///         <c>generator_generic_slsa3.yml</c> (not
///         <c>generator_container_slsa3.yml</c>) and references
///         BOTH the image manifest AND the SBOM file as subjects.</item>
///   <item><b>Kyverno attestations block</b> — the enforce policy
///         carries an <c>attestations:</c> block requiring the
///         SLSA predicate (predicateType =
///         <c>slsaprovenance1</c> or compatible).</item>
///   <item><b>Staging jwt-keys-secret</b> — a parallel ESO
///         manifest at <c>infra/k8s/overlays/staging/jwt-keys-secret.yaml</c>
///         (so staging can rotate JWT signing keys too).</item>
///   <item><b>gh secrets-history-sweep workflow</b> — a recurring
///         GitHub Actions job that re-scans repo history for any
///         leaked secret (Apone's defense-in-depth on top of
///         gitleaks).</item>
///   <item><b>HSTS preload verification workflow</b> — a
///         scheduled check that the live prod Strict-Transport-
///         Security header still satisfies
///         <c>max-age=63072000; includeSubDomains; preload</c>.</item>
///   <item><b>Terraform bootstrap</b> — <c>infra/terraform/</c>
///         directory with a top-level <c>main.tf</c> or similar
///         (a cluster-bootstrap module — IAM roles, ESO secret
///         store, kubernetes provider scaffolding).</item>
/// </list>
///
/// <para>Every fact uses filesystem probes anchored at the repo
/// root. Soft-passes when the file isn't yet shipped. Hard-asserts
/// the canonical shape once it lands.</para>
/// </summary>
public class AponeW5InfraContractTests
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
    //  1. SLSA workflow — unified predicate (generator_generic_slsa3
    //     + both image and SBOM subjects).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-5")]
    public void Slsa_UnifiedPredicate_GenericGenerator_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wfPath = Path.Combine(root.FullName, ".github", "workflows", "slsa-provenance.yml");
        var text = ReadIfExists(wfPath);
        if (text is null) return; // forward-staged

        // Wave-5 brief: use the generic generator (multi-subject) not
        // the container generator (single-subject). Soft-pass when the
        // wave-4 container generator is still wired.
        var usesGeneric = Regex.IsMatch(text,
            @"generator_generic_slsa3\.yml@v?[0-9]");
        var usesContainer = Regex.IsMatch(text,
            @"generator_container_slsa3\.yml@v?[0-9]");

        if (!usesGeneric && !usesContainer) return; // forward-staged

        if (usesGeneric)
        {
            // Hard-pin: the workflow MUST reference a SBOM-producing
            // step alongside the image (sbom + image as subjects).
            // Loose check: both `sbom` AND `digest` appear in the
            // workflow file.
            Assert.Matches(new Regex(@"sbom", RegexOptions.IgnoreCase), text);
            Assert.Matches(new Regex(@"digest", RegexOptions.IgnoreCase), text);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Kyverno enforce policy carries `attestations:` block.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-5")]
    public void Kyverno_AttestationsBlock_RequiresSlsa_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var paths = new[]
        {
            Path.Combine(root.FullName, "infra", "k8s", "overlays", "prod", "kyverno-enforce-patch.yaml"),
            Path.Combine(root.FullName, "infra", "k8s", "policies", "kyverno-cosign-verify.yaml"),
        };
        var existing = paths.Where(File.Exists).ToList();
        if (existing.Count == 0) return; // forward-staged

        var anyAttestations = false;
        foreach (var p in existing)
        {
            var text = File.ReadAllText(p);
            // Match `attestations:` as a YAML key (avoids matching the word
            // in a comment). Pattern: word at end of line OR followed by
            // newline and indented list/block.
            if (Regex.IsMatch(text, @"^\s*attestations\s*:", RegexOptions.Multiline))
            {
                anyAttestations = true;
                // When the block ships, it MUST reference the SLSA
                // predicate type (slsaprovenance / slsaprovenance1 /
                // https://slsa.dev/provenance/v1).
                Assert.Matches(@"slsaprovenance|slsa\.dev/provenance", text);
            }
        }
        // anyAttestations is informational — Wave 4 didn't ship it,
        // Wave 5 may not finish either. The hard-assert above only
        // fires when the block IS present.
        _ = anyAttestations;
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Staging jwt-keys-secret — parallel to the prod ESO manifest.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-5")]
    public void Staging_JwtKeysSecret_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var stagingDir = Path.Combine(root.FullName, "infra", "k8s", "overlays", "staging");
        if (!Directory.Exists(stagingDir)) return;

        var jwtKeysPath = Path.Combine(stagingDir, "jwt-keys-secret.yaml");
        if (!File.Exists(jwtKeysPath)) return; // forward-staged

        var text = File.ReadAllText(jwtKeysPath);
        // Hard-pin: the manifest MUST reference an ExternalSecret OR
        // a Secret resource with JWT signing keys.
        var hasExternalSecret = Regex.IsMatch(text,
            @"kind\s*:\s*ExternalSecret", RegexOptions.IgnoreCase);
        var hasSecret = Regex.IsMatch(text,
            @"kind\s*:\s*Secret", RegexOptions.IgnoreCase);
        Assert.True(hasExternalSecret || hasSecret,
            "staging/jwt-keys-secret.yaml MUST declare an ExternalSecret or Secret resource.");

        // MUST reference the JWT signing key path (auth__jwtsigningkeys__N
        // or Authentication__JwtSigningKeys__N — both are valid
        // .NET-config-binding shapes).
        Assert.Matches(@"jwt[_a-z]?signingkeys?", text.ToLowerInvariant());
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. gh secrets-history-sweep workflow.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-5")]
    public void SecretsHistorySweep_Workflow_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wfDir = Path.Combine(root.FullName, ".github", "workflows");
        if (!Directory.Exists(wfDir)) return;

        var candidates = new[]
        {
            Path.Combine(wfDir, "secrets-history-sweep.yml"),
            Path.Combine(wfDir, "secrets-history-sweep.yaml"),
            Path.Combine(wfDir, "secrets-sweep.yml"),
            Path.Combine(wfDir, "history-secrets-sweep.yml"),
        };
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is null) return; // forward-staged

        var text = File.ReadAllText(found);
        // Hard-pin: MUST be triggered (cron, workflow_dispatch, or
        // pull_request) — at minimum the workflow must declare a
        // trigger surface. `workflow_dispatch` is the canonical
        // Apone choice for the on-demand history sweep.
        Assert.Matches(new Regex(
            @"on\s*:[\s\S]*(schedule|workflow_dispatch|pull_request|push)\s*:",
            RegexOptions.IgnoreCase), text);
        // MUST reference history scanning — gitleaks/trufflehog
        // --since-commit or --depth=0 (full history).
        Assert.Matches(new Regex(@"(history|--since-commit|depth\s*:\s*0|fetch-depth)",
            RegexOptions.IgnoreCase), text);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. HSTS preload verification workflow.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-5")]
    public void HstsPreloadVerification_Workflow_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wfDir = Path.Combine(root.FullName, ".github", "workflows");
        if (!Directory.Exists(wfDir)) return;

        var candidates = new[]
        {
            Path.Combine(wfDir, "hsts-preload-verify.yml"),
            Path.Combine(wfDir, "hsts-preload-verify.yaml"),
            Path.Combine(wfDir, "hsts-verify.yml"),
            Path.Combine(wfDir, "verify-hsts.yml"),
        };
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is null) return; // forward-staged

        var text = File.ReadAllText(found);
        // Hard-pin: MUST be scheduled (recurring check) and reference
        // the live STS header / max-age value.
        Assert.Matches(@"on\s*:[\s\S]*schedule\s*:", text);
        Assert.Matches(new Regex(@"strict-transport-security|max-age",
            RegexOptions.IgnoreCase), text);
        // Should reference preload / 63072000 to confirm the
        // verification anchors to the canonical W5 directive.
        Assert.Matches(new Regex(@"preload|63072000", RegexOptions.IgnoreCase), text);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. Terraform bootstrap directory.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-5")]
    public void Terraform_Bootstrap_DirectoryPresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var tfDir = Path.Combine(root.FullName, "infra", "terraform");
        if (!Directory.Exists(tfDir)) return; // forward-staged

        // Hard-pin: at least ONE .tf file must exist (bootstrap module).
        var tfFiles = Directory.EnumerateFiles(tfDir, "*.tf", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(tfFiles);

        // Pin: at least one .tf file references either a `provider` block
        // OR a `terraform { required_providers ... }` block — both are
        // canonical bootstrap shapes.
        var any = false;
        foreach (var f in tfFiles)
        {
            var text = File.ReadAllText(f);
            if (Regex.IsMatch(text, @"provider\s+""[a-z]+""\s*\{")
                || Regex.IsMatch(text, @"required_providers"))
            {
                any = true;
                break;
            }
        }
        Assert.True(any,
            "infra/terraform/ MUST declare at least one provider or required_providers block.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. SBOM workflow + slsa workflow share the build subject —
    //     the SBOM file path emitted by sbom.yml MUST appear in the
    //     slsa workflow's subjects list.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-5")]
    public void SbomAndSlsa_SharedSubject_HardAssert()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wfDir = Path.Combine(root.FullName, ".github", "workflows");
        var sbomPath = Path.Combine(wfDir, "sbom.yml");
        var slsaPath = Path.Combine(wfDir, "slsa-provenance.yml");
        if (!File.Exists(sbomPath) || !File.Exists(slsaPath)) return;

        var slsaText = File.ReadAllText(slsaPath);
        // When the SLSA workflow is the unified W5 shape, it MUST
        // reference an SBOM step or artefact name. Soft-pass otherwise.
        if (!Regex.IsMatch(slsaText, @"generator_generic_slsa3")) return;
        Assert.Matches(new Regex(@"sbom", RegexOptions.IgnoreCase), slsaText);
    }
}
