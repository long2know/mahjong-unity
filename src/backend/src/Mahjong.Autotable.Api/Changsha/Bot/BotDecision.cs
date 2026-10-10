namespace Mahjong.Autotable.Api.Changsha.Bot;

/// <summary>
/// Phase J Wave 10 — explainable bot decisions. Wraps the legacy
/// <see cref="BotAction"/> (the actual move) with a numeric strategy
/// score and an ordered list of human-readable reasoning lines.
///
/// <para>Why this exists: Hicks's Wave 9 admin tab surfaces the per-hand
/// audit log v2 as a "replay scrubber + bot decision drilldown" view.
/// Without reasoning the audit row is just "bot discarded tile 12" with
/// no insight into <i>why</i>; with the reasoning list the view renders
/// each decision as a stack of bullets ("kept pair of 5-tong",
/// "shanten unchanged at 2", "no opponent has discarded 7-tong yet").</para>
///
/// <para><b>Wire contract.</b> The struct lives on the bot decision
/// path (<see cref="IChangshaBotStrategy.DecideWithReasoning"/>); the
/// audit-log persister serialises it into the v2 replay envelope's
/// <c>debugScore</c> field (Wave 9 placeholder). The reasoning array
/// is intentionally a list of plain strings — no structured fields,
/// no localisation keys — because the admin tab is operator-only.</para>
///
/// <para><b>Backward compat.</b> <see cref="IChangshaBotStrategy"/>
/// keeps <c>DecideAction</c> for non-audit hot paths (the runtime's
/// claim window driver). New code reads <c>DecideWithReasoning</c>;
/// the default interface method wraps <c>DecideAction</c> with empty
/// reasoning so unmodified custom strategies (none in-tree today)
/// don't break.</para>
/// </summary>
public readonly record struct BotDecision(
    BotAction Action,
    int? Tile,
    int Score,
    IReadOnlyList<string> Reasoning)
{
    /// <summary>Convenience wrapper for callers that haven't yet adopted
    /// the reasoning surface — wraps an existing <see cref="BotAction"/>
    /// with empty reasoning and a sentinel score of 0. The default
    /// <see cref="IChangshaBotStrategy.DecideWithReasoning"/> implementation
    /// uses this so strategies that don't override it still surface a
    /// valid (if uninformative) <see cref="BotDecision"/>.</summary>
    public static BotDecision FromAction(BotAction action) => new(
        Action: action,
        Tile: action.TileId,
        Score: 0,
        Reasoning: Array.Empty<string>());
}
