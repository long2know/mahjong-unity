using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Deploy;

/// <summary>
/// Phase J Wave 7 — Kubernetes manifest sanity tests (Vasquez).
///
/// <para>Apone's Wave 7 ships a base + overlay set under
/// <c>infra/k8s/</c> that wraps the runtime container with the canonical
/// k8s machinery (Deployment + Service + Ingress + ConfigMap + Secret
/// template + PVC + HPA + Kustomize overlays). These tests pin the
/// invariants the runtime depends on:</para>
///
/// <list type="bullet">
///   <item><b>Liveness + readiness probes both point at <c>/health</c></b>
///         — the Wave-3 contract Bishop's endpoint owns. If either probe
///         drifts (e.g. someone fixes the legacy <c>/api/health</c> path
///         instead), pod rollouts will silently stall or evict good pods.</item>
///   <item><b>Resource requests + limits are BOTH set on the API
///         container</b> — required for the HPA to compute utilisation
///         and for the namespace ResourceQuota to admit the pod. A common
///         regression is forgetting `limits:` when copy-pasting from a
///         dev manifest; the HPA then can't make scaling decisions.</item>
///   <item><b>Container port matches Dockerfile EXPOSE</b> — already
///         pinned in <see cref="ContainerHardeningTests"/>; we re-assert
///         from the manifest side so the two artefacts can't drift.</item>
/// </list>
///
/// <para>We do string-pattern matching (no YAML parser dependency) because
/// the assertions are structural / vocabulary-level, not full-graph
/// inspection. Adding a YAML parser to the test project for these would
/// inflate the dep tree for one test surface.</para>
/// </summary>
public class K8sManifestSanityTests
{
    private static string LoadManifest(string relativePath)
    {
        var root = LocateRepoRoot();
        var path = Path.Combine(root, "infra", "k8s", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path),
            $"k8s manifest not found: {path}");
        return File.ReadAllText(path);
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "infra", "k8s"))
             && File.Exists(Path.Combine(dir.FullName, "Dockerfile")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate repo root from {AppContext.BaseDirectory}");
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Deployment manifest exists with required apiVersion / kind / name
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void Deployment_HasRequiredTopLevelKeys()
    {
        var contents = LoadManifest("base/deployment.yaml");
        Assert.Matches(@"(?im)^\s*apiVersion:\s*apps/v1\s*$", contents);
        Assert.Matches(@"(?im)^\s*kind:\s*Deployment\s*$", contents);
        Assert.Matches(@"(?im)^\s*name:\s*mahjong-autotable\s*$", contents);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Liveness probe path is /health
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void Deployment_LivenessProbe_HitsCanonicalHealthPath()
    {
        // Multi-line regex: find a `livenessProbe:` block and scan ahead
        // for the first `path:` value. If anyone refactors the probe to
        // hit /api/health (legacy short-form) the k8s rollout will pass
        // green but the Phase-J-3 wire-shape contract is bypassed.
        var contents = LoadManifest("base/deployment.yaml");
        var match = Regex.Match(contents,
            @"livenessProbe:\s*\n(?:[^\n]*\n)*?\s*httpGet:\s*\n(?:[^\n]*\n)*?\s*path:\s*(?<path>\S+)",
            RegexOptions.Singleline);
        Assert.True(match.Success, "livenessProbe block with httpGet.path not found in deployment.yaml");
        Assert.Equal("/health", match.Groups["path"].Value.Trim());
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Readiness probe path is /health
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void Deployment_ReadinessProbe_HitsCanonicalHealthPath()
    {
        var contents = LoadManifest("base/deployment.yaml");
        var match = Regex.Match(contents,
            @"readinessProbe:\s*\n(?:[^\n]*\n)*?\s*httpGet:\s*\n(?:[^\n]*\n)*?\s*path:\s*(?<path>\S+)",
            RegexOptions.Singleline);
        Assert.True(match.Success, "readinessProbe block with httpGet.path not found in deployment.yaml");
        Assert.Equal("/health", match.Groups["path"].Value.Trim());
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Resource requests + limits BOTH declared
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void Deployment_DeclaresResourceRequestsAndLimits()
    {
        // Both `requests:` and `limits:` keys are required for the
        // HPA to compute utilisation + the namespace ResourceQuota to
        // admit the pod. We scan the resources block and assert both
        // sub-keys appear, each with a cpu + memory value below them.
        var contents = LoadManifest("base/deployment.yaml");

        var resourcesMatch = Regex.Match(contents,
            @"resources:\s*\n(?<body>(?:[ \t]+[^\n]*\n)+)",
            RegexOptions.Singleline);
        Assert.True(resourcesMatch.Success, "resources: block not found in deployment.yaml");

        var resourcesBody = resourcesMatch.Groups["body"].Value;
        Assert.Matches(@"(?m)^\s*requests:\s*$", resourcesBody);
        Assert.Matches(@"(?m)^\s*limits:\s*$", resourcesBody);
        Assert.Matches(@"(?m)^\s*cpu:\s*\S+", resourcesBody);
        Assert.Matches(@"(?m)^\s*memory:\s*\S+", resourcesBody);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Pod runs as non-root (runAsNonRoot: true)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void Deployment_PodSecurityContext_RunAsNonRoot()
    {
        // Pairs with ContainerHardeningTests.Dockerfile_DeclaresNonRootUser.
        // If either side drifts the pod will be rejected by clusters that
        // enforce the Pod Security Standard `restricted` profile.
        var contents = LoadManifest("base/deployment.yaml");
        Assert.Matches(@"(?im)^\s*runAsNonRoot:\s*true\s*$", contents);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. Container port 8080 matches Dockerfile EXPOSE
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void Deployment_ContainerPort_Is8080()
    {
        var contents = LoadManifest("base/deployment.yaml");
        Assert.Matches(@"(?im)^\s*containerPort:\s*8080\s*$", contents);
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. Service routes to the named "http" port
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void Service_HasRequiredTopLevelKeys()
    {
        var contents = LoadManifest("base/service.yaml");
        Assert.Matches(@"(?im)^\s*apiVersion:\s*v1\s*$", contents);
        Assert.Matches(@"(?im)^\s*kind:\s*Service\s*$", contents);
        Assert.Matches(@"(?im)^\s*name:\s*mahjong-autotable\s*$", contents);
        Assert.Matches(@"(?im)^\s*targetPort:\s*http\s*$", contents);
    }

    // ────────────────────────────────────────────────────────────────────
    //  8. Ingress declares sticky-session affinity (WS requirement)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void Ingress_DeclaresWebsocketStickyAffinity()
    {
        // SignalR + autotable-WS depend on a single pod handling the
        // entire upgrade lifecycle. The nginx-ingress affinity-cookie
        // annotation is what makes that work behind the LB; without it
        // WS frames will be load-balanced across pods and connections
        // will reset on every packet.
        var contents = LoadManifest("base/ingress.yaml");
        Assert.Matches(@"nginx\.ingress\.kubernetes\.io/affinity:\s*""?cookie""?", contents);
    }

    // ────────────────────────────────────────────────────────────────────
    //  9. Kustomization includes all base resources
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void BaseKustomization_IncludesAllResources()
    {
        // Resources list must enumerate every YAML in infra/k8s/base/
        // (excluding kustomization.yaml itself). A missing entry means
        // the file would silently NOT be deployed — invisible to a `git
        // diff` review.
        var contents = LoadManifest("base/kustomization.yaml");

        var baseDir = Path.Combine(LocateRepoRoot(), "infra", "k8s", "base");
        var yamlFiles = Directory.EnumerateFiles(baseDir, "*.yaml")
            .Select(Path.GetFileName)
            .Where(name => !string.Equals(name, "kustomization.yaml", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(yamlFiles);
        foreach (var file in yamlFiles)
            Assert.Contains(file!, contents);
    }

    // ────────────────────────────────────────────────────────────────────
    //  10. Prod + staging overlays reference the base
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void Overlays_ReferenceBaseKustomization()
    {
        foreach (var env in new[] { "prod", "staging" })
        {
            var contents = LoadManifest($"overlays/{env}/kustomization.yaml");
            Assert.Matches(@"resources:\s*\n\s*-\s*\.\./\.\./base", contents);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  11. ConfigMap carries the rate-limiting + persistence env keys
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void ConfigMap_ContainsApiContractKeys()
    {
        var contents = LoadManifest("base/configmap.yaml");
        Assert.Matches(@"(?im)^\s*Persistence__Provider:", contents);
        Assert.Matches(@"(?im)^\s*RateLimiting__Enabled:", contents);
    }

    // ────────────────────────────────────────────────────────────────────
    //  12. Secret template includes provider connection strings
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void SecretTemplate_ContainsBothProviderConnectionStrings()
    {
        var contents = LoadManifest("base/secret-template.yaml");
        Assert.Matches(@"(?im)^\s*ConnectionStrings__PostgreSql:", contents);
        Assert.Matches(@"(?im)^\s*ConnectionStrings__SqlServer:", contents);
    }
}
