using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Bot;

namespace Mahjong.Autotable.Api.Tests.ChangshaServices;

/// <summary>
/// Phase J Wave 10 — bot decision reasoning surface contract tests
/// (Vasquez).
///
/// <para>Wave 10 (Bishop) adds <see cref="IChangshaBotStrategy.DecideWithReasoning"/>
/// on every strategy tier. The return value is a <see cref="BotDecision"/>
/// (record-struct) carrying the chosen <see cref="BotAction"/>, the
/// (optional) tile id, a numeric strategy score, and an ordered list
/// of human-readable reasoning strings. The audit / admin tab consumes
/// these strings to render a drill-down "why did the bot do that?" view.</para>
///
/// <para><b>Contracts pinned by this suite:</b>
/// <list type="bullet">
///   <item>Every strategy (Easy / Medium / Hard / Master) implements
///         <c>DecideWithReasoning</c> and returns a non-default
///         <see cref="BotDecision"/>.</item>
///   <item><see cref="BotDecision.Reasoning"/> is non-null AND non-empty
///         for every shipped strategy.</item>
///   <item>Each reasoning entry is a non-empty string (no leading /
///         trailing whitespace-only lines).</item>
///   <item>The first reasoning line includes a strategy discriminator
///         (e.g. starts with <c>"strategy:"</c>) so the admin tab can
///         render a per-strategy header without parsing every line.</item>
///   <item><b>Master tier</b> includes a safety-analysis line — this is
///         Master's signature differentiator over Hard (per the Wave 10
///         brief).</item>
///   <item>Reasoning lines are immutable from the caller's perspective —
///         the contract type is a <c>readonly record struct</c> with an
///         <see cref="IReadOnlyList{T}"/> facade.</item>
/// </list></para>
/// </summary>
public class BotDecisionReasoningTests
{
    private static ChangshaGameState BuildSeededState()
    {
        // Reuse the same seed pattern as BotPolicyTests so we get a
        // deterministic mid-game state without hand-building hands.
        var seed = 42;
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed, [1, 2, 3]);
        ChangshaGameStateMachine.StartGame(state);
        var diceService = new DiceService(seed);
        ChangshaGameStateMachine.RollDice(state, diceService);
        ChangshaGameStateMachine.Deal(state);
        // After Deal the state is AwaitingDiscard for the active seat.
        return state;
    }

    private static readonly string[] AllTiers = { "easy", "medium", "hard", "master" };

    // ────────────────────────────────────────────────────────────────────
    //  1. Every strategy returns a populated Reasoning list
    // ────────────────────────────────────────────────────────────────────

    [Theory, Trait("Category", "Bot"), Trait("Wave", "Phase-J-10")]
    [InlineData("easy")]
    [InlineData("medium")]
    [InlineData("hard")]
    [InlineData("master")]
    public void DecideWithReasoning_PopulatesReasoning_EveryTier(string tier)
    {
        var state = BuildSeededState();
        var strategy = ChangshaBotEngine.Resolve(tier);
        var decision = strategy.DecideWithReasoning(state, state.ActiveSeatIndex);

        Assert.NotNull(decision.Reasoning);
        Assert.NotEmpty(decision.Reasoning);
        foreach (var line in decision.Reasoning)
        {
            Assert.False(string.IsNullOrWhiteSpace(line),
                $"Strategy '{tier}' emitted a blank reasoning line.");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. The first reasoning line carries the strategy discriminator
    // ────────────────────────────────────────────────────────────────────

    [Theory, Trait("Category", "Bot"), Trait("Wave", "Phase-J-10")]
    [InlineData("easy")]
    [InlineData("medium")]
    [InlineData("hard")]
    [InlineData("master")]
    public void DecideWithReasoning_FirstLineCarriesTierDiscriminator(string tier)
    {
        var state = BuildSeededState();
        var strategy = ChangshaBotEngine.Resolve(tier);
        var decision = strategy.DecideWithReasoning(state, state.ActiveSeatIndex);

        // Either the first line literally contains the tier name OR
        // contains the canonical "strategy:" prefix. The admin tab can
        // grep either.
        var first = decision.Reasoning.First().ToLowerInvariant();
        Assert.True(
            first.Contains(tier, StringComparison.Ordinal)
            || first.Contains("strategy:", StringComparison.Ordinal),
            $"Strategy '{tier}' first reasoning line '{decision.Reasoning.First()}' lacks a tier discriminator.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Master tier explicitly surfaces "safety" analysis line
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Bot"), Trait("Wave", "Phase-J-10")]
    public void DecideWithReasoning_Master_IncludesSafetyAnalysis()
    {
        var state = BuildSeededState();
        var master = ChangshaBotEngine.Resolve("master");
        var decision = master.DecideWithReasoning(state, state.ActiveSeatIndex);

        // Master's signature tier per the Wave 10 brief — at least one
        // reasoning line must mention safety/defense/opponent-discard.
        var joined = string.Join("\n", decision.Reasoning).ToLowerInvariant();
        Assert.True(
            joined.Contains("safety")
            || joined.Contains("defen")        // defen[ce|sive]
            || joined.Contains("opponent"),
            $"Master strategy missing safety-analysis reasoning. Lines:\n{joined}");
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Reasoning list is read-only (caller can't mutate the struct's
    //     internal view).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Bot"), Trait("Wave", "Phase-J-10")]
    public void BotDecision_Reasoning_IsReadOnlyList()
    {
        var state = BuildSeededState();
        var strategy = ChangshaBotEngine.Resolve("medium");
        var decision = strategy.DecideWithReasoning(state, state.ActiveSeatIndex);

        // The contract type is `IReadOnlyList<string>` — no Add/Remove
        // surface exposed to callers. We assert via reflection that the
        // property's declared type is the read-only one (so we catch a
        // regression that demotes it to List<string>).
        var prop = typeof(BotDecision).GetProperty("Reasoning");
        Assert.NotNull(prop);
        Assert.Equal(typeof(IReadOnlyList<string>), prop!.PropertyType);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. BotDecision.FromAction wraps an action with empty reasoning
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Bot"), Trait("Wave", "Phase-J-10")]
    public void BotDecision_FromAction_EmptyReasoningSentinel()
    {
        var pass = BotAction.Pass();
        var decision = BotDecision.FromAction(pass);
        Assert.Same(pass, decision.Action);
        Assert.Equal(0, decision.Score);
        Assert.NotNull(decision.Reasoning);
        Assert.Empty(decision.Reasoning);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. Action chosen by DecideWithReasoning matches DecideAction's
    //     legacy path (sanity contract — the new surface MUST agree on
    //     the action chosen for a given state).
    // ────────────────────────────────────────────────────────────────────

    [Theory, Trait("Category", "Bot"), Trait("Wave", "Phase-J-10")]
    [InlineData("easy")]
    [InlineData("medium")]
    [InlineData("hard")]
    [InlineData("master")]
    public void DecideWithReasoning_ActionMatchesLegacyDecideAction(string tier)
    {
        var state = BuildSeededState();
        var strategy = ChangshaBotEngine.Resolve(tier);

        var legacy = strategy.DecideAction(state, state.ActiveSeatIndex);
        var newSurf = strategy.DecideWithReasoning(state, state.ActiveSeatIndex);

        // Both paths see the same state, so they must choose the same
        // ACTION TYPE. Tile id can vary if the strategy involves any
        // tie-break that depends on a sort key the new surface tracks
        // differently — we don't pin tile equality, but the action
        // type AND any claim type must match.
        Assert.Equal(legacy.Type, newSurf.Action.Type);
        if (legacy.ClaimType.HasValue)
            Assert.Equal(legacy.ClaimType, newSurf.Action.ClaimType);
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. Strategy property still reports the canonical tier discriminator
    //     (the test asserts Phase F stability didn't regress).
    // ────────────────────────────────────────────────────────────────────

    [Theory, Trait("Category", "Bot"), Trait("Wave", "Phase-J-10")]
    [InlineData("easy")]
    [InlineData("medium")]
    [InlineData("hard")]
    [InlineData("master")]
    public void Strategy_DifficultyProperty_StillCanonical(string tier)
    {
        var strategy = ChangshaBotEngine.Resolve(tier);
        Assert.Equal(tier, strategy.Difficulty);
    }
}
