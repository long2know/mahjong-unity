using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.Changsha._TestHarness;

/// <summary>
/// Shared helpers for the bot-simulation step-machine harnesses. Centralises the kong-aware
/// "does this seat owe a draw?" gate so every harness computes it identically and none can
/// reintroduce the kong-blind livelock.
/// </summary>
internal static class BotTurnHarness
{
    /// <summary>
    /// The <c>concealed + meld</c> tile count at which a seat must DRAW at the start of its turn.
    /// A seat holds the 13-tile base plus one EXTRA physical tile per kong: a kong is a 4-tile set
    /// that still counts as a single 3-tile group toward the base, so each kong leaves the physical
    /// count one above 13 (<c>13 + kongs</c>).
    ///
    /// <para>The historical <c>== 13</c> gate was kong-blind — a seat with K kongs sat at 13+K,
    /// never matched, never drew, so the harness made it discard every turn until its concealed
    /// tiles were exhausted and it spun to the step guard. This was exposed (not caused) by F1's
    /// fairness-neutral deal re-anchoring: seed 111866 routes a 2-kong hand through this path — a
    /// harness gap, NOT a game-engine defect (the live runtime draws unconditionally on advance,
    /// and every draw/kong-replacement strictly depletes a finite wall or terminates).</para>
    /// </summary>
    public static int PreDrawTileCount(ChangshaHandState hand)
        => 13 + hand.Melds.Count(m =>
            m.Kind is MeldKind.ConcealedKong or MeldKind.ExposedKong or MeldKind.AddedKong);
}
