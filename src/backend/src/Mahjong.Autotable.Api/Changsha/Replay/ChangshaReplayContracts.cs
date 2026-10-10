namespace Mahjong.Autotable.Api.Changsha.Replay;

internal enum ReplayOperation
{
    InitializeRuntimeGame, StartGame, RollDice, Deal, BeginManualDeal, TakeTilesFromWall,
    DrawTile, Discard, ResolveClaim, PassClaim, DeclareSelfDrawWin, DeclareConcealedKong,
    DeclareAddedKong, Score, HandleWallExhausted, RotateBanker,
    BindHumanSeat, BindBotSeat, ReleaseSeatIdentity, SetDealMode, SetBotStrategyMetadata,
    SetPublicMetadata, TransferHost, BindAuthoritativeGameId, SnapshotRoundTrip,
    ObserveClaimWindowSchedule, MarkRemoved,
    EnableHandResultAcknowledgements, OpenHandResultContinuation, AcknowledgeHandResult
}

internal enum ReplayClockKind { EventUtc, ClaimOpenedUnixMs }
internal enum ReplayRecordingStatus { CompletePrefix, LegacyUnverifiable, CaptureGap }
internal enum ReplayCutKind { Checkpoint, NaturalCompletion, Removed }

internal sealed record ReplayClockFact(ReplayClockKind Kind, long Value);
internal sealed record ReplayEngineIdentity(string AssemblySha256, string CoreLibrarySha256,
    string Profile, string Codec, string Rng);

internal sealed record ReplayTimingOptions
{
    public int ClaimWindowTimeoutMs { get; init; } = 5000;
    public int BotTurnDelayMs { get; init; } = 350;
    public int BotClaimDelayMs { get; init; } = 250;
    public int BotPickupDelayMs { get; init; } = 500;
    public int DealBatchDelayMs { get; init; }
    public int BotDecisionTimeoutMs { get; init; } = 2000;
    public bool PersistSnapshots { get; init; } = true;
}

internal sealed record ReplayInitialization
{
    public required int Seed { get; init; }
    public string? GameId { get; init; }
    public ReplayEngineIdentity? EngineIdentity { get; init; }
    public int[] BotSeatIndexes { get; init; } = [1, 2, 3];
    public int BaseUnit { get; init; } = 1;
    public int MaxHands { get; init; } = 4;
    public string? CreatorPlayerId { get; init; }
    public DealMode DealMode { get; init; }
    public string? StoredBotDifficulty { get; init; }
    public bool RequireHandResultAcknowledgements { get; init; }
    public string EffectiveBotDifficulty { get; init; } = "medium";
    public ReplayTimingOptions Timing { get; init; } = new();
}

internal sealed record ReplayWindowSchedule
{
    public required long OpeningEventSequence { get; init; }
    public required long OpenedAtUnixMs { get; init; }
    public required int ConfiguredTimeoutMs { get; init; }
    public required int ScheduledDelayMs { get; init; }
    public required long DeadlineUnixMs { get; init; }
    public bool IsResume { get; init; }
}

internal sealed record ReplayInputs
{
    public ReplayInitialization? Initialization { get; init; }
    public int? SeatIndex { get; init; }
    public int? TileId { get; init; }
    public int? LogicalTile { get; init; }
    public int? Count { get; init; }
    public Tables.TableClaimType? ClaimType { get; init; }
    public int[]? ChosenTileIds { get; init; }
    public DiceRoll? Dice { get; init; }
    public int? DiceSeed { get; init; }
    public string? PlayerId { get; init; }
    public string? Difficulty { get; init; }
    public string? PublicName { get; init; }
    public bool? IsPublic { get; init; }
    public DealMode? DealMode { get; init; }
    public string? GameId { get; init; }
    public long? WindowOpeningSequence { get; init; }
    public string? ResolutionSource { get; init; }
    public ReplayWindowSchedule? Schedule { get; init; }
    public int? HandNumber { get; init; }
    public string? ResultToken { get; init; }
}

internal sealed record ReplayCheckpoint(string FullStateHash, ChangshaPhase Phase,
    int HandNumber, int TurnNumber, int StateVersion, long EventSequence);

internal sealed record ReplayEventObservation(ChangshaEvent Event, int HandNumber, int StateVersion);

internal sealed record ReplayObservations
{
    public IReadOnlyList<ReplayEventObservation> Events { get; init; } = [];
    public int[]? AcceptedChowPartners { get; init; }
    public ReplayWindowSchedule? WindowSchedule { get; init; }
}

internal sealed record ReplayTransition
{
    public required int FormatVersion { get; init; }
    public required long RecordSequence { get; init; }
    public required ReplayOperation Operation { get; init; }
    public required ReplayInputs Inputs { get; init; }
    public ReplayCheckpoint? Before { get; init; }
    public required ReplayCheckpoint After { get; init; }
    public required IReadOnlyList<ReplayClockFact> StateClockFacts { get; init; }
    public required ReplayObservations Observations { get; init; }
}

internal sealed record ChangshaReplayEnvelope
{
    public const int CurrentSchemaVersion = 3;
    public const int TransitionFormatVersion = 2;
    public const string DatabaseEventType = "replay-v3-step";

    public required int SchemaVersion { get; init; }
    public required ReplayRecordingStatus RecordingStatus { get; init; }
    public required ReplayEngineIdentity EngineIdentity { get; init; }
    public required IReadOnlyList<ReplayTransition> Records { get; init; }
    public required ReplayCheckpoint Final { get; init; }
    public required ReplayCutKind CutKind { get; init; }
    public string? Failure { get; init; }
}

internal sealed class ReplayJournalValidationException(string message) : InvalidOperationException(message);
