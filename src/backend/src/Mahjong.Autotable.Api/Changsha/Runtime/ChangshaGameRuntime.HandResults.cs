using Mahjong.Autotable.Api.Changsha.Replay;

namespace Mahjong.Autotable.Api.Changsha.Runtime;

public sealed partial class ChangshaGameRuntime
{
    public async Task EnableHandResultAcknowledgementsAsync(string gameId, CancellationToken ct = default)
    {
        var instance = Require(gameId);
        using var lifetime = instance.EnterOperation();
        await instance.Lock.WaitAsync(ct);
        try
        {
            ValidateHandResultContinuation(instance.State);
            if (instance.State.IsGameComplete || instance.State.Phase == ChangshaPhase.GameComplete) return;
            var changed = !instance.State.RequireHandResultAcknowledgements;
            if (changed)
                ApplyRecordedChange(instance, ReplayOperation.EnableHandResultAcknowledgements);
            changed |= PrepareHandResultContinuation(instance);
            if (changed) await PersistSnapshotAsync(instance, ct);
        }
        finally { instance.Lock.Release(); }
    }

    public async Task AcknowledgeHandResultAsync(string gameId, string playerId, string connectionId,
        int handNumber, string resultToken, CancellationToken ct = default)
    {
        var instance = Require(gameId);
        using var lifetime = instance.EnterOperation();
        bool ready;
        await instance.Lock.WaitAsync(ct);
        try
        {
            var seat = instance.State.Seats.FirstOrDefault(candidate => !candidate.IsBot
                && string.Equals(candidate.PlayerId, playerId, StringComparison.Ordinal)
                && instance.SeatConnections.TryGetValue(candidate.SeatIndex, out var owner)
                && string.Equals(owner, connectionId, StringComparison.Ordinal));
            if (seat is null)
                throw new HandResultAcknowledgementException("connection-owns-no-seat");
            if (instance.State.HandNumber != handNumber)
                throw new HandResultAcknowledgementException("stale-hand-result");
            var continuation = instance.State.HandResultContinuation;
            if (instance.State.Phase != ChangshaPhase.EndHand || continuation is null)
                throw new HandResultAcknowledgementException("hand-result-not-pending");
            if (continuation.HandNumber != handNumber
                || !string.Equals(continuation.ResultToken, resultToken, StringComparison.Ordinal))
                throw new HandResultAcknowledgementException("stale-hand-result");
            if (!continuation.RequiredSeats(instance.State).Contains(seat.SeatIndex))
                throw new HandResultAcknowledgementException("hand-result-not-required");
            if (continuation.AcknowledgedSeats.Contains(seat.SeatIndex))
                throw new HandResultAcknowledgementException("hand-result-already-acknowledged");

            ApplyRecordedChange(instance, ReplayOperation.AcknowledgeHandResult, new()
            {
                SeatIndex = seat.SeatIndex, PlayerId = playerId,
                HandNumber = handNumber, ResultToken = resultToken
            });
            ready = continuation.WaitingSeats(instance.State).Length == 0;
            await PersistSnapshotAsync(instance, ct);
        }
        finally { instance.Lock.Release(); }

        // The operation lease spans this continuation. A second arrival, old timer or
        // recovery resume must recheck both the phase and this settlement's identity.
        if (ready)
            await StartNextHandOrEndAsync(instance, instance.LifecycleCts.Token, handNumber, resultToken);
    }

    private static bool PrepareHandResultContinuation(ChangshaGameInstance instance)
    {
        var state = instance.State;
        if (state.IsGameComplete || state.Phase != ChangshaPhase.EndHand || !state.RequireHandResultAcknowledgements
            || state.HandResultContinuation is not null
            || !state.Seats.Any(ChangshaHandResultContinuation.IsParticipant))
            return false;

        // Use the existing rotation policy to detect BOTH match-terminal conditions
        // without changing banker, totals, wall, counters or replay clocks.
        var next = CopyState(state);
        ChangshaGameStateMachine.RotateBanker(next);
        if (next.IsGameComplete) return false;

        ApplyRecordedChange(instance, ReplayOperation.OpenHandResultContinuation,
            new() { ResultToken = Guid.NewGuid().ToString("N") });
        return true;
    }

    private static void ValidateHandResultContinuation(ChangshaGameState state)
    {
        if (state.HandResultContinuation is not { } continuation) return;
        if (!state.RequireHandResultAcknowledgements || state.IsGameComplete || state.Phase != ChangshaPhase.EndHand
            || continuation.HandNumber != state.HandNumber
            || !Guid.TryParseExact(continuation.ResultToken, "N", out _)
            || continuation.RequiredPlayers is null || continuation.AcknowledgedSeats is null
            || continuation.RequiredPlayers.Any(pair => pair.Key is < 0 or > 3 || string.IsNullOrEmpty(pair.Value))
            || continuation.AcknowledgedSeats.Any(seat => !continuation.RequiredPlayers.ContainsKey(seat)))
            throw new PublicRoomRecoveryException("room-hand-result-invalid");
    }
}

public sealed class HandResultAcknowledgementException(string reason) : InvalidOperationException(reason)
{
    public string Reason { get; } = reason;
}
