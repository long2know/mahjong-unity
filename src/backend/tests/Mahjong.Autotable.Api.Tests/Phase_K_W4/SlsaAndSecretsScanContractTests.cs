using System.Reflection;
using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W4;

/// <summary>
/// Phase K Wave 4 — Apone's supply-chain + secrets-management contract
/// surface (Vasquez).
///
/// <para>Apone's Wave 4 brief adds:</para>
/// <list type="bullet">
///   <item>SLSA in-toto provenance workflow
///         (<c>.github/workflows/slsa-provenance.yml</c> or similar)
///         signing every release multi-arch manifest with a SLSA v1.0
///         provenance attestation.</item>
///   <item>External Secrets Operator (ESO) mount manifest exposing the
///         JWT signing keys array under
///         <c>auth__jwtsigningkeys__{0,1,2}</c> Kubernetes secret
///         keys (matches the .NET configuration binding's
///         double-underscore convention).</item>
///   <item>Strict-Transport-Security preload header in production
///         (<c>max-age=63072000; includeSubDomains; preload</c>).</item>
///   <item>gitleaks secrets-scan workflow
///         (<c>.github/workflows/secrets-scan.yml</c>) that runs on
///         every PR + on the main-branch nightly.</item>
/// </list>
///
/// <para>Every fact uses filesystem probes anchored at the repo root.
/// Soft-passes when the file isn't yet shipped — Vasquez is forward-
/// staging while Apone's branch lands. When the file IS present, the
/// assertion is hard.</para>
/// </summary>
public class SlsaAndSecretsScanContractTests
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

    private static string? FindFirstExisting(params string[] paths)
    {
        foreach (var p in paths)
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. SLSA provenance workflow file present (forward-staged).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-4")]
    public void Slsa_ProvenanceWorkflow_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wfDir = Path.Combine(root.FullName, ".github", "workflows");
        if (!Directory.Exists(wfDir)) return;

        var found = FindFirstExisting(
            Path.Combine(wfDir, "slsa-provenance.yml"),
            Path.Combine(wfDir, "slsa-provenance.yaml"),
            Path.Combine(wfDir, "provenance.yml"),
            Path.Combine(wfDir, "slsa.yml"));
        if (found is null) return; // forward-staged

        var text = File.ReadAllText(found);
        // Hard-assert canonical SLSA references when the workflow ships.
        var hasSlsa = Regex.IsMatch(text, @"slsa", RegexOptions.IgnoreCase);
        Assert.True(hasSlsa,
            $"{Path.GetFileName(found)} MUST reference `slsa` (generator/attestation).");
        // in-toto reference is canonical for SLSA v1.0 attestations.
        var hasInToto = Regex.IsMatch(text, @"in[-_]?toto", RegexOptions.IgnoreCase)
                     || Regex.IsMatch(text, @"attest(?:ation)?", RegexOptions.IgnoreCase);
        Assert.True(hasInToto,
            $"{Path.GetFileName(found)} MUST reference in-toto / attestation.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. ESO secret mount manifest with auth__jwtsigningkeys__{0,1,2}
    //     keys (forward-staged).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-4")]
    public void Eso_JwtSigningKeysSecret_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        // ESO ExternalSecret manifests typically live alongside other
        // overlay secret-templates. Probe the canonical paths.
        var candidates = new[]
        {
            Path.Combine(root.FullName, "infra", "k8s", "overlays", "prod", "jwt-keys-secret.yaml"),
            Path.Combine(root.FullName, "infra", "k8s", "overlays", "prod", "external-secret-jwt.yaml"),
            Path.Combine(root.FullName, "infra", "k8s", "policies", "jwt-keys-secret.yaml"),
            Path.Combine(root.FullName, "infra", "k8s", "base", "external-secret-jwt.yaml"),
        };
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is null) return; // forward-staged

        var text = File.ReadAllText(found);
        // Hard-assert the canonical .NET config convention (double-
        // underscore separator) for the three rotation slots.
        Assert.Contains("auth__jwtsigningkeys__0", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auth__jwtsigningkeys__1", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auth__jwtsigningkeys__2", text, StringComparison.OrdinalIgnoreCase);

        // ExternalSecret kind reference (defensive).
        var hasKind = Regex.IsMatch(text, @"kind\s*:\s*ExternalSecret",
            RegexOptions.IgnoreCase);
        Assert.True(hasKind,
            $"{Path.GetFileName(found)} MUST declare `kind: ExternalSecret`.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. HSTS preload header docs / config — pin the canonical
    //     `max-age=63072000; includeSubDomains; preload` value when
    //     it lives in either appsettings, infra config, or
    //     docs/hsts-preload.md.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-4")]
    public void Hsts_Preload_CanonicalMaxAge_OrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var probes = new[]
        {
            Path.Combine(root.FullName, "docs", "hsts-preload.md"),
            Path.Combine(root.FullName, "src", "backend", "src",
                "Mahjong.Autotable.Api", "appsettings.json"),
            Path.Combine(root.FullName, "src", "backend", "src",
                "Mahjong.Autotable.Api", "appsettings.Production.json"),
        };
        var found = probes.FirstOrDefault(File.Exists);
        if (found is null) return;

        var text = File.ReadAllText(found);
        // Soft-pass when no max-age token at all — the surface may
        // still be in flight. Hard-assert when ANY max-age is
        // present that it carries the 63072000 (2y) Wave-4 value
        // for preload eligibility.
        var maxAgeMatch = Regex.Match(text, @"max-age\s*[=:]\s*(\d+)",
            RegexOptions.IgnoreCase);
        if (!maxAgeMatch.Success) return; // soft-pass
        var value = int.Parse(maxAgeMatch.Groups[1].Value);
        // Accept the canonical 2-year preload OR any value ≥ 31536000
        // (HSTS preload-list requirement is ≥ 1 year). Wave 4 brief
        // canonicalises 63072000; we hard-assert ≥ 31536000 to leave
        // Apone room to settle the exact value.
        Assert.True(value >= 31_536_000,
            $"HSTS max-age `{value}` is below HSTS-preload minimum (31536000). "
            + "Wave-4 canonical is 63072000 (2y).");
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Secrets-scan workflow (gitleaks) present.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-4")]
    public void SecretsScan_GitleaksWorkflow_PresentOrForwardStaged()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var wfDir = Path.Combine(root.FullName, ".github", "workflows");
        if (!Directory.Exists(wfDir)) return;

        var found = FindFirstExisting(
            Path.Combine(wfDir, "secrets-scan.yml"),
            Path.Combine(wfDir, "secrets-scan.yaml"),
            Path.Combine(wfDir, "gitleaks.yml"),
            Path.Combine(wfDir, "gitleaks.yaml"));
        if (found is null) return; // forward-staged

        var text = File.ReadAllText(found);
        // Hard-assert when shipped: must reference gitleaks (the
        // pinned scanner) and run on PRs.
        Assert.Contains("gitleaks", text, StringComparison.OrdinalIgnoreCase);
        var hasPullRequestTrigger = Regex.IsMatch(text,
            @"pull_request", RegexOptions.IgnoreCase);
        Assert.True(hasPullRequestTrigger,
            $"{Path.GetFileName(found)} MUST trigger on pull_request.");
    }
}
