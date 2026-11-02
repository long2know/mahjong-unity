using System.Text.Json;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Replay;

namespace Mahjong.Autotable.Api.Tests.Replay;

internal sealed record ReplayVerificationResult(bool Success, string Status, long RecordSequence,
    string? ExpectedHash = null, string? ActualHash = null, string? Detail = null,
    ChangshaGameState? ReconstructedState = null);

internal static class ChangshaFullStateReplayVerifier
{
    internal static ReplayVerificationResult VerifyJson(string json,
        IReadOnlyDictionary<long, string>? originalCheckpoints = null)
    {
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object
                || !parsed.RootElement.TryGetProperty("schemaVersion", out var version)
                || version.GetInt32() < ChangshaReplayEnvelope.CurrentSchemaVersion)
                return new(false, "LegacyUnverifiable", 0);
            var payload = parsed.RootElement.TryGetProperty("reconstruction", out var reconstruction)
                ? reconstruction.GetRawText() : json;
            var envelope = JsonSerializer.Deserialize<ChangshaReplayEnvelope>(payload, ChangshaReplayStateCodec.RecordJson);
            return envelope is null ? new(false, "InvalidFormat", 0) : Verify(envelope, originalCheckpoints);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return new(false, "InvalidFormat", 0, Detail: ex.Message);
        }
    }

    internal static ReplayVerificationResult Verify(ChangshaReplayEnvelope envelope,
        IReadOnlyDictionary<long, string>? originalCheckpoints = null)
    {
        if (envelope.SchemaVersion != ChangshaReplayEnvelope.CurrentSchemaVersion)
            return new(false, envelope.SchemaVersion < 3 ? "LegacyUnverifiable" : "UnsupportedFormat", 0);
        if (envelope.RecordingStatus != ReplayRecordingStatus.CompletePrefix)
            return new(false, envelope.RecordingStatus.ToString(), 0);
        if (envelope.EngineIdentity != ChangshaReplayStateCodec.EngineIdentity)
            return new(false, "UnsupportedEngineIdentity", 0);
        if (envelope.Records is null || envelope.Records.Count == 0)
            return new(false, "MissingInitialization", 0);
        for (var index = 0; index < envelope.Records.Count; index++)
        {
            if (envelope.Records[index] is null)
                return new(false, "InvalidRecordOrderOrFormat", (long)index + 1);
        }
        if (envelope.Records.All(record => record.FormatVersion < ChangshaReplayEnvelope.TransitionFormatVersion))
            return new(false, "LegacyUnverifiable", 0);

        ChangshaGameState? state = null;
        var windows = new Dictionary<long, long>();
        long ordinal = 0;
        try
        {
            foreach (var record in envelope.Records)
            {
                ordinal++;
                if (record is null || record.RecordSequence != ordinal
                    || record.FormatVersion != ChangshaReplayEnvelope.TransitionFormatVersion
                    || !Enum.IsDefined(record.Operation) || record.Inputs is null
                    || record.StateClockFacts is null || record.Observations is null)
                    return new(false, "InvalidRecordOrderOrFormat", ordinal);
                if ((ordinal == 1) != (record.Operation == ReplayOperation.InitializeRuntimeGame))
                    return new(false, "InvalidInitialization", ordinal);

                var context = new ChangshaReplayContext(record.StateClockFacts);
                var previousEventSequence = state?.EventSequence ?? 0;
                if (state is null)
                {
                    if (record.Before is not null || record.Inputs.Initialization is not { } init
                        || !Guid.TryParse(init.GameId, out _))
                        return new(false, "InvalidInitialization", ordinal);
                    if (init.EngineIdentity != envelope.EngineIdentity)
                        return new(false, "InvalidInitializationIdentity", ordinal);
                    if (init.MaxHands is not (1 or 4 or 8 or 16)
                        || !Enum.IsDefined(init.DealMode)
                        || init.BotSeatIndexes is null || init.BotSeatIndexes.Any(seat => seat is < 0 or > 3))
                        return new(false, "UnsupportedProfile", ordinal);
                    state = ChangshaReplayMetadataOperations.Initialize(init, context);
                }
                else
                {
                    var before = ChangshaReplayStateCodec.Checkpoint(state);
                    if (record.Before != before)
                        return new(false, "BeforeStateMismatch", ordinal,
                            record.Before?.FullStateHash, before.FullStateHash);
                    if (record.Inputs.Initialization is not null)
                        return new(false, "UnexpectedInitializationData", ordinal);
                    ValidateInputs(state, record, windows);
                    if (record.Operation == ReplayOperation.SnapshotRoundTrip)
                        state = ChangshaReplayStateCodec.RoundTrip(state);
                    else
                        ChangshaReplayMetadataOperations.Apply(state, record.Operation, record.Inputs, context);
                }
                context.Complete();
                var actual = ChangshaReplayStateCodec.Checkpoint(state);
                if (actual != record.After)
                    return new(false, "AfterStateMismatch", ordinal,
                        record.After?.FullStateHash, actual.FullStateHash);
                if (ChangshaReplayStateCodec.SerializeRecord(context.Observations)
                    != ChangshaReplayStateCodec.SerializeRecord(record.Observations))
                    return new(false, "EventOrChoiceObservationMismatch", ordinal);
                var emitted = context.Observations.Events;
                if (emitted.Count != state.EventSequence - previousEventSequence
                    || emitted.Where((ev, i) => ev.Event.Sequence != previousEventSequence + i + 1).Any())
                    return new(false, "EventSequenceMismatch", ordinal);
                if (state.ClaimWindow is not null)
                {
                    foreach (var observed in emitted.Where(e => e.Event.EventType == "claim-window-open"))
                        windows[observed.Event.Sequence] = state.ClaimWindow.OpenedAtUnixMs;
                }
                if (originalCheckpoints is not null)
                {
                    if (!originalCheckpoints.TryGetValue(ordinal, out var expected))
                        return new(false, "MissingOriginalCheckpoint", ordinal);
                    if (expected != ChangshaReplayStateCodec.Serialize(state))
                        return new(false, "OriginalCheckpointMismatch", ordinal,
                            ChangshaReplayStateCodec.Hash(expected), actual.FullStateHash);
                }
            }
            var final = ChangshaReplayStateCodec.Checkpoint(state!);
            if (envelope.Final != final)
                return new(false, "FinalStateMismatch", ordinal, envelope.Final?.FullStateHash, final.FullStateHash);
            if (!Enum.IsDefined(envelope.CutKind))
                return new(false, "InvalidCut", ordinal);
            if (envelope.CutKind == ReplayCutKind.NaturalCompletion
                && (!state!.IsGameComplete || state.Phase != ChangshaPhase.GameComplete
                    || envelope.Records[^1].Operation != ReplayOperation.RotateBanker
                    || !state.EventLog.Any(e => e.EventType == "game-ended")))
                return new(false, "InvalidNaturalCompletion", ordinal);
            if (envelope.CutKind == ReplayCutKind.Removed
                && envelope.Records[^1].Operation != ReplayOperation.MarkRemoved)
                return new(false, "InvalidRemovalCut", ordinal);
            return new(true, "Verified", ordinal, final.FullStateHash, final.FullStateHash,
                ReconstructedState: state);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or OverflowException or NullReferenceException or IndexOutOfRangeException or JsonException)
        {
            return new(false, "OperationOrFactRejected", ordinal, Detail: ex.Message);
        }
    }

    private static void ValidateInputs(ChangshaGameState state, ReplayTransition record,
        IReadOnlyDictionary<long, long> windows)
    {
        var input = record.Inputs;
        if (record.Operation is ReplayOperation.RollDice or ReplayOperation.BeginManualDeal)
        {
            var expectedSeed = record.Operation == ReplayOperation.RollDice && state.HandNumber == 1
                ? state.Seed : unchecked(state.Seed + state.HandNumber);
            if (input.DiceSeed != expectedSeed || input.Dice is not { } dice
                || dice.Die1 is < 1 or > 6 || dice.Die2 is < 1 or > 6
                || new DiceService(expectedSeed).Roll() != dice)
                throw new InvalidOperationException("Recorded dice/seed context disagrees with the executing profile.");
        }
        if (record.Operation is ReplayOperation.ResolveClaim or ReplayOperation.PassClaim)
        {
            var opening = state.EventLog.LastOrDefault(e => e.EventType == "claim-window-open")?.Sequence;
            if (state.ClaimWindow is null || input.WindowOpeningSequence != opening)
                throw new InvalidOperationException("Claim resolution refers to a different opening.");
        }
        if (record.Operation == ReplayOperation.ObserveClaimWindowSchedule)
        {
            var schedule = input.Schedule ?? throw new InvalidOperationException("Missing schedule.");
            if (!windows.TryGetValue(schedule.OpeningEventSequence, out var opened)
                || schedule.OpenedAtUnixMs != opened)
                throw new InvalidOperationException("Schedule refers to an unknown/different claim opening.");
        }
    }
}
