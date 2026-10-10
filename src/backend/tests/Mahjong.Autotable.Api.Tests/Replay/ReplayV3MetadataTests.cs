using System.Text.Json.Nodes;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Replay;

namespace Mahjong.Autotable.Api.Tests.Replay;

public class ReplayV3MetadataTests
{
    public static IEnumerable<object[]> Properties() =>
        FullStateReplayConformanceTests.FullProperties.Select(name => new object[] { name });

    [Theory]
    [InlineData("base-unit")]
    [InlineData("balanced-scores")]
    [InlineData("pass-hu")]
    [InlineData("physical-allocation")]
    public void CompleteStateRejectsMutantsWithIdenticalWallAndEventBytes(string mutation)
    {
        var driver = FullStateReplayConformanceTests.Chow();
        var mutant = ChangshaReplayStateCodec.RoundTrip(driver.State);
        switch (mutation)
        {
            case "base-unit": mutant.BaseUnit = 2; break;
            case "balanced-scores": mutant.CumulativeScores[0]++; mutant.CumulativeScores[1]--; break;
            case "pass-hu": mutant.MissedWinSeats.Add(2); break;
            case "physical-allocation":
                var before = System.Text.Json.JsonSerializer.Deserialize<ChangshaGameState>(
                    driver.Originals![driver.Journal.Count - 1], ChangshaReplayStateCodec.SnapshotJson)!;
                ChangshaGameStateMachine.ResolveClaim(before, 1, Mahjong.Autotable.Api.Tables.TableClaimType.Chow);
                mutant.Hands = before.Hands;
                break;
        }
        Assert.Equal(ChangshaReplayStateCodec.SerializeRecord(driver.State.EventLog),
            ChangshaReplayStateCodec.SerializeRecord(mutant.EventLog));
        Assert.Equal(driver.State.Wall, mutant.Wall);
        var originals = new Dictionary<long, string>(driver.Originals!)
        {
            [driver.Journal.Count] = ChangshaReplayStateCodec.Serialize(mutant)
        };
        var result = ChangshaFullStateReplayVerifier.Verify(driver.Export(), originals);
        Assert.False(result.Success);
        Assert.Equal("OriginalCheckpointMismatch", result.Status);
    }

    [Theory]
    [MemberData(nameof(Properties))]
    public void EveryPersistedRootProperty_IsPartOfTheExactComparison(string property)
    {
        var driver = FullStateReplayConformanceTests.Chow();
        driver.AssertVerified();
        var originals = new Dictionary<long, string>(driver.Originals!);
        var ordinal = driver.Journal.Count;
        var mutant = JsonNode.Parse(originals[ordinal])!.AsObject();
        var value = mutant[property];
        mutant[property] = value switch
        {
            JsonArray array => new JsonArray(array.DeepClone(), JsonValue.Create("different")),
            JsonObject map => new JsonObject { ["original"] = map.DeepClone(), ["changed"] = true },
            null => JsonValue.Create("was-null"),
            _ => JsonValue.Create("different-" + value.ToJsonString())
        };
        originals[ordinal] = mutant.ToJsonString(ChangshaReplayStateCodec.SnapshotJson);
        var result = ChangshaFullStateReplayVerifier.Verify(driver.Export(), originals);
        Assert.False(result.Success);
        Assert.Equal("OriginalCheckpointMismatch", result.Status);
    }

    [Fact]
    public void TypedMetadataOperations_PreserveActualValuesAndVersions()
    {
        var driver = new ReplayTestDriver(23);
        var originalVersion = driver.State.StateVersion;
        var originalSequence = driver.State.EventSequence;
        driver.Do(ReplayOperation.BindHumanSeat, new() { SeatIndex = 0, PlayerId = "synthetic-owner" });
        driver.Do(ReplayOperation.BindBotSeat, new() { SeatIndex = 2 });
        driver.Do(ReplayOperation.ReleaseSeatIdentity, new() { SeatIndex = 2 });
        driver.Do(ReplayOperation.SetDealMode, new() { DealMode = DealMode.Manual });
        driver.Do(ReplayOperation.SetBotStrategyMetadata, new() { Difficulty = "hard" });
        driver.Do(ReplayOperation.SetPublicMetadata, new() { IsPublic = true, PublicName = "  table  " });
        driver.Do(ReplayOperation.SetPublicMetadata, new() { IsPublic = true, PublicName = null });
        Assert.Equal("table", driver.State.PublicName);
        driver.Do(ReplayOperation.TransferHost, new() { PlayerId = "synthetic-owner" });
        driver.Do(ReplayOperation.BindAuthoritativeGameId, new() { GameId = Guid.NewGuid().ToString() });
        Assert.Equal(originalVersion, driver.State.StateVersion);
        Assert.Equal(originalSequence, driver.State.EventSequence);
        driver.RoundTrip();
        driver.AssertVerified();
        driver.Do(ReplayOperation.MarkRemoved);
        driver.AssertVerified(ReplayCutKind.Removed);
        Assert.False(ChangshaFullStateReplayVerifier.Verify(driver.Export(ReplayCutKind.NaturalCompletion)).Success);
    }

    [Fact]
    public void ClaimDeadlineAndRoundTrip_RetainExactOpeningWithoutReopening()
    {
        var driver = new ReplayTestDriver(0);
        driver.StartAndDeal(false);
        driver.Do(ReplayOperation.Discard, new() { SeatIndex = 0, TileId = 19 });
        var window = driver.State.ClaimWindow!;
        var sequence = driver.State.EventLog.Last(e => e.EventType == "claim-window-open").Sequence;
        var originalOpening = window.OpenedAtUnixMs;
        driver.RoundTrip();
        Assert.Equal(originalOpening, driver.State.ClaimWindow!.OpenedAtUnixMs);
        driver.Do(ReplayOperation.PassClaim);
        Assert.Null(driver.State.ClaimWindow);
        driver.Do(ReplayOperation.ObserveClaimWindowSchedule, new()
        {
            Schedule = new()
            {
                OpeningEventSequence = sequence, OpenedAtUnixMs = originalOpening,
                ConfiguredTimeoutMs = 5000, ScheduledDelayMs = 127,
                DeadlineUnixMs = originalOpening + 5000, IsResume = true
            }
        });
        Assert.Null(driver.State.ClaimWindow);
        driver.AssertVerified();
        var envelope = driver.Export();
        var records = envelope.Records.ToArray();
        var schedule = records[^1].Inputs.Schedule!;
        records[^1] = records[^1] with
        {
            Inputs = records[^1].Inputs with { Schedule = schedule with { OpenedAtUnixMs = originalOpening + 1 } }
        };
        Assert.False(ChangshaFullStateReplayVerifier.Verify(envelope with { Records = records }).Success);
    }

    [Fact]
    public void ClonePreflightDoesNotConsumeCommittedClockOrEventFacts()
    {
        var driver = new ReplayTestDriver(0);
        driver.StartAndDeal(false);
        var records = ChangshaReplayStateCodec.SerializeRecord(driver.Export());
        var clone = ChangshaReplayStateCodec.RoundTrip(driver.State);
        ChangshaGameStateMachine.Discard(clone, 0, 19);
        Assert.Equal(records, ChangshaReplayStateCodec.SerializeRecord(driver.Export()));
        driver.Do(ReplayOperation.Discard, new() { SeatIndex = 0, TileId = 19 });
        driver.AssertVerified();
    }

    [Fact]
    public void BackwardUtcClockValuesArePreserved_NotNormalized()
    {
        var ticks = DateTime.UtcNow.Ticks;
        var initialized = ChangshaReplayJournal.Initialize(new() { Seed = 0, BotSeatIndexes = [] },
            () => new ChangshaReplayContext(() => new DateTime(Interlocked.Add(ref ticks, -37), DateTimeKind.Utc)));
        initialized.Journal.Apply(initialized.State, ReplayOperation.StartGame, new());
        Assert.True(initialized.State.EventLog[1].OccurredUtc < initialized.State.EventLog[0].OccurredUtc);
        var result = ChangshaFullStateReplayVerifier.Verify(initialized.Journal.Export(initialized.State, ReplayCutKind.Checkpoint));
        Assert.True(result.Success, result.Detail);
    }
}
