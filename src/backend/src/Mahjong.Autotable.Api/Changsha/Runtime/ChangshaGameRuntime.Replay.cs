using Mahjong.Autotable.Api.Changsha.Replay;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using System.Text.Json;

namespace Mahjong.Autotable.Api.Changsha.Runtime;

public sealed partial class ChangshaGameRuntime
{
    private async Task<ChangshaReplayJournal?> LoadRecoveredReplayJournalAsync(
        AppDbContext db, ChangshaGame row, ChangshaGameState state, CancellationToken ct)
    {
        List<ChangshaGameEvent> rows;
        try
        {
            rows = await db.ChangshaGameEvents.AsNoTracking()
                .Where(entry => entry.GameId == row.Id).OrderBy(entry => entry.Sequence).ToListAsync(ct);
        }
        catch (DbException ex)
        {
            throw new PublicRoomRecoveryException("room-store-unavailable", ex);
        }
        if (!rows.Any(entry => entry.EventType == ChangshaReplayEnvelope.DatabaseEventType))
        {
            _logger.LogInformation("Recovered game {GameId} has legacy-unverifiable replay data.", row.Id);
            return null;
        }
        if (rows.Any(entry => entry.EventType != ChangshaReplayEnvelope.DatabaseEventType))
            throw new PublicRoomRecoveryException("room-replay-invalid");
        var records = new List<ReplayTransition>(rows.Count);
        try
        {
            foreach (var entry in rows)
            {
                var record = JsonSerializer.Deserialize<ReplayTransition>(entry.Detail, ChangshaReplayStateCodec.RecordJson)
                    ?? throw new ReplayJournalValidationException("Null replay record.");
                if (record.Inputs is null || record.After is null || entry.Sequence != record.RecordSequence)
                    throw new ReplayJournalValidationException("Stored replay ordinal disagrees with its payload.");
                records.Add(record);
            }
            if (records.All(record => record.FormatVersion == 1))
            {
                _logger.LogInformation("Recovered game {GameId} has legacy-unverifiable pre-identity replay records.", row.Id);
                return null;
            }
            var initialization = records.FirstOrDefault()?.Inputs?.Initialization;
            if (initialization?.EngineIdentity is null)
                throw new ReplayJournalValidationException("Missing durable replay engine identity.");
            if (initialization.EngineIdentity != ChangshaReplayStateCodec.EngineIdentity)
            {
                _logger.LogWarning("Recovered game {GameId} has an unsupported replay engine; playback remains legacy-unverifiable.", row.Id);
                return null;
            }
            if (row.StateVersion != state.StateVersion || row.Seed != state.Seed
                || row.CurrentHandNumber != state.HandNumber || row.CurrentRoundNumber != state.RoundNumber)
                throw new ReplayJournalValidationException("Snapshot columns disagree with the recorded game state.");
            foreach (var (entry, record) in rows.Zip(records))
            {
                if (entry.HandNumber != record.After.HandNumber || entry.TurnNumber != record.After.TurnNumber
                    || entry.StateVersion != record.After.StateVersion
                    || entry.SeatIndex != (record.Inputs.SeatIndex ?? -1) || entry.TileId != record.Inputs.TileId)
                    throw new ReplayJournalValidationException("Stored replay metadata disagrees with its payload.");
            }
            return ChangshaReplayJournal.Restore(records, state, row.Id);
        }
        catch (Exception ex) when (ex is JsonException or ReplayJournalValidationException)
        {
            throw new PublicRoomRecoveryException("room-replay-invalid", ex);
        }
        catch (IOException ex)
        {
            throw new PublicRoomRecoveryException("room-replay-unavailable", ex);
        }
    }

    private async Task PersistRecoveredReplayAsync(ChangshaGameInstance instance, CancellationToken ct)
    {
        if (instance.ReplayJournal is null || !_options.PersistSnapshots) return;
        try
        {
            await WriteSnapshotAsync(instance, ct);
        }
        catch (DbUpdateException ex)
        {
            await instance.DisposeAsync();
            throw new PublicRoomRecoveryException("room-replay-persistence-failed", ex);
        }
        catch (DbException ex)
        {
            await instance.DisposeAsync();
            throw new PublicRoomRecoveryException("room-store-unavailable", ex);
        }
        catch (ReplayJournalValidationException ex)
        {
            await instance.DisposeAsync();
            throw new PublicRoomRecoveryException("room-replay-invalid", ex);
        }
    }

    private static List<ChangshaEvent> ApplyRecordedChange(
        ChangshaGameInstance instance, ReplayOperation operation, ReplayInputs? inputs = null)
    {
        inputs ??= new();
        if (operation is ReplayOperation.ResolveClaim or ReplayOperation.PassClaim
            && instance.State.ClaimWindow is { } window)
            inputs = inputs with { WindowOpeningSequence = instance.ReplayJournal?.OpeningSequence(window) };
        return instance.ReplayJournal is { } journal
            ? journal.Apply(instance.State, operation, inputs)
            : ChangshaReplayMetadataOperations.Apply(instance.State, operation, inputs, null);
    }

    private ReplayTimingOptions ReplayTiming() => new()
    {
        ClaimWindowTimeoutMs = _options.ClaimWindowTimeoutMs,
        BotTurnDelayMs = _options.BotTurnDelayMs,
        BotClaimDelayMs = _options.BotClaimDelayMs,
        BotPickupDelayMs = _options.BotPickupDelayMs,
        DealBatchDelayMs = _options.DealBatchDelayMs,
        BotDecisionTimeoutMs = _options.BotDecisionTimeoutMs,
        PersistSnapshots = _options.PersistSnapshots
    };

    private (ChangshaGameState State, ChangshaReplayJournal? Journal) InitializeRecordedGame(ReplayInitialization input)
    {
        try
        {
            return ChangshaReplayJournal.Initialize(input);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Replay initialization unavailable; game will retain legacy-unverifiable playback.");
            return (ChangshaReplayMetadataOperations.Initialize(input, null), null);
        }
    }

    private async Task PersistMetadataOnlyAsync(ChangshaGameInstance instance, CancellationToken ct)
    {
        if (!_options.PersistSnapshots) return;
        try { await WriteSnapshotAsync(instance, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist metadata/replay prefix for {GameId}", instance.GameId);
        }
    }

    private static void ObserveClaimSchedule(ChangshaGameInstance instance, ChangshaClaimWindow window,
        int configuredTimeoutMs, int delayMs, bool isResume)
    {
        if (instance.ReplayJournal is not { } journal) return;
        if (journal.OpeningSequence(window) is not { } sequence)
        {
            journal.Invalidate("Claim schedule has no recorded opening.");
            return;
        }
        ApplyRecordedChange(instance, ReplayOperation.ObserveClaimWindowSchedule, new()
        {
            Schedule = new()
            {
                OpeningEventSequence = sequence,
                OpenedAtUnixMs = window.OpenedAtUnixMs,
                ConfiguredTimeoutMs = configuredTimeoutMs,
                ScheduledDelayMs = delayMs,
                DeadlineUnixMs = configuredTimeoutMs > 0 && window.OpenedAtUnixMs > 0
                    ? window.OpenedAtUnixMs + configuredTimeoutMs : 0,
                IsResume = isResume
            }
        });
    }

    private static async Task<long> AddPendingReplayRecordsAsync(
        AppDbContext db, ChangshaGameInstance instance, Guid gameId, CancellationToken ct)
    {
        if (instance.ReplayJournal is not { } journal) return 0;
        journal.CheckHead(instance.State);
        var pending = journal.Pending();
        foreach (var record in pending)
        {
            var payload = ChangshaReplayStateCodec.SerializeRecord(record);
            var existing = await db.ChangshaGameEvents
                .SingleOrDefaultAsync(e => e.GameId == gameId && e.Sequence == record.RecordSequence, ct);
            if (existing is not null)
            {
                if (existing.EventType != ChangshaReplayEnvelope.DatabaseEventType || existing.Detail != payload)
                {
                    journal.Invalidate("A persisted replay ordinal contains different data.");
                    throw new ReplayJournalValidationException("Replay journal ordinal conflict.");
                }
                continue;
            }
            db.ChangshaGameEvents.Add(new ChangshaGameEvent
            {
                GameId = gameId,
                Sequence = record.RecordSequence,
                EventType = ChangshaReplayEnvelope.DatabaseEventType,
                SeatIndex = record.Inputs.SeatIndex ?? -1,
                TurnNumber = record.After.TurnNumber,
                TileId = record.Inputs.TileId,
                Detail = payload,
                HandNumber = record.After.HandNumber,
                StateVersion = record.After.StateVersion,
                OccurredUtc = record.Observations.Events.LastOrDefault()?.Event.OccurredUtc
                    ?? instance.State.EventLog.LastOrDefault()?.OccurredUtc ?? DateTime.UnixEpoch,
                PersistedUtc = DateTime.UtcNow
            });
        }
        return journal.Count;
    }

    private static async Task ValidateCommittedReplayPrefixAsync(
        AppDbContext db, ChangshaGameInstance instance, Guid gameId, CancellationToken ct)
    {
        if (instance.ReplayJournal is not { } journal) return;
        var committed = await db.ChangshaGameEvents.AsNoTracking()
            .Where(e => e.GameId == gameId).OrderBy(e => e.Sequence).ToListAsync(ct);
        var expected = journal.Records();
        foreach (var row in committed)
        {
            if (row.Sequence < 1 || row.Sequence > expected.Count
                || row.EventType != ChangshaReplayEnvelope.DatabaseEventType
                || row.Detail != ChangshaReplayStateCodec.SerializeRecord(expected[(int)row.Sequence - 1]))
            {
                journal.Invalidate("Committed journal disagrees with the export prefix.");
                throw new ReplayJournalValidationException("Committed replay prefix conflict.");
            }
        }
        for (long sequence = 1; sequence <= journal.PersistedRecordSequence; sequence++)
        {
            if (!committed.Any(row => row.Sequence == sequence))
            {
                journal.Invalidate("Missing committed replay prefix.");
                throw new ReplayJournalValidationException("Missing committed replay prefix.");
            }
        }
    }

    private static async Task UpsertReplayCompletionAsync(
        AppDbContext db, Guid gameId, ReplayCompletionPayload payload, CancellationToken ct)
    {
        var existing = await db.ChangshaGameReplays.FirstOrDefaultAsync(r => r.GameId == gameId, ct);
        if (existing is null)
        {
            db.ChangshaGameReplays.Add(new ChangshaGameReplay
            {
                Id = Guid.NewGuid(), GameId = gameId, CreatedAt = DateTime.UtcNow,
                EventsJson = payload.Json, SchemaVersion = payload.SchemaVersion
            });
        }
        else
        {
            existing.CreatedAt = DateTime.UtcNow;
            existing.EventsJson = payload.Json;
            existing.SchemaVersion = payload.SchemaVersion;
        }
    }

    private async Task PersistCompletionAsync(
        ChangshaGameInstance instance, ReplayCompletionPayload payload, CancellationToken ct)
    {
        if (_options.PersistSnapshots)
        {
            await WriteSnapshotAsync(instance, ct, completion: payload);
            return;
        }
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await UpsertReplayCompletionAsync(db, Guid.Parse(instance.GameId), payload, ct);
        await db.SaveChangesAsync(ct);
    }

    private sealed record ReplayCompletionPayload(string Json, int SchemaVersion);
}
