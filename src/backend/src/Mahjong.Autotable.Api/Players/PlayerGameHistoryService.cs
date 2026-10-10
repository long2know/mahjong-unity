using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Players;

/// <summary>
/// Phase K Wave 1 — per-player game-history denormalization writer.
///
/// <para>One row per (human PlayerId, GameId) written at game completion
/// by <c>ChangshaGameRuntime.EmitGameCompletedAsync</c>. Powers the
/// public <c>GET /api/games?playerId=…</c> match-history surface
/// without a JSON-scan of <see cref="ChangshaGame.StateJson"/>. The
/// service is intentionally idempotent — re-completion (the rare
/// hydrate-then-replay path) refreshes the existing row rather than
/// stacking duplicates.</para>
///
/// <para>Bots are filtered (any <c>PlayerId</c> starting with
/// <c>"bot-"</c>); only verified human PlayerIds get a row. This
/// mirrors <see cref="PlayerProfileService"/>'s posture so the two
/// per-completion writers stay in shape-sync.</para>
/// </summary>
public sealed class PlayerGameHistoryService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PlayerGameHistoryService> _logger;

    public PlayerGameHistoryService(IServiceScopeFactory scopeFactory, ILogger<PlayerGameHistoryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Records every human seat's per-player history row for the
    /// supplied game state. Best-effort: DB exceptions are logged and
    /// swallowed so a history-write hiccup never breaks the
    /// game-completion hot path.
    /// </summary>
    public async Task RecordAsync(
        Guid gameId,
        DateTime startedAt,
        ChangshaGameState state,
        Guid? rulePresetId,
        CancellationToken ct = default)
    {
        if (state is null) return;

        try
        {
            // Project per-seat data once outside the scope so the DB
            // operation is a single SaveChangesAsync transaction.
            var topScore = state.CumulativeScores.Count == 0
                ? 0
                : state.CumulativeScores.Values.Max();

            var humanSeats = state.Seats
                .Where(s => !string.IsNullOrEmpty(s.PlayerId)
                            && !s.PlayerId.StartsWith("bot-", StringComparison.Ordinal))
                .OrderBy(s => s.SeatIndex)
                .ToList();
            if (humanSeats.Count == 0) return;

            var completedAt = DateTime.UtcNow;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            foreach (var seat in humanSeats)
            {
                var score = state.CumulativeScores.TryGetValue(seat.SeatIndex, out var s) ? s : 0;
                var opponents = humanSeats
                    .Where(o => o.SeatIndex != seat.SeatIndex)
                    .Select(o => o.PlayerId)
                    .ToArray();
                var opponentsCsv = string.Join(",", opponents);
                // Truncate to the column cap so a 16-player hypothetical
                // future never overflows. 1024 is more than enough for
                // the 3 opponent ids the 4-seat Changsha format ever
                // produces; the clamp is purely defence-in-depth.
                if (opponentsCsv.Length > 1024) opponentsCsv = opponentsCsv[..1024];

                var existing = await db.PlayerGameHistory
                    .FirstOrDefaultAsync(h => h.PlayerId == seat.PlayerId && h.GameId == gameId, ct);
                if (existing is null)
                {
                    db.PlayerGameHistory.Add(new PlayerGameHistory
                    {
                        PlayerId = seat.PlayerId,
                        GameId = gameId,
                        SeatIndex = seat.SeatIndex,
                        FinalScore = score,
                        Won = score == topScore,
                        StartedAt = startedAt,
                        CompletedAt = completedAt,
                        OpponentPlayerIdsCsv = opponentsCsv,
                        RulePresetId = rulePresetId,
                    });
                }
                else
                {
                    existing.SeatIndex = seat.SeatIndex;
                    existing.FinalScore = score;
                    existing.Won = score == topScore;
                    existing.StartedAt = startedAt;
                    existing.CompletedAt = completedAt;
                    existing.OpponentPlayerIdsCsv = opponentsCsv;
                    existing.RulePresetId = rulePresetId;
                }
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recording PlayerGameHistory for game {GameId} failed; surface will be stale.", gameId);
        }
    }
}
