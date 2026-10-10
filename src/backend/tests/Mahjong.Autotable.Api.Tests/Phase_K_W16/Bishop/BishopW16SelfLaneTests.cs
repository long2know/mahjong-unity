using System.Reflection;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Commentary;
using Mahjong.Autotable.Api.Replays;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Bishop;

/// <summary>
/// Phase K Wave 16 — Bishop. Self-lane invariants — every W16
/// Bishop deliverable surfaces a hard reflection assertion here
/// so a future maintainer can't silently drop one without a
/// red test. Pattern mirrors
/// <c>BishopW15SelfLaneTests</c>.
/// </summary>
public sealed class BishopW16SelfLaneTests
{
    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotationValidator_TypeExists()
    {
        Assert.NotNull(typeof(PerTenantJwksRotationValidator));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotationValidator_EvaluateAsync_Exists()
    {
        var m = typeof(PerTenantJwksRotationValidator).GetMethod("EvaluateAsync");
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotationValidator_EnforceSigningAsync_Exists()
    {
        var m = typeof(PerTenantJwksRotationValidator).GetMethod("EnforceSigningAsync");
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotationValidator_DefaultOverlapDays_IsSeven()
    {
        Assert.Equal(7, PerTenantJwksRotationValidator.DefaultOverlapDays);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotationValidator_ErrorPolicyStale_IsWireStable()
    {
        Assert.Equal("per-tenant-rotation-stale",
            PerTenantJwksRotationValidator.ErrorPolicyStale);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotationValidator_ErrorStoreMissing_IsWireStable()
    {
        Assert.Equal("per-tenant-rotation-store-missing",
            PerTenantJwksRotationValidator.ErrorStoreMissing);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotationPolicy_OverlapWindowDays_Exists()
    {
        var p = typeof(PerTenantJwksRotationPolicy).GetProperty("OverlapWindowDays");
        Assert.NotNull(p);
        Assert.Equal(typeof(int), p!.PropertyType);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotationOptions_DefaultOverlapDays_Exists()
    {
        var p = typeof(PerTenantJwksRotationOptions).GetProperty("DefaultOverlapDays");
        Assert.NotNull(p);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantRotationAdminController_TypeExists()
    {
        Assert.NotNull(typeof(PerTenantRotationAdminController));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantRotationAdminController_Create_Exists()
    {
        var m = typeof(PerTenantRotationAdminController).GetMethod("Create");
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantRotationAdminController_Update_Exists()
    {
        var m = typeof(PerTenantRotationAdminController).GetMethod("Update");
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantRotationAdminController_Delete_Exists()
    {
        var m = typeof(PerTenantRotationAdminController).GetMethod("Delete");
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantRotationAdminController_List_Exists()
    {
        var m = typeof(PerTenantRotationAdminController).GetMethod("List");
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void JwtStagedRotationPolicy_IsWithinOverlapWindow_DateTimeOffsetOverload_Exists()
    {
        var m = typeof(JwtStagedRotationPolicy).GetMethod(
            "IsWithinOverlapWindow", new[] { typeof(DateTimeOffset) });
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void JwtStagedRotationPolicy_RemainingOverlapDays_DateTimeOffsetOverload_Exists()
    {
        var m = typeof(JwtStagedRotationPolicy).GetMethod(
            "RemainingOverlapDays", new[] { typeof(DateTimeOffset) });
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void JwtStagedRotationPolicy_RotationStartUtcOffset_Exists()
    {
        var p = typeof(JwtStagedRotationPolicy).GetProperty("RotationStartUtcOffset");
        Assert.NotNull(p);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void JwtStagedRotationPolicy_OverlapWindowEndsAtOffset_Exists()
    {
        var p = typeof(JwtStagedRotationPolicy).GetProperty("OverlapWindowEndsAtOffset");
        Assert.NotNull(p);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void ReplayRetentionPolicy_TypeExists()
    {
        Assert.NotNull(typeof(ReplayRetentionPolicy));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void IReplayRetentionPolicyStore_TypeExists()
    {
        Assert.NotNull(typeof(IReplayRetentionPolicyStore));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void InMemoryReplayRetentionPolicyStore_TypeExists()
    {
        Assert.NotNull(typeof(InMemoryReplayRetentionPolicyStore));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void EfReplayRetentionPolicyStore_TypeExists()
    {
        Assert.NotNull(typeof(EfReplayRetentionPolicyStore));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void ReplayStore_SweepWithPerTenantPolicyAsync_Exists()
    {
        var m = typeof(IReplayStore).GetMethod("SweepWithPerTenantPolicyAsync");
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void ReplayRecord_TenantId_PropertyExists()
    {
        var p = typeof(ReplayRecord).GetProperty("TenantId");
        Assert.NotNull(p);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void CommentaryCostBudgetEnforcer_TypeExists()
    {
        Assert.NotNull(typeof(CommentaryCostBudgetEnforcer));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void CommentaryCostBudgetEnforcer_Evaluate_Exists()
    {
        var m = typeof(CommentaryCostBudgetEnforcer).GetMethod("Evaluate");
        Assert.NotNull(m);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void CommentaryCostBudgetEnforcer_ReasonOverBudget_IsWireStable()
    {
        Assert.Equal("commentary-cost-budget-exhausted",
            CommentaryCostBudgetEnforcer.ReasonOverBudget);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void CommentaryCostBudgetEnforcer_StatusOverBudget_Is402()
    {
        Assert.Equal(402, CommentaryCostBudgetEnforcer.StatusOverBudget);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void CommentaryOptions_CostBudget_AdminOverride_Exists()
    {
        var nested = typeof(CommentaryOptions).GetNestedType("CostBudgetOptions");
        Assert.NotNull(nested);
        var p = nested!.GetProperty("AdminOverride");
        Assert.NotNull(p);
        Assert.Equal(typeof(bool), p!.PropertyType);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void EnforcementVerdict_TypeExists()
    {
        Assert.NotNull(typeof(EnforcementVerdict));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void EnforcementVerdictKind_HasThreeValues()
    {
        var values = Enum.GetValues<EnforcementVerdictKind>();
        Assert.Equal(3, values.Length);
        Assert.Contains(EnforcementVerdictKind.Allowed, values);
        Assert.Contains(EnforcementVerdictKind.AdminOverride, values);
        Assert.Contains(EnforcementVerdictKind.Rejected, values);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantRotationVerdictKind_HasSixValues()
    {
        var values = Enum.GetValues<PerTenantRotationVerdictKind>();
        Assert.Equal(6, values.Length);
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void GrafanaDashboard_JsonFile_PresentInRepo()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName,
            "src", "backend", "src", "Mahjong.Autotable.Api",
            "Observability", "dashboards", "tournament-query-duration.json");
        Assert.True(File.Exists(path));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void SignalRSequenceSloDoc_PresentInRepo()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "signalr-sequence-slo.md");
        Assert.True(File.Exists(path));
    }

    [Fact, Trait("Category", "SelfLane"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotationDoc_PresentInRepo()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "docs", "per-tenant-jwks-rotation.md");
        Assert.True(File.Exists(path));
    }

    private static DirectoryInfo? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                && Directory.Exists(Path.Combine(dir.FullName, ".squad")))
            {
                return dir;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
