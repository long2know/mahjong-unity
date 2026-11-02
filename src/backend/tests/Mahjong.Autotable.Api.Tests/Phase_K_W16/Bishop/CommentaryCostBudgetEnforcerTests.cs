using Mahjong.Autotable.Api.Commentary;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Bishop;

/// <summary>
/// Phase K Wave 16 — Bishop. Behaviour tests for
/// <see cref="CommentaryCostBudgetEnforcer"/>. Drives the
/// budget into Healthy / Warning / Exhausted by varying the
/// usage-meter token count + the option-bound cap.
/// </summary>
public sealed class CommentaryCostBudgetEnforcerTests
{
    private static (CommentaryCostBudgetEnforcer enforcer, InMemoryCommentaryUsageMeter meter)
        MakeEnforcer(
            decimal monthlyCapUsd = 0m,
            long tokensPerDollar = 1_000L,
            double warnThreshold = 0.8,
            bool adminOverride = true)
    {
        var opts = new CommentaryOptions
        {
            CostBudget = new CommentaryOptions.CostBudgetOptions
            {
                MonthlyCapUsd = monthlyCapUsd,
                TokensPerDollar = tokensPerDollar,
                WarnThreshold = warnThreshold,
                AdminOverride = adminOverride,
            },
        };
        var monitor = new TestOptionsMonitor(opts);
        var meter = new InMemoryCommentaryUsageMeter();
        var budget = new CommentaryCostBudget(
            monitor,
            meter,
            NullLogger<CommentaryCostBudget>.Instance);
        var enforcer = new CommentaryCostBudgetEnforcer(
            NullLogger<CommentaryCostBudgetEnforcer>.Instance,
            budget,
            monitor);
        return (enforcer, meter);
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<CommentaryOptions>
    {
        public TestOptionsMonitor(CommentaryOptions value) { CurrentValue = value; }
        public CommentaryOptions CurrentValue { get; private set; }
        public CommentaryOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<CommentaryOptions, string?> listener) => null;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void NoCapConfigured_VerdictAllowed()
    {
        var (e, _) = MakeEnforcer(monthlyCapUsd: 0m);
        var v = e.Evaluate("acme", isAdmin: false, DateTime.UtcNow);
        Assert.Equal(EnforcementVerdictKind.Allowed, v.Kind);
        Assert.False(v.ShouldShortCircuit);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void HealthyState_VerdictAllowed()
    {
        var (e, meter) = MakeEnforcer(monthlyCapUsd: 10m, tokensPerDollar: 1_000L);
        meter.RecordUsage(Guid.NewGuid(), inputTokens: 100, outputTokens: 100); // 200 tokens = $0.20
        var v = e.Evaluate("acme", isAdmin: false, DateTime.UtcNow);
        Assert.Equal(EnforcementVerdictKind.Allowed, v.Kind);
        Assert.Equal(BudgetState.Healthy, v.State);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void WarningState_VerdictAllowed()
    {
        var (e, meter) = MakeEnforcer(monthlyCapUsd: 10m, tokensPerDollar: 1_000L, warnThreshold: 0.5);
        meter.RecordUsage(Guid.NewGuid(), 6_000, 0); // $6 of $10 (60%)
        var v = e.Evaluate("acme", isAdmin: false, DateTime.UtcNow);
        Assert.Equal(EnforcementVerdictKind.Allowed, v.Kind);
        Assert.Equal(BudgetState.Warning, v.State);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void ExhaustedState_NotAdmin_VerdictRejected()
    {
        var (e, meter) = MakeEnforcer(monthlyCapUsd: 10m, tokensPerDollar: 1_000L);
        meter.RecordUsage(Guid.NewGuid(), 10_500, 0); // $10.50 > $10 cap
        var v = e.Evaluate("acme", isAdmin: false, DateTime.UtcNow);
        Assert.Equal(EnforcementVerdictKind.Rejected, v.Kind);
        Assert.True(v.ShouldShortCircuit);
        Assert.True(v.IsRejected);
        Assert.Equal(CommentaryCostBudgetEnforcer.ReasonOverBudget, v.Reason);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void ExhaustedState_AdminWithOverrideToggle_VerdictAdminOverride()
    {
        var (e, meter) = MakeEnforcer(monthlyCapUsd: 10m, tokensPerDollar: 1_000L, adminOverride: true);
        meter.RecordUsage(Guid.NewGuid(), 10_500, 0);
        var v = e.Evaluate("acme", isAdmin: true, DateTime.UtcNow);
        Assert.Equal(EnforcementVerdictKind.AdminOverride, v.Kind);
        Assert.True(v.IsAdminOverride);
        Assert.False(v.ShouldShortCircuit);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void ExhaustedState_AdminWithoutOverrideToggle_VerdictRejected()
    {
        var (e, meter) = MakeEnforcer(monthlyCapUsd: 10m, tokensPerDollar: 1_000L, adminOverride: false);
        meter.RecordUsage(Guid.NewGuid(), 10_500, 0);
        var v = e.Evaluate("acme", isAdmin: true, DateTime.UtcNow);
        Assert.Equal(EnforcementVerdictKind.Rejected, v.Kind);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Verdict_ToWireEnvelope_HasCanonicalShape()
    {
        var (e, meter) = MakeEnforcer(monthlyCapUsd: 10m, tokensPerDollar: 1_000L);
        meter.RecordUsage(Guid.NewGuid(), 12_000, 0);
        var v = e.Evaluate("acme", isAdmin: false, DateTime.UtcNow);
        var env = v.ToWireEnvelope();
        var json = System.Text.Json.JsonSerializer.Serialize(env);
        Assert.Contains("commentary-cost-budget-exhausted", json);
        Assert.Contains("monthlyUsd", json);
        Assert.Contains("monthlyCapUsd", json);
        Assert.Contains("percentUsed", json);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void StatusOverBudget_Is402()
    {
        Assert.Equal(402, CommentaryCostBudgetEnforcer.StatusOverBudget);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void NoBudgetWired_VerdictAllowed()
    {
        var e = new CommentaryCostBudgetEnforcer(
            NullLogger<CommentaryCostBudgetEnforcer>.Instance,
            budget: null,
            options: null);
        var v = e.Evaluate("acme", isAdmin: false, DateTime.UtcNow);
        Assert.Equal(EnforcementVerdictKind.Allowed, v.Kind);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void NullLoggerCtor_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new CommentaryCostBudgetEnforcer(null!));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void EnforcementVerdictKind_ValueOrderingStable()
    {
        Assert.Equal(0, (int)EnforcementVerdictKind.Allowed);
        Assert.Equal(1, (int)EnforcementVerdictKind.AdminOverride);
        Assert.Equal(2, (int)EnforcementVerdictKind.Rejected);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void ReasonOverBudget_IsWireStable()
    {
        Assert.Equal("commentary-cost-budget-exhausted", CommentaryCostBudgetEnforcer.ReasonOverBudget);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void AdminOverrideToggle_DefaultsToTrue()
    {
        var opts = new CommentaryOptions();
        Assert.True(opts.CostBudget.AdminOverride);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Verdict_RejectedVerdict_FactoryCarriesReason()
    {
        var v = EnforcementVerdict.RejectedVerdict(10m, 5m, 2d, "test-reason");
        Assert.Equal(EnforcementVerdictKind.Rejected, v.Kind);
        Assert.Equal("test-reason", v.Reason);
        Assert.True(v.IsRejected);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Verdict_AllowedFactory_HasNullReason()
    {
        var v = EnforcementVerdict.Allowed(BudgetState.Healthy, 1m, 10m, 0.1);
        Assert.Null(v.Reason);
        Assert.False(v.IsRejected);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Verdict_AdminOverrideFactory_HasOverrideReason()
    {
        var v = EnforcementVerdict.AdminOverrideVerdict(10m, 5m, 2d);
        Assert.Equal("admin-override", v.Reason);
        Assert.True(v.IsAdminOverride);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void EnforcerEvaluation_CarriesUsageDeltaAcrossCalls()
    {
        var (e, meter) = MakeEnforcer(monthlyCapUsd: 10m, tokensPerDollar: 1_000L);
        meter.RecordUsage(Guid.NewGuid(), 100, 0);
        var v1 = e.Evaluate("acme", isAdmin: false, DateTime.UtcNow);
        meter.RecordUsage(Guid.NewGuid(), 9_950, 0);
        var v2 = e.Evaluate("acme", isAdmin: false, DateTime.UtcNow);
        Assert.NotEqual(v1.State, v2.State);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void Verdict_ToWireEnvelope_PercentRoundedToTwoPlaces()
    {
        var (e, meter) = MakeEnforcer(monthlyCapUsd: 10m, tokensPerDollar: 1_000L);
        meter.RecordUsage(Guid.NewGuid(), 13_333, 0); // 133.33%
        var v = e.Evaluate("acme", isAdmin: false, DateTime.UtcNow);
        var env = v.ToWireEnvelope();
        var json = System.Text.Json.JsonSerializer.Serialize(env);
        Assert.Contains("\"percentUsed\":133.33", json);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void EnforcerEvaluate_ToleratesNullTenantId()
    {
        var (e, _) = MakeEnforcer();
        var v = e.Evaluate(tenantId: null, isAdmin: false, DateTime.UtcNow);
        Assert.NotNull(v);
    }
}
