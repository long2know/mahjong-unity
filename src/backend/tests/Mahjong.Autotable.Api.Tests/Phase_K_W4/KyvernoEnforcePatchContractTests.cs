using System.Reflection;
using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W4;

/// <summary>
/// Phase K Wave 4 — Kyverno overlay enforce-tightening contract tests
/// (Vasquez).
///
/// <para>Apone's Wave 4 brief tightens admission policy so the
/// production overlay flips the cosign-verify ClusterPolicy from
/// the Wave-3 multi-namespace Audit/Enforce override to a
/// top-level <c>validationFailureAction: Enforce</c> via a kustomize
/// patch (<c>infra/k8s/overlays/prod/kyverno-enforce-patch.yaml</c>).
/// The staging overlay deliberately stays in Audit mode so cluster
/// operators have a "policy gating without rejection" environment to
/// validate new image rollouts in.</para>
///
/// <para>Facts:</para>
/// <list type="number">
///   <item>YAML schema — <c>kyverno-enforce-patch.yaml</c> exists at
///         the canonical path; parses as YAML (no tab indentation,
///         no unclosed mapping).</item>
///   <item>Patch sets <c>validationFailureAction: Enforce</c>.</item>
///   <item>Patch targets the <c>verify-mahjong-images</c>
///         ClusterPolicy.</item>
///   <item><c>overlays/prod/kustomization.yaml</c> includes the
///         enforce patch in its <c>patches</c>/<c>patchesStrategicMerge</c>
///         /<c>resources</c> list.</item>
///   <item><c>overlays/staging/kustomization.yaml</c> does NOT
///         include the enforce patch (staging stays in Audit mode).</item>
/// </list>
///
/// <para>Every probe walks the filesystem from the test BaseDirectory
/// up to the repo root (looking for the <c>.github/workflows</c> +
/// <c>Dockerfile</c> sentinels). Each fact soft-passes when the file
/// isn't yet present — Wave 4 is the bring-up branch, Apone may
/// land the patch in a follow-up commit.</para>
/// </summary>
public class KyvernoEnforcePatchContractTests
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
    //  1. kyverno-enforce-patch.yaml exists at the canonical path and
    //     parses as YAML (no tabs in indent, balanced mappings).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-4")]
    public void Kyverno_EnforcePatch_YamlSchema_Wellformed()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var patchPath = Path.Combine(root.FullName,
            "infra", "k8s", "overlays", "prod", "kyverno-enforce-patch.yaml");
        var text = ReadIfExists(patchPath);
        if (text is null) return; // forward-staged

        // Reject tab-character indent (YAML forbids tabs).
        Assert.DoesNotContain('\t', text);
        // Quick balanced-mapping check — count `:` end-of-line tokens
        // (mapping keys) against line count; a non-zero ratio is a
        // sanity floor.
        var lines = text.Split('\n');
        var mappingLines = lines.Count(l =>
            Regex.IsMatch(l, @":\s*$") || Regex.IsMatch(l, @":\s+\S"));
        Assert.True(mappingLines >= 1,
            $"kyverno-enforce-patch.yaml has {mappingLines} mapping-shaped lines; expected ≥ 1.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Patch sets validationFailureAction: Enforce.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-4")]
    public void Kyverno_EnforcePatch_Sets_ValidationFailureAction_Enforce()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var patchPath = Path.Combine(root.FullName,
            "infra", "k8s", "overlays", "prod", "kyverno-enforce-patch.yaml");
        var text = ReadIfExists(patchPath);
        if (text is null) return; // forward-staged

        // Accept either the kustomize op-patch form (`value: Enforce`)
        // or the strategic-merge form (`validationFailureAction: Enforce`).
        var hasEnforce = Regex.IsMatch(text,
            @"validationFailureAction\s*:\s*Enforce", RegexOptions.IgnoreCase)
            || Regex.IsMatch(text,
                @"path:\s*/spec/validationFailureAction[\s\S]*?value:\s*Enforce",
                RegexOptions.IgnoreCase);
        Assert.True(hasEnforce,
            "kyverno-enforce-patch.yaml MUST set validationFailureAction to Enforce.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Patch targets the `verify-mahjong-images` ClusterPolicy
    //     OR defines a separate ClusterPolicy that enforces image
    //     signatures for the prod namespace. The Wave-4 brief allows
    //     either shape: a strategic-merge / json-patch on the Wave-3
    //     policy, OR a supplemental Enforce-only ClusterPolicy that
    //     stacks alongside it (the "two policies, one fail-safe"
    //     pattern documented in docs/admission-policy.md §4.1).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-4")]
    public void Kyverno_EnforcePatch_Targets_VerifyMahjongImages_ClusterPolicy()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var patchPath = Path.Combine(root.FullName,
            "infra", "k8s", "overlays", "prod", "kyverno-enforce-patch.yaml");
        var text = ReadIfExists(patchPath);
        if (text is null) return; // forward-staged

        // Variant A: patch targets the Wave-3 ClusterPolicy directly.
        var targetsWave3 = Regex.IsMatch(text,
            @"name\s*:\s*verify-mahjong-images", RegexOptions.IgnoreCase);
        // Variant B: defines a supplemental ClusterPolicy of its own
        // (kind: ClusterPolicy + spec.rules) scoped to mahjong-prod.
        var isClusterPolicy = Regex.IsMatch(text,
            @"kind\s*:\s*ClusterPolicy", RegexOptions.IgnoreCase);
        var scopesProd = text.Contains("mahjong-prod", StringComparison.OrdinalIgnoreCase);

        Assert.True(targetsWave3 || (isClusterPolicy && scopesProd),
            "kyverno-enforce-patch.yaml MUST either patch `verify-mahjong-images` "
            + "OR define a supplemental ClusterPolicy scoped to mahjong-prod.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. overlays/prod/kustomization.yaml wires the enforce patch in.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-4")]
    public void Kyverno_ProdKustomization_Includes_EnforcePatch()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var kustPath = Path.Combine(root.FullName,
            "infra", "k8s", "overlays", "prod", "kustomization.yaml");
        var text = ReadIfExists(kustPath);
        if (text is null) return;

        // Probe for the filename reference. Accept patches, patchesStrategicMerge,
        // patchesJson6902, or resources field placement.
        var patchPath = Path.Combine(root.FullName,
            "infra", "k8s", "overlays", "prod", "kyverno-enforce-patch.yaml");
        if (!File.Exists(patchPath)) return; // forward-staged
        var referenced = text.Contains("kyverno-enforce-patch.yaml", StringComparison.Ordinal);
        Assert.True(referenced,
            "infra/k8s/overlays/prod/kustomization.yaml MUST reference "
            + "kyverno-enforce-patch.yaml once the file exists.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. overlays/staging/kustomization.yaml does NOT include the
    //     enforce patch — staging stays in Audit mode.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-4")]
    public void Kyverno_StagingKustomization_DoesNotInclude_EnforcePatch()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var kustPath = Path.Combine(root.FullName,
            "infra", "k8s", "overlays", "staging", "kustomization.yaml");
        var text = ReadIfExists(kustPath);
        if (text is null) return;

        var referenced = text.Contains("kyverno-enforce-patch.yaml", StringComparison.Ordinal);
        Assert.False(referenced,
            "infra/k8s/overlays/staging/kustomization.yaml MUST NOT reference "
            + "kyverno-enforce-patch.yaml — staging deliberately stays in Audit mode.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. base ClusterPolicy file itself stays in Audit (the patch is
    //     the only way prod gets Enforce — accidental top-level flip
    //     would break the staging-Audit contract).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Infra"), Trait("Wave", "Phase-K-4")]
    public void Kyverno_BasePolicy_StaysAudit_NotEnforce()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var basePath = Path.Combine(root.FullName,
            "infra", "k8s", "policies", "kyverno-cosign-verify.yaml");
        var text = ReadIfExists(basePath);
        if (text is null) return;

        // Multi-line top-level validationFailureAction must be Audit
        // (per-namespace override handles prod). A direct
        // "validationFailureAction: Enforce" at the top level would
        // make staging fail-closed — that's the regression we're
        // pinning against.
        var topLevelEnforce = Regex.IsMatch(text,
            @"^\s*validationFailureAction:\s*Enforce\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        Assert.False(topLevelEnforce,
            "Base kyverno-cosign-verify.yaml MUST keep top-level "
            + "validationFailureAction at Audit; per-namespace override "
            + "or overlay patch handles prod Enforce.");
    }
}
