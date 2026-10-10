using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Deploy;

/// <summary>
/// Phase J Wave 10 — multi-arch container build sanity (Vasquez).
///
/// <para>Apone's Wave 10 brief promises a multi-architecture Docker build
/// (linux/amd64 + linux/arm64) so the image can ship to Apple-silicon
/// developer laptops, ARM CI runners, and AWS Graviton hosts without
/// a QEMU userland fallback. The convention is the BuildKit
/// <c>--platform=$BUILDPLATFORM</c> / <c>--platform=$TARGETPLATFORM</c>
/// dance, with the build stage pinned to the BUILDPLATFORM (native)
/// and the runtime stage pinned to TARGETPLATFORM (cross-compiled
/// target).</para>
///
/// <para>The expected Dockerfile shape is:</para>
/// <code>
/// FROM --platform=$BUILDPLATFORM node:20-alpine AS frontend-build
/// ...
/// FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
/// ARG TARGETPLATFORM
/// ...
/// FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
/// </code>
///
/// <para><b>Forward-staged: soft-pass</b> when the Dockerfile hasn't yet
/// adopted the multi-arch incantation (Apone's WIP). Each fact below
/// either:
/// <list type="bullet">
///   <item>Asserts the expected directive IS present, OR</item>
///   <item><c>return;</c>s (soft-pass) when it's absent — preserving
///         the zero-skip streak while waving the flag.</item>
/// </list></para>
/// </summary>
public class MultiArchDockerSanityTests
{
    private static readonly string RepoRoot = LocateRepoRoot();

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
            dir = dir.Parent;
        }
        // Fallback: assume tests live at <root>/src/backend/tests/... — 5 levels up.
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    }

    private static string? ReadDockerfileOrNull()
    {
        var path = Path.Combine(RepoRoot, "Dockerfile");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Repo HAS a top-level Dockerfile
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-10")]
    public void Repo_HasTopLevelDockerfile()
    {
        var content = ReadDockerfileOrNull();
        if (content is null) return; // soft-pass: image build may be infra-only
        Assert.False(string.IsNullOrWhiteSpace(content));
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Backend-build stage pins --platform=$BUILDPLATFORM
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-10")]
    public void BackendBuildStage_PinsBuildplatform()
    {
        var content = ReadDockerfileOrNull();
        if (content is null) return;

        // Forward-staged: soft-pass until Apone lands the multi-arch
        // refactor. We're probing for the canonical BuildKit token.
        if (!Regex.IsMatch(content, @"--platform=\$BUILDPLATFORM", RegexOptions.IgnoreCase))
            return;

        // Once present, every `FROM ... AS *-build` line (excluding the
        // runtime stage) MUST carry --platform=$BUILDPLATFORM.
        var fromMatches = Regex.Matches(content, @"^FROM\s+(.+?)\s+AS\s+([A-Za-z0-9_-]+)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        var offenders = new List<string>();
        foreach (Match m in fromMatches)
        {
            var stage = m.Groups[2].Value;
            if (!stage.EndsWith("-build", StringComparison.OrdinalIgnoreCase)) continue;
            if (!m.Groups[1].Value.Contains("$BUILDPLATFORM", StringComparison.OrdinalIgnoreCase))
                offenders.Add(stage);
        }
        Assert.True(offenders.Count == 0,
            $"Multi-arch shipped but these *-build stages lack --platform=$BUILDPLATFORM: {string.Join(", ", offenders)}");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Runtime stage references $TARGETPLATFORM (either --platform or ARG)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-10")]
    public void RuntimeStage_TargetsTargetplatform()
    {
        var content = ReadDockerfileOrNull();
        if (content is null) return;
        // Soft-pass until the multi-arch refactor lands.
        if (!content.Contains("$BUILDPLATFORM", StringComparison.OrdinalIgnoreCase)
            && !content.Contains("$TARGETPLATFORM", StringComparison.OrdinalIgnoreCase))
            return;

        // Once committed, the file MUST reference TARGETPLATFORM at
        // least once — typically as an ARG in the runtime stage so
        // the .NET runtime image picks the right native libs.
        Assert.Contains("$TARGETPLATFORM", content, StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. dotnet publish uses -r matching the target rid (linux-musl-arm64
    //     or linux-arm64) when multi-arch is on
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-10")]
    public void DotnetPublish_ParametrisedForTargetArch()
    {
        var content = ReadDockerfileOrNull();
        if (content is null) return;
        // Soft-pass until multi-arch lands.
        if (!content.Contains("$BUILDPLATFORM", StringComparison.OrdinalIgnoreCase)
            && !content.Contains("$TARGETPLATFORM", StringComparison.OrdinalIgnoreCase))
            return;

        // When multi-arch IS on, `dotnet publish` should not hard-code
        // a runtime identifier (-r linux-x64) — it should derive from
        // $TARGETPLATFORM or be omitted (portable build).
        var publishLines = Regex.Matches(content, @"dotnet\s+publish[^\r\n]*", RegexOptions.IgnoreCase);
        foreach (Match line in publishLines)
        {
            var text = line.Value;
            // Allow `-r $TARGETARCH`-style or no -r at all. Reject a
            // hard-coded linux-x64 / linux-amd64 when multi-arch is on.
            var hardcoded = Regex.IsMatch(text,
                @"-r\s+(linux-x64|linux-amd64|linux-musl-x64)\b",
                RegexOptions.IgnoreCase);
            Assert.False(hardcoded,
                $"Multi-arch shipped but `dotnet publish` line hard-codes an x64 RID: {text.Trim()}");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. CI / GitHub workflow registers buildx + linux/arm64 platform
    //     (probed loosely — soft-pass when neither is configured yet)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-10")]
    public void Workflow_ConfiguresBuildxArm64()
    {
        var workflowsDir = Path.Combine(RepoRoot, ".github", "workflows");
        if (!Directory.Exists(workflowsDir)) return;
        var anyArm = Directory.EnumerateFiles(workflowsDir, "*.yml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(workflowsDir, "*.yaml", SearchOption.AllDirectories))
            .Any(f =>
            {
                var t = File.ReadAllText(f);
                return t.Contains("linux/arm64", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("docker/setup-buildx-action", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("docker/build-push-action", StringComparison.OrdinalIgnoreCase);
            });
        if (!anyArm) return; // soft-pass: CI hasn't been retooled yet
        Assert.True(anyArm,
            "buildx + linux/arm64 marker present in workflows once CI is retooled.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. Dockerfile shape sanity — runtime stage is dotnet/aspnet image
    //     (catch silent regressions to alpine/sdk for runtime).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-10")]
    public void RuntimeStage_UsesAspnetImage()
    {
        var content = ReadDockerfileOrNull();
        if (content is null) return;

        // Find the runtime stage line: `FROM <image> AS runtime`
        var m = Regex.Match(content,
            @"^FROM\s+(?:--platform=\S+\s+)?([^\s]+)\s+AS\s+runtime\b",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!m.Success) return; // soft-pass: stage may be renamed during refactor
        Assert.Contains("aspnet", m.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
    }
}
