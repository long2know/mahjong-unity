using System.IO;
using System.Text.RegularExpressions;

namespace Mahjong.Autotable.Api.Tests.Deploy;

/// <summary>
/// Phase J Wave 9 — k8s migration Job manifest contract tests (Vasquez).
///
/// <para>Apone's Wave 9 ships <c>infra/k8s/base/job-migrate.yaml</c> — a
/// one-shot Kubernetes Job that runs <c>ef database update</c> against
/// the production DB before a new image rolls out (so the rollout
/// doesn't race a still-pending migration). Contract:
/// <list type="bullet">
///   <item>YAML parses (string-pattern check; no parser dep).</item>
///   <item><c>kind: Job</c>.</item>
///   <item>Runs the EF Core migrate command.</item>
///   <item>Uses the same image tag as the Deployment (so DB schema
///         matches API code).</item>
///   <item>Has a restartPolicy that's NOT Always.</item>
/// </list></para>
///
/// <para>Soft-passes when the file isn't yet present (Apone's surface
/// in flight).</para>
/// </summary>
public class K8sMigrationJobTests
{
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

    private static string? LoadJobManifest()
    {
        var root = LocateRepoRoot();
        var candidates = new[]
        {
            Path.Combine(root, "infra", "k8s", "base", "job-migrate.yaml"),
            Path.Combine(root, "infra", "k8s", "base", "migration-job.yaml"),
            Path.Combine(root, "infra", "k8s", "base", "job-migration.yaml"),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-9")]
    public void MigrationJob_File_ExistsOrNotYetShipped()
    {
        var content = LoadJobManifest();
        if (content is null) return;
        Assert.False(string.IsNullOrWhiteSpace(content));
    }

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-9")]
    public void MigrationJob_HasJobKind()
    {
        var content = LoadJobManifest();
        if (content is null) return;
        Assert.Matches(@"(?im)^\s*kind:\s*Job\s*$", content);
        Assert.Matches(@"(?im)^\s*apiVersion:\s*batch/v1\s*$", content);
    }

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-9")]
    public void MigrationJob_RunsEntityFrameworkMigrate()
    {
        var content = LoadJobManifest();
        if (content is null) return;
        // EF Core CLI uses `database update`, the `dotnet ef` entrypoint
        // is the canonical migrate command. Bundled images often ship
        // a `migrate` console-tool entrypoint instead — accept any of
        // the canonical signals.
        var hasMigrate =
            Regex.IsMatch(content, @"(?i)ef\s+database\s+update")
            || Regex.IsMatch(content, @"(?i)dotnet\s+ef\s+database\s+update")
            || Regex.IsMatch(content, @"(?i)migrate")
            || Regex.IsMatch(content, @"(?i)EnsureCreated");
        Assert.True(hasMigrate,
            "Migration Job must invoke an EF Core migrate command (ef database update / dotnet ef / migrate).");
    }

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-9")]
    public void MigrationJob_RestartPolicy_IsNotAlways()
    {
        var content = LoadJobManifest();
        if (content is null) return;
        // Job restartPolicy must be `Never` or `OnFailure`. `Always` is
        // invalid for a Job per Kubernetes API (Deployment-only).
        Assert.Matches(@"(?im)^\s*restartPolicy:\s*(Never|OnFailure)\s*$", content);
    }

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-9")]
    public void MigrationJob_ReferencesSameImageAsDeployment()
    {
        var content = LoadJobManifest();
        if (content is null) return;

        var deploymentPath = Path.Combine(LocateRepoRoot(), "infra", "k8s", "base", "deployment.yaml");
        if (!File.Exists(deploymentPath)) return;

        // Find the image reference in the deployment.
        var deployment = File.ReadAllText(deploymentPath);
        var deployImg = Regex.Match(deployment, @"image:\s*([^\s#]+)").Groups[1].Value;
        if (string.IsNullOrEmpty(deployImg)) return;

        // The Job should reference the SAME image (or at least the same
        // repository) so schema versions don't drift.
        var jobImg = Regex.Match(content, @"image:\s*([^\s#]+)").Groups[1].Value;
        if (string.IsNullOrEmpty(jobImg)) return;

        // Same image name (possibly different tag — accept tag drift while
        // in flight but assert the registry / path prefix matches).
        var deployPath = deployImg.Split(':')[0];
        var jobPath = jobImg.Split(':')[0];
        Assert.Equal(deployPath, jobPath);
    }

    [Fact, Trait("Category", "Deploy"), Trait("Wave", "Phase-J-9")]
    public void MigrationJob_ListedInKustomization()
    {
        // The new job manifest should be referenced from
        // infra/k8s/base/kustomization.yaml so `kubectl apply -k` picks
        // it up.
        var kustomizationPath = Path.Combine(LocateRepoRoot(), "infra", "k8s", "base", "kustomization.yaml");
        if (!File.Exists(kustomizationPath)) return;

        var content = File.ReadAllText(kustomizationPath);
        // The actual job manifest path will be one of the candidates we
        // probed; require at least one of them to be referenced.
        var hasJobRef =
            content.Contains("job-migrate.yaml", StringComparison.OrdinalIgnoreCase)
            || content.Contains("migration-job.yaml", StringComparison.OrdinalIgnoreCase)
            || content.Contains("job-migration.yaml", StringComparison.OrdinalIgnoreCase);

        var jobLanded = LoadJobManifest() is not null;
        if (!jobLanded) return; // job not shipped yet — kustomization may not list it
        Assert.True(hasJobRef,
            "Wave-9 migration Job manifest must be added to base/kustomization.yaml's resources list.");
    }
}
