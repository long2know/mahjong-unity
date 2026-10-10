using Mahjong.Autotable.Api.Changsha;

namespace Mahjong.Autotable.Api.Tests.Changsha;

/// <summary>
/// FIX-3 (Phase 3 stream B): the per-hand wall must mix `state.Seed` with `state.HandNumber`
/// so consecutive hands of one game produce different shuffled walls while the same
/// (seed, handNumber) pair remains deterministic for replay.
/// </summary>
public class WallSeedTests
{
    [Fact, Trait("Category", "Changsha")]
    public void WallSeed_SameHand_SameGameSeed_Deterministic()
    {
        // Same game-seed and same HandNumber → identical wall. This is the replay contract.
        var s1 = NewStateDealt(seed: 4242, handNumber: 1);
        var s2 = NewStateDealt(seed: 4242, handNumber: 1);

        Assert.Equal(s1.Wall, s2.Wall);
        Assert.Equal(s1.Hands[0].ConcealedTiles, s2.Hands[0].ConcealedTiles);
        Assert.Equal(s1.Hands[1].ConcealedTiles, s2.Hands[1].ConcealedTiles);
        Assert.Equal(s1.Hands[2].ConcealedTiles, s2.Hands[2].ConcealedTiles);
        Assert.Equal(s1.Hands[3].ConcealedTiles, s2.Hands[3].ConcealedTiles);
    }

    [Fact, Trait("Category", "Changsha")]
    public void WallSeed_DifferentHands_DifferentShuffles_SameGameSeed()
    {
        // Same game-seed across hands 1..4 — wall must differ on at least one hand pair.
        // (Strictly: all 4 should differ; we assert at least pair-1-2 differ which is the
        // bare-minimum guard against the legacy "every hand uses the same wall" bug.)
        var hand1 = NewStateDealt(seed: 4242, handNumber: 1).Wall;
        var hand2 = NewStateDealt(seed: 4242, handNumber: 2).Wall;
        var hand3 = NewStateDealt(seed: 4242, handNumber: 3).Wall;
        var hand4 = NewStateDealt(seed: 4242, handNumber: 4).Wall;

        Assert.NotEqual(hand1, hand2);
        Assert.NotEqual(hand2, hand3);
        Assert.NotEqual(hand3, hand4);
        Assert.NotEqual(hand1, hand4);
    }

    [Fact, Trait("Category", "Changsha")]
    public void WallSeed_DifferentGameSeeds_DifferentShuffles_SameHandNumber()
    {
        // Different game-seeds with the same HandNumber → different walls (game-scoped uniqueness).
        var a = NewStateDealt(seed: 1, handNumber: 1).Wall;
        var b = NewStateDealt(seed: 2, handNumber: 1).Wall;

        Assert.NotEqual(a, b);
    }

    [Fact, Trait("Category", "Changsha")]
    public void WallSeed_HandNumber_NotZeroIndexed()
    {
        // HandNumber is 1-indexed in v1; seed=N + hand=1 must NOT equal seed=N+1 + hand=0.
        // (Just a guard against any future code that decrements HandNumber.)
        var hand1 = NewStateDealt(seed: 100, handNumber: 1).Wall;
        var hand0 = NewStateDealt(seed: 100, handNumber: 0).Wall;
        Assert.NotEqual(hand1, hand0);
    }

    private static ChangshaGameState NewStateDealt(int seed, int handNumber)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed);
        state.HandNumber = handNumber;
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(seed));
        ChangshaGameStateMachine.Deal(state);
        return state;
    }
}
