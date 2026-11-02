namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Bishop;

/// <summary>
/// Phase K Wave 16 — Bishop. Contract tests for the
/// <c>docs/signalr-sequence-slo.md</c> + <c>docs/per-tenant-jwks-rotation.md</c>
/// operator-facing documents added in this wave.
/// </summary>
public sealed class DocsContractTests
{
    private static string DocsRoot()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        return Path.Combine(root!, "docs");
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void SignalRSequenceSlo_Exists()
    {
        Assert.True(File.Exists(Path.Combine(DocsRoot(), "signalr-sequence-slo.md")));
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void SignalRSequenceSlo_References9995Promise()
    {
        var text = File.ReadAllText(Path.Combine(DocsRoot(), "signalr-sequence-slo.md"));
        Assert.Contains("99.95%", text);
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void SignalRSequenceSlo_References216MinuteErrorBudget()
    {
        var text = File.ReadAllText(Path.Combine(DocsRoot(), "signalr-sequence-slo.md"));
        Assert.Contains("21.6", text);
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void SignalRSequenceSlo_ReferencesCanonicalMetricNames()
    {
        var text = File.ReadAllText(Path.Combine(DocsRoot(), "signalr-sequence-slo.md"));
        Assert.Contains("signalr_seq_replay_from_ack_total", text);
        Assert.Contains("signalr_seq_store_rows_active", text);
        Assert.Contains("signalr_seq_retention_sweep_deleted_total", text);
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void SignalRSequenceSlo_HasBurnRateSection()
    {
        var text = File.ReadAllText(Path.Combine(DocsRoot(), "signalr-sequence-slo.md"));
        Assert.Contains("burn", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void SignalRSequenceSlo_HasRunbookSection()
    {
        var text = File.ReadAllText(Path.Combine(DocsRoot(), "signalr-sequence-slo.md"));
        Assert.Contains("Runbook", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void SignalRSequenceSlo_HasAlertsSection()
    {
        var text = File.ReadAllText(Path.Combine(DocsRoot(), "signalr-sequence-slo.md"));
        Assert.Contains("Alert", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void SignalRSequenceSlo_HasMeasurementSection()
    {
        var text = File.ReadAllText(Path.Combine(DocsRoot(), "signalr-sequence-slo.md"));
        Assert.Contains("PromQL", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void SignalRSequenceSlo_HasWaveHistory()
    {
        var text = File.ReadAllText(Path.Combine(DocsRoot(), "signalr-sequence-slo.md"));
        Assert.Contains("W14", text);
        Assert.Contains("W16", text);
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotation_Exists()
    {
        Assert.True(File.Exists(Path.Combine(DocsRoot(), "per-tenant-jwks-rotation.md")));
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotation_ReferencesValidator()
    {
        var text = File.ReadAllText(Path.Combine(DocsRoot(), "per-tenant-jwks-rotation.md"));
        Assert.Contains("PerTenantJwksRotationValidator", text);
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotation_HasAllVerdictKinds()
    {
        var text = File.ReadAllText(Path.Combine(DocsRoot(), "per-tenant-jwks-rotation.md"));
        Assert.Contains("ToggleDisabled", text);
        Assert.Contains("NoPolicy", text);
        Assert.Contains("PolicyFresh", text);
        Assert.Contains("WithinOverlapWindow", text);
        Assert.Contains("Stale", text);
        Assert.Contains("StoreMissing", text);
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotation_HasConfigurationSection()
    {
        var text = File.ReadAllText(Path.Combine(DocsRoot(), "per-tenant-jwks-rotation.md"));
        Assert.Contains("JwksRotation:PerTenant", text);
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotation_HasRotationProcedure()
    {
        var text = File.ReadAllText(Path.Combine(DocsRoot(), "per-tenant-jwks-rotation.md"));
        Assert.Contains("rotation", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("overlap", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotation_ReferencesAdminController()
    {
        var text = File.ReadAllText(Path.Combine(DocsRoot(), "per-tenant-jwks-rotation.md"));
        Assert.Contains("/api/admin/jwks-rotation/per-tenant", text);
    }

    [Fact, Trait("Category", "Docs"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotation_DocumentsWireStableReasons()
    {
        var text = File.ReadAllText(Path.Combine(DocsRoot(), "per-tenant-jwks-rotation.md"));
        Assert.Contains("per-tenant-rotation-stale", text);
        Assert.Contains("per-tenant-rotation-store-missing", text);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                && Directory.Exists(Path.Combine(dir.FullName, ".squad")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
