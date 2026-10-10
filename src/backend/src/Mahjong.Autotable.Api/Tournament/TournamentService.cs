using System.Text.Json;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase J Wave 10 — Tournament CRUD + lifecycle + match advancement.
/// All persistence flows through <see cref="AppDbContext"/>; the
/// service is registered as a scoped DI service (matches the
/// AppDbContext lifetime) so each REST call gets a fresh tracker.
///
/// <para>The service is intentionally pairing-algorithm agnostic — it
/// delegates to <see cref="TournamentPairing"/> for the seed-list →
/// pairing transformation, then persists the result as
/// <see cref="TournamentMatch"/> rows.</para>
///
/// <para>Match advancement (<see cref="AdvanceMatchAsync"/>) is the
/// hook invoked by <c>ChangshaGameRuntime.EmitGameCompletedAsync</c>
/// once a game finishes. It scans for a TournamentMatch whose
/// <see cref="TournamentMatch.GameIdsJson"/> includes the completed
/// game's id, records the winner, and (for single-elim/Swiss)
/// schedules the next round if every match in the current round
/// is now complete.</para>
/// </summary>
public sealed class TournamentService
{
    private readonly AppDbContext _db;
    private readonly SeasonRolloverService? _rollover;
    private readonly TournamentBracketBroadcaster? _bracketBroadcaster;
    private readonly IBracketStore? _bracketStore;

    public TournamentService(
        AppDbContext db,
        SeasonRolloverService? rollover = null,
        TournamentBracketBroadcaster? bracketBroadcaster = null,
        IBracketStore? bracketStore = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _rollover = rollover;
        _bracketBroadcaster = bracketBroadcaster;
        _bracketStore = bracketStore;
    }

    public async Task<Data.Entities.Tournament> CreateAsync(
        string name,
        string format,
        string createdByPlayerId,
        int maxPlayers,
        int gamesPerMatch,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tournament name must be non-empty.", nameof(name));
        if (!IsKnownFormat(format))
            throw new ArgumentException($"Unknown tournament format '{format}'.", nameof(format));
        if (string.IsNullOrWhiteSpace(createdByPlayerId))
            throw new ArgumentException("Creator player id must be non-empty.", nameof(createdByPlayerId));
        if (maxPlayers < 2) maxPlayers = 2;
        if (gamesPerMatch < 1) gamesPerMatch = 1;

        var tournament = new Data.Entities.Tournament
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Format = format,
            Status = "open",
            CreatedByPlayerId = createdByPlayerId,
            MaxPlayers = maxPlayers,
            GamesPerMatch = gamesPerMatch,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Tournaments.Add(tournament);
        await _db.SaveChangesAsync(ct);
        return tournament;
    }

    public Task<List<Data.Entities.Tournament>> ListAsync(string? statusFilter, CancellationToken ct = default)
    {
        var q = _db.Tournaments.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            q = q.Where(t => t.Status == statusFilter);
        }
        return q.OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
    }

    public Task<Data.Entities.Tournament?> GetAsync(Guid id, CancellationToken ct = default)
        => _db.Tournaments.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<List<TournamentRegistration>> ListRegistrationsAsync(Guid tournamentId, CancellationToken ct = default)
        => _db.TournamentRegistrations.AsNoTracking()
            .Where(r => r.TournamentId == tournamentId)
            .OrderBy(r => r.Seed)
            .ToListAsync(ct);

    public Task<List<TournamentMatch>> ListMatchesAsync(Guid tournamentId, CancellationToken ct = default)
        => _db.TournamentMatches.AsNoTracking()
            .Where(m => m.TournamentId == tournamentId)
            .OrderBy(m => m.Round)
            .ThenBy(m => m.CreatedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Register <paramref name="playerId"/> for a tournament. Allowed
    /// only while the tournament is <c>draft</c> or <c>open</c>; once
    /// the creator calls <see cref="StartAsync"/> the field is closed.
    /// Idempotent: re-registering an already-registered player is a no-op.
    /// </summary>
    public async Task<TournamentRegistration> RegisterAsync(Guid tournamentId, string playerId, CancellationToken ct = default)
    {
        var t = await _db.Tournaments.FirstOrDefaultAsync(x => x.Id == tournamentId, ct)
            ?? throw new InvalidOperationException("Tournament not found.");
        if (t.Status != "draft" && t.Status != "open")
            throw new InvalidOperationException($"Cannot register: tournament is '{t.Status}'.");
        var existing = await _db.TournamentRegistrations.FirstOrDefaultAsync(
            r => r.TournamentId == tournamentId && r.PlayerId == playerId, ct);
        if (existing is not null) return existing;

        var count = await _db.TournamentRegistrations.CountAsync(r => r.TournamentId == tournamentId, ct);
        if (count >= t.MaxPlayers)
            throw new InvalidOperationException("Tournament is full.");

        var reg = new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            TournamentId = tournamentId,
            PlayerId = playerId,
            Seed = count + 1,
            RegisteredAt = DateTime.UtcNow,
        };
        _db.TournamentRegistrations.Add(reg);
        await _db.SaveChangesAsync(ct);
        return reg;
    }

    /// <summary>Unregister a player. Allowed only before <c>start</c>.</summary>
    public async Task<bool> UnregisterAsync(Guid tournamentId, string playerId, CancellationToken ct = default)
    {
        var t = await _db.Tournaments.FirstOrDefaultAsync(x => x.Id == tournamentId, ct);
        if (t is null) return false;
        if (t.Status != "draft" && t.Status != "open")
            throw new InvalidOperationException($"Cannot unregister: tournament is '{t.Status}'.");
        var reg = await _db.TournamentRegistrations.FirstOrDefaultAsync(
            r => r.TournamentId == tournamentId && r.PlayerId == playerId, ct);
        if (reg is null) return false;
        _db.TournamentRegistrations.Remove(reg);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Phase K Wave 3 — Bishop. Admin-only seed-assignment surface
    /// backing <c>POST /api/tournaments/{id}/seed</c>. Each entry in
    /// <paramref name="seeds"/> upserts the
    /// <see cref="TournamentRegistration.Seed"/> column for that
    /// player; unknown players in the body are skipped (not an error)
    /// so a single push can target a subset of the bracket. Returns
    /// the updated registration count.
    ///
    /// <para>Permitted only while the tournament is <c>draft</c> or
    /// <c>open</c> — once <c>StartAsync</c> has emitted bracket
    /// matches the seeding is locked. Idempotent: re-issuing the
    /// same seeds is a no-op.</para>
    /// </summary>
    public async Task<int> SeedAsync(
        Guid tournamentId,
        IReadOnlyList<TournamentSeedAssignment> seeds,
        CancellationToken ct = default)
    {
        if (seeds is null) throw new ArgumentNullException(nameof(seeds));
        var t = await _db.Tournaments.FirstOrDefaultAsync(x => x.Id == tournamentId, ct)
            ?? throw new InvalidOperationException("Tournament not found.");
        if (t.Status != "draft" && t.Status != "open")
            throw new InvalidOperationException($"Cannot reseed: tournament is '{t.Status}'.");

        var regs = await _db.TournamentRegistrations
            .Where(r => r.TournamentId == tournamentId)
            .ToListAsync(ct);
        var lookup = regs.ToDictionary(r => r.PlayerId, StringComparer.Ordinal);

        var updated = 0;
        foreach (var assign in seeds)
        {
            if (string.IsNullOrWhiteSpace(assign.PlayerId)) continue;
            if (assign.SeedNumber < 1) continue;
            if (!lookup.TryGetValue(assign.PlayerId, out var reg)) continue;
            if (reg.Seed == assign.SeedNumber) continue;
            reg.Seed = assign.SeedNumber;
            updated++;
        }
        if (updated > 0) await _db.SaveChangesAsync(ct);
        return updated;
    }

    /// <summary>Phase K Wave 3 — single seed assignment record passed
    /// to <see cref="SeedAsync"/>.</summary>
    public sealed record TournamentSeedAssignment(string PlayerId, int SeedNumber);

    /// <summary>
    /// Transition the tournament from <c>open</c> → <c>in-progress</c>
    /// and emit the first round's <see cref="TournamentMatch"/> rows.
    /// Only the creator can call this; the controller enforces auth
    /// via <see cref="Auth.AuthCookieService"/> and passes the resolved
    /// player id here for the creator check.
    /// </summary>
    public async Task<List<TournamentMatch>> StartAsync(Guid tournamentId, string requestingPlayerId, CancellationToken ct = default)
    {
        var t = await _db.Tournaments.FirstOrDefaultAsync(x => x.Id == tournamentId, ct)
            ?? throw new InvalidOperationException("Tournament not found.");
        if (t.CreatedByPlayerId != requestingPlayerId)
            throw new UnauthorizedAccessException("Only the creator can start the tournament.");
        if (t.Status != "draft" && t.Status != "open")
            throw new InvalidOperationException($"Cannot start: tournament is '{t.Status}'.");

        var regs = await _db.TournamentRegistrations
            .Where(r => r.TournamentId == tournamentId)
            .OrderBy(r => r.Seed)
            .ToListAsync(ct);
        if (regs.Count < 2)
            throw new InvalidOperationException("Need at least 2 registrations to start.");

        var seededPlayers = regs.Select(r => r.PlayerId).ToList();
        var matches = new List<TournamentMatch>();
        // Phase K Wave 13 — Bishop. Track per-round slot indices so
        // we can upsert a durable BracketRecord per pairing. The
        // store is optional — when not wired, only the
        // TournamentMatches rows land. The slot index is NOT
        // persisted on the TournamentMatch entity itself; it lives
        // only on the BracketRecord row (natural-key column). The
        // result-recording path (AdvanceMatchAsync /
        // ForfeitMatchAsync) re-derives the slot by matching
        // seeds against the stored BracketRecord rows.
        var slotByRound = new Dictionary<int, int>();
        int NextSlot(int round)
        {
            var slot = slotByRound.TryGetValue(round, out var s) ? s : 0;
            slotByRound[round] = slot + 1;
            return slot;
        }
        var matchSlots = new List<int>();
        switch (t.Format)
        {
            case "round-robin":
                foreach (var (round, pair) in TournamentPairing.RoundRobin(seededPlayers))
                {
                    matches.Add(BuildMatch(t.Id, round, pair));
                    matchSlots.Add(NextSlot(round));
                }
                break;
            case "single-elimination":
                foreach (var pair in TournamentPairing.SingleEliminationFirstRound(seededPlayers))
                {
                    matches.Add(BuildMatch(t.Id, round: 1, pair));
                    matchSlots.Add(NextSlot(1));
                }
                break;
            case "swiss":
                foreach (var pair in TournamentPairing.SwissFirstRound(seededPlayers))
                {
                    matches.Add(BuildMatch(t.Id, round: 1, pair));
                    matchSlots.Add(NextSlot(1));
                }
                break;
            case "double-elimination":
                // Phase K Wave 6 — Bishop. Winners-bracket round 1
                // shares the single-elim seeding shape; the losers
                // bracket + grand final slots get populated lazily by
                // the service as matches resolve. We only persist the
                // winners-bracket round 1 today — placeholder rows for
                // the losers bracket would introduce phantom "pending"
                // matches in the leaderboard before any real losers
                // exist.
                foreach (var pair in TournamentPairing.SingleEliminationFirstRound(seededPlayers))
                {
                    matches.Add(BuildMatch(t.Id, round: 1, pair));
                    matchSlots.Add(NextSlot(1));
                }
                break;
            default:
                throw new InvalidOperationException($"Unknown format '{t.Format}'.");
        }

        _db.TournamentMatches.AddRange(matches);
        t.Status = "in-progress";
        t.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        // Phase K Wave 13 — Bishop. Persist the durable
        // BracketRecord rows AFTER SaveChangesAsync so the match
        // ids are stable. Idempotent on the natural key
        // (TournamentId, RoundNumber, MatchSlot) — replaying StartAsync
        // (operator retry, test fixture reset) updates instead of
        // duplicating.
        await UpsertBracketRecordsAsync(matches, matchSlots, ct);
        return matches;
    }

    private async Task UpsertBracketRecordsAsync(
        IReadOnlyList<TournamentMatch> matches,
        IReadOnlyList<int> matchSlots,
        CancellationToken ct)
    {
        if (_bracketStore is null) return;
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var slot = i < matchSlots.Count ? matchSlots[i] : i;
            var record = new BracketRecord
            {
                TournamentId = match.TournamentId,
                RoundNumber = match.Round,
                MatchSlot = slot,
                SeedA = match.Player1Id ?? string.Empty,
                SeedB = string.IsNullOrWhiteSpace(match.Player2Id)
                    ? BracketByeSeed
                    : match.Player2Id!,
                Status = match.Status switch
                {
                    "complete" => string.IsNullOrWhiteSpace(match.Player2Id) ? "bye" : "completed",
                    _ => "pending",
                },
                WinnerSeed = match.WinnerPlayerId,
                CompletedAt = match.CompletedAt,
            };
            await _bracketStore.UpsertAsync(record, ct);
        }
    }

    /// <summary>Phase K Wave 13 — Bishop. Seed literal stamped on
    /// the B side of a bye pairing (matches the convention used
    /// elsewhere in the bracket surface).</summary>
    public const string BracketByeSeed = "__bye__";

    /// <summary>
    /// Phase K Wave 13 — Bishop. Stamp the durable bracket row
    /// with the completion details. The slot is re-derived by
    /// listing existing rows for the match's (tournamentId,
    /// round) and matching against
    /// <see cref="TournamentMatch.Player1Id"/> /
    /// <see cref="TournamentMatch.Player2Id"/>. When no row
    /// exists (the StartAsync was invoked before W13 wired the
    /// store), the call is a no-op.
    /// </summary>
    private async Task RecordBracketResultAsync(
        TournamentMatch match, string status, CancellationToken ct)
    {
        if (_bracketStore is null) return;
        if (match.WinnerPlayerId is null) return;
        if (match.CompletedAt is null) return;
        var rows = await _bracketStore.ListAsync(match.TournamentId, ct);
        var row = rows.FirstOrDefault(r =>
            r.RoundNumber == match.Round
            && SeedsMatchMatch(r, match));
        if (row is null) return;
        await _bracketStore.RecordResultAsync(
            match.TournamentId,
            row.RoundNumber,
            row.MatchSlot,
            match.WinnerPlayerId,
            status,
            match.CompletedAt.Value,
            ct);
    }

    /// <summary>
    /// Phase K Wave 13 — Bishop. Upsert BracketRecord rows for a
    /// round's worth of newly emitted next-round pairings. The
    /// slot index is derived from the row's position in the
    /// supplied list (matches insertion order).
    /// </summary>
    private async Task UpsertNextRoundBracketRecordsAsync(
        IReadOnlyList<TournamentMatch> newMatches, CancellationToken ct)
    {
        if (_bracketStore is null) return;
        if (newMatches.Count == 0) return;
        // Group by round in case the caller emitted multiple
        // rounds in one tick (defensive — today's flow only emits
        // one round at a time).
        foreach (var roundGroup in newMatches.GroupBy(m => m.Round))
        {
            var tournamentId = roundGroup.First().TournamentId;
            // Phase K Wave 13 — Bishop. When a previous attempt
            // partially populated the next round, the existing
            // rows pin the slot ordering; new pairings extend the
            // tail. Listing the round once + re-using the count
            // keeps the slot allocation deterministic across
            // replays.
            var existing = await _bracketStore.ListAsync(tournamentId, ct);
            var slotBase = existing
                .Where(r => r.RoundNumber == roundGroup.Key)
                .Select(r => r.MatchSlot)
                .DefaultIfEmpty(-1)
                .Max() + 1;
            var i = 0;
            foreach (var m in roundGroup)
            {
                // Skip if a row with these seeds already exists
                // (replay scenario) — RecordResultAsync /
                // upstream UpsertAsync will refresh it.
                var sa = m.Player1Id ?? string.Empty;
                var sb = string.IsNullOrWhiteSpace(m.Player2Id) ? BracketByeSeed : m.Player2Id!;
                if (existing.Any(r => r.RoundNumber == roundGroup.Key
                    && r.SeedA == sa && r.SeedB == sb))
                {
                    continue;
                }
                var rec = new BracketRecord
                {
                    TournamentId = m.TournamentId,
                    RoundNumber = m.Round,
                    MatchSlot = slotBase + i,
                    SeedA = sa,
                    SeedB = sb,
                    Status = "pending",
                };
                await _bracketStore.UpsertAsync(rec, ct);
                i++;
            }
        }
    }

    /// <summary>Phase K Wave 13 — Bishop. Returns true when the
    /// supplied BracketRecord's seeds match the
    /// TournamentMatch's player1/player2 (in either ordering — A/B
    /// vs B/A — to tolerate seed-swap pair builders).</summary>
    private static bool SeedsMatchMatch(BracketRecord r, TournamentMatch m)
    {
        var p1 = m.Player1Id ?? string.Empty;
        var p2 = string.IsNullOrWhiteSpace(m.Player2Id) ? BracketByeSeed : m.Player2Id!;
        return (r.SeedA == p1 && r.SeedB == p2)
            || (r.SeedA == p2 && r.SeedB == p1);
    }

    /// <summary>
    /// Attach <paramref name="gameId"/> to a pending match for two of
    /// the supplied players. The runtime invokes this when a tournament
    /// match needs a Changsha game spun up — Wave 10 keeps the
    /// game-creation flow external (creator manually creates a Changsha
    /// game, then calls this to bind it). Returns the match if a
    /// pending pairing was found that matches the player set; null if
    /// no pending pairing matches (idempotent retry from the runtime).
    /// </summary>
    public async Task<TournamentMatch?> AttachGameAsync(
        Guid tournamentId,
        Guid gameId,
        IReadOnlyList<string> playerIds,
        CancellationToken ct = default)
    {
        var matches = await _db.TournamentMatches
            .Where(m => m.TournamentId == tournamentId && m.Status == "pending")
            .ToListAsync(ct);
        TournamentMatch? hit = null;
        foreach (var m in matches)
        {
            var ids = new HashSet<string>(playerIds, StringComparer.Ordinal);
            var matchIds = new HashSet<string>(MatchPlayerIds(m), StringComparer.Ordinal);
            if (ids.SetEquals(matchIds)) { hit = m; break; }
        }
        if (hit is null) return null;
        var gameIds = DeserializeGameIds(hit.GameIdsJson);
        if (!gameIds.Contains(gameId))
        {
            gameIds.Add(gameId);
            hit.GameIdsJson = SerializeGameIds(gameIds);
        }
        hit.Status = "in-progress";
        await _db.SaveChangesAsync(ct);
        return hit;
    }

    /// <summary>
    /// Match advancement hook. Called by
    /// <c>ChangshaGameRuntime.EmitGameCompletedAsync</c> when a game
    /// belonging to a tournament finishes. The runtime supplies the
    /// completed <paramref name="gameId"/> + the winner's PlayerId; we
    /// flip the match to <c>complete</c>, persist the winner, and
    /// (for elim/Swiss) schedule the next round if the current round
    /// is now fully complete. Returns null if no match owns the game.
    ///
    /// <para>Phase K Wave 2 — also writes a
    /// <see cref="ReconnectAuditEntry"/> row with
    /// <see cref="ReconnectAuditEntry.KindTournamentMatchComplete"/> so
    /// the audit trail can answer "did this match settle cleanly or via
    /// forfeit?" without a JOIN against the <c>TournamentMatches</c>
    /// table.</para>
    /// </summary>
    public async Task<TournamentMatch?> AdvanceMatchAsync(Guid gameId, string winnerPlayerId, CancellationToken ct = default)
    {
        // SQLite/Postgres-portable: pull matches with the game id in their
        // serialised list. We can't push the JSON contains into the query
        // tree across all providers, so we filter the candidates in memory.
        var candidates = await _db.TournamentMatches
            .Where(m => m.Status == "in-progress")
            .ToListAsync(ct);
        var match = candidates.FirstOrDefault(m => DeserializeGameIds(m.GameIdsJson).Contains(gameId));
        if (match is null) return null;

        match.WinnerPlayerId = winnerPlayerId;
        match.Status = "complete";
        match.CompletedAt = DateTime.UtcNow;

        // Phase K Wave 2 — append the canonical completion audit row.
        // PlayerId carries the winner; UserAgentHash overloaded to carry
        // the match id stringified so operators can pivot on either.
        _db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
        {
            PlayerId = winnerPlayerId,
            OldTokenId = Guid.Empty,
            NewTokenId = match.Id,
            Ipv4Hash = string.Empty,
            UserAgentHash = match.Id.ToString("N"),
            At = match.CompletedAt!.Value,
            Kind = ReconnectAuditEntry.KindTournamentMatchComplete,
        });

        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == match.TournamentId, ct);
        List<TournamentMatch>? newlyEmittedMatches = null;
        if (tournament is not null)
        {
            newlyEmittedMatches = await MaybeAdvanceRoundAsync(tournament, match.Round, ct);
        }
        await _db.SaveChangesAsync(ct);
        await MaybeDrainSeasonDeferralsAsync(tournament, ct);
        // Phase K Wave 13 — Bishop. Idempotent durable bracket
        // completion. Re-applying a `game-complete` event with the
        // same winner is a no-op on the row.
        await RecordBracketResultAsync(match, "completed", ct);
        // Phase K Wave 13 — Bishop. Persist next-round pairings.
        if (newlyEmittedMatches is { Count: > 0 })
        {
            await UpsertNextRoundBracketRecordsAsync(newlyEmittedMatches, ct);
        }
        await BroadcastBracketUpdateAsync(match.TournamentId, ct);
        return match;
    }

    /// <summary>
    /// Phase K Wave 1 — forfeit variant of <see cref="AdvanceMatchAsync"/>.
    /// Identifies the in-progress match owning <paramref name="gameId"/>,
    /// marks it complete with <paramref name="winnerPlayerId"/>, and
    /// sets the forfeit metadata so leaderboard + audit can distinguish
    /// a regular win from a disconnect-forfeit. Returns the mutated
    /// match (or null when no match owns the game).
    ///
    /// <para>Phase K Wave 2 — emits a
    /// <see cref="ReconnectAuditEntry"/> tagged
    /// <see cref="ReconnectAuditEntry.KindTournamentForfeit"/>. The
    /// background sweeper (<see cref="TournamentForfeitService"/>) also
    /// writes its own row at the disconnect-detection moment; manual
    /// surrender hits this path directly so the two writers are
    /// independent.</para>
    /// </summary>
    public async Task<TournamentMatch?> ForfeitMatchAsync(
        Guid gameId,
        string winnerPlayerId,
        string forfeitedPlayerId,
        CancellationToken ct = default)
    {
        var candidates = await _db.TournamentMatches
            .Where(m => m.Status == "in-progress")
            .ToListAsync(ct);
        var match = candidates.FirstOrDefault(m => DeserializeGameIds(m.GameIdsJson).Contains(gameId));
        if (match is null) return null;

        match.WinnerPlayerId = winnerPlayerId;
        match.Status = "complete";
        match.CompletedAt = DateTime.UtcNow;
        match.ForfeitedByDisconnect = true;
        match.ForfeitedPlayerId = forfeitedPlayerId;

        _db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
        {
            PlayerId = forfeitedPlayerId,
            OldTokenId = Guid.Empty,
            NewTokenId = match.Id,
            Ipv4Hash = string.Empty,
            UserAgentHash = winnerPlayerId,
            At = match.CompletedAt!.Value,
            Kind = ReconnectAuditEntry.KindTournamentForfeit,
        });

        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == match.TournamentId, ct);
        List<TournamentMatch>? newlyEmittedMatches = null;
        if (tournament is not null)
        {
            newlyEmittedMatches = await MaybeAdvanceRoundAsync(tournament, match.Round, ct);
        }
        await _db.SaveChangesAsync(ct);
        await MaybeDrainSeasonDeferralsAsync(tournament, ct);
        // Phase K Wave 13 — Bishop. Stamp the forfeit on the
        // durable bracket row so a multi-replica admin client
        // sees the forfeit status without reconstructing it.
        await RecordBracketResultAsync(match, "forfeit", ct);
        if (newlyEmittedMatches is { Count: > 0 })
        {
            await UpsertNextRoundBracketRecordsAsync(newlyEmittedMatches, ct);
        }
        await BroadcastBracketUpdateAsync(match.TournamentId, ct);
        return match;
    }

    /// <summary>
    /// Phase K Wave 2 — manual-surrender forfeit by match id (the
    /// disconnect-driven sweeper goes through
    /// <see cref="ForfeitMatchAsync(Guid,string,string,CancellationToken)"/>
    /// which keys on the bound game). Resolves the in-progress match
    /// by primary key, derives the winner as the first non-bot
    /// participant other than <paramref name="forfeitedPlayerId"/>,
    /// then advances + writes the audit row exactly as the game-id
    /// path does. Returns null when the match doesn't exist OR is not
    /// currently <c>in-progress</c> (idempotent re-forfeit → null
    /// rather than 500). Throws <see cref="ArgumentException"/> when
    /// the requested forfeit player isn't a participant.
    /// </summary>
    public async Task<TournamentMatch?> ForfeitMatchByIdAsync(
        Guid tournamentId,
        Guid matchId,
        string forfeitedPlayerId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(forfeitedPlayerId))
            throw new ArgumentException("forfeitedPlayerId is required.", nameof(forfeitedPlayerId));

        var match = await _db.TournamentMatches
            .FirstOrDefaultAsync(m => m.Id == matchId && m.TournamentId == tournamentId, ct);
        if (match is null) return null;
        if (match.Status != "in-progress" && match.Status != "pending") return null;

        var participants = MatchPlayerIds(match).ToList();
        if (!participants.Contains(forfeitedPlayerId, StringComparer.Ordinal))
            throw new ArgumentException(
                "forfeitedPlayerId is not a participant of the match.", nameof(forfeitedPlayerId));

        var winnerId = participants.FirstOrDefault(p =>
            !string.IsNullOrEmpty(p)
            && !string.Equals(p, forfeitedPlayerId, StringComparison.Ordinal)
            && !p.StartsWith("bot-", StringComparison.Ordinal));
        if (winnerId is null) return null;

        match.WinnerPlayerId = winnerId;
        match.Status = "complete";
        match.CompletedAt = DateTime.UtcNow;
        match.ForfeitedByDisconnect = false;
        match.ForfeitedPlayerId = forfeitedPlayerId;

        _db.ReconnectAuditEntries.Add(new ReconnectAuditEntry
        {
            PlayerId = forfeitedPlayerId,
            OldTokenId = Guid.Empty,
            NewTokenId = match.Id,
            Ipv4Hash = string.Empty,
            UserAgentHash = winnerId,
            At = match.CompletedAt!.Value,
            Kind = ReconnectAuditEntry.KindTournamentForfeit,
        });

        var tournament = await _db.Tournaments.FirstOrDefaultAsync(t => t.Id == match.TournamentId, ct);
        List<TournamentMatch>? newlyEmittedMatches = null;
        if (tournament is not null)
        {
            newlyEmittedMatches = await MaybeAdvanceRoundAsync(tournament, match.Round, ct);
        }
        await _db.SaveChangesAsync(ct);
        await MaybeDrainSeasonDeferralsAsync(tournament, ct);
        await RecordBracketResultAsync(match, "forfeit", ct);
        if (newlyEmittedMatches is { Count: > 0 })
        {
            await UpsertNextRoundBracketRecordsAsync(newlyEmittedMatches, ct);
        }
        await BroadcastBracketUpdateAsync(match.TournamentId, ct);
        return match;
    }

    /// <summary>
    /// Phase K Wave 2 — best-effort hook called after a tournament
    /// transitions to <c>complete</c>. Triggers the season-rollover
    /// drain so any rating snapshots that were pinned for this
    /// tournament's roster get applied immediately rather than waiting
    /// on the next 30-min sweeper tick. No-op when
    /// <see cref="SeasonRolloverService"/> wasn't injected (test
    /// harnesses can omit it) or when the tournament isn't actually
    /// complete (intermediate-round advance).
    /// </summary>
    private async Task MaybeDrainSeasonDeferralsAsync(Data.Entities.Tournament? tournament, CancellationToken ct)
    {
        if (_rollover is null || tournament is null) return;
        if (tournament.Status != "complete") return;
        try
        {
            await _rollover.DrainDeferralsAsync(ct);
        }
        catch
        {
            // Best-effort. The hosted-service tick is the canonical
            // safety net; we swallow here so a flaky drain can't
            // poison the match-advance path.
        }
    }

    /// <summary>
    /// Phase K Wave 1 helper — true iff <paramref name="gameIdsJson"/>
    /// (a serialised <c>List&lt;Guid&gt;</c>) contains
    /// <paramref name="gameId"/>. Exposed publicly so cross-service
    /// consumers (<see cref="TournamentForfeitService"/>) can reuse
    /// the deserialise logic without duplicating it.
    /// </summary>
    public static bool GameIdsContains(string? gameIdsJson, Guid gameId)
    {
        if (string.IsNullOrWhiteSpace(gameIdsJson)) return false;
        return DeserializeGameIds(gameIdsJson).Contains(gameId);
    }

    /// <summary>
    /// Leaderboard: aggregate win-count + buchholz tie-breaker per
    /// player. Returns rows ordered by (wins desc, buchholz desc).
    /// </summary>
    public async Task<List<TournamentLeaderboardRow>> LeaderboardAsync(Guid tournamentId, CancellationToken ct = default)
    {
        var matches = await _db.TournamentMatches
            .Where(m => m.TournamentId == tournamentId)
            .ToListAsync(ct);
        var regs = await _db.TournamentRegistrations
            .Where(r => r.TournamentId == tournamentId)
            .ToListAsync(ct);

        var wins = new Dictionary<string, int>(StringComparer.Ordinal);
        var opponents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var reg in regs)
        {
            wins[reg.PlayerId] = 0;
            opponents[reg.PlayerId] = new();
        }
        foreach (var m in matches)
        {
            foreach (var pid in MatchPlayerIds(m))
            {
                if (!opponents.ContainsKey(pid))
                {
                    opponents[pid] = new();
                    wins[pid] = 0;
                }
            }
            if (m.Status == "complete" && !string.IsNullOrWhiteSpace(m.WinnerPlayerId))
            {
                wins[m.WinnerPlayerId!] = wins.GetValueOrDefault(m.WinnerPlayerId!) + 1;
            }
            var players = MatchPlayerIds(m).ToList();
            foreach (var p in players)
            {
                foreach (var o in players.Where(o => o != p))
                {
                    opponents[p].Add(o);
                }
            }
        }

        var leaderboard = wins.Select(kv => new TournamentLeaderboardRow(
            PlayerId: kv.Key,
            Wins: kv.Value,
            Buchholz: TournamentPairing.BuchholzScore(wins, opponents.GetValueOrDefault(kv.Key) ?? new())))
            .OrderByDescending(r => r.Wins)
            .ThenByDescending(r => r.Buchholz)
            .ThenBy(r => r.PlayerId, StringComparer.Ordinal)
            .ToList();
        return leaderboard;
    }

    private static bool IsKnownFormat(string format)
        => format is "single-elimination" or "round-robin" or "swiss" or "double-elimination";

    private static TournamentMatch BuildMatch(Guid tournamentId, int round, TournamentPairing.Pairing pair)
        => new()
        {
            Id = Guid.NewGuid(),
            TournamentId = tournamentId,
            Round = round,
            Player1Id = pair.P1,
            Player2Id = pair.P2,
            Player3Id = pair.P3,
            Player4Id = pair.P4,
            Status = "pending",
            GameIdsJson = "[]",
            CreatedAt = DateTime.UtcNow,
        };

    private static IEnumerable<string> MatchPlayerIds(TournamentMatch m)
    {
        yield return m.Player1Id;
        yield return m.Player2Id;
        if (!string.IsNullOrWhiteSpace(m.Player3Id)) yield return m.Player3Id!;
        if (!string.IsNullOrWhiteSpace(m.Player4Id)) yield return m.Player4Id!;
    }

    /// <summary>
    /// Phase K Wave 8 — Bishop. Broadcasts a bracket-update over
    /// SignalR after a match transitions to <c>complete</c>. The
    /// broadcaster is optional so unit-test constructions that pass
    /// only the DbContext skip the broadcast cleanly.
    /// </summary>
    private Task BroadcastBracketUpdateAsync(Guid tournamentId, CancellationToken ct)
    {
        if (_bracketBroadcaster is null) return Task.CompletedTask;
        return _bracketBroadcaster.BroadcastAsync(tournamentId, ct);
    }

    private static List<Guid> DeserializeGameIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return new();
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static string SerializeGameIds(List<Guid> ids)
        => JsonSerializer.Serialize(ids);

    private async Task<List<TournamentMatch>> MaybeAdvanceRoundAsync(Data.Entities.Tournament tournament, int round, CancellationToken ct)
    {
        var emitted = new List<TournamentMatch>();
        var roundMatches = await _db.TournamentMatches
            .Where(m => m.TournamentId == tournament.Id && m.Round == round)
            .ToListAsync(ct);
        // Note: the just-completed match still has its old Status in
        // the change tracker until SaveChanges runs; we treat it as
        // complete since the caller already flipped it.
        if (roundMatches.Any(m => m.Status != "complete"))
            return emitted;

        if (tournament.Format == "round-robin")
        {
            // All rounds were emitted at start time. Tournament is
            // complete when every match is complete.
            var anyOutstanding = await _db.TournamentMatches
                .Where(m => m.TournamentId == tournament.Id && m.Status != "complete")
                .AnyAsync(ct);
            if (!anyOutstanding)
            {
                tournament.Status = "complete";
                tournament.CompletedAt = DateTime.UtcNow;
            }
            return emitted;
        }

        var winners = roundMatches.Select(m => m.WinnerPlayerId).Where(w => !string.IsNullOrWhiteSpace(w)).Cast<string>().ToList();

        if (winners.Count <= 1)
        {
            tournament.Status = "complete";
            tournament.CompletedAt = DateTime.UtcNow;
            return emitted;
        }

        var nextRound = round + 1;
        if (tournament.Format == "single-elimination" || tournament.Format == "double-elimination")
        {
            // Phase K Wave 6 — Bishop. Double-elimination shares the
            // winners-bracket advancement shape; the losers-bracket
            // resurrection path lands in a follow-up wave (Phase L).
            // Today's path keeps double-elim functioning as a
            // single-bracket tournament so the format string is
            // usable end-to-end.
            for (var i = 0; i < winners.Count; i += 2)
            {
                if (i + 1 >= winners.Count) break;
                var m = BuildMatch(
                    tournament.Id,
                    nextRound,
                    new TournamentPairing.Pairing(winners[i], winners[i + 1], null, null));
                _db.TournamentMatches.Add(m);
                emitted.Add(m);
            }
        }
        else if (tournament.Format == "swiss")
        {
            // Swiss next-round: pair by current standings (wins desc).
            var standings = await LeaderboardAsync(tournament.Id, ct);
            var ordered = standings.Select(s => s.PlayerId).ToList();
            for (var i = 0; i + 1 < ordered.Count; i += 2)
            {
                var m = BuildMatch(
                    tournament.Id,
                    nextRound,
                    new TournamentPairing.Pairing(ordered[i], ordered[i + 1], null, null));
                _db.TournamentMatches.Add(m);
                emitted.Add(m);
            }
        }
        return emitted;
    }

    public sealed record TournamentLeaderboardRow(string PlayerId, int Wins, int Buchholz);
}
