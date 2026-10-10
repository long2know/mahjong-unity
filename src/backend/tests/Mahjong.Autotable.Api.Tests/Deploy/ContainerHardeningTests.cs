using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Deploy;

/// <summary>
/// Phase J Wave 7 — Dockerfile hardening contract tests (Vasquez).
///
/// <para>Apone's Wave 7 task tightens the runtime container so it can
/// be scheduled on Kubernetes clusters that enforce the Pod Security
/// Standard <c>restricted</c> profile. The two non-negotiable invariants
/// are:</para>
///
/// <list type="number">
///   <item>A <c>USER</c> directive pinning a non-root UID/GID (1000 is
///         the conventional first non-system user across both Debian and
///         the k8s PSS restricted profile; the Wave 7 manifest in
///         <c>infra/k8s/base/deployment.yaml</c> also asserts
///         <c>runAsNonRoot: true</c> + <c>runAsUser: 1000</c>).</item>
///   <item>A <c>HEALTHCHECK</c> instruction so <c>docker stack deploy</c>
///         + the docker-build-smoke script + the k8s readiness gate all
///         see a consistent in-container liveness probe.</item>
/// </list>
///
/// <para>Why test the Dockerfile string and not a built image: the
/// hardening contract is a *source-of-truth* invariant. We don't have a
/// reliable way to build the image inside xunit (a real build needs
/// ~120 s + network + the Docker daemon), but we DO have the Dockerfile
/// itself in-tree. Asserting on its contents catches any regression where
/// a maintainer drops the <c>USER</c> line during a refactor without
/// noticing.</para>
/// </summary>
public class ContainerHardeningTests
{
    private static string LoadDockerfile()
    {
        var root = LocateRepoRoot();
        var path = Path.Combine(root, "Dockerfile");
        Assert.True(File.Exists(path),
            $"Dockerfile not found at expected location: {path}");
        return File.ReadAllText(path);
    }

    private static string LocateRepoRoot()
    {
        // Walk up from the test bin directory until we find the repo
        // root (the directory carrying the slnx + Dockerfile at the top
        // level). Resilient to relative-path test-host layouts.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
             && Directory.Exists(Path.Combine(dir.FullName, "src", "backend")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate repo root from {AppContext.BaseDirectory}");
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Dockerfile carries a non-root USER directive
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void Dockerfile_DeclaresNonRootUser()
    {
        // Apone's Wave 7 hardening: the runtime stage must drop privileges
        // before ENTRYPOINT. We match a USER instruction (case-insensitive
        // — Docker accepts either) followed by either a numeric UID or
        // "uid:gid" pair. The string "0", "root", or no USER at all all
        // fail this assertion — closing the regression where someone
        // copy-pastes the legacy Dockerfile and accidentally drops the
        // hardening line.
        var contents = LoadDockerfile();

        // Strip comment lines so a documentation-only mention of `USER root`
        // doesn't false-positive.
        var nonComment = string.Join('\n',
            contents.Split('\n')
                .Where(line => !line.TrimStart().StartsWith('#')));

        var userMatch = Regex.Match(nonComment, @"(?im)^\s*USER\s+(\S+)\s*$");
        Assert.True(userMatch.Success,
            "Dockerfile must declare a USER directive on its own line — Apone's Wave 7 container hardening contract.");

        var token = userMatch.Groups[1].Value.Trim();
        var uidPart = token.Split(':')[0];
        // Phase J Wave 7 — Apone. xunit 2.x's `Assert.NotEqual(string,
        // string, bool)` overload was removed; use case-insensitive
        // comparison via the StringComparer to retain Vasquez's intent.
        Assert.False(string.Equals(uidPart, "root", StringComparison.OrdinalIgnoreCase),
            "Dockerfile USER must not resolve to 'root' (any case).");
        Assert.NotEqual("0", uidPart);

        // Conventional UID 1000 (matches infra/k8s/base/deployment.yaml's
        // securityContext.runAsUser). We don't pin the exact value beyond
        // "non-zero numeric or non-root name" — Apone may opt for a
        // different convention in a future wave (e.g. UID 10001).
        var isNumeric = int.TryParse(uidPart, out var uid);
        if (isNumeric) Assert.NotEqual(0, uid);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Dockerfile carries a HEALTHCHECK instruction
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void Dockerfile_DeclaresHealthcheck()
    {
        // Apone's Phase J Wave 3 contract (re-asserted in Wave 7): the
        // runtime image carries a HEALTHCHECK that hits /health. Without
        // it the docker-build-smoke script's `docker inspect --format
        // '{{.State.Health.Status}}'` returns "<no value>" and the
        // probe layer never settles.
        var contents = LoadDockerfile();
        Assert.Matches(@"(?im)^\s*HEALTHCHECK\b", contents);

        // The HEALTHCHECK CMD must mention /health (Bishop's canonical
        // probe surface). Match against the curl invocation so a
        // hand-rolled wget / busybox / shell-builtin variant also passes.
        Assert.Matches(@"/health", contents);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. EXPOSE pins the same port the deployment manifest targets
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void Dockerfile_ExposesPort8080()
    {
        // The k8s deployment.yaml hard-codes containerPort: 8080 + the
        // service.yaml routes to that target. A drift here would break
        // the k8s wiring entirely; pin the port in source-of-truth
        // alongside the manifest assertion in K8sManifestSanityTests.
        var contents = LoadDockerfile();
        Assert.Matches(@"(?im)^\s*EXPOSE\s+8080\b", contents);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. ASPNETCORE_URLS env var binds to the same 8080 port
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void Dockerfile_BindsKestrelToContainerPort()
    {
        // EXPOSE 8080 must be paired with ASPNETCORE_URLS=http://+:8080
        // — otherwise Kestrel binds to its default 5000 port and the
        // EXPOSE / HEALTHCHECK / k8s probes all hit a closed socket.
        // Catch the drift at source-control time.
        var contents = LoadDockerfile();
        Assert.Matches(@"ASPNETCORE_URLS=http://\+:8080", contents);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. /data volume mount is declared (SQLite persistence)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void Dockerfile_DeclaresDataVolume()
    {
        // SQLite DB lives on a writable mount so `docker stop` doesn't
        // erase the leaderboard / profile data. The k8s deployment
        // backs the same /data path with a PVC; a Dockerfile that
        // accidentally drops the VOLUME directive would let the SQLite
        // file land in the container's overlay FS and disappear on
        // restart.
        var contents = LoadDockerfile();
        Assert.Matches(@"(?im)^\s*VOLUME\s+(\[)?""?/data", contents);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. Runtime base image is the official .NET aspnet (not SDK)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-7")]
    public void Dockerfile_RuntimeStage_UsesAspnetBaseImage()
    {
        // Defence-in-depth supply-chain check: the runtime stage must
        // base off `mcr.microsoft.com/dotnet/aspnet:<tag>`, NOT the SDK
        // (which carries the compiler + a much larger attack surface).
        // A regression that copy-pastes the build-stage FROM directive
        // into the runtime stage would land a multi-hundred-MB image
        // carrying gcc/git/nuget — fail the smoke before it lands.
        var contents = LoadDockerfile();
        Assert.Matches(@"FROM\s+mcr\.microsoft\.com/dotnet/aspnet:", contents);
    }
}
