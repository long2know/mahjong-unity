using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Tournament;

/// <summary>
/// Phase K Wave 8 — Bishop. Computes the UI-facing bracket snapshot
/// for a given tournament. The snapshot is the canonical shape
/// Hicks's W8 renderer consumes:
///
/// <code>
/// {
///   "format": "DoubleElimination",
///   "tournamentId": "...",
///   "winnersBracket": [ { "roundNumber": 1, "slots": [...] }, ... ],
///   "losersBracket":  [ { "roundNumber": 1, "slots": [...] }, ... ],
///   "grandFinal":     { "match": {...}, "resetMatch": {...|null} }
/// }
/// </code>
///
/// <para>Each <c>slot</c> carries:
/// <list type="bullet">
///   <item><c>seedA</c>, <c>seedB</c> — player ids OR
///         placeholder tokens (<c>"__pending_wb_r2_m0_p1__"</c>)
///         emitted by the bracket generator for slots that have
///         not yet been filled by upstream match completion.</item>
///   <item><c>winnerSeed</c> — null when the match has not
///         completed; otherwise the winning player id.</item>
///   <item><c>status</c> — <c>"pending"</c> / <c>"live"</c> /
///         <c>"complete"</c> derived from the
///         <see cref="TournamentMatch.Status"/> + completion fields.</item>
/// </list></para>
///
/// <para>For non-double-elimination formats the response collapses
/// the losers/grandFinal sections to empty arrays + null
/// respectively; the wire shape stays the same so the frontend can
/// dispatch on <c>format</c> without branching on response keys.</para>
/// </summary>
public sealed class TournamentBracketSnapshotService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TournamentBracketGenerator _generators;

    public TournamentBracketSnapshotService(
        IServiceScopeFactory scopeFactory,
        TournamentBracketGenerator generators)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _generators = generators ?? throw new ArgumentNullException(nameof(generators));
    }

    public async Task<BracketSnapshot?> BuildAsync(Guid tournamentId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tournament = await db.Tournaments
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tournamentId, ct);
        if (tournament is null) return null;

        var regs = await db.TournamentRegistrations
            .AsNoTracking()
            .Where(r => r.TournamentId == tournamentId)
            .OrderBy(r => r.Seed)
            .ToListAsync(ct);
        var seeds = regs.Select(r => r.PlayerId).ToList();

        var matches = await db.TournamentMatches
            .AsNoTracking()
            .Where(m => m.TournamentId == tournamentId)
            .OrderBy(m => m.Round)
            .ThenBy(m => m.CreatedAt)
            .ToListAsync(ct);

        return Compose(tournament, seeds, matches);
    }

    /// <summary>
    /// Pure compose path — used by tests that exercise the snapshot
    /// shape without standing up the DB.
    /// </summary>
    public BracketSnapshot Compose(
        Data.Entities.Tournament tournament,
        IReadOnlyList<string> seededPlayers,
        IReadOnlyList<TournamentMatch> matches)
    {
        var formatString = NormalisedFormat(tournament.Format);
        if (string.Equals(formatString, "DoubleElimination", StringComparison.Ordinal))
        {
            return ComposeDoubleElim(tournament.Id, seededPlayers, matches);
        }

        // Single-elim / Swiss / round-robin all collapse the layout
        // into the winners bracket only — Hicks's renderer keys on
        // `format` to branch.
        return ComposeSingleSided(tournament.Id, formatString, seededPlayers, matches);
    }

    private BracketSnapshot ComposeDoubleElim(
        Guid tournamentId,
        IReadOnlyList<string> seededPlayers,
        IReadOnlyList<TournamentMatch> matches)
    {
        IReadOnlyList<BracketPairing> generated;
        if (seededPlayers.Count >= 2)
        {
            generated = _generators.Resolve(BracketFormat.DoubleElimination).Generate(seededPlayers);
        }
        else
        {
            generated = Array.Empty<BracketPairing>();
        }

        var winners = BuildRounds(generated.Where(p => p.Bracket == BracketSide.Winners), matches);
        var losers = BuildRounds(generated.Where(p => p.Bracket == BracketSide.Losers), matches);

        var grandFinalPairings = generated.Where(p => p.Bracket == BracketSide.GrandFinal).ToList();
        BracketSlot? finalSlot = null;
        BracketSlot? resetSlot = null;
        if (grandFinalPairings.Count > 0)
        {
            finalSlot = BuildSlot(grandFinalPairings[0], 0, matches, BracketSide.GrandFinal);
        }
        if (grandFinalPairings.Count > 1)
        {
            resetSlot = BuildSlot(grandFinalPairings[1], 0, matches, BracketSide.GrandFinal);
        }

        return new BracketSnapshot(
            Format: "DoubleElimination",
            TournamentId: tournamentId,
            WinnersBracket: winners,
            LosersBracket: losers,
            GrandFinal: new GrandFinalView(finalSlot, resetSlot));
    }

    private BracketSnapshot ComposeSingleSided(
        Guid tournamentId,
        string format,
        IReadOnlyList<string> seededPlayers,
        IReadOnlyList<TournamentMatch> matches)
    {
        IReadOnlyList<BracketPairing> generated = Array.Empty<BracketPairing>();
        if (seededPlayers.Count >= 2)
        {
            try
            {
                generated = _generators.Resolve(format switch
                {
                    "SingleElimination" => BracketFormat.SingleElimination,
                    "Swiss" => BracketFormat.Swiss,
                    "RoundRobin" => BracketFormat.RoundRobin,
                    _ => BracketFormat.SingleElimination,
                }).Generate(seededPlayers);
            }
            catch (ArgumentOutOfRangeException)
            {
                generated = Array.Empty<BracketPairing>();
            }
        }
        var winners = BuildRounds(generated, matches);
        return new BracketSnapshot(
            Format: format,
            TournamentId: tournamentId,
            WinnersBracket: winners,
            LosersBracket: Array.Empty<BracketRound>(),
            GrandFinal: null);
    }

    private static IReadOnlyList<BracketRound> BuildRounds(
        IEnumerable<BracketPairing> pairings,
        IReadOnlyList<TournamentMatch> matches)
    {
        var grouped = pairings
            .GroupBy(p => p.Round)
            .OrderBy(g => g.Key)
            .Select(g => new BracketRound(
                RoundNumber: g.Key,
                Slots: g.Select((pair, idx) => BuildSlot(pair, idx, matches, pair.Bracket)).ToArray()))
            .ToArray();
        return grouped;
    }

    private static BracketSlot BuildSlot(
        BracketPairing pairing,
        int matchIndex,
        IReadOnlyList<TournamentMatch> matches,
        BracketSide bracketSide)
    {
        // Match-row resolution: a TournamentMatch with matching round
        // + players takes precedence; we use it to surface the live
        // winner/status. Pre-round-1 placeholders never resolve a row
        // (the placeholder strings can't match a real PlayerId).
        TournamentMatch? row = null;
        if (!IsPlaceholder(pairing.P1) && !IsPlaceholder(pairing.P2))
        {
            row = matches.FirstOrDefault(m =>
                m.Round == pairing.Round
                && ((m.Player1Id == pairing.P1 && m.Player2Id == pairing.P2)
                    || (m.Player1Id == pairing.P2 && m.Player2Id == pairing.P1)));
        }

        var status = row?.Status switch
        {
            "complete" => "complete",
            "in-progress" => "live",
            _ => "pending",
        };

        return new BracketSlot(
            MatchIndex: matchIndex,
            SeedA: pairing.P1,
            SeedB: pairing.P2,
            WinnerSeed: row?.WinnerPlayerId,
            Status: status,
            BracketSide: bracketSide.ToString());
    }

    internal static bool IsPlaceholder(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith("__pending", StringComparison.Ordinal);

    internal static string NormalisedFormat(string format) =>
        BracketFormats.TryParse(format, out var parsed) ? parsed.ToString() : format;
}

/// <summary>
/// Phase K Wave 8 — Bishop. The canonical bracket-snapshot envelope
/// returned by <c>GET /api/tournaments/{id}/bracket</c>.
/// </summary>
public sealed record BracketSnapshot(
    string Format,
    Guid TournamentId,
    IReadOnlyList<BracketRound> WinnersBracket,
    IReadOnlyList<BracketRound> LosersBracket,
    GrandFinalView? GrandFinal);

public sealed record BracketRound(int RoundNumber, IReadOnlyList<BracketSlot> Slots);

public sealed record BracketSlot(
    int MatchIndex,
    string SeedA,
    string SeedB,
    string? WinnerSeed,
    string Status,
    string BracketSide);

public sealed record GrandFinalView(BracketSlot? Match, BracketSlot? ResetMatch);
