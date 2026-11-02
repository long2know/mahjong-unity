using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Replay;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Tables;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Tests.Replay;

public class ReplayV3PersistenceAndOrderingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ReachableAddedKong_ZeroOrFinalReplacement_ReconstructsExactly(int remaining)
    {
        var fixture = ReplayReachableBranches.Get(remaining == 0 ? "zero-replacement-added" : "added-kong");
        var driver = new ReplayTestDriver(fixture.Seed, captureOriginals: false);
        var declaration = fixture.Steps.Select((value, index) => (value, index))
            .Last(entry => entry.value.Operation == ReplayOperation.DeclareAddedKong);
        foreach (var (operation, input) in fixture.Steps.Take(declaration.index)) driver.Do(operation, input);
        var declarer = declaration.value.Inputs.SeatIndex!.Value;
        var fourth = declaration.value.Inputs.TileId!.Value;
        for (var step = 0; step < 256 && remaining == 1; step++)
        {
            var state = driver.State;
            if (state.Wall.Count == 1 && state.ActiveSeatIndex == declarer
                && state.Phase == ChangshaPhase.AwaitingDiscard
                && state.Hands[declarer].ConcealedTiles.Count + 3 * state.Hands[declarer].Melds.Count == 14)
                break;
            if (state.Phase == ChangshaPhase.AwaitingClaim) driver.Do(ReplayOperation.PassClaim);
            else
            {
                Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
                var seat = state.ActiveSeatIndex;
                var hand = state.Hands[seat];
                if (hand.ConcealedTiles.Count + 3 * hand.Melds.Count == 13) driver.Do(ReplayOperation.DrawTile);
                else
                {
                    var tile = ChangshaBotPolicy.SelectDiscardTile(hand);
                    if (seat == declarer && tile == fourth) tile = hand.ConcealedTiles.First(t => t != fourth);
                    driver.Do(ReplayOperation.Discard, new() { SeatIndex = seat, TileId = tile });
                }
            }
        }
        Assert.Equal(remaining, driver.State.Wall.Count);
        Assert.Equal(declarer, driver.State.ActiveSeatIndex);
        var previousBackDraws = driver.State.WallBackDrawn;
        var replacement = driver.State.Wall.LastOrDefault(-1);
        driver.Do(ReplayOperation.DeclareAddedKong, new() { SeatIndex = declarer, TileId = fourth });
        if (driver.State.Phase == ChangshaPhase.AwaitingClaim) driver.Do(ReplayOperation.PassClaim);
        Assert.Empty(driver.State.Wall);
        Assert.Equal(previousBackDraws + remaining, driver.State.WallBackDrawn);
        Assert.Equal(remaining == 0 ? ChangshaPhase.WallExhausted : ChangshaPhase.AwaitingDiscard, driver.State.Phase);
        if (remaining == 1)
        {
            Assert.Equal(replacement, driver.State.Hands[declarer].ConcealedTiles[^1]);
            Assert.True(driver.State.LastDrawWasKongReplacement);
            Assert.Equal(declarer, driver.State.LastDrawSeatIndex);
        }
        driver.AssertConservation();
        driver.AssertVerified();
    }

    [Theory]
    [InlineData("easy", "easy")]
    [InlineData("medium", "medium")]
    [InlineData("hard", "hard")]
    [InlineData("master", "master")]
    [InlineData("unknown", "medium")]
    public async Task ActualResolvedBotMetadata_ReconstructsWithoutStrategyExecution(string requested, string expected)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var id = await fixture.CreateSeating(13);
        await fixture.Runtime.SetBotStrategyAsync(id, requested);
        Assert.Equal(expected, fixture.State(id).BotDifficulty);
        var (envelope, original, _) = await fixture.ReadPrefix(id);
        var result = ChangshaFullStateReplayVerifier.Verify(envelope);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(original, ChangshaReplayStateCodec.Serialize(result.ReconstructedState!));
    }

    [Fact]
    public async Task CorruptCommittedPrefix_DoesNotPublishACompleteReplay()
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var id = await fixture.CreateHumans(7, manual: false, cap: 1);
        var gid = Guid.Parse(id);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.ChangshaGameEvents.SingleAsync(e => e.GameId == gid && e.Sequence == 1);
            var record = JsonSerializer.Deserialize<ReplayTransition>(row.Detail, ChangshaReplayStateCodec.RecordJson)!;
            row.Detail = ChangshaReplayStateCodec.SerializeRecord(record with
            {
                Inputs = record.Inputs with { Initialization = record.Inputs.Initialization! with { Seed = 999 } }
            });
            await db.SaveChangesAsync();
        }
        await fixture.FinishGame(id);
        using var check = fixture.Services.CreateScope();
        var database = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await database.ChangshaGameReplays.AnyAsync(r => r.GameId == gid));
        var (invalid, _, _) = await fixture.ReadPrefix(id);
        Assert.False(ChangshaFullStateReplayVerifier.Verify(invalid).Success);
    }

    [Fact]
    public async Task DisabledSnapshots_StillPersistsACompleteExportWithoutCreatingSnapshotRows()
    {
        await using var fixture = await ReplayRuntimeFixture.Create(persistSnapshots: false);
        var id = await fixture.CreateHumans(7, manual: false, cap: 1);
        await fixture.FinishGame(id);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gid = Guid.Parse(id);
        Assert.False(await db.ChangshaGames.AnyAsync(g => g.Id == gid));
        Assert.False(await db.ChangshaGameEvents.AnyAsync(e => e.GameId == gid));
        var replay = await db.ChangshaGameReplays.SingleAsync(r => r.GameId == gid);
        var result = ChangshaFullStateReplayVerifier.VerifyJson(replay.EventsJson);
        Assert.True(result.Success, result.Detail);
        Assert.Equal(ChangshaReplayStateCodec.Serialize(fixture.State(id)),
            ChangshaReplayStateCodec.Serialize(result.ReconstructedState!));
    }

    [Fact]
    public void GeneratedProviderMigrations_WidenOnlyReplayDetail_AndRefuseUnsafeDown()
    {
        var pg = new Mahjong.Autotable.Api.Persistence.Migrations.Postgres.WidenChangshaReplayDetail();
        var sql = new Mahjong.Autotable.Api.Persistence.Migrations.SqlServer.WidenChangshaReplayDetail();
        var sqlite = new Mahjong.Autotable.Api.Persistence.Migrations.Sqlite.WidenChangshaReplayDetail();
        foreach (var migration in new Microsoft.EntityFrameworkCore.Migrations.Migration[] { pg, sql })
        {
            var widening = Assert.IsType<Microsoft.EntityFrameworkCore.Migrations.Operations.AlterColumnOperation>(
                Assert.Single(migration.UpOperations));
            Assert.Equal("ChangshaGameEvents", widening.Table);
            Assert.Equal("Detail", widening.Name);
            Assert.Null(widening.MaxLength);
            Assert.Equal(256, widening.OldColumn.MaxLength);
            Assert.IsType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>(migration.DownOperations[0]);
            Assert.IsType<Microsoft.EntityFrameworkCore.Migrations.Operations.AlterColumnOperation>(migration.DownOperations[1]);
        }
        var pgGuard = ((Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation)pg.DownOperations[0]).Sql;
        Assert.Contains("ACCESS EXCLUSIVE", pgGuard);
        Assert.Contains("length(\"Detail\") > 256", pgGuard);
        Assert.Contains("RAISE EXCEPTION", pgGuard);
        var sqlGuard = ((Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation)sql.DownOperations[0]).Sql;
        Assert.Contains("TABLOCKX, HOLDLOCK", sqlGuard);
        Assert.Contains("DATALENGTH([Detail]) > 512", sqlGuard);
        Assert.Contains("THROW", sqlGuard);
        Assert.Empty(sqlite.UpOperations);
        Assert.Empty(sqlite.DownOperations);
    }

    [Theory]
    [InlineData("""{"schemaVersion":3,"events":[{"turn":1}]}""")]
    [InlineData("""{"schemaVersion":3,"events":[{"sequence":1},{"sequence":1}]}""")]
    [InlineData("""{"schemaVersion":3,"events":[{"sequence":"not-a-number"}]}""")]
    [InlineData("""{"schemaVersion":3,"events":[{"sequence":2}]}""")]
    [InlineData("""{"schemaVersion":3,"events":{}}""")]
    [InlineData("{")]
    public async Task MalformedV3HttpReadback_IsExplicitlyRejected(string payload)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var id = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChangshaGameReplays.Add(new() { GameId = id, SchemaVersion = 3, EventsJson = payload, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        using var response = await fixture.Client.GetAsync($"/api/games/{id}/replay");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LegacyFormats_MetadataAndStatusCodesRemainCompatible(int version)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var id = Guid.NewGuid();
        const string events = """[{"turn":8,"action":"draw-hand","timestampUtc":"2026-09-14T00:00:00Z"},{"turn":1,"action":"dice-rolled","timestampUtc":"2026-09-14T00:00:01Z"}]""";
        var payload = version == 1 ? events : "{\"schemaVersion\":2,\"events\":" + events + "}";
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChangshaGameReplays.Add(new() { GameId = id, SchemaVersion = version, EventsJson = payload, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        using var response = await fixture.Client.GetAsync($"/api/games/{id}/replay");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(version, body.RootElement.GetProperty("schemaVersion").GetInt32());
        var entries = body.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(new[] { 8, 1 }, entries.Select(e => e.GetProperty("turn").GetInt32()));
        Assert.All(entries, e =>
        {
            Assert.Equal("unknown", e.GetProperty("source").GetString());
            Assert.Equal(JsonValueKind.Null, e.GetProperty("durationMs").ValueKind);
            Assert.Equal(JsonValueKind.Null, e.GetProperty("debugScore").ValueKind);
        });
        Assert.Equal("LegacyUnverifiable", ChangshaFullStateReplayVerifier.VerifyJson(payload).Status);
        using var missing = await fixture.Client.GetAsync($"/api/games/{Guid.NewGuid()}/replay");
        using var invalid = await fixture.Client.GetAsync("/api/games/not-a-guid/replay");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Theory]
    [InlineData("self-draw")]
    [InlineData("discard-hu")]
    [InlineData("concealed-kong")]
    [InlineData("added-kong")]
    [InlineData("kong-replacement-hu")]
    public void ReachableAcceptedBranches_ReconstructFullStateAndSettlement(string branch)
    {
        var fixture = ReplayReachableBranches.Get(branch);
        var driver = fixture.Record(baseUnit: 4);
        driver.AssertVerified();
        if (driver.State.Phase == ChangshaPhase.Scoring)
        {
            driver.Do(ReplayOperation.Score);
            Assert.NotNull(driver.State.CurrentScore);
            Assert.NotEmpty(driver.State.CurrentScore.Payments);
            Assert.All(driver.State.CurrentScore.Payments, payment => Assert.Equal(0, payment.Amount % 4));
            Assert.Equal(0, driver.State.CumulativeScores.Values.Sum());
            driver.AssertVerified();
            driver.Do(ReplayOperation.RotateBanker);
            driver.AssertVerified();
        }
    }

    [Fact]
    public void ReachablePassHu_LockoutAndOwnFrontDrawExpiry_ReconstructExactly()
    {
        var fixture = ReplayReachableBranches.Get("discard-hu");
        var driver = new ReplayTestDriver(fixture.Seed, captureOriginals: false);
        foreach (var (operation, inputs) in fixture.Steps.Take(fixture.Steps.Count - 1))
            driver.Do(operation, inputs);
        var seat = fixture.Steps[^1].Inputs.SeatIndex!.Value;
        driver.Do(ReplayOperation.PassClaim);
        Assert.Contains(seat, driver.State.MissedWinSeats);
        driver.AssertVerified();
        var drew = false;
        for (var turn = 0; turn < 32 && !drew; turn++)
        {
            Assert.Equal(ChangshaPhase.AwaitingDiscard, driver.State.Phase);
            var active = driver.State.ActiveSeatIndex;
            driver.Do(ReplayOperation.DrawTile);
            if (active == seat)
            {
                drew = true;
                Assert.DoesNotContain(seat, driver.State.MissedWinSeats);
            }
            else
            {
                driver.Do(ReplayOperation.Discard, new() { SeatIndex = active, TileId = driver.State.Hands[active].ConcealedTiles[0] });
                if (driver.State.Phase == ChangshaPhase.AwaitingClaim) driver.Do(ReplayOperation.PassClaim);
            }
        }
        Assert.True(drew);
        driver.AssertVerified();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReachableRobbingWindow_AllPassOrHu_ReconstructsExactly(bool hu)
    {
        var fixture = ReplayReachableBranches.Get("robbing-window");
        var driver = fixture.Record();
        Assert.True(driver.State.ClaimWindow!.IsKongRobbing);
        driver.AssertVerified();
        if (hu)
        {
            var winner = driver.State.ClaimWindow.Opportunities.First(o => o.ClaimType == Mahjong.Autotable.Api.Tables.TableClaimType.Hu);
            driver.Do(ReplayOperation.ResolveClaim, new()
            {
                SeatIndex = winner.SeatIndex, ClaimType = Mahjong.Autotable.Api.Tables.TableClaimType.Hu
            });
            Assert.Equal(WinMethod.RobbingKong, driver.State.CurrentWin!.Method);
            driver.AssertVerified();
            driver.Do(ReplayOperation.Score);
            driver.AssertVerified();
        }
        else
        {
            var declarer = driver.State.ClaimWindow.KongDeclarerSeatIndex!.Value;
            driver.Do(ReplayOperation.PassClaim);
            Assert.Contains(driver.State.Hands[declarer].Melds, m => m.Kind == MeldKind.AddedKong);
            driver.AssertVerified();
        }
        driver.AssertConservation();
    }

    [Fact]
    public async Task ActualMetadataCallers_PersistFaithfulValuesWithoutInventingEvents()
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var id = await fixture.CreateSeating(23);
        var runtime = fixture.Runtime;
        await runtime.TakeSeatAsync(id, "replay-owner", "owner-connection", 0);
        await runtime.FillEmptySeatsWithBotsAsync(id);
        await runtime.ReleaseSeatAsync(id, "replay-owner", "owner-connection");
        Assert.False(await runtime.ReconnectAsync(id, 0, "replay-owner", "released-owner-connection"));
        await runtime.TakeSeatAsync(id, "replay-owner", "owner-connection", 0);
        await runtime.HandleDisconnectAsync("replay-owner", "owner-connection");
        var stateChanges = 0;
        runtime.StateChanged += (_, _) => stateChanges++;
        Assert.True(await runtime.ReconnectAsync(id, 0, "replay-owner", "owner-reconnected"));
        await runtime.ApplyDealModeAsync(id, DealMode.Manual);
        Assert.Equal(0, stateChanges);
        await runtime.SetBotStrategyAsync(id, "hard");
        await runtime.SetGamePublicAsync(id, "replay-owner", true, "  replay table  ");
        await runtime.SetGamePublicAsync(id, "replay-owner", true, null);
        Assert.Equal("replay table", fixture.State(id).PublicName);
        await runtime.TakeSeatAsync(id, "successor", "successor-connection", 1);
        var beforeTransfer = stateChanges;
        await runtime.HandleDisconnectAsync("replay-owner", "owner-reconnected");
        Assert.Equal(beforeTransfer, stateChanges);
        Assert.Equal("successor", fixture.State(id).CreatorPlayerId);
        await runtime.SetGamePublicAsync(id, "successor", false, null);
        Assert.Equal(0, fixture.State(id).StateVersion);
        Assert.Equal(1, fixture.State(id).EventSequence);
        var (envelope, original, _) = await fixture.ReadPrefix(id);
        var result = ChangshaFullStateReplayVerifier.Verify(envelope);
        Assert.True(result.Success, $"{result.Status}: {result.Detail}");
        Assert.Equal(original, ChangshaReplayStateCodec.Serialize(result.ReconstructedState!));
        await runtime.RemoveGameAsync(id);
        var (removed, removedOriginal, _) = await fixture.ReadPrefix(id, ReplayCutKind.Removed);
        var removal = ChangshaFullStateReplayVerifier.Verify(removed);
        Assert.True(removal.Success, removal.Detail);
        Assert.Equal(removedOriginal, ChangshaReplayStateCodec.Serialize(removal.ReconstructedState!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistenceFailure_RetainsPendingPrefixAndHandlesCommittedRetry(bool afterCommit)
    {
        var fault = new ReplaySaveFault();
        await using var fixture = await ReplayRuntimeFixture.Create(interceptor: fault);
        var id = await fixture.CreateSeating(33);
        fault.Arm(afterCommit);
        Assert.True(await fixture.Runtime.ApplyDealModeAsync(id, DealMode.Manual));
        var (_, savedBefore, beforeRows) = await fixture.ReadPrefix(id);
        if (!afterCommit)
        {
            Assert.Single(beforeRows);
            Assert.NotEqual(ChangshaReplayStateCodec.Serialize(fixture.State(id)), savedBefore);
        }
        await fixture.Runtime.SetBotStrategyAsync(id, "hard");
        var (envelope, original, rows) = await fixture.ReadPrefix(id);
        Assert.Equal(3, rows.Length);
        Assert.Equal(Enumerable.Range(1, 3).Select(i => (long)i), rows.Select(row => row.Sequence));
        var result = ChangshaFullStateReplayVerifier.Verify(envelope);
        Assert.True(result.Success, $"{result.Status}: {result.Detail}");
        Assert.Equal(original, ChangshaReplayStateCodec.Serialize(result.ReconstructedState!));
    }

    [Fact]
    public async Task LegacyHttpReadback_PreservesStoredOrderAcrossTurnReset()
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var id = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChangshaGameReplays.Add(new()
            {
                GameId = id, CreatedAt = DateTime.UtcNow, SchemaVersion = 2,
                EventsJson = """{"schemaVersion":2,"events":[{"turn":57,"action":"draw-hand"},{"turn":1,"action":"dice-rolled"}]}"""
            });
            await db.SaveChangesAsync();
        }
        using var response = await fixture.Client.GetAsync($"/api/games/{id}/replay");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { 57, 1 }, json.RootElement.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("turn").GetInt32()));
    }

    [Fact]
    public async Task ActualFourHandKong_703Events_SnapshotJournalExportAndHttpAgree()
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var id = await fixture.CreateHumans(7, manual: true);
        await fixture.Runtime.DiscardAsync(id, 0, 33);
        await fixture.Runtime.ClaimAsync(id, 1, "Kong", null);
        Assert.Equal(89, fixture.State(id).Hands[1].ConcealedTiles[^1]);
        await fixture.FinishGame(id);
        var original = ChangshaReplayStateCodec.Serialize(fixture.State(id));
        Assert.Equal(703, fixture.State(id).EventLog.Count);
        string stored;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var gid = Guid.Parse(id);
            var game = await db.ChangshaGames.AsNoTracking().SingleAsync(g => g.Id == gid);
            Assert.Equal(original, game.StateJson);
            var replay = await db.ChangshaGameReplays.AsNoTracking().SingleAsync(r => r.GameId == gid);
            Assert.Equal(3, replay.SchemaVersion);
            stored = replay.EventsJson;
            using var parsed = JsonDocument.Parse(stored);
            var envelope = JsonSerializer.Deserialize<ChangshaReplayEnvelope>(
                parsed.RootElement.GetProperty("reconstruction"), ChangshaReplayStateCodec.RecordJson)!;
            var rows = await db.ChangshaGameEvents.AsNoTracking()
                .Where(e => e.GameId == gid).OrderBy(e => e.Sequence).ToArrayAsync();
            Assert.Equal(envelope.Records.Count, rows.Length);
            Assert.Equal(ReplayOperation.RotateBanker, envelope.Records[^1].Operation);
            Assert.Equal(ChangshaReplayStateCodec.Hash(original), envelope.Final.FullStateHash);
            for (var i = 0; i < rows.Length; i++)
            {
                Assert.Equal(i + 1, rows[i].Sequence);
                Assert.Equal(ChangshaReplayEnvelope.DatabaseEventType, rows[i].EventType);
                Assert.Equal(ChangshaReplayStateCodec.SerializeRecord(envelope.Records[i]), rows[i].Detail);
            }
            Assert.Contains(rows, row => row.Detail.Length > 256);
        }
        var verified = ChangshaFullStateReplayVerifier.VerifyJson(stored);
        Assert.True(verified.Success, $"{verified.Status} {verified.RecordSequence}: {verified.Detail}");
        Assert.Equal(original, ChangshaReplayStateCodec.Serialize(verified.ReconstructedState!));

        using var response = await fixture.Client.GetAsync($"/api/games/{id}/replay");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.TryGetProperty("reconstruction", out _));
        var events = body.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(703, events.Length);
        Assert.Equal(Enumerable.Range(1, 703).Select(i => (long)i), events.Select(e => e.GetProperty("sequence").GetInt64()));
        Assert.Equal(25, events[24].GetProperty("sequence").GetInt64());
        Assert.All(events, e =>
        {
            Assert.True(e.TryGetProperty("handNumber", out _));
            Assert.True(e.TryGetProperty("detail", out _));
        });
        ReplayRuntimeFixture.SaveEvidence("four-hand-runtime", stored, original,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ActualExplicitChow_PartnersAndFullStateSurviveEfReadback()
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var id = await fixture.CreateHumans(0, manual: false);
        await fixture.Runtime.DiscardAsync(id, 0, 19);
        await fixture.Runtime.ClaimAsync(id, 1, "Chow", [12, 20]);
        if (fixture.State(id).Phase == ChangshaPhase.AwaitingClaim)
            await fixture.PassRemaining(id, exceptSeat: 1);
        Assert.Equal(new[] { 12, 19, 20 }, fixture.State(id).Hands[1].Melds[0].TileIds);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gid = Guid.Parse(id);
        var rows = await db.ChangshaGameEvents.AsNoTracking()
            .Where(e => e.GameId == gid).OrderBy(e => e.Sequence).ToArrayAsync();
        var records = rows.Select(row =>
            JsonSerializer.Deserialize<ReplayTransition>(row.Detail, ChangshaReplayStateCodec.RecordJson)!).ToArray();
        var claim = Assert.Single(records, r => r.Operation == ReplayOperation.ResolveClaim);
        Assert.Equal(new[] { 12, 20 }, claim.Inputs.ChosenTileIds);
        Assert.Equal(new[] { 12, 20 }, claim.Observations.AcceptedChowPartners);
        var original = ChangshaReplayStateCodec.Serialize(fixture.State(id));
        var envelope = new ChangshaReplayEnvelope
        {
            SchemaVersion = 3, RecordingStatus = ReplayRecordingStatus.CompletePrefix,
            EngineIdentity = ChangshaReplayStateCodec.EngineIdentity, Records = records,
            Final = ChangshaReplayStateCodec.Checkpoint(fixture.State(id)), CutKind = ReplayCutKind.Checkpoint
        };
        var verified = ChangshaFullStateReplayVerifier.Verify(envelope);
        Assert.True(verified.Success, $"{verified.Status} {verified.RecordSequence}: {verified.Detail}");
        Assert.Equal(original, ChangshaReplayStateCodec.Serialize(verified.ReconstructedState!));
    }
}

internal sealed class ReplayRuntimeFixture : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private IHost _host;
    private readonly bool _persistSnapshots;
    private readonly SaveChangesInterceptor? _interceptor;
    private readonly bool _profileCompletion;
    private readonly List<string> _games = [];
    internal IServiceProvider Services => _host.Services;
    internal HttpClient Client { get; private set; }
    internal ChangshaGameRuntime Runtime => Services.GetRequiredService<ChangshaGameRuntime>();
    internal ReplayHubProbe Hub { get; }

    private ReplayRuntimeFixture(SqliteConnection connection, IHost host, ReplayHubProbe hub,
        bool persistSnapshots, SaveChangesInterceptor? interceptor, bool profileCompletion)
    {
        _connection = connection; _host = host; Hub = hub;
        _persistSnapshots = persistSnapshots; _interceptor = interceptor;
        _profileCompletion = profileCompletion;
        Client = host.GetTestClient();
    }

    internal static async Task<ReplayRuntimeFixture> Create(bool persistSnapshots = true,
        SaveChangesInterceptor? interceptor = null, bool profileCompletion = false)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var hub = new ReplayHubProbe();
        var host = await BuildHost(connection, hub, persistSnapshots, interceptor, profileCompletion);
        var fixture = new ReplayRuntimeFixture(connection, host, hub, persistSnapshots, interceptor, profileCompletion);
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
        return fixture;
    }

    private static Task<IHost> BuildHost(SqliteConnection connection, ReplayHubProbe hub,
        bool persistSnapshots, SaveChangesInterceptor? interceptor, bool profileCompletion) =>
        new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddDbContext<AppDbContext>(options =>
                {
                    options.UseSqlite(connection);
                    if (interceptor is not null) options.AddInterceptors(interceptor);
                });
                services.AddSingleton<IHubContext<ChangshaHub>>(hub);
                services.AddSingleton<IOptions<ChangshaRuntimeOptions>>(Options.Create(
                    new ChangshaRuntimeOptions { PersistSnapshots = persistSnapshots }));
                if (profileCompletion)
                {
                    services.AddSingleton<Mahjong.Autotable.Api.Players.PlayerProfileService>();
                    services.AddSingleton<Mahjong.Autotable.Api.Players.PlayerGameHistoryService>();
                }
                services.AddSingleton<ChangshaGameRuntime>();
                services.AddSingleton<IChangshaGameRuntime>(provider => provider.GetRequiredService<ChangshaGameRuntime>());
                services.AddControllers().AddApplicationPart(typeof(ChangshaReplayController).Assembly);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            })).StartAsync();

    internal async Task Restart(bool eager)
    {
        foreach (var instance in Instances().Values)
            await instance.DisposeAsync();
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        _host = await BuildHost(_connection, Hub, _persistSnapshots, _interceptor, _profileCompletion);
        Client = _host.GetTestClient();
        if (eager) await Runtime.HydrateAsync(Services);
    }

    internal ConcurrentDictionary<string, ChangshaGameInstance> Instances() =>
        Assert.IsType<ConcurrentDictionary<string, ChangshaGameInstance>>(
            typeof(ChangshaGameRuntime).GetField("_games",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(Runtime));

    internal void Track(string id)
    {
        if (!_games.Contains(id)) _games.Add(id);
    }

    internal async Task<(string Id, string Room)> CreatePublicSeating(
        int seed, DealMode mode = DealMode.Manual, int cap = 4, int baseUnit = 1, string difficulty = "medium")
    {
        var room = $"d17-{Guid.NewGuid():N}";
        var id = await Runtime.CreateGameAsync(seed, [], "replay-owner", null, maxHands: cap,
            baseUnit: baseUnit, publicRoom: new(room, mode, difficulty));
        Track(id);
        return (id, room);
    }

    internal async Task BindHumans(string id)
    {
        for (var seat = 0; seat < 4; seat++)
            await Runtime.TakeSeatAsync(id, $"replay-human-{seat}", $"{id}-connection-{seat}", seat);
    }

    internal async Task ReconnectHumans(string id)
    {
        for (var seat = 0; seat < 4; seat++)
            Assert.True(await Runtime.ReconnectAsync(id, seat, $"replay-human-{seat}", $"{id}-reconnected-{seat}"));
    }

    internal async Task<string> CreateHumans(int seed, bool manual, int cap = 4, int baseUnit = 1)
    {
        var id = await CreateSeating(seed, cap, baseUnit);
        for (var seat = 0; seat < 4; seat++)
            await Runtime.TakeSeatAsync(id, $"replay-human-{seat}", $"{id}-connection-{seat}", seat);
        if (manual) await Runtime.ApplyDealModeAsync(id, DealMode.Manual);
        await Runtime.StartGameAsync(id);
        await CompleteDeal(id);
        return id;
    }

    internal async Task<string> CreateSeating(int seed, int cap = 4, int baseUnit = 1)
    {
        var id = await Runtime.CreateGameAsync(seed, [], "replay-owner", null, maxHands: cap, baseUnit: baseUnit);
        _games.Add(id);
        return id;
    }

    internal async Task<(ChangshaReplayEnvelope Envelope, string Original, ChangshaGameEvent[] Rows)>
        ReadPrefix(string id, ReplayCutKind kind = ReplayCutKind.Checkpoint)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gid = Guid.Parse(id);
        var game = await db.ChangshaGames.AsNoTracking().SingleAsync(g => g.Id == gid);
        var rows = await db.ChangshaGameEvents.AsNoTracking().Where(e => e.GameId == gid)
            .OrderBy(e => e.Sequence).ToArrayAsync();
        var records = rows.Select(row => JsonSerializer.Deserialize<ReplayTransition>(
            row.Detail, ChangshaReplayStateCodec.RecordJson)!).ToArray();
        return (new()
        {
            SchemaVersion = 3, RecordingStatus = ReplayRecordingStatus.CompletePrefix,
            EngineIdentity = ChangshaReplayStateCodec.EngineIdentity,
            Records = records, Final = records[^1].After, CutKind = kind
        }, game.StateJson, rows);
    }

    internal ChangshaGameState State(string id)
    {
        Assert.True(Runtime.TryGetSnapshot(id, out var state));
        return state!;
    }

    internal async Task CompleteDeal(string id)
    {
        if (State(id).Phase == ChangshaPhase.RollingDice)
            await Runtime.RollDiceAsync(id, State(id).DealerSeatIndex);
        while (ChangshaGameStateMachine.IsPickupPhase(State(id).Phase))
            await Runtime.TakeTilesFromWallAsync(id, State(id).PickupSeatIndex!.Value,
                ChangshaGameStateMachine.ExpectedPickupCount(State(id).Phase));
        if (State(id).Phase == ChangshaPhase.AwaitingDiscard)
            for (var seat = 0; seat < 4; seat++) await Runtime.AcknowledgeDealAsync(id, seat);
    }

    internal async Task PassRemaining(string id, int? exceptSeat = null)
    {
        var state = State(id);
        var seats = state.ClaimWindow!.Opportunities.Select(o => o.SeatIndex).Distinct().ToArray();
        foreach (var seat in seats)
            if (State(id).Phase == ChangshaPhase.AwaitingClaim && seat != exceptSeat)
                await Runtime.PassAsync(id, seat);
    }

    internal async Task FinishGame(string id)
    {
        for (var step = 0; step < 2048 && !State(id).IsGameComplete; step++)
        {
            var state = State(id);
            if (state.Phase == ChangshaPhase.RollingDice || ChangshaGameStateMachine.IsPickupPhase(state.Phase))
                await CompleteDeal(id);
            else if (state.Phase == ChangshaPhase.EndHand)
                await HandResultTestActions.AcknowledgeAll(this, id);
            else if (state.Phase == ChangshaPhase.AwaitingClaim)
                await PassRemaining(id);
            else
            {
                Assert.Equal(ChangshaPhase.AwaitingDiscard, state.Phase);
                await Runtime.DiscardAsync(id, state.ActiveSeatIndex, state.Hands[state.ActiveSeatIndex].ConcealedTiles[0]);
            }
        }
        Assert.True(State(id).IsGameComplete);
    }

    internal static void SaveEvidence(string name, string replay, string original, string wire)
    {
        var root = Environment.GetEnvironmentVariable("D17_IMPLEMENTATION_EVIDENCE");
        if (root is null) return;
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "recorded-replay.json"), replay);
        File.WriteAllText(Path.Combine(directory, "original-state.json"), original);
        File.WriteAllText(Path.Combine(directory, "http-playback.json"), wire);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in Instances().Values)
            if (instance.CompletionEffectsTask is { } effects) await effects;
        foreach (var game in _games) await Runtime.RemoveGameAsync(game);
        Client.Dispose();
        _host.Dispose();
        await _connection.DisposeAsync();
    }
}

internal sealed class ReplayHubProbe : IHubContext<ChangshaHub>, IHubClients, IClientProxy, IGroupManager
{
    internal ConcurrentQueue<string> Methods { get; } = new();
    IHubClients IHubContext<ChangshaHub>.Clients => this;
    IGroupManager IHubContext<ChangshaHub>.Groups => this;
    public IClientProxy All => this;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
    public IClientProxy Client(string connectionId) => this;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => this;
    public IClientProxy Group(string groupName) => this;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => this;
    public IClientProxy User(string userId) => this;
    public IClientProxy Users(IReadOnlyList<string> userIds) => this;
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        Methods.Enqueue(method);
        return Task.CompletedTask;
    }

    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class ReplaySaveFault : SaveChangesInterceptor
{
    private int _mode;
    internal void Arm(bool afterCommit) => _mode = afterCommit ? 2 : 1;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _mode, 0, 1) == 1)
            throw new DbUpdateException("Synthetic failure before commit.");
        return ValueTask.FromResult(result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _mode, 0, 2) == 2)
            throw new DbUpdateException("Synthetic lost completion acknowledgement.");
        return ValueTask.FromResult(result);
    }
}

internal sealed record ReplayReachableStep(ReplayOperation Operation, ReplayInputs Inputs);

internal sealed record ReplayReachableBranch(int Seed, IReadOnlyList<ReplayReachableStep> Steps)
{
    internal ReplayTestDriver Record(int baseUnit = 1)
    {
        var driver = new ReplayTestDriver(Seed, baseUnit: baseUnit, captureOriginals: false);
        foreach (var (operation, inputs) in Steps) driver.Do(operation, inputs);
        return driver;
    }
}

internal static class ReplayReachableBranches
{
    private static readonly Lazy<IReadOnlyDictionary<string, ReplayReachableBranch>> Fixtures = new(Load);

    internal static ReplayReachableBranch Get(string branch)
    {
        Assert.True(Fixtures.Value.TryGetValue(branch, out var fixture), $"Missing fixed replay fixture: {branch}.");
        return fixture!;
    }

    private static IReadOnlyDictionary<string, ReplayReachableBranch> Load()
    {
        var sourcePath = SourceFixturePath();
        if (File.Exists(sourcePath))
            return Read(sourcePath);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "Replay", "ReplayV3ReachableCases.json");
            if (File.Exists(path))
                return Read(path);
        }
        throw new FileNotFoundException("Fixed replay fixture data must accompany the test source.");
    }

    private static string SourceFixturePath([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "ReplayV3ReachableCases.json");

    private static IReadOnlyDictionary<string, ReplayReachableBranch> Read(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, ReplayReachableBranch>>(
            File.ReadAllText(path), ChangshaReplayStateCodec.RecordJson)
            ?? throw new InvalidOperationException("Empty fixed replay corpus.");
}
