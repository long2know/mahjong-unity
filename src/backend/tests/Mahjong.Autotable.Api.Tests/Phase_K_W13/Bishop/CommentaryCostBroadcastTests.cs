using Mahjong.Autotable.Api.Commentary;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W13.Bishop;

/// <summary>
/// Phase K Wave 13 — Bishop. Hard-asserted contract for the
/// commentary cost SignalR broadcast surface.
///
/// <list type="number">
///   <item><see cref="CommentaryCostAdminHub"/> exists.</item>
///   <item>Hub group literal is <c>commentary:cost:admin</c>
///         (every admin client joins the same group).</item>
///   <item>Warning event method = <c>CommentaryCostWarning</c>.</item>
///   <item>Cap-reached event method = <c>CommentaryCostCapReached</c>.</item>
///   <item><see cref="CommentaryCostBroadcaster"/> exists.</item>
///   <item>BroadcastWarningAsync sends to the admin group.</item>
///   <item>BroadcastCapReachedAsync sends to the admin group.</item>
///   <item>Broadcaster swallows transient hub failures
///         (best-effort — never bubble up to the generator).</item>
///   <item>Budget's one-shot gate fires broadcaster on first
///         Warning transition.</item>
///   <item>Budget's one-shot gate fires broadcaster on first
///         Exhausted transition.</item>
///   <item>Budget's one-shot gate does NOT re-fire within the
///         same calendar month.</item>
/// </list>
/// </summary>
public sealed class CommentaryCostBroadcastTests
{
    private sealed class CapturingClientProxy : IClientProxy
    {
        public List<(string Method, object?[] Args)> Sent { get; } = new();
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
        {
            Sent.Add((method, args));
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingHubClients : IHubClients
    {
        public CapturingClientProxy Captured { get; } = new();
        public IClientProxy All => Captured;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Captured;
        public IClientProxy Client(string connectionId) => Captured;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Captured;
        public IClientProxy Group(string groupName)
        {
            Captured.Sent.Add(("__group__", new object?[] { groupName }));
            return Captured;
        }
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Captured;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Captured;
        public IClientProxy User(string userId) => Captured;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Captured;
    }

    private sealed class CapturingHubContext : IHubContext<CommentaryCostAdminHub>
    {
        public CapturingHubClients Capturing { get; } = new();
        public IHubClients Clients => Capturing;
        public IGroupManager Groups => throw new NotImplementedException();
    }

    private sealed class ThrowingClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) =>
            Task.FromException(new InvalidOperationException("hub-down"));
    }

    private sealed class ThrowingHubClients : IHubClients
    {
        private readonly ThrowingClientProxy _p = new();
        public IClientProxy All => _p;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _p;
        public IClientProxy Client(string connectionId) => _p;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _p;
        public IClientProxy Group(string groupName) => _p;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _p;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _p;
        public IClientProxy User(string userId) => _p;
        public IClientProxy Users(IReadOnlyList<string> userIds) => _p;
    }

    private sealed class ThrowingHubContext : IHubContext<CommentaryCostAdminHub>
    {
        public IHubClients Clients { get; } = new ThrowingHubClients();
        public IGroupManager Groups => throw new NotImplementedException();
    }

    private sealed class FakeMeter : ICommentaryUsageMeter
    {
        public long Tokens { get; set; }
        public void RecordUsage(Guid gameId, int inputTokens, int outputTokens) =>
            Tokens += inputTokens + outputTokens;
        public long PerGameTokens(Guid gameId) => 0;
        public long MonthlyTokens(DateTime utcNow) => Tokens;
        public bool ExceedsMonthlyCap(long cap, DateTime utcNow) => Tokens >= cap;
    }

    private sealed class OptionsMonitorStub : IOptionsMonitor<CommentaryOptions>
    {
        private readonly CommentaryOptions _opts;
        public OptionsMonitorStub(CommentaryOptions opts) { _opts = opts; }
        public CommentaryOptions CurrentValue => _opts;
        public CommentaryOptions Get(string? name) => _opts;
        public IDisposable? OnChange(Action<CommentaryOptions, string?> listener) => null;
    }

    private static CommentaryOptions OptsWithCap(decimal capUsd) =>
        new()
        {
            Model = "gpt-test",
            CostBudget = new CommentaryOptions.CostBudgetOptions
            {
                MonthlyCapUsd = capUsd,
                TokensPerDollar = 1000,
                WarnThreshold = 0.8,
            },
        };

    private static BudgetEvaluation MakeEval(BudgetState state) =>
        new(state, 1m, 1m, 1.0, 1000, 1000);

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Hub_TypeExists()
    {
        Assert.NotNull(typeof(CommentaryCostAdminHub));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Hub_GroupLiteralStable()
    {
        Assert.Equal("commentary:cost:admin", CommentaryCostAdminHub.AdminGroup);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Hub_WarningEventMethodIsStable()
    {
        Assert.Equal("CommentaryCostWarning", CommentaryCostAdminHub.WarningEvent);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Hub_CapReachedEventMethodIsStable()
    {
        Assert.Equal("CommentaryCostCapReached", CommentaryCostAdminHub.CapReachedEvent);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public void Broadcaster_TypeExists()
    {
        Assert.NotNull(typeof(CommentaryCostBroadcaster));
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task Broadcaster_Warning_SendsToAdminGroup()
    {
        var ctx = new CapturingHubContext();
        var broadcaster = new CommentaryCostBroadcaster(ctx, NullLogger<CommentaryCostBroadcaster>.Instance);
        await broadcaster.BroadcastWarningAsync(MakeEval(BudgetState.Warning), "gpt-test");
        Assert.Contains(ctx.Capturing.Captured.Sent, e =>
            e.Method == "__group__" && (string?)e.Args[0] == CommentaryCostAdminHub.AdminGroup);
        Assert.Contains(ctx.Capturing.Captured.Sent, e =>
            e.Method == CommentaryCostAdminHub.WarningEvent);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task Broadcaster_CapReached_SendsToAdminGroup()
    {
        var ctx = new CapturingHubContext();
        var broadcaster = new CommentaryCostBroadcaster(ctx, NullLogger<CommentaryCostBroadcaster>.Instance);
        await broadcaster.BroadcastCapReachedAsync(MakeEval(BudgetState.Exhausted), "gpt-test");
        Assert.Contains(ctx.Capturing.Captured.Sent, e =>
            e.Method == CommentaryCostAdminHub.CapReachedEvent);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task Broadcaster_SwallowsHubFailure()
    {
        var ctx = new ThrowingHubContext();
        var broadcaster = new CommentaryCostBroadcaster(ctx, NullLogger<CommentaryCostBroadcaster>.Instance);
        // Should not throw — best-effort surface.
        await broadcaster.BroadcastWarningAsync(MakeEval(BudgetState.Warning), "gpt-test");
        await broadcaster.BroadcastCapReachedAsync(MakeEval(BudgetState.Exhausted), "gpt-test");
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task Budget_Warning_FiresBroadcasterOnce()
    {
        var ctx = new CapturingHubContext();
        var broadcaster = new CommentaryCostBroadcaster(ctx, NullLogger<CommentaryCostBroadcaster>.Instance);
        var meter = new FakeMeter { Tokens = 850 };
        // Cap = $1 at 1000 tokens/$ → 850 tokens = $0.85 → 85% (over 80% warn).
        var budget = new CommentaryCostBudget(
            new OptionsMonitorStub(OptsWithCap(1m)), meter,
            NullLogger<CommentaryCostBudget>.Instance, broadcaster);
        var when = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        budget.Evaluate(when);
        budget.Evaluate(when);
        // Wait for the fire-and-forget broadcast to settle.
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(20);
            if (ctx.Capturing.Captured.Sent.Any(e => e.Method == CommentaryCostAdminHub.WarningEvent))
                break;
        }
        var warningHits = ctx.Capturing.Captured.Sent
            .Count(e => e.Method == CommentaryCostAdminHub.WarningEvent);
        Assert.Equal(1, warningHits);
    }

    [Fact, Trait("Category", "Commentary"), Trait("Wave", "Phase-K-13"), Trait("Lane", "Bishop")]
    public async Task Budget_Exhausted_FiresBroadcasterOnce()
    {
        var ctx = new CapturingHubContext();
        var broadcaster = new CommentaryCostBroadcaster(ctx, NullLogger<CommentaryCostBroadcaster>.Instance);
        var meter = new FakeMeter { Tokens = 1200 };
        var budget = new CommentaryCostBudget(
            new OptionsMonitorStub(OptsWithCap(1m)), meter,
            NullLogger<CommentaryCostBudget>.Instance, broadcaster);
        var when = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        budget.Evaluate(when);
        budget.Evaluate(when);
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(20);
            if (ctx.Capturing.Captured.Sent.Any(e => e.Method == CommentaryCostAdminHub.CapReachedEvent))
                break;
        }
        var capHits = ctx.Capturing.Captured.Sent
            .Count(e => e.Method == CommentaryCostAdminHub.CapReachedEvent);
        Assert.Equal(1, capHits);
    }
}
