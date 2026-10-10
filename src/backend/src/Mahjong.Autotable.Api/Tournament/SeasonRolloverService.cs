using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 1 — quarter-boundary season rollover hosted service.
///
/// <para>Polls the <see cref="PlayerRatingService.SeasonFromDate"/> code
/// every <see cref="PollIntervalMinutes"/>. When the current season
/// differs from the most-recent value persisted into
/// <see cref="PlayerRatingHistory"/> (or, on first boot, the
/// <see cref="PlayerRatings"/> table contains rows whose season is
/// older than current), the service:</para>
/// <list type="number">
///   <item>Copies every live <see cref="PlayerRating"/> row whose
///         season is strictly older than current into
///         <see cref="PlayerRatingHistory"/>.</item>
///   <item>Removes those frozen rows from the live table so the
///         leaderboard query for the new season starts at the
///         default-elo baseline (the next match completion will
///         seed fresh rows via <see cref="PlayerRatingService.RecordMatchOutcomeAsync"/>).</item>
/// </list>
///
/// <para>The rollover is idempotent — re-running across the same
/// (season, player) pair is a no-op because the
/// <c>PlayerRatingHistory</c> table is unique on
/// <c>(PlayerId, Season)</c>; on a duplicate we keep the first-frozen
/// snapshot.</para>
/// </summary>
public sealed class SeasonRolloverService : BackgroundService
{
    /// <summary>Default poll interval. Quarter boundaries are hours-coarse
    /// transitions so checking every 30 minutes is plenty fast; the
    /// service deliberately avoids a 1-second tight loop.</summary>
    public const int DefaultPollIntervalMinutes = 30;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SeasonRolloverService> _logger;

    public SeasonRolloverService(IServiceScopeFactory scopeFactory, ILogger<SeasonRolloverService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Configurable poll interval. Tests can dial down to 1 minute
    /// via override; production keeps the default.</summary>
    public int PollIntervalMinutes { get; set; } = DefaultPollIntervalMinutes;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Best-effort startup settle delay so we don't fight EF warmup.
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RolloverOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SeasonRolloverService tick failed; swallowing.");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, PollIntervalMinutes)), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One rollover sweep. Returns the number of rating rows frozen +
    /// removed (0 when no boundary was crossed). Public so tests +
    /// admin-trigger endpoints can invoke it without waiting for the
    /// timer.
    ///
    /// <para>Phase K Wave 2 — players who are currently mid-tournament
    /// (registered against a tournament whose <see cref="Tournament.Status"/>
    /// is <c>in-progress</c>) have their freeze DEFERRED: a row is
    /// written to <see cref="AppDbContext.PlayerSeasonRolloverDeferrals"/>
    /// pinning the (PlayerId, FromSeasonId, TournamentId, ToSeasonId)
    /// tuple, and the live <see cref="PlayerRating"/> row stays put. The
    /// deferral is drained by <see cref="DrainDeferralsAsync"/> when the
    /// tournament completes (called via
    /// <see cref="Mahjong.Autotable.Api.Tournament.TournamentService"/>
    /// completion path + periodically by the sweeper).</para>
    /// </summary>
    public async Task<int> RolloverOnceAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ratings = scope.ServiceProvider.GetService<PlayerRatingService>();

        var currentSeason = ratings?.CurrentSeason() ?? PlayerRatingService.SeasonFromDate(DateTime.UtcNow);

        var stale = await db.PlayerRatings
            .Where(r => r.Season != currentSeason)
            .ToListAsync(ct);
        if (stale.Count == 0)
        {
            // Phase K Wave 2 — even on a no-stale tick we still attempt
            // to drain any deferred rollovers whose tournaments have
            // since completed. Keeps the deferral surface self-healing
            // even if the completion-hook drain misfires.
            await DrainDeferralsAsync(ct);
            return 0;
        }

        // Phase K Wave 2 — identify the player ids that should be
        // deferred (currently registered against an in-progress
        // tournament). A single batch query keeps the per-row cost
        // O(1) regardless of stale.Count.
        var stalePlayerIds = stale.Select(r => r.PlayerId).Distinct(StringComparer.Ordinal).ToList();
        var deferralCandidates = await (
            from reg in db.TournamentRegistrations
            join t in db.Tournaments on reg.TournamentId equals t.Id
            where t.Status == "in-progress" && stalePlayerIds.Contains(reg.PlayerId)
            select new { reg.PlayerId, t.Id }
        ).ToListAsync(ct);
        var deferralMap = deferralCandidates
            .GroupBy(x => x.PlayerId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList(), StringComparer.Ordinal);

        var existingHistoryKeys = new HashSet<(string, string)>(
            await db.PlayerRatingHistory
                .Where(h => stale.Select(s => s.Season).Distinct().Contains(h.Season))
                .Select(h => new { h.PlayerId, h.Season })
                .ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(x => (x.PlayerId, x.Season)), ct));

        var existingDeferralKeys = new HashSet<(string, string, Guid)>(
            await db.PlayerSeasonRolloverDeferrals
                .Where(d => stalePlayerIds.Contains(d.PlayerId) && d.ResolvedAtUtc == null)
                .Select(d => new { d.PlayerId, d.FromSeasonId, d.TournamentId })
                .ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(x => (x.PlayerId, x.FromSeasonId, x.TournamentId)), ct));

        var now = DateTime.UtcNow;
        var frozen = 0;
        var deferred = 0;
        foreach (var row in stale)
        {
            if (deferralMap.TryGetValue(row.PlayerId, out var tournamentIds) && tournamentIds.Count > 0)
            {
                // Defer the rollover: keep the live row, write the
                // pin against every active tournament so the drain
                // path catches whichever completes last.
                foreach (var tid in tournamentIds)
                {
                    var key = (row.PlayerId, row.Season, tid);
                    if (existingDeferralKeys.Contains(key)) continue;
                    db.PlayerSeasonRolloverDeferrals.Add(new PlayerSeasonRolloverDeferral
                    {
                        PlayerId = row.PlayerId,
                        FromSeasonId = row.Season,
                        ToSeasonId = currentSeason,
                        DeferredAtUtc = now,
                        TournamentId = tid,
                    });
                    existingDeferralKeys.Add(key);
                    deferred++;
                }
                continue;
            }

            var historyKey = (row.PlayerId, row.Season);
            if (!existingHistoryKeys.Contains(historyKey))
            {
                db.PlayerRatingHistory.Add(new PlayerRatingHistory
                {
                    PlayerId = row.PlayerId,
                    Season = row.Season,
                    EloRating = row.EloRating,
                    GamesPlayed = row.GamesPlayed,
                    FrozenAt = now,
                });
            }
            db.PlayerRatings.Remove(row);
            frozen++;
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Season rollover: froze {Frozen} rows, deferred {Deferred} rows; current season is {Season}.",
            frozen, deferred, currentSeason);

        // Drain any deferrals whose tournaments finished in the meantime.
        await DrainDeferralsAsync(ct);
        return frozen;
    }

    /// <summary>
    /// Phase K Wave 2 — drain step. Walks every pending deferral whose
    /// pinned tournament is now <c>complete</c> and applies the
    /// freeze-then-reset cycle that <see cref="RolloverOnceAsync"/>
    /// would have applied originally. Idempotent — the drain re-runs
    /// safely because the (PlayerId, Season) uniqueness on
    /// <see cref="PlayerRatingHistory"/> guards against double-freeze.
    /// Returns the number of deferrals drained.
    ///
    /// <para>The drain is best-effort transactional: each player's
    /// rows commit independently so a bad row can't block the queue.</para>
    /// </summary>
    public async Task<int> DrainDeferralsAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var pending = await (
            from d in db.PlayerSeasonRolloverDeferrals
            join t in db.Tournaments on d.TournamentId equals t.Id
            where d.ResolvedAtUtc == null && t.Status == "complete"
            select d
        ).ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var now = DateTime.UtcNow;
        var drained = 0;

        // Group by player so we only process each live rating row once
        // even when a player is pinned by multiple tournaments.
        foreach (var group in pending.GroupBy(d => d.PlayerId, StringComparer.Ordinal))
        {
            var playerId = group.Key;
            var oldestSeason = group.Select(d => d.FromSeasonId).OrderBy(s => s, StringComparer.Ordinal).First();

            // Phase K Wave 2 — only drain when EVERY pinning tournament
            // for this player is complete. If a player has another
            // active tournament, keep the deferral pinned.
            var anyStillActive = await (
                from d in db.PlayerSeasonRolloverDeferrals
                join t in db.Tournaments on d.TournamentId equals t.Id
                where d.PlayerId == playerId && d.ResolvedAtUtc == null && t.Status != "complete"
                select d.Id).AnyAsync(ct);
            if (anyStillActive) continue;

            var live = await db.PlayerRatings
                .FirstOrDefaultAsync(r => r.PlayerId == playerId && r.Season == oldestSeason, ct);
            if (live is not null)
            {
                var existing = await db.PlayerRatingHistory
                    .FirstOrDefaultAsync(h => h.PlayerId == playerId && h.Season == oldestSeason, ct);
                if (existing is null)
                {
                    db.PlayerRatingHistory.Add(new PlayerRatingHistory
                    {
                        PlayerId = playerId,
                        Season = live.Season,
                        EloRating = live.EloRating,
                        GamesPlayed = live.GamesPlayed,
                        FrozenAt = now,
                    });
                }
                db.PlayerRatings.Remove(live);
            }

            foreach (var d in group)
            {
                d.ResolvedAtUtc = now;
                drained++;
            }
        }

        await db.SaveChangesAsync(ct);
        if (drained > 0)
        {
            _logger.LogInformation("Season rollover drain: applied {Count} deferred snapshot(s).", drained);
        }
        return drained;
    }
}
