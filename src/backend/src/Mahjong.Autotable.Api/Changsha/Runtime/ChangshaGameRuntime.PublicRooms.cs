using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Bot;
using Mahjong.Autotable.Api.Changsha.Scoring;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Changsha.Runtime;

public sealed partial class ChangshaGameRuntime
{
    private readonly ConcurrentDictionary<string, string> _publicRoomBindings = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _publicRoomLock = new(1, 1);

    private static string PublicRoomKey(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId) || roomId.Length > 64
            || !string.Equals(roomId, roomId.Trim(), StringComparison.Ordinal)
            || roomId.Any(char.IsControl))
            throw new PublicRoomRecoveryException("invalid-public-room");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(roomId))).ToLowerInvariant();
    }

    public Task<string?> RestorePublicRoomAsync(string roomId, CancellationToken ct = default) =>
        RunRuntimeAdmissionAsync<string?>(admission => RestorePublicRoomAdmittedAsync(admission, roomId, ct));

    private async Task<string?> RestorePublicRoomAdmittedAsync(
        RuntimeAdmission admission, string roomId, CancellationToken ct)
    {
        var key = PublicRoomKey(roomId);
        await _publicRoomLock.WaitAsync(ct);
        try
        {
            if (_publicRoomBindings.TryGetValue(roomId, out var cached) && _games.ContainsKey(cached))
                return cached;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var binding = await db.AutotableRoomBindings.AsNoTracking()
                .SingleOrDefaultAsync(row => row.RoomKey == key, ct);
            if (binding is null) return null;
            if (!string.Equals(binding.RoomId, roomId, StringComparison.Ordinal))
                throw new PublicRoomRecoveryException("room-binding-conflict");

            var gameId = binding.RuntimeGameId.ToString();
            if (!_games.ContainsKey(gameId))
            {
                var row = await db.ChangshaGames.AsNoTracking()
                    .SingleOrDefaultAsync(game => game.Id == binding.RuntimeGameId, ct);
                if (row is null || string.IsNullOrEmpty(row.StateJson))
                    throw new PublicRoomRecoveryException("room-snapshot-unavailable");
                var state = JsonSerializer.Deserialize<ChangshaGameState>(row.StateJson, SnapshotJson)
                    ?? throw new PublicRoomRecoveryException("room-snapshot-unavailable");
                if (!Guid.TryParse(state.GameId, out var stateId) || stateId != binding.RuntimeGameId)
                    throw new PublicRoomRecoveryException("room-snapshot-identity-mismatch");
                ChangshaBaseUnit.Validate(state.BaseUnit);
                var instance = await CreateRecoveredInstance(admission, db, row, gameId, state, ct);
                TryPublishAdmittedInstance(admission, instance);
            }
            PublishRecoveredBinding(roomId, gameId);
            return gameId;
        }
        catch (DbException ex)
        {
            throw new PublicRoomRecoveryException("room-store-unavailable", ex);
        }
        catch (JsonException ex)
        {
            throw new PublicRoomRecoveryException("room-snapshot-invalid", ex);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new PublicRoomRecoveryException("room-snapshot-invalid", ex);
        }
        finally
        {
            _publicRoomLock.Release();
        }
    }

    private async Task<ChangshaGameInstance> CreateRecoveredInstance(
        RuntimeAdmission admission, AppDbContext db, ChangshaGame row, string gameId, ChangshaGameState state, CancellationToken ct)
    {
        if (!HasValidRecoveredStructure(state))
            throw new PublicRoomRecoveryException("room-snapshot-invalid");

        IChangshaBotStrategy? strategy = null;
        if (state.BotDifficulty is not null)
        {
            strategy = ChangshaBotEngine.Resolve(state.BotDifficulty);
            if (!string.Equals(strategy.Difficulty, state.BotDifficulty, StringComparison.OrdinalIgnoreCase))
                throw new PublicRoomRecoveryException("room-strategy-unavailable");
        }
        var journal = await LoadRecoveredReplayJournalAsync(db, row, state, ct);
        if (journal is not null)
        {
            journal.ObserveRecoveredRoundTrip(state);
            journal.Apply(state, Replay.ReplayOperation.BindAuthoritativeGameId, new() { GameId = gameId });
            if (journal.Status != Replay.ReplayRecordingStatus.CompletePrefix)
                throw new PublicRoomRecoveryException("room-replay-invalid");
        }
        else
        {
            state.GameId = gameId;
        }
        var instance = new ChangshaGameInstance(gameId, state, this)
        {
            RecoveryPending = true,
            WasRecovered = true,
            BotStrategy = strategy,
            ReplayJournal = journal
        };
        admission.Own(instance);
        foreach (var seat in state.Seats.Where(seat => !seat.IsBot
            && !string.IsNullOrEmpty(seat.PlayerId)
            && !string.Equals(seat.PlayerId, $"human-{seat.SeatIndex}", StringComparison.Ordinal)))
            instance.RecoveredSeatOwners[seat.SeatIndex] = seat.PlayerId;
        if (instance.ReplayJournal is not null && _options.PersistSnapshots)
        {
            // Admission owns cleanup; preserve the recovery error even if
            // disposing this unpublished candidate also fails.
            try { await WriteSnapshotAsync(instance, ct); }
            catch (DbUpdateException ex)
            {
                throw new PublicRoomRecoveryException("room-replay-persistence-failed", ex);
            }
            catch (DbException ex)
            {
                throw new PublicRoomRecoveryException("room-store-unavailable", ex);
            }
            catch (Replay.ReplayJournalValidationException ex)
            {
                throw new PublicRoomRecoveryException("room-replay-invalid", ex);
            }
        }
        return instance;
    }

    private static bool HasValidRecoveredStructure(ChangshaGameState state)
    {
        // Validate before allocating an instance or publishing either recovery cache.
        // This is a shape/bounds check, not a hand-legality solver or a repair/redeal.
        if (state.Seats is not { Count: 4 } || state.Hands is not { Count: 4 }
            || InvalidRecoveredTiles(state.Wall, 108)
            || state.DiscardPile is null || state.MissedWinSeats is null
            || state.FalseHuPenalties is null || state.CumulativeScores is null
            || state.EventLog is null)
            return false;

        // Ordinal 18 is the legacy terminal value already recognized by HydrateAsync.
        if ((!Enum.IsDefined(state.Phase) && (int)state.Phase != 18)
            || !Enum.IsDefined(state.DealMode)
            || state.DealerSeatIndex is < 0 or > 3 || state.ActiveSeatIndex is < 0 or > 3
            || state.PickupSeatIndex is < 0 or > 3 || state.LastDrawSeatIndex is < 0 or > 3
            || state.MissedWinSeats.Any(seat => seat is < 0 or > 3)
            || state.CumulativeScores.Keys.Any(seat => seat is < 0 or > 3)
            || (ChangshaGameStateMachine.IsPickupPhase(state.Phase) && state.PickupSeatIndex is null)
            || (state.Phase == ChangshaPhase.AwaitingClaim && state.ClaimWindow is null))
            return false;

        for (var index = 0; index < 4; index++)
        {
            var seat = state.Seats[index];
            var hand = state.Hands[index];
            if (seat is null || seat.SeatIndex != index
                || hand is null || hand.SeatIndex != index
                || InvalidRecoveredTiles(hand.ConcealedTiles, 14)
                || hand.Melds is null || hand.Melds.Count > 4)
                return false;
            if (hand.Melds.Any(meld => meld is null || !Enum.IsDefined(meld.Kind)
                || InvalidRecoveredTiles(meld.TileIds, 4)
                || meld.TileIds.Count != (meld.Kind is MeldKind.Chow or MeldKind.Pung ? 3 : 4)
                || meld.ClaimedFromSeatIndex is < 0 or > 3))
                return false;
        }

        if (state.DiscardPile.Any(discard => discard is null
                || discard.SeatIndex is < 0 or > 3 || discard.TileId is < 0 or >= 108)
            || state.EventLog.Any(entry => entry is null)
            || state.FalseHuPenalties.Any(penalty => penalty is null
                || penalty.OffendingSeatIndex is < 0 or > 3
                || InvalidRecoveredPayments(penalty.Payments)))
            return false;

        if (state.ClaimWindow is { } claim
            && (claim.DiscardSeatIndex is < 0 or > 3 || claim.DiscardTileId is < 0 or >= 108
                || claim.KongDeclarerSeatIndex is < 0 or > 3 || claim.Opportunities is null
                || claim.Opportunities.Any(opportunity => opportunity is null
                    || opportunity.SeatIndex is < 0 or > 3 || !Enum.IsDefined(opportunity.ClaimType))))
            return false;
        if (state.CurrentWin is { } win
            && (win.AllPatterns is null || win.PatternKeys is null
                || win.PatternKeys.Any(key => key is null)
                || win.WinningSeatIndex is < 0 or > 3 || win.SourceSeatIndex is < 0 or > 3
                || win.WinningTileId is < 0 or >= 108))
            return false;
        if (state.CurrentScore is { } score
            && (InvalidRecoveredPayments(score.Payments) || score.Fans is null
                || score.Fans.Any(fan => fan is null || !FanCatalog.Entries.ContainsKey(fan.Fan))))
            return false;
        return HasValidRecoveredContinuation(state);
    }

    private static bool HasValidRecoveredContinuation(ChangshaGameState state)
    {
        if (state.Phase == ChangshaPhase.AwaitingDiscard)
            return state.ClaimWindow is null
                && RecoveredEffectiveCount(state.Hands[state.ActiveSeatIndex]) is 13 or 14;

        if (ChangshaGameStateMachine.IsPickupPhase(state.Phase))
        {
            var offset = state.PickupRoundIndex;
            if (offset is < 0 or >= 4
                || (state.Phase is ChangshaPhase.BreakPointMarked or ChangshaPhase.DealerExtra && offset != 0)
                || state.PickupSeatIndex != (state.DealerSeatIndex + offset) % 4)
                return false;

            var count = ChangshaGameStateMachine.ExpectedPickupCount(state.Phase);
            if (state.Wall.Count < count)
                return false;
            var beforeRound = state.Phase switch
            {
                ChangshaPhase.PickupRound2 => 4,
                ChangshaPhase.PickupRound3 => 8,
                ChangshaPhase.SingleTilePickup => 12,
                ChangshaPhase.DealerExtra => 13,
                _ => 0
            };
            // The cursor describes completed pickups, not just the next seat.
            // Mismatched prefixes can repeat a take or finish with an unusable hand.
            for (var seatOffset = 0; seatOffset < 4; seatOffset++)
            {
                var hand = state.Hands[(state.DealerSeatIndex + seatOffset) % 4];
                if (hand.Melds.Count != 0
                    || hand.ConcealedTiles.Count != beforeRound + (seatOffset < offset ? count : 0))
                    return false;
            }
        }

        if (state.Phase != ChangshaPhase.AwaitingClaim)
            return true;
        if (state.ClaimWindow is not { } window)
            return false;
        if (!window.IsKongRobbing)
            return state.Wall.Count == 0
                || RecoveredEffectiveCount(state.Hands[(window.DiscardSeatIndex + 1) % 4]) == 13;

        // All-pass resumes the saved declaration; its pung and fourth tile must
        // still exist before either a FULL snapshot or the claim timer is published.
        if (window.Opportunities.Any(opportunity => opportunity.ClaimType != Tables.TableClaimType.Hu)
            || window.KongDeclarerSeatIndex is not { } declarer || declarer != window.DiscardSeatIndex)
            return false;
        var declarerHand = state.Hands[declarer];
        var logicalTile = ChangshaDeckBuilder.GetLogicalTile(window.DiscardTileId);
        return RecoveredEffectiveCount(declarerHand) == 14
            && declarerHand.ConcealedTiles.Contains(window.DiscardTileId)
            && declarerHand.Melds.Any(meld => meld.Kind == MeldKind.Pung
                && meld.TileIds.All(tile => ChangshaDeckBuilder.GetLogicalTile(tile) == logicalTile));
    }

    private static int RecoveredEffectiveCount(ChangshaHandState hand) =>
        hand.ConcealedTiles.Count + 3 * hand.Melds.Count;

    private static bool InvalidRecoveredTiles(IReadOnlyCollection<int>? tiles, int maximumCount) =>
        tiles is null || tiles.Count > maximumCount || tiles.Any(tile => tile is < 0 or >= 108);

    private static bool InvalidRecoveredPayments(IReadOnlyCollection<PaymentEntry>? payments) =>
        payments is null || payments.Any(payment => payment is null
            || payment.FromSeatIndex is < 0 or > 3 || payment.ToSeatIndex is < 0 or > 3);

    public Task EnsurePublicRoomCreationAllowedAsync(
        string? playerId, bool explicitNew, CancellationToken ct = default) =>
        RunRuntimeAdmissionAsync(_ => EnsurePublicRoomCreationAllowedCoreAsync(playerId, explicitNew, ct));

    private async Task EnsurePublicRoomCreationAllowedCoreAsync(
        string? playerId, bool explicitNew, CancellationToken ct)
    {
        if (explicitNew || string.IsNullOrEmpty(playerId)) return;
        var candidates = _games.Values.Where(instance => instance.WasRecovered
                && !instance.State.IsGameComplete && instance.State.Phase != ChangshaPhase.WallExhausted
                && instance.State.Seats.Any(seat => !seat.IsBot
                    && string.Equals(seat.PlayerId, playerId, StringComparison.Ordinal)))
            .Select(instance => Guid.Parse(instance.GameId)).ToArray();
        if (candidates.Length == 0) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var bound = await db.AutotableRoomBindings.AsNoTracking()
                .Where(binding => candidates.Contains(binding.RuntimeGameId))
                .Select(binding => binding.RuntimeGameId).ToListAsync(ct);
            // Absence is ambiguous, not evidence that any particular game owns the alias.
            if (candidates.Except(bound).Any())
                throw new PublicRoomRecoveryException("legacy-room-binding-unavailable");
        }
        catch (DbException ex)
        {
            throw new PublicRoomRecoveryException("room-store-unavailable", ex);
        }
    }

    private async Task PublishPublicRoomAsync(
        RuntimeAdmission admission, ChangshaGameInstance instance, PublicRoomCreation room, CancellationToken ct)
    {
        _ = PublicRoomKey(room.RoomId);
        await _publicRoomLock.WaitAsync(ct);
        try
        {
            if (_publicRoomBindings.TryGetValue(room.RoomId, out var current)
                && !string.Equals(current, room.ReplacesRuntimeGameId, StringComparison.Ordinal))
                throw new PublicRoomRecoveryException("room-already-bound");

            await EnsurePublicRoomCreationAllowedCoreAsync(instance.State.CreatorPlayerId, room.IsExplicitNew, ct);
            if (_options.PersistSnapshots)
                await WriteSnapshotAsync(instance, ct, room);
            PublishCreatedInstance(admission, instance, room.RoomId);
        }
        catch (DbUpdateException ex)
        {
            throw new PublicRoomRecoveryException("room-persistence-failed", ex);
        }
        catch (DbException ex)
        {
            throw new PublicRoomRecoveryException("room-store-unavailable", ex);
        }
        finally
        {
            _publicRoomLock.Release();
        }
    }

    private static async Task WritePublicRoomBindingAsync(
        AppDbContext db, Guid runtimeId, PublicRoomCreation room, CancellationToken ct)
    {
        var key = PublicRoomKey(room.RoomId);
        var existing = await db.AutotableRoomBindings.SingleOrDefaultAsync(row => row.RoomKey == key, ct);
        if (existing is null)
        {
            db.AutotableRoomBindings.Add(new AutotableRoomBinding
            {
                RoomKey = key,
                RoomId = room.RoomId,
                RuntimeGameId = runtimeId,
                CreatedUtc = DateTime.UtcNow
            });
            return;
        }
        if (!string.Equals(existing.RoomId, room.RoomId, StringComparison.Ordinal)
            || !Guid.TryParse(room.ReplacesRuntimeGameId, out var previousId)
            || existing.RuntimeGameId != previousId)
            throw new PublicRoomRecoveryException("room-already-bound");
        existing.RuntimeGameId = runtimeId;
    }

    public async Task ResumeRecoveredPublicRoomAsync(string gameId, CancellationToken ct = default)
    {
        var instance = Require(gameId);
        using var lifetime = instance.EnterOperation();
        ChangshaPhase phase;
        long claimOpenedAt;
        await instance.Lock.WaitAsync(ct);
        try
        {
            if (!instance.RecoveryPending) return;
            instance.RecoveryPending = false;
            phase = instance.State.Phase;
            claimOpenedAt = instance.State.ClaimWindow?.OpenedAtUnixMs ?? 0;
        }
        finally { instance.Lock.Release(); }

        if (phase == ChangshaPhase.AwaitingClaim)
        {
            int? remaining = null;
            if (_options.ClaimWindowTimeoutMs >= 0 && claimOpenedAt > 0)
                remaining = (int)Math.Clamp(
                    claimOpenedAt + _options.ClaimWindowTimeoutMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    0, _options.ClaimWindowTimeoutMs);
            await OpenClaimWindowAsync(instance, ct, remaining);
        }
        else if (phase == ChangshaPhase.EndHand)
        {
            await StartNextHandOrEndAsync(instance, ct);
        }
        else if (phase == ChangshaPhase.WallExhausted)
        {
            await HandleWallExhaustedAsync(instance, ct);
        }
        else if (phase == ChangshaPhase.AwaitingDiscard)
        {
            await DriveAfterAdvanceAsync(instance, ct, resuming: true);
        }
        else
        {
            await ScheduleBotIfNeededAsync(instance, ct);
        }
    }
}
