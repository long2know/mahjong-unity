using System.Text.Json;
using System.Net.WebSockets;
using System.Text;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Replay;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Tests.RulesQualification;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Replay;

public class ReplayV3RecoveryIntegrationTests
{
    [Theory]
    [InlineData("JOIN")]
    [InlineData("NEW")]
    public async Task KnownBrokenJournal_RejectsOnlySenderWith1011BeforeJoined(string operation)
    {
        await using var host = new HudsonOwnTurnWsFixture(persist: true);
        await using var bad = await host.OpenHumanTableAsync();
        await using var good = await host.OpenHumanTableAsync();
        var goodBefore = await host.SnapshotAsync(good);
        var badId = Guid.Parse(bad.GameId);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var record = await db.ChangshaGameEvents.FirstAsync(row => row.GameId == badId);
            record.Detail = "{";
            await db.SaveChangesAsync();
        }
        await bad.Peer.DisposeAsync();
        await good.Peer.DisposeAsync();
        await host.RestartAsync();
        using var socket = await host.OpenSocketAsync(
            $"variant=changsha&gameId={bad.RoomId}&bots=false&botCount=0&dealMode=manual");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = operation, gameId = bad.RoomId }),
            WebSocketMessageType.Text, true, deadline.Token);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
            Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
            bytes.Write(buffer, 0, frame.Count);
            Assert.True(bytes.Length <= 4096);
        } while (!frame.EndOfMessage);
        using var first = JsonDocument.Parse(bytes.ToArray());
        Assert.Equal("UPDATE", first.RootElement.GetProperty("type").GetString());
        Assert.False(first.RootElement.GetProperty("full").GetBoolean());
        var rejection = Assert.Single(first.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("actionRejected", rejection[0].GetString());
        Assert.Equal("room-replay-invalid", rejection[2].GetProperty("reason").GetString());
        var close = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(WebSocketCloseStatus.InternalServerError, close.CloseStatus);
        Assert.Equal("room-replay-invalid", close.CloseStatusDescription);
        Assert.False(host.Runtime.TryGetSnapshot(bad.GameId, out _));
        Assert.Null(host.Manager.GetRuntimeGameIdBoundTo(bad.RoomId));
        var unchangedGood = await host.Runtime.TryGetSnapshotCopyAsync(good.GameId);
        Assert.NotNull(unchangedGood);
        Assert.Equal(goodBefore.StateVersion, unchangedGood.StateVersion);
        Assert.Equal(goodBefore.Phase, unchangedGood.Phase);
        using var check = host.Services.CreateScope();
        Assert.Equal(badId, (await check.ServiceProvider.GetRequiredService<AppDbContext>()
            .AutotableRoomBindings.SingleAsync(binding => binding.RoomId == bad.RoomId)).RuntimeGameId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedThirteenTileBoundary_ResumesExactlyOneFrontDraw(bool eager)
    {
        var fault = new ReplayBoundaryWriteFault();
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: fault);
        var (id, room) = await fixture.CreatePublicSeating(19, DealMode.Auto);
        await fixture.BindHumans(id);
        await fixture.Runtime.StartGameAsync(id);
        await fixture.CompleteDeal(id);
        var discard = fixture.State(id).Hands[0].ConcealedTiles.First(tile =>
            new ClaimAdjudicator().GetOpportunities(0, tile, fixture.State(id).Hands).Count == 0);
        fault.BlockAfter(state => state.Phase == ChangshaPhase.AwaitingDiscard
            && state.Hands[state.ActiveSeatIndex].ConcealedTiles.Count
                + 3 * state.Hands[state.ActiveSeatIndex].Melds.Count == 13);
        await fixture.Runtime.DiscardAsync(id, 0, discard);
        var (_, savedJson, _) = await fixture.ReadPrefix(id);
        var saved = JsonSerializer.Deserialize<ChangshaGameState>(savedJson, ChangshaReplayStateCodec.SnapshotJson)!;
        var seat = saved.ActiveSeatIndex;
        Assert.Equal(13, saved.Hands[seat].ConcealedTiles.Count);
        Assert.Equal(14, fixture.State(id).Hands[seat].ConcealedTiles.Count);
        var expectedTile = saved.Wall[0];
        fault.Clear();
        await fixture.Restart(eager);
        Assert.Equal(id, await fixture.Runtime.RestorePublicRoomAsync(room));
        Assert.Equal(savedJson, ChangshaReplayStateCodec.Serialize(fixture.State(id)));
        await fixture.ReconnectHumans(id);
        await fixture.Runtime.ResumeRecoveredPublicRoomAsync(id);
        Assert.Equal(14, fixture.State(id).Hands[seat].ConcealedTiles.Count);
        Assert.Equal(expectedTile, fixture.State(id).Hands[seat].ConcealedTiles[^1]);
        Assert.Equal(saved.EventSequence + 1, fixture.State(id).EventSequence);
        var (envelope, actual, _) = await fixture.ReadPrefix(id);
        var verified = ChangshaFullStateReplayVerifier.Verify(envelope);
        Assert.True(verified.Success, verified.Detail);
        Assert.Equal(actual, ChangshaReplayStateCodec.Serialize(verified.ReconstructedState!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedHuSettlement_WaitsForAcknowledgementsThenRotatesWithoutRepeatingPayments(bool eager)
    {
        var fault = new ReplayBoundaryWriteFault();
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: fault);
        var branch = ReplayReachableBranches.Get("self-draw");
        var (id, room) = await DriveRuntimePrefix(fixture, branch, stopBeforeLast: true);
        var winner = branch.Steps[^1].Inputs.SeatIndex!.Value;
        fault.BlockAfter(state => state.Phase == ChangshaPhase.EndHand && state.CurrentScore is not null);
        await fixture.Runtime.DeclareWinAsync(id, winner);
        var (_, savedJson, _) = await fixture.ReadPrefix(id);
        var saved = JsonSerializer.Deserialize<ChangshaGameState>(savedJson, ChangshaReplayStateCodec.SnapshotJson)!;
        Assert.Equal(ChangshaPhase.EndHand, saved.Phase);
        Assert.NotNull(saved.CurrentScore);
        var scores = new Dictionary<int, int>(saved.CumulativeScores);
        var rotations = saved.EventLog.Count(e => e.EventType == "banker-rotated");
        fault.Clear();
        await fixture.Restart(eager);
        Assert.Equal(id, await fixture.Runtime.RestorePublicRoomAsync(room));
        Assert.Equal(savedJson, ChangshaReplayStateCodec.Serialize(fixture.State(id)));
        await fixture.ReconnectHumans(id);
        await fixture.Runtime.ResumeRecoveredPublicRoomAsync(id);
        Assert.Equal(ChangshaPhase.EndHand, fixture.State(id).Phase);
        Assert.Equal(saved.HandNumber, fixture.State(id).HandNumber);
        await HandResultTestActions.AcknowledgeAll(fixture, id);
        Assert.Equal(saved.HandNumber + 1, fixture.State(id).HandNumber);
        Assert.Equal(winner, fixture.State(id).DealerSeatIndex);
        Assert.Equal(scores.OrderBy(p => p.Key), fixture.State(id).CumulativeScores.OrderBy(p => p.Key));
        Assert.Equal(rotations + 1, fixture.State(id).EventLog.Count(e => e.EventType == "banker-rotated"));
        var (envelope, actual, _) = await fixture.ReadPrefix(id);
        var verified = ChangshaFullStateReplayVerifier.Verify(envelope);
        Assert.True(verified.Success, $"{verified.Status}: {verified.Detail}");
        Assert.Equal(actual, ChangshaReplayStateCodec.Serialize(verified.ReconstructedState!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RecordedRobbingWindow_RetainsDeadlineAndResolvesAfterRestart(bool eager, bool hu)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var (id, room) = await DriveRuntimePrefix(fixture, ReplayReachableBranches.Get("robbing-window"));
        var before = fixture.State(id);
        Assert.True(before.ClaimWindow!.IsKongRobbing);
        var opening = before.ClaimWindow.OpenedAtUnixMs;
        var claimant = before.ClaimWindow.Opportunities[0].SeatIndex;
        var declarer = before.ClaimWindow.KongDeclarerSeatIndex!.Value;
        var original = ChangshaReplayStateCodec.Serialize(before);
        await fixture.Restart(eager);
        Assert.Equal(id, await fixture.Runtime.RestorePublicRoomAsync(room));
        Assert.Equal(original, ChangshaReplayStateCodec.Serialize(fixture.State(id)));
        Assert.Equal(opening, fixture.State(id).ClaimWindow!.OpenedAtUnixMs);
        await fixture.ReconnectHumans(id);
        await fixture.Runtime.ResumeRecoveredPublicRoomAsync(id);
        if (hu)
        {
            await fixture.Runtime.ClaimAsync(id, claimant, "Hu", null);
            if (fixture.State(id).Phase == ChangshaPhase.AwaitingClaim)
                await fixture.PassRemaining(id, claimant);
            Assert.Contains(fixture.State(id).EventLog, entry => entry.EventType == "win-declared"
                && entry.Detail.Contains("robbingKong", StringComparison.Ordinal));
        }
        else
        {
            await fixture.PassRemaining(id);
            Assert.Contains(fixture.State(id).Hands[declarer].Melds, meld => meld.Kind == MeldKind.AddedKong);
        }
        var (envelope, actual, _) = await fixture.ReadPrefix(id);
        Assert.Contains(envelope.Records, record => record.Operation == ReplayOperation.ObserveClaimWindowSchedule
            && record.Inputs.Schedule!.IsResume && record.Inputs.Schedule.OpenedAtUnixMs == opening);
        var verified = ChangshaFullStateReplayVerifier.Verify(envelope);
        Assert.True(verified.Success, $"{verified.Status}: {verified.Detail}");
        Assert.Equal(actual, ChangshaReplayStateCodec.Serialize(verified.ReconstructedState!));
    }

    [Fact]
    public async Task InitialPublicBindingSnapshotAndJournal_ShareTheFirstCommit()
    {
        var observer = new ReplayAtomicWriteObserver();
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: observer);
        var (id, room) = await fixture.CreatePublicSeating(7, DealMode.Manual, baseUnit: 4, difficulty: "hard");
        Assert.Contains(observer.AddedTypes, types => types.Contains(nameof(ChangshaGame))
            && types.Contains(nameof(AutotableRoomBinding)) && types.Contains(nameof(ChangshaGameEvent)));
        var (envelope, original, rows) = await fixture.ReadPrefix(id);
        Assert.Single(rows);
        using (var scope = fixture.Services.CreateScope())
        {
            var gid = Guid.Parse(id);
            Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .ChangshaGames.SingleAsync(game => game.Id == gid)).StateVersion);
        }
        Assert.NotNull(envelope.Records[0].Inputs.Initialization!.EngineIdentity);
        Assert.Equal(ChangshaReplayEnvelope.TransitionFormatVersion, envelope.Records[0].FormatVersion);
        Assert.Equal(id, await fixture.Runtime.RestorePublicRoomAsync(room));
        var result = ChangshaFullStateReplayVerifier.Verify(envelope);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(original, ChangshaReplayStateCodec.Serialize(result.ReconstructedState!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialCommitFailure_DoesNotPublishPartialOrReplacementRuntime(bool afterCommit)
    {
        var observer = new ReplayAtomicWriteObserver();
        observer.Arm(afterCommit);
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: observer);
        var room = $"d17-atomic-{Guid.NewGuid():N}";
        var failure = await Assert.ThrowsAsync<PublicRoomRecoveryException>(() => fixture.Runtime.CreateGameAsync(
            7, [], "replay-owner", null, publicRoom: new(room, DealMode.Manual, "medium")));
        Assert.Equal("room-persistence-failed", failure.Reason);
        Assert.Equal(0, fixture.Runtime.GameCount);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(afterCommit ? 1 : 0, await db.ChangshaGames.CountAsync());
        Assert.Equal(afterCommit ? 1 : 0, await db.AutotableRoomBindings.CountAsync());
        Assert.Equal(afterCommit ? 1 : 0, await db.ChangshaGameEvents.CountAsync());
        if (afterCommit)
        {
            var binding = await db.AutotableRoomBindings.SingleAsync();
            var restored = await fixture.Runtime.RestorePublicRoomAsync(room);
            Assert.Equal(binding.RuntimeGameId.ToString(), restored);
            fixture.Track(restored!);
            var (envelope, original, _) = await fixture.ReadPrefix(restored!);
            var verified = ChangshaFullStateReplayVerifier.Verify(envelope);
            Assert.True(verified.Success, verified.Detail);
            Assert.Equal(original, ChangshaReplayStateCodec.Serialize(verified.ReconstructedState!));
        }
        else Assert.Null(await fixture.Runtime.RestorePublicRoomAsync(room));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EagerOrLazyRestart_FourHandKongTapeRemainsFullyReconstructible(bool eager)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var (id, room) = await fixture.CreatePublicSeating(7, DealMode.Manual, difficulty: "hard");
        await fixture.BindHumans(id);
        await fixture.Runtime.StartGameAsync(id);
        await fixture.CompleteDeal(id);
        await fixture.Runtime.DiscardAsync(id, 0, 33);
        await fixture.Runtime.ClaimAsync(id, 1, "Kong", null);
        var original = ChangshaReplayStateCodec.Serialize(fixture.State(id));
        var oldRecords = (await fixture.ReadPrefix(id)).Rows.Select(row => row.Detail).ToArray();
        await fixture.Restart(eager);
        Assert.Equal(id, await fixture.Runtime.RestorePublicRoomAsync(room));
        Assert.Equal(original, ChangshaReplayStateCodec.Serialize(fixture.State(id)));
        var (restored, persisted, restoredRows) = await fixture.ReadPrefix(id);
        Assert.Equal(original, persisted);
        Assert.Equal(oldRecords, restoredRows.Take(oldRecords.Length).Select(row => row.Detail));
        Assert.Equal(ReplayOperation.SnapshotRoundTrip, restored.Records[^2].Operation);
        Assert.Equal(ReplayOperation.BindAuthoritativeGameId, restored.Records[^1].Operation);
        Assert.True(ChangshaFullStateReplayVerifier.Verify(restored).Success);
        var rejection = await Assert.ThrowsAsync<RoomAdmissionException>(() =>
            fixture.Runtime.TakeSeatAsync(id, "wrong-owner", "wrong-connection", 1));
        Assert.Equal("room-not-seating", rejection.Reason);
        Assert.Equal(original, ChangshaReplayStateCodec.Serialize(fixture.State(id)));
        Assert.Equal(restoredRows.Select(row => row.Detail),
            (await fixture.ReadPrefix(id)).Rows.Select(row => row.Detail));
        await fixture.ReconnectHumans(id);
        var count = fixture.State(id).Hands[1].ConcealedTiles.Count;
        var version = fixture.State(id).StateVersion;
        await fixture.Runtime.ResumeRecoveredPublicRoomAsync(id);
        Assert.Equal(count, fixture.State(id).Hands[1].ConcealedTiles.Count);
        Assert.Equal(version, fixture.State(id).StateVersion);
        await fixture.FinishGame(id);
        Assert.Equal(703, fixture.State(id).EventLog.Count);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gid = Guid.Parse(id);
        var replay = await db.ChangshaGameReplays.AsNoTracking().SingleAsync(row => row.GameId == gid);
        var verified = ChangshaFullStateReplayVerifier.VerifyJson(replay.EventsJson);
        Assert.True(verified.Success, $"{verified.Status} {verified.RecordSequence}: {verified.Detail}");
        var final = ChangshaReplayStateCodec.Serialize(fixture.State(id));
        Assert.Equal(final, ChangshaReplayStateCodec.Serialize(verified.ReconstructedState!));
        using var response = await fixture.Client.GetAsync($"/api/games/{id}/replay");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var wire = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(wire);
        Assert.Equal(Enumerable.Range(1, 703).Select(i => (long)i),
            body.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("sequence").GetInt64()));
        ReplayRuntimeFixture.SaveEvidence(eager ? "restart-eager" : "restart-lazy", replay.EventsJson, final, wire);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualPickupRestart_PreservesExactCursorConfigurationAndVersion(bool eager)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var (id, room) = await fixture.CreatePublicSeating(19, DealMode.Manual, cap: 8, baseUnit: 4, difficulty: "master");
        await fixture.BindHumans(id);
        await fixture.Runtime.StartGameAsync(id);
        await fixture.Runtime.RollDiceAsync(id, 0);
        for (var pickup = 0; pickup < 5; pickup++)
            await fixture.Runtime.TakeTilesFromWallAsync(id, fixture.State(id).PickupSeatIndex!.Value, 4);
        var original = ChangshaReplayStateCodec.Serialize(fixture.State(id));
        await fixture.Restart(eager);
        Assert.Equal(id, await fixture.Runtime.RestorePublicRoomAsync(room));
        Assert.Equal(original, ChangshaReplayStateCodec.Serialize(fixture.State(id)));
        Assert.Equal(8, fixture.State(id).MaxHands);
        Assert.Equal(4, fixture.State(id).BaseUnit);
        Assert.Equal("master", fixture.State(id).BotDifficulty);
        await fixture.ReconnectHumans(id);
        await fixture.Runtime.ResumeRecoveredPublicRoomAsync(id);
        var stale = fixture.State(id).StateVersion - 1;
        await Assert.ThrowsAsync<ChangshaConcurrencyException>(() => fixture.Runtime.TakeTilesFromWallAsync(
            id, fixture.State(id).PickupSeatIndex!.Value, 4, expectedVersion: stale));
        await fixture.CompleteDeal(id);
        var (envelope, actual, _) = await fixture.ReadPrefix(id);
        var verified = ChangshaFullStateReplayVerifier.Verify(envelope);
        Assert.True(verified.Success, verified.Detail);
        Assert.Equal(actual, ChangshaReplayStateCodec.Serialize(verified.ReconstructedState!));
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "ordinal")]
    [InlineData(true, "ordinal")]
    [InlineData(false, "json")]
    [InlineData(true, "json")]
    [InlineData(false, "clock")]
    [InlineData(true, "clock")]
    [InlineData(false, "snapshot")]
    [InlineData(true, "snapshot")]
    [InlineData(false, "identity")]
    [InlineData(true, "identity")]
    public async Task BrokenCurrentJournal_IsRejectedBeforeRecoveryPublication(bool eager, string mutation)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var (id, room) = await fixture.CreatePublicSeating(23);
        await fixture.BindHumans(id);
        await fixture.Runtime.StartGameAsync(id);
        await fixture.Runtime.RollDiceAsync(id, 0);
        var gid = Guid.Parse(id);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rows = await db.ChangshaGameEvents.Where(row => row.GameId == gid).OrderBy(row => row.Sequence).ToArrayAsync();
            var row = rows[1];
            var record = JsonSerializer.Deserialize<ReplayTransition>(row.Detail, ChangshaReplayStateCodec.RecordJson)!;
            switch (mutation)
            {
                case "missing": db.ChangshaGameEvents.Remove(row); break;
                case "ordinal": row.Detail = ChangshaReplayStateCodec.SerializeRecord(record with { RecordSequence = 1 }); break;
                case "json": row.Detail = "{"; break;
                case "identity":
                    var first = JsonSerializer.Deserialize<ReplayTransition>(rows[0].Detail, ChangshaReplayStateCodec.RecordJson)!;
                    rows[0].Detail = ChangshaReplayStateCodec.SerializeRecord(first with
                    {
                        Inputs = first.Inputs with { Initialization = first.Inputs.Initialization! with { EngineIdentity = null } }
                    });
                    break;
                case "clock":
                    var timedRow = rows.First(r => JsonSerializer.Deserialize<ReplayTransition>(r.Detail,
                        ChangshaReplayStateCodec.RecordJson)!.StateClockFacts.Count > 0);
                    var timed = JsonSerializer.Deserialize<ReplayTransition>(timedRow.Detail, ChangshaReplayStateCodec.RecordJson)!;
                    var facts = timed.StateClockFacts.ToArray();
                    facts[0] = facts[0] with { Value = facts[0].Value + 1 };
                    timedRow.Detail = ChangshaReplayStateCodec.SerializeRecord(timed with { StateClockFacts = facts });
                    break;
                case "snapshot":
                    var game = await db.ChangshaGames.SingleAsync(g => g.Id == gid);
                    var state = JsonSerializer.Deserialize<ChangshaGameState>(game.StateJson, ChangshaReplayStateCodec.SnapshotJson)!;
                    state.CumulativeScores[0]++;
                    state.CumulativeScores[1]--;
                    game.StateJson = ChangshaReplayStateCodec.Serialize(state);
                    break;
            }
            await db.SaveChangesAsync();
        }
        await fixture.Restart(eager);
        Assert.Equal(0, fixture.Runtime.GameCount);
        var failure = await Assert.ThrowsAsync<PublicRoomRecoveryException>(() => fixture.Runtime.RestorePublicRoomAsync(room));
        Assert.Equal("room-replay-invalid", failure.Reason);
        Assert.Equal(0, fixture.Runtime.GameCount);
        using var check = fixture.Services.CreateScope();
        Assert.Equal(gid, (await check.ServiceProvider.GetRequiredService<AppDbContext>()
            .AutotableRoomBindings.SingleAsync(binding => binding.RoomId == room)).RuntimeGameId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryJournalWriteFailure_RemainsUnpublishedAndCanResumeTheCommittedPrefix(bool afterCommit)
    {
        var fault = new ReplayAtomicWriteObserver();
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: fault);
        var (id, room) = await fixture.CreatePublicSeating(17);
        await fixture.Restart(eager: false);
        fault.Arm(afterCommit);
        var failure = await Assert.ThrowsAsync<PublicRoomRecoveryException>(() => fixture.Runtime.RestorePublicRoomAsync(room));
        Assert.Equal("room-replay-persistence-failed", failure.Reason);
        Assert.Equal(0, fixture.Runtime.GameCount);
        Assert.Equal(id, await fixture.Runtime.RestorePublicRoomAsync(room));
        var (envelope, original, _) = await fixture.ReadPrefix(id);
        var result = ChangshaFullStateReplayVerifier.Verify(envelope);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(original, ChangshaReplayStateCodec.Serialize(result.ReconstructedState!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LegitimateLegacyRecovery_RemainsPlayableButExplicitlyUnverifiable(bool eager, bool oldFormat)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var (id, room) = await fixture.CreatePublicSeating(7, DealMode.Auto, cap: 1);
        await fixture.BindHumans(id);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var gid = Guid.Parse(id);
            var records = await db.ChangshaGameEvents.Where(row => row.GameId == gid).ToArrayAsync();
            if (!oldFormat) db.ChangshaGameEvents.RemoveRange(records);
            else
                foreach (var row in records)
                {
                    var record = JsonSerializer.Deserialize<ReplayTransition>(row.Detail, ChangshaReplayStateCodec.RecordJson)!;
                    row.Detail = ChangshaReplayStateCodec.SerializeRecord(record with { FormatVersion = 1 });
                }
            await db.SaveChangesAsync();
        }
        await fixture.Restart(eager);
        Assert.Equal(id, await fixture.Runtime.RestorePublicRoomAsync(room));
        Assert.Null(fixture.Instances()[id].ReplayJournal);
        await fixture.ReconnectHumans(id);
        await fixture.Runtime.StartGameAsync(id);
        await fixture.CompleteDeal(id);
        await fixture.FinishGame(id);
        using var check = fixture.Services.CreateScope();
        var gameId = Guid.Parse(id);
        var replay = await check.ServiceProvider.GetRequiredService<AppDbContext>()
            .ChangshaGameReplays.SingleAsync(row => row.GameId == gameId);
        Assert.Equal(2, replay.SchemaVersion);
        Assert.Equal("LegacyUnverifiable", ChangshaFullStateReplayVerifier.VerifyJson(replay.EventsJson).Status);
    }

    internal static async Task<(string Id, string Room)> DriveRuntimePrefix(
        ReplayRuntimeFixture fixture, ReplayReachableBranch branch, bool stopBeforeLast = false, int cap = 4,
        bool aliasedRoom = true)
    {
        (string Id, string Room) created;
        if (aliasedRoom)
            created = await fixture.CreatePublicSeating(branch.Seed, DealMode.Auto, cap: cap);
        else
        {
            var nativeId = await fixture.CreateSeating(branch.Seed, cap: cap);
            created = (nativeId, nativeId);
        }
        var id = created.Id;
        await fixture.BindHumans(id);
        await fixture.Runtime.StartGameAsync(id);
        await fixture.CompleteDeal(id);
        foreach (var step in branch.Steps.Take(branch.Steps.Count - (stopBeforeLast ? 1 : 0)))
        {
            if (fixture.State(id).Phase == ChangshaPhase.EndHand)
            {
                await HandResultTestActions.AcknowledgeAll(fixture, id);
                await fixture.CompleteDeal(id);
            }
            var input = step.Inputs;
            switch (step.Operation)
            {
                case ReplayOperation.Discard:
                    await fixture.Runtime.DiscardAsync(id, input.SeatIndex!.Value, input.TileId!.Value);
                    break;
                case ReplayOperation.PassClaim:
                    await fixture.PassRemaining(id);
                    break;
                case ReplayOperation.ResolveClaim:
                    await fixture.Runtime.ClaimAsync(id, input.SeatIndex!.Value, input.ClaimType!.Value.ToString(), input.ChosenTileIds);
                    if (fixture.State(id).Phase == ChangshaPhase.AwaitingClaim)
                        await fixture.PassRemaining(id, input.SeatIndex);
                    break;
                case ReplayOperation.DeclareSelfDrawWin:
                    await fixture.Runtime.DeclareWinAsync(id, input.SeatIndex!.Value);
                    break;
                case ReplayOperation.DeclareConcealedKong:
                    var tiles = fixture.State(id).Hands[input.SeatIndex!.Value].ConcealedTiles
                        .Where(tile => tile / 4 == input.LogicalTile).ToArray();
                    await fixture.Runtime.DeclareKongAsync(id, input.SeatIndex.Value, tiles, requestedKind: MeldKind.ConcealedKong);
                    break;
                case ReplayOperation.DeclareAddedKong:
                    await fixture.Runtime.DeclareKongAsync(id, input.SeatIndex!.Value, [input.TileId!.Value], requestedKind: MeldKind.AddedKong);
                    break;
            }
            if (fixture.State(id).EventLog[^1].EventType == "tiles-dealt")
                await fixture.CompleteDeal(id);
        }
        return created;
    }
}

internal sealed class ReplayAtomicWriteObserver : SaveChangesInterceptor
{
    private int _failure;
    internal List<HashSet<string>> AddedTypes { get; } = [];
    internal void Arm(bool afterCommit) => _failure = afterCommit ? 2 : 1;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        AddedTypes.Add(eventData.Context!.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity.GetType().Name).ToHashSet());
        if (Interlocked.CompareExchange(ref _failure, 0, 1) == 1)
            throw new DbUpdateException("Synthetic atomic-write failure before commit.");
        return ValueTask.FromResult(result);
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
        int result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _failure, 0, 2) == 2)
            throw new DbUpdateException("Synthetic atomic-write lost acknowledgement.");
        return ValueTask.FromResult(result);
    }
}

internal sealed class ReplayBoundaryWriteFault : SaveChangesInterceptor
{
    private Func<ChangshaGameState, bool>? _boundary;
    private bool _block;
    internal void BlockAfter(Func<ChangshaGameState, bool> boundary) => _boundary = boundary;
    internal void Clear() { _boundary = null; _block = false; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (_block) throw new DbUpdateException("Synthetic crash boundary rejects later writes.");
        return ValueTask.FromResult(result);
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
        int result, CancellationToken cancellationToken = default)
    {
        if (_boundary is not null)
            foreach (var entry in eventData.Context!.ChangeTracker.Entries<ChangshaGame>())
            {
                var state = JsonSerializer.Deserialize<ChangshaGameState>(entry.Entity.StateJson, ChangshaReplayStateCodec.SnapshotJson)!;
                if (_boundary(state)) _block = true;
            }
        return ValueTask.FromResult(result);
    }
}
