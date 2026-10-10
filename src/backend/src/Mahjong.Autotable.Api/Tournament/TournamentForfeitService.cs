using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 1 — tournament forfeit policy. Bound from the
/// <c>Tournament</c> section in <c>appsettings.json</c>:
/// <code>
/// "Tournament": {
///   "ReconnectGracePeriodSeconds": 120,
///   "ForfeitSweepIntervalSeconds": 15
/// }
/// </code>
/// </summary>
public sealed class TournamentForfeitOptions
{
    /// <summary>How long a player can be disconnected before a tournament
    /// match auto-forfeits to the opposing seat. Default 120s.</summary>
    public int ReconnectGracePeriodSeconds { get; set; } = 120;

    /// <summary>Sweeper poll interval. Tests can dial down to 1s; the
    /// production default (15s) keeps DB pressure negligible.</summary>
    public int ForfeitSweepIntervalSeconds { get; set; } = 15;
}

/// <summary>
/// Phase K Wave 1 — auto-forfeits tournament matches whose participants
/// have been disconnected longer than the configured grace period.
///
/// <para>The runtime feeds disconnect events into
/// <see cref="NoteDisconnect(string, string)"/> (gameId + playerId);
/// reconnects clear the entry via <see cref="NoteReconnect"/>. A timer
/// sweep walks the disconnect map at <see cref="TournamentForfeitOptions.ForfeitSweepIntervalSeconds"/>
/// and, for any entry older than the grace period whose game is
/// currently bound to an in-progress tournament match, advances the
/// match to the opposing seat (preferring the seat that didn't drop)
/// with <c>ForfeitedByDisconnect = true</c>. An audit-log row is
/// written via the existing append-only <see cref="ReconnectAuditEntry"/>
/// table with a synthetic "tournament-forfeit" marker so the operator
/// trail is self-contained.</para>
///
/// <para>Best-effort: every sweep error is logged + swallowed; the
/// timer never tears down on a transient DB hiccup.</para>
/// </summary>
public sealed class TournamentForfeitService : BackgroundService
{
    /// <summary>Synthetic player-id stamp used on the
    /// <see cref="ReconnectAuditEntry"/> rows the service writes so an
    /// operator searching the audit trail can pivot on "all forfeits"
    /// without joining tables.</summary>
    public const string ForfeitAuditMarker = "tournament-forfeit";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TournamentForfeitOptions _options;
    private readonly ILogger<TournamentForfeitService> _logger;

    // Tracks active disconnects keyed by (gameId, playerId). The value
    // is the UTC instant the disconnect was first observed; reconnects
    // remove the entry.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string GameId, string PlayerId), DateTime> _disconnects = new();

    public TournamentForfeitService(
        IServiceScopeFactory scopeFactory,
        IOptions<TournamentForfeitOptions> options,
        ILogger<TournamentForfeitService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public int ReconnectGracePeriodSeconds => Math.Max(1, _options.ReconnectGracePeriodSeconds);
    public int ForfeitSweepIntervalSeconds => Math.Max(1, _options.ForfeitSweepIntervalSeconds);

    /// <summary>
    /// Records that <paramref name="playerId"/> dropped from
    /// <paramref name="gameId"/>. Subsequent calls with the same key
    /// are no-ops (we keep the first-observed instant so the grace
    /// window starts at the actual drop, not the most-recent
    /// re-notification).
    /// </summary>
    public void NoteDisconnect(string gameId, string playerId)
    {
        if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(playerId)) return;
        if (playerId.StartsWith("bot-", StringComparison.Ordinal)) return;
        _disconnects.TryAdd((gameId, playerId), DateTime.UtcNow);
    }

    /// <summary>Clears a player's pending-disconnect entry (idempotent).</summary>
    public void NoteReconnect(string gameId, string playerId)
    {
        if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(playerId)) return;
        _disconnects.TryRemove((gameId, playerId), out _);
    }

    /// <summary>Visible-for-testing snapshot of the tracked entries.</summary>
    public IReadOnlyDictionary<(string GameId, string PlayerId), DateTime> PendingDisconnects =>
        new Dictionary<(string, string), DateTime>(_disconnects);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TournamentForfeitService sweep failed; swallowing.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(ForfeitSweepIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Single sweep — walks the disconnect map, identifies entries
    /// older than the grace period whose game maps to an in-progress
    /// tournament match, and forfeits each. Returns the number of
    /// matches forfeited. Public so tests + admin-trigger endpoints
    /// can drive the sweep deterministically without waiting on the
    /// background timer.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct = default)
    {
        var grace = TimeSpan.FromSeconds(ReconnectGracePeriodSeconds);
        var cutoff = DateTime.UtcNow - grace;

        var candidates = _disconnects
            .Where(kv => kv.Value <= cutoff)
            .Select(kv => (kv.Key.GameId, kv.Key.PlayerId, DroppedAt: kv.Value))
            .ToList();
        if (candidates.Count == 0) return 0;

        var forfeited = 0;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tournamentSvc = scope.ServiceProvider.GetService<TournamentService>();
        var ratingSvc = scope.ServiceProvider.GetService<PlayerRatingService>();

        foreach (var c in candidates)
        {
            try
            {
                if (!Guid.TryParse(c.GameId, out var gameGuid))
                {
                    _disconnects.TryRemove((c.GameId, c.PlayerId), out _);
                    continue;
                }

                // Find an in-progress match that owns this game id. The
                // GameIdsJson column stores a JSON array; we filter
                // candidates in memory because not every DB provider
                // supports a JSON-contains predicate uniformly.
                var inProgress = await db.TournamentMatches
                    .Where(m => m.Status == "in-progress")
                    .ToListAsync(ct);
                var match = inProgress.FirstOrDefault(m => TournamentService.GameIdsContains(m.GameIdsJson, gameGuid));
                if (match is null)
                {
                    // No live match references the game. Either the
                    // match never existed, was already settled, or the
                    // game is non-tournament. Clear the entry.
                    _disconnects.TryRemove((c.GameId, c.PlayerId), out _);
                    continue;
                }

                // Pick the opposing seat — first listed participant
                // that isn't the disconnected player (and isn't a bot).
                var participants = new List<string> { match.Player1Id, match.Player2Id };
                if (!string.IsNullOrWhiteSpace(match.Player3Id)) participants.Add(match.Player3Id!);
                if (!string.IsNullOrWhiteSpace(match.Player4Id)) participants.Add(match.Player4Id!);
                var winnerId = participants.FirstOrDefault(p =>
                    !string.IsNullOrEmpty(p)
                    && !string.Equals(p, c.PlayerId, StringComparison.Ordinal)
                    && !p.StartsWith("bot-", StringComparison.Ordinal));
                if (winnerId is null)
                {
                    _disconnects.TryRemove((c.GameId, c.PlayerId), out _);
                    continue;
                }

                // Flip the match through the service (handles round
                // progression + atomic save). If the service isn't
                // available — extremely unlikely outside of a partial
                // DI setup in tests — fall back to a direct mutation.
                TournamentMatch? settled;
                if (tournamentSvc is not null)
                {
                    settled = await tournamentSvc.ForfeitMatchAsync(gameGuid, winnerId, c.PlayerId, ct);
                }
                else
                {
                    match.WinnerPlayerId = winnerId;
                    match.Status = "complete";
                    match.CompletedAt = DateTime.UtcNow;
                    match.ForfeitedByDisconnect = true;
                    match.ForfeitedPlayerId = c.PlayerId;
                    await db.SaveChangesAsync(ct);
                    settled = match;
                }
                if (settled is null)
                {
                    _disconnects.TryRemove((c.GameId, c.PlayerId), out _);
                    continue;
                }

                // Append-only audit log entry so the trail is
                // self-contained. The synthetic "forfeit" marker on
                // PlayerId lets operators pivot on the event class
                // without a new audit table. Phase K Wave 2 — also
                // stamps the canonical <c>tournament.forfeit</c> Kind
                // classifier (Vasquez's contract pin).
                db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
                {
                    PlayerId = ForfeitAuditMarker,
                    OldTokenId = Guid.Empty,
                    NewTokenId = settled.Id,
                    Ipv4Hash = string.Empty,
                    UserAgentHash = c.PlayerId, // best-effort: stamp the dropped player id
                    At = DateTime.UtcNow,
                    Kind = ReconnectAuditEntry.KindTournamentForfeit,
                });
                await db.SaveChangesAsync(ct);

                // Apply the rating delta as if the opposing player won
                // a normal match. Best-effort.
                if (ratingSvc is not null)
                {
                    try { await ratingSvc.RecordMatchOutcomeAsync(participants, winnerId, ct: ct); }
                    catch (Exception rex)
                    {
                        _logger.LogWarning(rex, "Forfeit rating delta failed for {Match}.", settled.Id);
                    }
                }

                _disconnects.TryRemove((c.GameId, c.PlayerId), out _);
                forfeited++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Forfeit attempt failed for player {PlayerId} game {GameId}; will retry.",
                    c.PlayerId, c.GameId);
            }
        }

        return forfeited;
    }
}
