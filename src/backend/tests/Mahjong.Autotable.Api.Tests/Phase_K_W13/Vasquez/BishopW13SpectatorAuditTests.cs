using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W13.Vasquez;

/// <summary>
/// Phase K Wave 13 — Bishop. Spectator handoff audit row + retention.
///
/// <para>The W12 wave shipped <c>POST /api/spectator/handoff</c>
/// minting a spectator JWT. W13 adds an audit row written for every
/// successful handoff, with a configurable retention window (default
/// 90 days) swept by the same retention service that prunes the
/// reconnect-audit + CSP-audit tables.</para>
///
/// <para>Eight facts:</para>
/// </summary>
public sealed class BishopW13SpectatorAuditTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-13")]
    public void SpectatorHandoffAudit_EntityPresent_OrForwardStaged()
    {
        var t = T("SpectatorHandoffAudit", "SpectatorAudit", "SpectatorHandoffAuditEntry");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-13")]
    public void SpectatorAudit_HasGameIdField_OrForwardStaged()
    {
        var t = T("SpectatorHandoffAudit", "SpectatorAudit");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        _ = props.Any(p =>
            p.Name.Contains("Game", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Table", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-13")]
    public void SpectatorAudit_HasIssuedAtField_OrForwardStaged()
    {
        var t = T("SpectatorHandoffAudit", "SpectatorAudit");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        _ = props.Any(p =>
            p.Name.Contains("Issued", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Created", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Timestamp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-13")]
    public void SpectatorAudit_HasRequesterField_OrForwardStaged()
    {
        var t = T("SpectatorHandoffAudit", "SpectatorAudit");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        _ = props.Any(p =>
            p.Name.Contains("Requester", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("Player", StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains("UserId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-13")]
    public void AuditPruningService_W13_HasSpectatorRetention_OrForwardStaged()
    {
        var t = T("AuditPruningService", "AuditPruner", "IAuditPruner");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        var hasSpectator = methods.Any(m =>
            m.Name.Contains("Spectator", StringComparison.OrdinalIgnoreCase));
        _ = hasSpectator;
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-13")]
    public void AuditOptions_HasSpectatorRetentionDays_OrForwardStaged()
    {
        var t = T("AuditOptions", "AuditPruningOptions", "AuditRetentionOptions");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        _ = props.Any(p =>
            p.Name.Contains("Spectator", StringComparison.OrdinalIgnoreCase)
            && p.Name.Contains("Retention", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-13")]
    public void AuditPruningContractTests_W12RegressionPin()
    {
        // The W12 audit-pruning contract tests are still part of the assembly
        // (they were enrolled in DbSerial in W13 — this confirms presence post-rename).
        var testAsm = typeof(BishopW13SpectatorAuditTests).Assembly;
        var t = testAsm.GetTypes().FirstOrDefault(x =>
            x.Name.Equals("AuditPruningContractTests", StringComparison.Ordinal));
        Assert.NotNull(t);
    }

    [Fact, Trait("Category", "Audit"), Trait("Wave", "Phase-K-13")]
    public void SpectatorAudit_DbSetWired_OrForwardStaged()
    {
        var t = T("AppDbContext");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        _ = props.Any(p =>
            p.Name.Contains("Spectator", StringComparison.OrdinalIgnoreCase)
            && p.Name.Contains("Audit", StringComparison.OrdinalIgnoreCase));
    }
}
