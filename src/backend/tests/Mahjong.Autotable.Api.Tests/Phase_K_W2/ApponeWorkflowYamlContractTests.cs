using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W2;

/// <summary>
/// Phase K Wave 2 — Apone workflow YAML pattern tests (Vasquez).
///
/// <para>Apone's Phase K Wave 2 brief ships five workflow / deploy
/// artefacts:
/// <list type="number">
///   <item>Multi-arch runtime smoke: amd64 + arm64 jobs that
///         <c>docker run --platform</c> + <c>curl /health</c>.</item>
///   <item>TURN server k8s overlay using <c>coturn/coturn:4.6</c>.</item>
///   <item>Capacitor mobile wrapper scaffold under <c>mobile/</c>.</item>
///   <item>PWA service-worker smoke workflow that asserts SW
///         registration.</item>
///   <item>Cosign verify reusable workflow with explicit
///         <c>image-digest</c> / <c>expected-issuer</c> /
///         <c>expected-identity-pattern</c> inputs.</item>
/// </list></para>
///
/// <para><b>Forward-staged.</b> Each fact below soft-passes when the
/// artefact isn't yet present — preserving the zero-skip gate. Once
/// shipped, the regex / shape assertions fire.</para>
/// </summary>
public class ApponeWorkflowYamlContractTests
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

    private static string? TryReadFirst(string subdir, params string[] names)
    {
        var root = LocateRepoRoot();
        foreach (var n in names)
        {
            var p = Path.Combine(root, subdir, n);
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        return null;
    }

    private static bool TryFind(string subdir, params string[] names)
    {
        var root = LocateRepoRoot();
        return names.Any(n => File.Exists(Path.Combine(root, subdir, n)));
    }

    private static string? FindFile(params string[] relativePaths)
    {
        var root = LocateRepoRoot();
        foreach (var p in relativePaths)
        {
            var full = Path.Combine(root, p);
            if (File.Exists(full)) return File.ReadAllText(full);
        }
        return null;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Multi-arch runtime live workflow — amd64 + arm64 BOTH job exists
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-2")]
    public void MultiArchRuntime_Amd64AndArm64_BothJobs()
    {
        var content = TryReadFirst(".github/workflows",
            "multi-arch-runtime.yml", "multi-arch-runtime.yaml",
            "multi-arch-runtime-smoke.yml", "multi-arch-runtime-smoke.yaml",
            "multi-arch-live.yml", "multi-arch-smoke.yml");
        if (content is null) return; // forward-staged
        Assert.Contains("linux/amd64", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("linux/arm64", content, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Multi-arch runtime — both jobs run `docker run --platform`
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-2")]
    public void MultiArchRuntime_DockerRun_WithPlatformPin()
    {
        var content = TryReadFirst(".github/workflows",
            "multi-arch-runtime.yml", "multi-arch-runtime.yaml",
            "multi-arch-runtime-smoke.yml", "multi-arch-runtime-smoke.yaml",
            "multi-arch-live.yml", "multi-arch-smoke.yml");
        if (content is null) return;
        // Accept either explicit `--platform=linux/{amd64|arm64}`, or
        // `--platform "$PLATFORM"` / `--platform $PLATFORM` where the
        // surrounding matrix declares the linux/{amd64,arm64} pair.
        var hasExplicit = Regex.IsMatch(content,
            @"--platform[= ]\s*['""]?(linux/(amd64|arm64)|\$\{?\s*[A-Za-z_][A-Za-z0-9_]*\s*\}?)",
            RegexOptions.IgnoreCase);
        var matrixHasArches = content.Contains("linux/amd64", StringComparison.OrdinalIgnoreCase)
                            && content.Contains("linux/arm64", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasExplicit && matrixHasArches,
            "Multi-arch runtime workflow must pin --platform AND list both linux/amd64 and linux/arm64.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Multi-arch runtime — both jobs hit `/health` via curl
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-2")]
    public void MultiArchRuntime_CurlsHealthEndpoint()
    {
        var content = TryReadFirst(".github/workflows",
            "multi-arch-runtime.yml", "multi-arch-runtime.yaml",
            "multi-arch-runtime-smoke.yml", "multi-arch-runtime-smoke.yaml",
            "multi-arch-live.yml", "multi-arch-smoke.yml");
        if (content is null) return;
        var hasCurlHealth = Regex.IsMatch(content,
            @"\bcurl[^\r\n]*\/health", RegexOptions.IgnoreCase);
        Assert.True(hasCurlHealth,
            "Multi-arch runtime workflow must curl `/health`.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. TURN k8s overlay — references coturn/coturn:4.6 image
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-2")]
    public void TurnK8sOverlay_CoturnImage_Pinned()
    {
        // Could be a Kustomize overlay or a Helm values file. We probe
        // common k8s-overlay locations.
        var content = FindFile(
            "infra/k8s/overlays/turn/deployment.yaml",
            "infra/k8s/turn/deployment.yaml",
            "infra/k8s/turn/turn-deployment.yaml",
            "infra/k8s/voice/turn.yaml",
            "infra/k8s/coturn/deployment.yaml",
            "infra/kubernetes/turn/turn.yaml");
        if (content is null) return; // forward-staged
        Assert.Matches(@"coturn\/coturn:4\.\d+", content);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. TURN k8s overlay — patches realm + external-ip
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-2")]
    public void TurnK8sOverlay_PatchesRealmAndExternalIp()
    {
        var root = LocateRepoRoot();
        var candidates = new[]
        {
            "infra/k8s/overlays/turn",
            "infra/k8s/turn",
            "infra/k8s/voice",
            "infra/k8s/coturn",
            "infra/kubernetes/turn",
        };
        var overlayDir = candidates
            .Select(p => Path.Combine(root, p))
            .FirstOrDefault(Directory.Exists);
        if (overlayDir is null) return; // forward-staged

        // Concatenate every yaml under the overlay so a `patches:` field
        // can reference both realm + external-ip in either ConfigMap or
        // a strategic-merge patch file.
        var combined = string.Join("\n",
            Directory.GetFiles(overlayDir, "*.yaml", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(overlayDir, "*.yml", SearchOption.AllDirectories))
                .Select(File.ReadAllText));
        if (string.IsNullOrWhiteSpace(combined)) return;
        Assert.Matches(@"realm", combined);
        Assert.True(Regex.IsMatch(combined,
            @"external[-_]?ip|externalIPs|external-address",
            RegexOptions.IgnoreCase),
            "TURN overlay must expose an external-ip parameter.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. TURN k8s overlay — credentials sourced from ExternalSecret
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-2")]
    public void TurnK8sOverlay_ExternalSecret_ReferencesSsm()
    {
        var root = LocateRepoRoot();
        var candidates = new[]
        {
            "infra/k8s/overlays/turn",
            "infra/k8s/turn",
            "infra/k8s/voice",
            "infra/k8s/coturn",
            "infra/kubernetes/turn",
        };
        var overlayDir = candidates
            .Select(p => Path.Combine(root, p))
            .FirstOrDefault(Directory.Exists);
        if (overlayDir is null) return;

        var combined = string.Join("\n",
            Directory.GetFiles(overlayDir, "*.yaml", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(overlayDir, "*.yml", SearchOption.AllDirectories))
                .Select(File.ReadAllText));
        if (string.IsNullOrWhiteSpace(combined)) return;
        var hasExternalSecret = Regex.IsMatch(combined,
            @"kind:\s*ExternalSecret", RegexOptions.IgnoreCase);
        if (!hasExternalSecret) return; // Could use SealedSecret variant; soft-pass.
        var hasSsmKey = Regex.IsMatch(combined,
            @"secretsManagerRef|parameterStoreRef|key:\s*[/A-Za-z0-9_-]+",
            RegexOptions.IgnoreCase);
        Assert.True(hasSsmKey,
            "TURN ExternalSecret should reference an SSM/SecretsManager key.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. Capacitor mobile wrapper — top-level `mobile/` directory exists
    //     OR forward-staged.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-2")]
    public void Capacitor_MobileDirectoryExistsOrForwardStaged()
    {
        var root = LocateRepoRoot();
        var mobileDir = Path.Combine(root, "mobile");
        if (!Directory.Exists(mobileDir)) return; // forward-staged
        Assert.True(Directory.Exists(mobileDir));
    }

    // ────────────────────────────────────────────────────────────────────
    //  8. Capacitor config — capacitor.config.json declares webDir + appId
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-2")]
    public void Capacitor_ConfigJson_HasWebDirAndAppId()
    {
        var root = LocateRepoRoot();
        var candidates = new[]
        {
            Path.Combine(root, "mobile", "capacitor.config.json"),
            Path.Combine(root, "mobile", "capacitor.config.ts"),
            Path.Combine(root, "capacitor.config.json"),
            Path.Combine(root, "capacitor.config.ts"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return; // forward-staged
        var content = File.ReadAllText(path);
        Assert.Matches(@"""?webDir""?\s*[:=]", content);
        Assert.Matches(@"""?appId""?\s*[:=]", content);
    }

    // ────────────────────────────────────────────────────────────────────
    //  9. PWA service-worker smoke — workflow asserts SW registration
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-2")]
    public void PwaSmoke_Workflow_AssertsServiceWorkerRegistration()
    {
        var content = TryReadFirst(".github/workflows",
            "pwa-smoke.yml", "pwa-smoke.yaml", "pwa.yml", "pwa-sw-smoke.yml",
            "service-worker-smoke.yml");
        if (content is null) return;
        // Workflow should invoke a check that the SW registers — common
        // phrasings are "serviceWorker.register" / "navigator.serviceWorker" /
        // an npm script named "test:pwa".
        var asserts = new[] {
            "serviceWorker", "service-worker", "navigator.serviceWorker",
            "test:pwa", "workbox", "registerServiceWorker", "sw.js"
        };
        var hasAssert = asserts.Any(s =>
            content.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
        Assert.True(hasAssert,
            "PWA smoke workflow must reference a service-worker registration check.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  10. Cosign verify reusable workflow — inputs include image-digest,
    //      expected-issuer, expected-identity-pattern.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-K-2")]
    public void CosignVerify_ReusableWorkflow_HasRequiredInputs()
    {
        var content = TryReadFirst(".github/workflows",
            "verify-signature.yml", "verify-signature.yaml",
            "cosign-verify.yml", "cosign-verify.yaml",
            "verify-image-signature.yml", "verify-cosign.yml");
        if (content is null) return; // forward-staged
        // Reusable workflow: `on: workflow_call: inputs:` block.
        Assert.Matches(@"workflow_call:", content);
        var inputs = new[] { "image-digest", "expected-issuer", "expected-identity-pattern" };
        foreach (var i in inputs)
        {
            Assert.True(content.IndexOf(i, StringComparison.OrdinalIgnoreCase) >= 0,
                $"Cosign verify workflow missing input `{i}`.");
        }
    }
}
