using Mahjong.Autotable.Api.Commentary;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W12.Bishop;

/// <summary>
/// Phase K Wave 12 — Bishop. Hard-asserted contract for the
/// commentary LLM cost-budget gate
/// (<see cref="CommentaryCostBudget"/>).
///
/// <list type="number">
///   <item><see cref="CommentaryCostBudget"/> exists.</item>
///   <item><see cref="CommentaryOptions.CostBudgetOptions"/>
///         default MonthlyCapUsd = 0 (no cap).</item>
///   <item>Default TokensPerDollar = 200_000.</item>
///   <item>Default WarnThreshold = 0.8.</item>
///   <item>Healthy when zero usage (with cap configured).</item>
///   <item>Warning at 80% of cap.</item>
///   <item>Exhausted at 100% of cap.</item>
///   <item>Healthy when MonthlyCapUsd = 0 (unlimited).</item>
///   <item>USD computation = tokens / TokensPerDollar.</item>
///   <item>BudgetState enum carries Healthy/Warning/Exhausted.</item>
/// </list>
/// </summary>
public sealed class CommentaryCostBudgetFacts
{
    private sealed class FakeMeter : ICommentaryUsageMeter
    {
        public long Tokens { get; set; }
        public void RecordUsage(Guid gameId, int inputTokens, int outputTokens) => Tokens += inputTokens + outputTokens;
        public long PerGameTokens(Guid gameId) => 0;
        public long MonthlyTokens(DateTime utcNow) => Tokens;
        public bool ExceedsMonthlyCap(long cap, DateTime utcNow) => Tokens >= cap;
    }

    private static CommentaryCostBudget NewBudget(FakeMeter meter, CommentaryOptions opts) =>
        new(new OptionsMonitorStub(opts), meter, NullLogger<CommentaryCostBudget>.Instance);

    private sealed class OptionsMonitorStub : IOptionsMonitor<CommentaryOptions>
    {
        private readonly CommentaryOptions _opts;
        public OptionsMonitorStub(CommentaryOptions opts) { _opts = opts; }
        public CommentaryOptions CurrentValue => _opts;
        public CommentaryOptions Get(string? name) => _opts;
        public IDisposable? OnChange(Action<CommentaryOptions, string?> listener) => null;
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void CommentaryCostBudget_TypeExists()
    {
        Assert.NotNull(typeof(CommentaryCostBudget));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void Options_DefaultMonthlyCapIsZero()
    {
        var opts = new CommentaryOptions.CostBudgetOptions();
        Assert.Equal(0m, opts.MonthlyCapUsd);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void Options_DefaultTokensPerDollarIs200k()
    {
        var opts = new CommentaryOptions.CostBudgetOptions();
        Assert.Equal(200_000L, opts.TokensPerDollar);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void Options_DefaultWarnThresholdIs80Percent()
    {
        var opts = new CommentaryOptions.CostBudgetOptions();
        Assert.Equal(0.8, opts.WarnThreshold);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void Healthy_WhenZeroUsageWithCap()
    {
        var meter = new FakeMeter { Tokens = 0 };
        var opts = new CommentaryOptions
        {
            CostBudget = new CommentaryOptions.CostBudgetOptions { MonthlyCapUsd = 100m },
        };
        var budget = NewBudget(meter, opts);
        var verdict = budget.Evaluate(DateTime.UtcNow);
        Assert.Equal(BudgetState.Healthy, verdict.State);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void Warning_At80PercentOfCap()
    {
        // 200_000 tokens = $1. Cap=$10. 80% = $8 = 1_600_000 tokens.
        var meter = new FakeMeter { Tokens = 1_600_000L };
        var opts = new CommentaryOptions
        {
            CostBudget = new CommentaryOptions.CostBudgetOptions { MonthlyCapUsd = 10m },
        };
        var budget = NewBudget(meter, opts);
        var verdict = budget.Evaluate(DateTime.UtcNow);
        Assert.Equal(BudgetState.Warning, verdict.State);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void Exhausted_AtFullCap()
    {
        // 200_000 tokens = $1. Cap=$5 = 1_000_000 tokens.
        var meter = new FakeMeter { Tokens = 1_000_000L };
        var opts = new CommentaryOptions
        {
            CostBudget = new CommentaryOptions.CostBudgetOptions { MonthlyCapUsd = 5m },
        };
        var budget = NewBudget(meter, opts);
        var verdict = budget.Evaluate(DateTime.UtcNow);
        Assert.Equal(BudgetState.Exhausted, verdict.State);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void Unlimited_WhenCapIsZero()
    {
        // Even with huge tokens, MonthlyCapUsd=0 means no cap → Healthy.
        var meter = new FakeMeter { Tokens = 9_999_999L };
        var opts = new CommentaryOptions
        {
            CostBudget = new CommentaryOptions.CostBudgetOptions { MonthlyCapUsd = 0m },
        };
        var budget = NewBudget(meter, opts);
        var verdict = budget.Evaluate(DateTime.UtcNow);
        Assert.Equal(BudgetState.Healthy, verdict.State);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void Usd_ComputedAsTokensOverTokensPerDollar()
    {
        var meter = new FakeMeter { Tokens = 400_000L };
        var opts = new CommentaryOptions
        {
            CostBudget = new CommentaryOptions.CostBudgetOptions
            {
                MonthlyCapUsd = 100m,
                TokensPerDollar = 200_000L,
            },
        };
        var budget = NewBudget(meter, opts);
        var verdict = budget.Evaluate(DateTime.UtcNow);
        Assert.Equal(2m, verdict.MonthlyUsd);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-12")]
    public void BudgetState_HasThreeValues()
    {
        Assert.Contains(BudgetState.Healthy, Enum.GetValues<BudgetState>());
        Assert.Contains(BudgetState.Warning, Enum.GetValues<BudgetState>());
        Assert.Contains(BudgetState.Exhausted, Enum.GetValues<BudgetState>());
    }
}
