using Mahjong.Autotable.Api.Changsha.Bot;

namespace Mahjong.Autotable.Api.Changsha;

/// <summary>
/// Legacy bot policy facade. Phase F (Bishop 2026-05-XX) refactored the bot
/// decision logic into a 3-tier strategy system under <c>Changsha/Bot/*</c>;
/// this class now delegates to <see cref="MediumStrategy"/> so the existing
/// test harness (<c>BotMatchHarness</c>) and any external callers continue
/// to work unchanged. Prefer <see cref="ChangshaBotEngine.Resolve(string?)"/>
/// in new code — it returns an <see cref="IChangshaBotStrategy"/> that
/// honours the table's configured difficulty.
/// </summary>
public sealed class ChangshaBotPolicy
{
    /// <summary>
    /// Returns the next action for <paramref name="botSeatIndex"/> using the
    /// Medium-difficulty strategy (the historical default). Equivalent to
    /// <c>ChangshaBotEngine.Resolve("medium").DecideAction(state, botSeatIndex)</c>.
    /// </summary>
    public BotAction DecideAction(ChangshaGameState state, int botSeatIndex)
        => ChangshaBotEngine.Resolve("medium").DecideAction(state, botSeatIndex);

    /// <summary>
    /// Selects a tile to discard from <paramref name="hand"/> using the
    /// Medium-difficulty heuristic. Kept as a static for backward compatibility
    /// with callers that didn't need a full <see cref="DecideAction"/> walk.
    /// </summary>
    public static int SelectDiscardTile(ChangshaHandState hand)
        => MediumStrategy.SelectDiscardTile(hand);
}

public sealed class BotAction
{
    public BotActionType Type { get; init; }
    public int? TileId { get; init; }
    public int? LogicalTile { get; init; }
    public Tables.TableClaimType? ClaimType { get; init; }

    public static BotAction Wait() => new() { Type = BotActionType.Wait };
    public static BotAction Discard(int tileId) => new() { Type = BotActionType.Discard, TileId = tileId };
    public static BotAction DeclareWin() => new() { Type = BotActionType.DeclareWin };
    public static BotAction DeclareConcealedKong(int logicalTile) =>
        new() { Type = BotActionType.DeclareConcealedKong, LogicalTile = logicalTile };
    public static BotAction DeclareAddedKong(int tileId) =>
        new() { Type = BotActionType.DeclareAddedKong, TileId = tileId };
    public static BotAction Claim(Tables.TableClaimType claimType) =>
        new() { Type = BotActionType.Claim, ClaimType = claimType };
    public static BotAction Pass() => new() { Type = BotActionType.Pass };
}

public enum BotActionType
{
    Wait,
    Discard,
    DeclareWin,
    DeclareConcealedKong,
    DeclareAddedKong,
    Claim,
    Pass
}
