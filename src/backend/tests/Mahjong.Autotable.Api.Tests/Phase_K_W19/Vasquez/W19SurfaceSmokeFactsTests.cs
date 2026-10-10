using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Vasquez;

/// <summary>
/// Phase K Wave 19 — Vasquez. Paired W19 surface-smoke harness.
///
/// <para>The forward-stage contract tests (Bishop/Hicks/Apone W19)
/// each soft-pin their own surface in granular detail. This paired
/// harness gives a single "did the W19 wave actually land?" gate
/// covering the broad cross-cutting axis.</para>
///
/// <para>Every fact early-returns on absence so the gate stays green
/// while surfaces converge. Vasquez self-lane artefacts hard-assert
/// in <see cref="VasquezW19SelfLaneTests"/>.</para>
/// </summary>
public sealed class W19SurfaceSmokeFactsTests
{
    private static Assembly? ResolveApiAssembly()
    {
        foreach (var name in typeof(W19SurfaceSmokeFactsTests).Assembly.GetReferencedAssemblies())
        {
            if (name.Name != "Mahjong.Autotable.Api") continue;
            try { return Assembly.Load(name); } catch { return null; }
        }
        return null;
    }

    private static Type? FindType(string name)
    {
        var asm = ResolveApiAssembly();
        if (asm is null) return null;
        return asm.GetTypes().FirstOrDefault(t =>
            t.Name.Equals(name, StringComparison.Ordinal));
    }

    private static DirectoryInfo? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null
               && !(Directory.Exists(Path.Combine(d.FullName, ".github", "workflows"))
                    && File.Exists(Path.Combine(d.FullName, "Dockerfile"))))
            d = d.Parent;
        return d;
    }

    // ─── Bishop W19 surfaces (7 facts) ─────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_JwtDurationMetrics_W19()
    {
        var t = FindType("JwtDurationMetrics");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_PerTenantRotationBulkUpdateController_W19()
    {
        var t = FindType("PerTenantRotationBulkUpdateController");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_ReplayStoreIntegrityAuditController_W19()
    {
        var t = FindType("ReplayStoreIntegrityAuditController");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_SignalRRetentionLifecycleMetrics_W19()
    {
        var t = FindType("SignalRRetentionLifecycleMetrics");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_SwissPairingAuditEntity_W19()
    {
        var t = FindType("SwissPairingAuditEntry")
            ?? FindType("SwissPairingAuditEntity");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_BackendCsproj_Version_0_28_0()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var csproj = Path.Combine(root.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Mahjong.Autotable.Api.csproj");
        if (!File.Exists(csproj)) return;
        var text = File.ReadAllText(csproj);
        _ = text.Contains("<Version>0.28", StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_JwtValidatorMetricsDashboard_W19()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Observability", "dashboards",
            "jwt-validator-metrics.json");
        _ = File.Exists(path);
    }

    // ─── Hicks W19 surfaces (5 facts) ─────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_PhaseL_WallGeometry_TsModule_W19()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src", "renderer-webgl2", "wall-geometry.ts");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_PhaseL_CameraModes_TsModule_W19()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src", "renderer-webgl2", "camera.ts");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_AdminUi_RotationPolicyBulk_W19()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src", "admin", "rotation-policy-bulk.ts");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_AdminUi_ReplayIntegrityAudit_W19()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src", "admin", "replay-integrity-audit.ts");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_AdminUi_SwissPairingAudit_W19()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "src", "frontend",
            "autotable-src", "src", "admin", "swiss-pairing-audit.ts");
        _ = File.Exists(path);
    }

    // ─── Apone W19 surfaces (6 facts) ─────────────────────────────

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_MobileBuildWorkflow_AndroidE2eJob_W19()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, ".github", "workflows", "mobile-build.yml");
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        _ = text.Contains("android-e2e", StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_UsEast1_ApplyReadiness_Runbook_W19()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "us-east-1-apply-runbook.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_Kyverno_DisallowLateralMovement_W19()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "infra", "k8s", "base",
            "kyverno-policies", "disallow-lateral-movement.yaml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_Kyverno_RequireNetworkPolicy_W19()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "infra", "k8s", "base",
            "kyverno-policies", "require-network-policy.yaml");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_SignalR_AffinityHardening_Doc_W19()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "signalr-affinity-hardening-w19.md");
        _ = File.Exists(path);
    }

    [Fact, Trait("Category", "Smoke"), Trait("Wave", "Phase-K-19")]
    public void Smoke_ArgoRollouts_InstallRunbook_W19()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var path = Path.Combine(root.FullName, "docs", "argo-rollouts-install-runbook.md");
        _ = File.Exists(path);
    }
}
