using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Replay;

namespace Mahjong.Autotable.Api.Tests.Replay;

public sealed class HandResultReplayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GateAndAcknowledgements_AreVersionedHashedAndExactlyReconstructible(bool manual)
    {
        var driver = new ReplayTestDriver(23);
        driver.Do(ReplayOperation.EnableHandResultAcknowledgements);
        for (var seat = 0; seat < 2; seat++)
            driver.Do(ReplayOperation.BindHumanSeat, new() { SeatIndex = seat, PlayerId = $"human-player-{seat}" });
        driver.StartAndDeal(manual);
        driver.FinishHand();
        var version = driver.State.StateVersion;
        var before = ChangshaReplayStateCodec.Checkpoint(driver.State);
        var token = Guid.NewGuid().ToString("N");
        driver.Do(ReplayOperation.OpenHandResultContinuation, new() { ResultToken = token });
        Assert.Equal(version + 1, driver.State.StateVersion);
        Assert.NotEqual(before.FullStateHash, ChangshaReplayStateCodec.Checkpoint(driver.State).FullStateHash);
        Assert.Equal(new[] { 0, 1 }, driver.State.HandResultContinuation!.RequiredSeats(driver.State));
        Assert.Throws<InvalidOperationException>(() =>
            ChangshaReplayMetadataOperations.Apply(ChangshaReplayStateCodec.RoundTrip(driver.State),
                ReplayOperation.RotateBanker, new(), null));
        driver.Do(ReplayOperation.AcknowledgeHandResult, new()
        {
            SeatIndex = 0, PlayerId = "human-player-0", HandNumber = 1, ResultToken = token
        });
        driver.RoundTrip();
        Assert.Equal(new[] { 0 }, driver.State.HandResultContinuation!.AcknowledgedSeats);
        Assert.Equal(new[] { 1 }, driver.State.HandResultContinuation.WaitingSeats(driver.State));
        driver.AssertVerified();
        var wire = JsonSerializer.SerializeToElement(ChangshaToAutotableTranslator.BuildHandResult(driver.State),
            AutotableJson.Options);
        var continuation = wire.GetProperty("continuation");
        Assert.Equal(new[] { "acknowledgedSeats", "gameId", "handNumber", "requiredSeats", "resultToken", "waitingSeats" },
            continuation.EnumerateObject().Select(property => property.Name).Order());
        Assert.DoesNotContain("human-player", continuation.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Array, wire.GetProperty("score").ValueKind);
        Assert.Equal(driver.State.GameId, continuation.GetProperty("gameId").GetString());
        Assert.Equal(token, continuation.GetProperty("resultToken").GetString());
        driver.Do(ReplayOperation.AcknowledgeHandResult, new()
        {
            SeatIndex = 1, PlayerId = "human-player-1", HandNumber = 1, ResultToken = token
        });
        driver.Do(ReplayOperation.RotateBanker);
        Assert.Null(driver.State.HandResultContinuation);
        driver.AssertVerified();
    }

    [Fact]
    public void ActualRecordedSeatRetirementStopsBlocking_ButPlayerRebindDoesNotAcknowledge()
    {
        var driver = new ReplayTestDriver(23);
        driver.Do(ReplayOperation.EnableHandResultAcknowledgements);
        driver.Do(ReplayOperation.BindHumanSeat, new() { SeatIndex = 0, PlayerId = "owner" });
        driver.StartAndDeal(false);
        driver.FinishHand();
        driver.Do(ReplayOperation.OpenHandResultContinuation, new() { ResultToken = Guid.NewGuid().ToString("N") });
        driver.Do(ReplayOperation.BindHumanSeat, new() { SeatIndex = 0, PlayerId = "owner" });
        Assert.Equal(new[] { 0 }, driver.State.HandResultContinuation!.WaitingSeats(driver.State));
        driver.Do(ReplayOperation.ReleaseSeatIdentity, new() { SeatIndex = 0 });
        Assert.Empty(driver.State.HandResultContinuation.WaitingSeats(driver.State));
        driver.Do(ReplayOperation.RotateBanker);
        Assert.Equal(2, driver.State.HandNumber);
        driver.AssertVerified();
    }

    [Fact]
    public void LegacySnapshotDefaultsAndExplicitNullWire_AreStable()
    {
        var old = JsonSerializer.Deserialize<ChangshaGameState>(
            "{\"gameId\":\"old-game\",\"handNumber\":2}", ChangshaReplayStateCodec.SnapshotJson)!;
        Assert.False(old.RequireHandResultAcknowledgements);
        Assert.Null(old.HandResultContinuation);
        var wire = JsonSerializer.SerializeToElement(new HandResultEntry(), AutotableJson.Options);
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("continuation").ValueKind);
    }
}
