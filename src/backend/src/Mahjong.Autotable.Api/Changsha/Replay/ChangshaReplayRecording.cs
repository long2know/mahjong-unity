namespace Mahjong.Autotable.Api.Changsha.Replay;

internal sealed class ChangshaReplayContext
{
    private readonly Func<DateTime> _utc;
    private readonly Func<long> _unixMs;
    private readonly IReadOnlyList<ReplayClockFact>? _recorded;
    private readonly List<ReplayClockFact> _facts = [];
    private readonly List<ReplayEventObservation> _events = [];
    private int _cursor;
    private int[]? _partners;
    private ReplayWindowSchedule? _schedule;

    internal ChangshaReplayContext(Func<DateTime>? utc = null, Func<long>? unixMs = null)
    {
        _utc = utc ?? (() => DateTime.UtcNow);
        _unixMs = unixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    internal ChangshaReplayContext(IReadOnlyList<ReplayClockFact> recorded)
    {
        _recorded = recorded ?? throw new ArgumentNullException(nameof(recorded));
        _utc = () => throw new InvalidOperationException("Replay cannot read the live clock.");
        _unixMs = () => throw new InvalidOperationException("Replay cannot read the live clock.");
    }

    internal DateTime EventUtc()
    {
        if (_recorded is not null)
            return new DateTime(Consume(ReplayClockKind.EventUtc), DateTimeKind.Utc);
        var value = _utc();
        if (value.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException("Event clock must return UTC.");
        _facts.Add(new(ReplayClockKind.EventUtc, value.Ticks));
        return value;
    }

    internal long ClaimOpenedUnixMs()
    {
        var value = _recorded is null ? _unixMs() : Consume(ReplayClockKind.ClaimOpenedUnixMs);
        if (_recorded is null) _facts.Add(new(ReplayClockKind.ClaimOpenedUnixMs, value));
        return value;
    }

    internal void ObserveEvent(ChangshaGameState state, ChangshaEvent evt) =>
        _events.Add(new(ChangshaReplayStateCodec.CopyRecord(evt), state.HandNumber, state.StateVersion));

    internal void ObserveChowPartners(IEnumerable<int> partners)
    {
        if (_partners is not null) throw new InvalidOperationException("Duplicate Chow observation.");
        _partners = partners.ToArray();
    }

    internal void ObserveSchedule(ReplayWindowSchedule schedule)
    {
        if (_schedule is not null) throw new InvalidOperationException("Duplicate schedule observation.");
        _schedule = schedule;
    }

    internal IReadOnlyList<ReplayClockFact> Facts => (_recorded ?? _facts).ToArray();
    internal ReplayObservations Observations => new()
    {
        Events = _events.ToArray(),
        AcceptedChowPartners = _partners?.ToArray(),
        WindowSchedule = _schedule
    };

    internal void Complete()
    {
        if (_recorded is not null && _cursor != _recorded.Count)
            throw new InvalidOperationException("Surplus replay clock facts.");
    }

    private long Consume(ReplayClockKind kind)
    {
        if (_recorded is null || _cursor >= _recorded.Count || _recorded[_cursor].Kind != kind)
            throw new InvalidOperationException($"Missing or wrong-kind replay clock fact: {kind}.");
        return _recorded[_cursor++].Value;
    }
}

internal sealed class RecordedDiceService(DiceRoll roll) : IDiceService
{
    private bool _consumed;
    public DiceRoll Roll()
    {
        if (_consumed || roll.Die1 is < 1 or > 6 || roll.Die2 is < 1 or > 6)
            throw new InvalidOperationException("Invalid or repeated recorded dice consumption.");
        _consumed = true;
        return roll;
    }
}

internal sealed class ChangshaReplayJournal
{
    private readonly List<ReplayTransition> _records = [];
    private readonly Dictionary<ChangshaClaimWindow, long> _windowTokens = [];
    private readonly Func<ChangshaReplayContext> _contextFactory;
    private readonly ReplayEngineIdentity _identity;
    private ReplayCheckpoint? _head;
    internal ReplayRecordingStatus Status { get; private set; } = ReplayRecordingStatus.CompletePrefix;
    internal string? Failure { get; private set; }
    internal long PersistedRecordSequence { get; private set; }
    internal int Count => _records.Count;
    internal long? OpeningSequence(ChangshaClaimWindow window) =>
        _windowTokens.TryGetValue(window, out var sequence) ? sequence : null;

    private ChangshaReplayJournal(Func<ChangshaReplayContext>? contextFactory = null)
    {
        _contextFactory = contextFactory ?? (() => new ChangshaReplayContext());
        _identity = ChangshaReplayStateCodec.EngineIdentity;
    }

    internal static (ChangshaGameState State, ChangshaReplayJournal Journal) Initialize(
        ReplayInitialization initialization, Func<ChangshaReplayContext>? contextFactory = null)
    {
        var journal = new ChangshaReplayJournal(contextFactory);
        var context = journal._contextFactory();
        var state = ChangshaReplayMetadataOperations.Initialize(initialization, context);
        context.Complete();
        var inputs = new ReplayInputs
        {
            Initialization = initialization with { GameId = state.GameId, EngineIdentity = journal._identity }
        };
        journal.Append(ReplayOperation.InitializeRuntimeGame, inputs, null, state, context);
        return (state, journal);
    }

    internal static ChangshaReplayJournal Restore(
        IReadOnlyList<ReplayTransition> records, ChangshaGameState snapshot, Guid gameId)
    {
        if (records.Count == 0 || records[0] is null || records[0].Operation != ReplayOperation.InitializeRuntimeGame
            || records[0].Inputs?.Initialization is not { } initialization
            || !Guid.TryParse(initialization.GameId, out var recordedId) || recordedId != gameId
            || initialization.EngineIdentity != ChangshaReplayStateCodec.EngineIdentity)
            throw new ReplayJournalValidationException("Invalid replay initialization or engine identity.");

        var journal = new ChangshaReplayJournal();
        long eventSequence = 0;
        ReplayCheckpoint? previous = null;
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (record is null || record.RecordSequence != index + 1L
                || record.FormatVersion != ChangshaReplayEnvelope.TransitionFormatVersion
                || !Enum.IsDefined(record.Operation)
                || (index == 0) != (record.Operation == ReplayOperation.InitializeRuntimeGame)
                || record.Before != previous || record.After is null
                || record.Inputs is null || record.StateClockFacts is null
                || record.StateClockFacts.Any(fact => fact is null)
                || record.Observations?.Events is null)
                throw new ReplayJournalValidationException("Replay record order or checkpoint chain is incomplete.");
            foreach (var observation in record.Observations.Events)
            {
                if (observation?.Event is null || observation.Event.Sequence != ++eventSequence)
                    throw new ReplayJournalValidationException("Replay event sequence is incomplete.");
            }
            var eventTimes = record.StateClockFacts.Where(fact => fact.Kind == ReplayClockKind.EventUtc)
                .Select(fact => fact.Value).ToArray();
            if (record.StateClockFacts.Any(fact => !Enum.IsDefined(fact.Kind))
                || !eventTimes.SequenceEqual(record.Observations.Events.Select(observation => observation.Event.OccurredUtc.Ticks)))
                throw new ReplayJournalValidationException("Replay event clock facts disagree with recorded events.");
            if (record.After.EventSequence != eventSequence)
                throw new ReplayJournalValidationException("Replay event counter disagrees with its observations.");
            journal._records.Add(ChangshaReplayStateCodec.CopyRecord(record));
            previous = record.After;
        }
        if (previous != ChangshaReplayStateCodec.Checkpoint(snapshot))
            throw new ReplayJournalValidationException("Snapshot does not match the committed replay prefix.");
        var observedEvents = records.SelectMany(record => record.Observations.Events)
            .Select(observation => observation.Event).ToArray();
        if (ChangshaReplayStateCodec.SerializeRecord(observedEvents)
            != ChangshaReplayStateCodec.SerializeRecord(snapshot.EventLog))
            throw new ReplayJournalValidationException("Snapshot event history differs from the committed journal.");
        journal._head = previous;
        journal.PersistedRecordSequence = records.Count;
        if (snapshot.ClaimWindow is { } window)
        {
            var opening = snapshot.EventLog.LastOrDefault(e => e.EventType == "claim-window-open");
            if (opening is null || opening.TileId != window.DiscardTileId
                || opening.SeatIndex != window.DiscardSeatIndex)
                throw new ReplayJournalValidationException("Recovered claim has no matching recorded opening.");
            journal._windowTokens[window] = opening.Sequence;
        }
        return journal;
    }

    internal void ObserveRecoveredRoundTrip(ChangshaGameState state)
    {
        var before = ChangshaReplayStateCodec.Checkpoint(state);
        if (_head != before)
            throw new ReplayJournalValidationException("Recovered state changed before journal attachment.");
        Append(ReplayOperation.SnapshotRoundTrip, new(), before, state, new ChangshaReplayContext());
    }

    internal List<ChangshaEvent> Apply(ChangshaGameState state, ReplayOperation operation, ReplayInputs inputs)
    {
        ReplayCheckpoint before;
        ChangshaReplayContext context;
        try
        {
            before = ChangshaReplayStateCodec.Checkpoint(state);
            if (_head != before) Invalidate("Unrecorded persisted-state mutation.");
            context = _contextFactory();
        }
        catch (Exception ex)
        {
            Invalidate($"Recording preflight failed: {ex.GetType().Name}.");
            return ChangshaReplayMetadataOperations.Apply(state, operation, inputs, null);
        }
        if (Status != ReplayRecordingStatus.CompletePrefix)
            return ChangshaReplayMetadataOperations.Apply(state, operation, inputs, null);

        List<ChangshaEvent> result;
        try
        {
            result = ChangshaReplayMetadataOperations.Apply(state, operation, inputs, context);
        }
        catch
        {
            try
            {
                if (ChangshaReplayStateCodec.Checkpoint(state) != before)
                    Invalidate("An unsuccessful operation mutated persisted state.");
            }
            catch (Exception)
            {
                Invalidate("An unsuccessful operation left an unreadable state.");
            }
            throw;
        }

        try
        {
            context.Complete();
            Append(operation, inputs, before, state, context);
        }
        catch (Exception ex)
        {
            Invalidate($"Recording completion failed: {ex.GetType().Name}.");
        }
        return result;
    }

    internal IReadOnlyList<ReplayTransition> Records() =>
        _records.Select(ChangshaReplayStateCodec.CopyRecord).ToArray();

    internal ChangshaGameState RoundTrip(ChangshaGameState state)
    {
        var before = ChangshaReplayStateCodec.Checkpoint(state);
        if (before != _head) Invalidate("Unrecorded state before snapshot round trip.");
        var copy = ChangshaReplayStateCodec.RoundTrip(state);
        if (Status == ReplayRecordingStatus.CompletePrefix)
            Append(ReplayOperation.SnapshotRoundTrip, new(), before, copy, new ChangshaReplayContext());
        if (state.ClaimWindow is { } previous && copy.ClaimWindow is { } current
            && _windowTokens.TryGetValue(previous, out var token))
            _windowTokens[current] = token;
        return copy;
    }

    internal IReadOnlyList<ReplayTransition> Pending() =>
        _records.Where(r => r.RecordSequence > PersistedRecordSequence)
            .Select(ChangshaReplayStateCodec.CopyRecord).ToArray();

    internal void MarkPersisted(long sequence)
    {
        if (sequence < PersistedRecordSequence || sequence > Count)
            throw new InvalidOperationException("Invalid replay persistence watermark.");
        PersistedRecordSequence = sequence;
    }

    internal void CheckHead(ChangshaGameState state)
    {
        if (_head != ChangshaReplayStateCodec.Checkpoint(state))
            Invalidate("Snapshot/export does not match the recorded prefix.");
    }

    internal ChangshaReplayEnvelope Export(ChangshaGameState state, ReplayCutKind kind)
    {
        CheckHead(state);
        return new()
        {
            SchemaVersion = ChangshaReplayEnvelope.CurrentSchemaVersion,
            RecordingStatus = Status,
            EngineIdentity = _identity,
            Records = Records(),
            Final = ChangshaReplayStateCodec.Checkpoint(state),
            CutKind = kind,
            Failure = Failure
        };
    }

    internal void Invalidate(string reason)
    {
        Status = ReplayRecordingStatus.CaptureGap;
        Failure ??= reason;
    }

    private void Append(ReplayOperation operation, ReplayInputs inputs, ReplayCheckpoint? before,
        ChangshaGameState state, ChangshaReplayContext context)
    {
        var after = ChangshaReplayStateCodec.Checkpoint(state);
        var record = new ReplayTransition
        {
            FormatVersion = ChangshaReplayEnvelope.TransitionFormatVersion,
            RecordSequence = _records.Count + 1L,
            Operation = operation,
            Inputs = inputs,
            Before = before,
            After = after,
            StateClockFacts = context.Facts,
            Observations = context.Observations
        };
        _records.Add(ChangshaReplayStateCodec.CopyRecord(record));
        if (state.ClaimWindow is { } window
            && context.Observations.Events.LastOrDefault(e => e.Event.EventType == "claim-window-open") is { } opening)
            _windowTokens[window] = opening.Event.Sequence;
        _head = after;
    }
}
