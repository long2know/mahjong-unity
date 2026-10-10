namespace Mahjong.Autotable.Api.Changsha.Replay;

internal static class ChangshaReplayMetadataOperations
{
    internal static ChangshaGameState Initialize(ReplayInitialization input, ChangshaReplayContext? context)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(
            input.Seed, input.BotSeatIndexes, input.BaseUnit, context, input.GameId);
        state.MaxHands = input.MaxHands;
        state.StateVersion = 0;
        state.CreatorPlayerId = string.IsNullOrEmpty(input.CreatorPlayerId) ? null : input.CreatorPlayerId;
        state.DealMode = input.DealMode;
        state.BotDifficulty = input.StoredBotDifficulty;
        state.RequireHandResultAcknowledgements = input.RequireHandResultAcknowledgements;
        return state;
    }

    internal static List<ChangshaEvent> Apply(ChangshaGameState state, ReplayOperation operation,
        ReplayInputs input, ChangshaReplayContext? context)
    {
        switch (operation)
        {
            case ReplayOperation.StartGame: return ChangshaGameStateMachine.StartGame(state, context);
            case ReplayOperation.RollDice:
                return ChangshaGameStateMachine.RollDice(state,
                    new RecordedDiceService(input.Dice ?? throw Missing("dice")), context);
            case ReplayOperation.Deal: return ChangshaGameStateMachine.Deal(state, context);
            case ReplayOperation.BeginManualDeal:
                return ChangshaGameStateMachine.BeginManualDeal(state, input.Dice ?? throw Missing("dice"), context);
            case ReplayOperation.TakeTilesFromWall:
                return ChangshaGameStateMachine.TakeTilesFromWall(state, Seat(input), input.Count ?? throw Missing("count"), context);
            case ReplayOperation.DrawTile: return ChangshaGameStateMachine.DrawTile(state, context);
            case ReplayOperation.Discard:
                return ChangshaGameStateMachine.Discard(state, Seat(input), input.TileId ?? throw Missing("tileId"), context);
            case ReplayOperation.ResolveClaim:
                return ChangshaGameStateMachine.ResolveClaim(state, Seat(input),
                    input.ClaimType ?? throw Missing("claimType"), input.ChosenTileIds, context);
            case ReplayOperation.PassClaim: return ChangshaGameStateMachine.PassClaim(state, context);
            case ReplayOperation.DeclareSelfDrawWin:
                return ChangshaGameStateMachine.DeclareSelfDrawWin(state, Seat(input), context);
            case ReplayOperation.DeclareConcealedKong:
                return ChangshaGameStateMachine.DeclareConcealedKong(state, Seat(input),
                    input.LogicalTile ?? throw Missing("logicalTile"), context);
            case ReplayOperation.DeclareAddedKong:
                return ChangshaGameStateMachine.DeclareAddedKong(state, Seat(input),
                    input.TileId ?? throw Missing("tileId"), context);
            case ReplayOperation.Score: return ChangshaGameStateMachine.Score(state, null, context);
            case ReplayOperation.HandleWallExhausted: return ChangshaGameStateMachine.HandleWallExhausted(state, context);
            case ReplayOperation.RotateBanker:
                if (state.HandResultContinuation?.WaitingSeats(state).Length > 0)
                    throw new InvalidOperationException("The hand result is awaiting human acknowledgement.");
                var rotation = ChangshaGameStateMachine.RotateBanker(state, context);
                state.HandResultContinuation = null;
                return rotation;
            case ReplayOperation.EnableHandResultAcknowledgements:
                if (!state.RequireHandResultAcknowledgements)
                {
                    state.RequireHandResultAcknowledgements = true;
                    state.StateVersion = checked(state.StateVersion + 1);
                }
                break;
            case ReplayOperation.OpenHandResultContinuation:
                if (state.Phase != ChangshaPhase.EndHand || !state.RequireHandResultAcknowledgements
                    || state.HandResultContinuation is not null)
                    throw new InvalidOperationException("A hand-result barrier cannot be opened here.");
                var token = input.ResultToken ?? throw Missing("resultToken");
                if (!Guid.TryParseExact(token, "N", out _))
                    throw new InvalidOperationException("Invalid hand-result token.");
                state.HandResultContinuation = new()
                {
                    HandNumber = state.HandNumber,
                    ResultToken = token,
                    RequiredPlayers = state.Seats.Where(ChangshaHandResultContinuation.IsParticipant)
                        .OrderBy(seat => seat.SeatIndex).ToDictionary(seat => seat.SeatIndex, seat => seat.PlayerId)
                };
                state.StateVersion = checked(state.StateVersion + 1);
                break;
            case ReplayOperation.AcknowledgeHandResult:
                var continuation = state.HandResultContinuation;
                var acknowledgingSeat = Seat(input);
                if (state.Phase != ChangshaPhase.EndHand || continuation is null
                    || input.HandNumber != state.HandNumber || continuation.HandNumber != state.HandNumber
                    || !string.Equals(input.ResultToken, continuation.ResultToken, StringComparison.Ordinal)
                    || !continuation.RequiredSeats(state).Contains(acknowledgingSeat)
                    || !string.Equals(continuation.RequiredPlayers[acknowledgingSeat], input.PlayerId, StringComparison.Ordinal)
                    || !continuation.AcknowledgedSeats.Add(acknowledgingSeat))
                    throw new InvalidOperationException("Invalid recorded hand-result acknowledgement.");
                state.StateVersion = checked(state.StateVersion + 1);
                break;
            case ReplayOperation.BindHumanSeat:
                var playerId = input.PlayerId ?? throw Missing("playerId");
                var human = state.Seats[Seat(input)];
                human.IsBot = false;
                human.PlayerId = playerId;
                break;
            case ReplayOperation.BindBotSeat:
                var botSeat = Seat(input);
                state.Seats[botSeat].IsBot = true;
                state.Seats[botSeat].PlayerId = $"bot-{botSeat}";
                break;
            case ReplayOperation.ReleaseSeatIdentity:
                state.Seats[Seat(input)].IsBot = false;
                state.Seats[Seat(input)].PlayerId = string.Empty;
                break;
            case ReplayOperation.SetDealMode:
                state.DealMode = input.DealMode ?? throw Missing("dealMode");
                break;
            case ReplayOperation.SetBotStrategyMetadata:
                state.BotDifficulty = input.Difficulty ?? throw Missing("difficulty");
                break;
            case ReplayOperation.SetPublicMetadata:
                state.IsPublic = input.IsPublic ?? throw Missing("isPublic");
                if (!state.IsPublic) state.PublicName = null;
                else if (input.PublicName is not null)
                {
                    var trimmed = input.PublicName.Trim();
                    state.PublicName = trimmed.Length == 0 ? null : trimmed.Length > 64 ? trimmed[..64] : trimmed;
                }
                break;
            case ReplayOperation.TransferHost:
                state.CreatorPlayerId = input.PlayerId ?? throw Missing("playerId");
                break;
            case ReplayOperation.BindAuthoritativeGameId:
                state.GameId = input.GameId ?? throw Missing("gameId");
                break;
            case ReplayOperation.ObserveClaimWindowSchedule:
                var schedule = input.Schedule ?? throw Missing("schedule");
                ValidateSchedule(schedule);
                context?.ObserveSchedule(schedule);
                break;
            case ReplayOperation.MarkRemoved:
                state.Phase = ChangshaPhase.GameComplete;
                state.IsGameComplete = true;
                state.HandResultContinuation = null;
                break;
            default:
                throw new InvalidOperationException($"Operation requires initialization/recovery dispatch or is unknown: {operation}.");
        }
        return [];
    }

    internal static void ValidateSchedule(ReplayWindowSchedule schedule)
    {
        var deadline = schedule.ConfiguredTimeoutMs > 0 && schedule.OpenedAtUnixMs > 0
            ? schedule.OpenedAtUnixMs + schedule.ConfiguredTimeoutMs : 0;
        if (schedule.OpeningEventSequence <= 0 || schedule.DeadlineUnixMs != deadline)
            throw new InvalidOperationException("Invalid recorded claim deadline.");
    }

    private static int Seat(ReplayInputs input)
    {
        var seat = input.SeatIndex ?? throw Missing("seatIndex");
        if (seat is < 0 or > 3) throw new InvalidOperationException("Invalid recorded seat.");
        return seat;
    }

    private static InvalidOperationException Missing(string name) => new($"Missing replay argument: {name}.");
}
