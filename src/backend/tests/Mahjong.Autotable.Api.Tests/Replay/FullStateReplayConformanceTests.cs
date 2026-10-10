using System.Text.Json;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Replay;
using Mahjong.Autotable.Api.Tables;

namespace Mahjong.Autotable.Api.Tests.Replay;

public class FullStateReplayConformanceTests
{
    internal static readonly string[] FullProperties = """
        gameId seed phase roundWind roundNumber handNumber handInRound maxHands baseUnit
        botDifficulty isGameComplete dealerSeatIndex activeSeatIndex seats wall wallDrawIndex
        wallBackIndex wallBackDrawn hands discardPile claimWindow turnNumber currentWin currentScore
        requireHandResultAcknowledgements handResultContinuation
        missedWinSeats falseHuPenalties cumulativeScores lastDiceRoll breakPoint dealMode
        pickupSeatIndex pickupRoundIndex eventLog eventSequence stateVersion lastDrawWasKongReplacement
        lastDrawSeatIndex discardsThisHand isPublic publicName creatorPlayerId
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Initialization_PreservesAllPropertiesAndRuntimeVersionReset()
    {
        var driver = new ReplayTestDriver(17, baseUnit: 4);
        Assert.Equal(0, driver.State.StateVersion);
        Assert.Equal(1, driver.State.EventSequence);
        Assert.Null(driver.State.BotDifficulty);
        using var json = JsonDocument.Parse(ChangshaReplayStateCodec.Serialize(driver.State));
        Assert.Equal(FullProperties.Order(), json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        driver.AssertVerified();
    }

    [Fact]
    public void ExplicitChow_Seed0_PreservesAcceptedPartnersAndRejectsDefaultSubstitution()
    {
        var driver = Chow();
        Assert.Equal(new[] { 12, 19, 20 }, driver.State.Hands[1].Melds[0].TileIds);
        Assert.Equal(new[] { 12, 20 }, driver.Journal.Records()[^1].Observations.AcceptedChowPartners);
        driver.AssertVerified();
        var envelope = driver.Export();
        var records = envelope.Records.ToArray();
        records[^1] = records[^1] with { Inputs = records[^1].Inputs with { ChosenTileIds = [9, 12] } };
        Assert.False(ChangshaFullStateReplayVerifier.Verify(envelope with { Records = records }).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyChowSelection_RecomputesActualAcceptedPartners(bool emptyArray)
    {
        var driver = new ReplayTestDriver(0);
        driver.StartAndDeal(false);
        driver.Do(ReplayOperation.Discard, new() { SeatIndex = 0, TileId = 19 });
        driver.Do(ReplayOperation.ResolveClaim, new()
        {
            SeatIndex = 1, ClaimType = TableClaimType.Chow, ChosenTileIds = emptyArray ? [] : null
        });
        Assert.Equal(new[] { 9, 12, 19 }, driver.State.Hands[1].Melds[0].TileIds);
        driver.AssertVerified();
    }

    [Fact]
    public void FourManualHands_Seed7_ExposedKong_Reconstructs703Events()
    {
        var driver = FourHands();
        Assert.Equal(703, driver.State.EventLog.Count);
        Assert.Equal(4, driver.State.EventLog.Count(e => e.EventType == "draw-hand"));
        Assert.True(driver.State.IsGameComplete);
        Assert.Equal(ReplayOperation.RotateBanker, driver.Journal.Records()[^1].Operation);
        driver.AssertVerified(ReplayCutKind.NaturalCompletion);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(8, false)]
    [InlineData(16, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(16, true)]
    public void SupportedCapsAndModes_ReconstructCompleteGames(int cap, bool manual)
    {
        var driver = new ReplayTestDriver(4242, cap, captureOriginals: false);
        driver.StartAndDeal(manual);
        while (!driver.State.IsGameComplete)
        {
            driver.FinishHand();
            driver.Do(ReplayOperation.RotateBanker);
            if (!driver.State.IsGameComplete) driver.DealNext(manual);
        }
        Assert.Equal(cap + 1, driver.State.HandNumber);
        driver.AssertVerified(ReplayCutKind.NaturalCompletion);
    }

    [Theory]
    [InlineData("missing-init")]
    [InlineData("duplicate")]
    [InlineData("reorder")]
    [InlineData("clock-extra")]
    [InlineData("clock-missing")]
    [InlineData("clock-tick")]
    [InlineData("clock-kind")]
    [InlineData("choice")]
    [InlineData("after-hash")]
    [InlineData("engine")]
    [InlineData("incomplete")]
    [InlineData("unsupported")]
    public void RecordAndClockTampering_IsRejected(string mutation)
    {
        var envelope = Chow().Export();
        var records = envelope.Records.ToList();
        switch (mutation)
        {
            case "missing-init": records.RemoveAt(0); break;
            case "duplicate": records.Insert(1, records[1]); break;
            case "reorder": (records[1], records[2]) = (records[2], records[1]); break;
            case "clock-extra":
                records[1] = records[1] with { StateClockFacts = [.. records[1].StateClockFacts, new(ReplayClockKind.EventUtc, 123)] };
                break;
            case "clock-missing": records[1] = records[1] with { StateClockFacts = [] }; break;
            case "clock-tick":
                records[1] = records[1] with { StateClockFacts = [records[1].StateClockFacts[0] with { Value = records[1].StateClockFacts[0].Value + 1 }] };
                break;
            case "clock-kind":
                records[1] = records[1] with { StateClockFacts = [records[1].StateClockFacts[0] with { Kind = ReplayClockKind.ClaimOpenedUnixMs }] };
                break;
            case "choice": records[^1] = records[^1] with { Inputs = records[^1].Inputs with { ChosenTileIds = null } }; break;
            case "after-hash": records[^1] = records[^1] with { After = records[^1].After with { FullStateHash = new string('0', 64) } }; break;
            case "engine": envelope = envelope with { EngineIdentity = envelope.EngineIdentity with { Codec = "other" } }; break;
            case "incomplete": envelope = envelope with { RecordingStatus = ReplayRecordingStatus.CaptureGap }; break;
            case "unsupported": records[1] = records[1] with { Operation = (ReplayOperation)999 }; break;
        }
        Assert.False(ChangshaFullStateReplayVerifier.Verify(envelope with { Records = records }).Success);
    }

    [Fact]
    public void LegacyJson_IsExplicitlyUnverifiable()
    {
        Assert.Equal("LegacyUnverifiable", ChangshaFullStateReplayVerifier.VerifyJson("[]").Status);
        Assert.Equal("LegacyUnverifiable", ChangshaFullStateReplayVerifier.VerifyJson("""{"schemaVersion":2,"events":[]}""").Status);
    }

    [Fact]
    public void UnrecordedStateMutation_DoesNotBecomeANewReplayBaseline()
    {
        var driver = new ReplayTestDriver(0);
        driver.State.BaseUnit = 2;
        driver.Do(ReplayOperation.StartGame);
        Assert.Equal(ReplayRecordingStatus.CaptureGap, driver.Journal.Status);
        Assert.False(ChangshaFullStateReplayVerifier.Verify(driver.Export()).Success);
    }

    [Fact]
    public void TwoInterleavedGames_HaveIndependentClockFacts()
    {
        var a = new ReplayTestDriver(0);
        var b = new ReplayTestDriver(7);
        a.StartAndDeal(false);
        b.StartAndDeal(true);
        a.Do(ReplayOperation.Discard, new() { SeatIndex = 0, TileId = 19 });
        b.Do(ReplayOperation.Discard, new() { SeatIndex = 0, TileId = 33 });
        a.Do(ReplayOperation.ResolveClaim, new() { SeatIndex = 1, ClaimType = TableClaimType.Chow, ChosenTileIds = [12, 20] });
        b.Do(ReplayOperation.ResolveClaim, new() { SeatIndex = 1, ClaimType = TableClaimType.Kong });
        a.AssertVerified();
        b.AssertVerified();
        var bad = a.Export() with { Records = b.Export().Records };
        Assert.False(ChangshaFullStateReplayVerifier.Verify(bad).Success);
    }

    [Fact]
    public async Task ParallelGamesAndVerifiers_DoNotShareClockCursors()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(seed => Task.Run(() =>
        {
            var driver = new ReplayTestDriver(seed);
            driver.StartAndDeal(seed % 2 == 0);
            driver.Do(ReplayOperation.Discard, new() { SeatIndex = 0, TileId = driver.State.Hands[0].ConcealedTiles[0] });
            if (driver.State.Phase == ChangshaPhase.AwaitingClaim) driver.Do(ReplayOperation.PassClaim);
            driver.AssertVerified();
            return driver.State.GameId;
        })));
        Assert.Equal(4, results.Distinct().Count());
    }

    internal static ReplayTestDriver Chow()
    {
        var driver = new ReplayTestDriver(0);
        driver.StartAndDeal(false);
        driver.Do(ReplayOperation.Discard, new() { SeatIndex = 0, TileId = 19 });
        driver.Do(ReplayOperation.ResolveClaim, new()
        {
            SeatIndex = 1, ClaimType = TableClaimType.Chow, ChosenTileIds = [12, 20]
        });
        return driver;
    }

    internal static ReplayTestDriver FourHands()
    {
        var driver = new ReplayTestDriver(7, captureOriginals: false);
        driver.StartAndDeal(true);
        driver.Do(ReplayOperation.Discard, new() { SeatIndex = 0, TileId = 33 });
        driver.Do(ReplayOperation.ResolveClaim, new() { SeatIndex = 1, ClaimType = TableClaimType.Kong });
        Assert.Equal(89, driver.State.Hands[1].ConcealedTiles[^1]);
        Assert.Equal(1, driver.State.WallBackDrawn);
        while (!driver.State.IsGameComplete)
        {
            driver.FinishHand();
            driver.Do(ReplayOperation.RotateBanker);
            if (!driver.State.IsGameComplete) driver.DealNext(true);
        }
        return driver;
    }
}

internal sealed class ReplayTestDriver
{
    internal ChangshaGameState State { get; private set; }
    internal ChangshaReplayJournal Journal { get; }
    internal Dictionary<long, string>? Originals { get; }

    internal ReplayTestDriver(int seed, int cap = 4, int baseUnit = 1, bool captureOriginals = true)
    {
        long ticks = DateTime.UnixEpoch.Ticks + 1234567890L + seed * 100000L;
        var initialized = ChangshaReplayJournal.Initialize(new()
        {
            Seed = seed, BotSeatIndexes = [], MaxHands = cap, BaseUnit = baseUnit
        }, () => new ChangshaReplayContext(
            () => new DateTime(Interlocked.Add(ref ticks, 37), DateTimeKind.Utc),
            () => Interlocked.Add(ref ticks, 10000) / TimeSpan.TicksPerMillisecond));
        State = initialized.State;
        Journal = initialized.Journal;
        Originals = captureOriginals ? new() : null;
        Capture();
    }

    internal void Do(ReplayOperation operation, ReplayInputs? inputs = null)
    {
        inputs ??= new();
        if (operation is ReplayOperation.ResolveClaim or ReplayOperation.PassClaim)
            inputs = inputs with
            {
                WindowOpeningSequence = State.EventLog.Last(e => e.EventType == "claim-window-open").Sequence
            };
        Journal.Apply(State, operation, inputs);
        Capture();
    }

    internal void StartAndDeal(bool manual)
    {
        if (manual) Do(ReplayOperation.SetDealMode, new() { DealMode = DealMode.Manual });
        Do(ReplayOperation.StartGame);
        DealNext(manual);
    }

    internal void DealNext(bool manual)
    {
        var seed = manual || State.HandNumber > 1 ? unchecked(State.Seed + State.HandNumber) : State.Seed;
        var dice = new DiceService(seed).Roll();
        Do(manual ? ReplayOperation.BeginManualDeal : ReplayOperation.RollDice, new() { Dice = dice, DiceSeed = seed });
        if (manual)
        {
            while (ChangshaGameStateMachine.IsPickupPhase(State.Phase))
                Do(ReplayOperation.TakeTilesFromWall, new()
                {
                    SeatIndex = State.PickupSeatIndex,
                    Count = ChangshaGameStateMachine.ExpectedPickupCount(State.Phase)
                });
        }
        else Do(ReplayOperation.Deal);
        AssertConservation();
    }

    internal void FinishHand()
    {
        for (var i = 0; i < 512 && State.Phase != ChangshaPhase.WallExhausted; i++)
        {
            if (State.Phase == ChangshaPhase.AwaitingClaim) Do(ReplayOperation.PassClaim);
            else
            {
                Assert.Equal(ChangshaPhase.AwaitingDiscard, State.Phase);
                var hand = State.Hands[State.ActiveSeatIndex];
                if (hand.ConcealedTiles.Count + 3 * hand.Melds.Count == 14)
                    Do(ReplayOperation.Discard, new() { SeatIndex = State.ActiveSeatIndex, TileId = hand.ConcealedTiles[0] });
                else Do(ReplayOperation.DrawTile);
            }
            AssertConservation();
        }
        Assert.Equal(ChangshaPhase.WallExhausted, State.Phase);
        Do(ReplayOperation.HandleWallExhausted);
    }

    internal ChangshaReplayEnvelope Export(ReplayCutKind kind = ReplayCutKind.Checkpoint) => Journal.Export(State, kind);

    internal void RoundTrip()
    {
        State = Journal.RoundTrip(State);
        Capture();
    }

    internal void AssertVerified(ReplayCutKind kind = ReplayCutKind.Checkpoint)
    {
        var originalFinal = ChangshaReplayStateCodec.Serialize(State);
        var result = ChangshaFullStateReplayVerifier.Verify(Export(kind), Originals);
        Assert.True(result.Success, $"{result.Status} at {result.RecordSequence}: {result.Detail} ({result.ExpectedHash} != {result.ActualHash})");
        Assert.Equal(originalFinal, ChangshaReplayStateCodec.Serialize(result.ReconstructedState!));
    }

    internal void AssertConservation() =>
        Assert.Equal(Enumerable.Range(0, 108), State.Wall
            .Concat(State.Hands.SelectMany(h => h.ConcealedTiles.Concat(h.Melds.SelectMany(m => m.TileIds))))
            .Concat(State.DiscardPile.Select(d => d.TileId)).Order());

    private void Capture()
    {
        if (Originals is not null) Originals[Journal.Count] = ChangshaReplayStateCodec.Serialize(State);
    }
}
