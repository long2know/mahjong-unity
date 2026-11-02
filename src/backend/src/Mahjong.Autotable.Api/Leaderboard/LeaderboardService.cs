using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Players;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Leaderboard;

/// <summary>
/// Phase J Wave 6 — sort axes supported by <c>GET /api/leaderboard</c>.
/// Wire name (lowercase, no underscores) is parsed case-insensitively by
/// <see cref="LeaderboardService.ParseSort(string?)"/>; unknown values fall
/// back to <see cref="GamesWon"/>.
/// </summary>
public enum LeaderboardSort
{
    GamesWon = 0,
    TotalScore = 1,
    WinRate = 2,
    LongestStreak = 3,
    HighestScore = 4,
}

/// <summary>
/// Phase J Wave 6 — denormalised leaderboard row. The <c>rank</c> is
/// 1-based and reflects the position within the filtered + sorted result
/// set (NOT a global ranking — paging shifts the rank window). The
/// <c>winRate</c> field is <c>GamesWon / GamesPlayed</c>, clamped to 0
/// when <c>GamesPlayed == 0</c>.
/// </summary>
public sealed record LeaderboardRow(
    int Rank,
    string PlayerId,
    string DisplayName,
    string AvatarColor,
    int GamesPlayed,
    int GamesWon,
    double WinRate,
    long TotalScore,
    int HighestSingleGameScore,
    int LongestWinStreak);

/// <summary>
/// Phase J Wave 6 — full leaderboard response envelope.
/// <c>total</c> is the count of profiles that pass the
/// <c>minGames</c> filter (paging-independent); <c>rows</c> is the
/// current paginated slice.
/// </summary>
public sealed record LeaderboardResponse(int Total, IReadOnlyList<LeaderboardRow> Rows);

/// <summary>
/// Phase J Wave 6 — career-stat leaderboard service. Joins
/// <see cref="PlayerStats"/> with <see cref="PlayerProfile"/>, applies the
/// <c>minGames</c> filter, sorts by the requested axis, and paginates.
///
/// <para><b>Performance posture:</b> the table is small in V1 (one row per
/// player), so we hit the DB twice — once for <c>COUNT</c>, once for the
/// sorted slice — without an explicit covering index. Both queries are
/// EF Core-translatable to single SQL statements; the SQLite query
/// planner picks a table scan + sort for tables under a few thousand
/// rows, which is well inside our V1 envelope. Add an index on
/// <c>(GamesWon DESC)</c> in a future wave if profiling shows hot-path
/// pressure.</para>
///
/// <para><b>WinRate computation:</b> projected SQL-side as
/// <c>(double)GamesWon / GamesPlayed</c>. The <c>minGames &gt;= 0</c>
/// clamp combined with the explicit branch <c>GamesPlayed &gt; 0 ? … : 0</c>
/// keeps the divisor safe even when <c>minGames</c> is 0.</para>
/// </summary>
public sealed class LeaderboardService
{
    /// <summary>Default page size when <c>limit</c> is omitted.</summary>
    public const int DefaultLimit = 50;

    /// <summary>
    /// Upper bound on <c>limit</c>. Larger values are silently clamped — the
    /// frontend should paginate via <c>offset</c> rather than asking for a
    /// huge page.
    /// </summary>
    public const int MaxLimit = 100;

    /// <summary>
    /// Default value for the <c>minGames</c> filter. Keeps the leaderboard
    /// meaningful by hiding profiles with too few games to have a stable
    /// win-rate. The default of 5 mirrors what most casual leaderboards
    /// use; the frontend can override per-axis (e.g. raise to 20 for
    /// <c>winRate</c> when noise is high).
    /// </summary>
    public const int DefaultMinGames = 5;

    private readonly IServiceScopeFactory _scopeFactory;

    public LeaderboardService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Parses the case-insensitive wire <c>sort</c> string into a
    /// <see cref="LeaderboardSort"/>. Unknown / null / empty values fall
    /// back to <see cref="LeaderboardSort.GamesWon"/>.
    /// </summary>
    public static LeaderboardSort ParseSort(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return LeaderboardSort.GamesWon;
        return raw.Trim().ToLowerInvariant() switch
        {
            "gameswon" => LeaderboardSort.GamesWon,
            "totalscore" => LeaderboardSort.TotalScore,
            "winrate" => LeaderboardSort.WinRate,
            "longeststreak" => LeaderboardSort.LongestStreak,
            "highestscore" => LeaderboardSort.HighestScore,
            _ => LeaderboardSort.GamesWon,
        };
    }

    /// <summary>
    /// Loads a leaderboard page. <paramref name="limit"/> is clamped to
    /// <see cref="MaxLimit"/>; <paramref name="offset"/> is clamped to
    /// <c>&gt;= 0</c>; <paramref name="minGames"/> is clamped to
    /// <c>&gt;= 0</c>. The total count reflects the post-filter row count
    /// so the frontend can render pagination controls without re-fetching.
    /// </summary>
    public async Task<LeaderboardResponse> GetAsync(
        LeaderboardSort sort,
        int limit,
        int offset,
        int minGames,
        CancellationToken ct = default)
    {
        if (limit <= 0) limit = DefaultLimit;
        if (limit > MaxLimit) limit = MaxLimit;
        if (offset < 0) offset = 0;
        if (minGames < 0) minGames = 0;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Inner-join projection — pairs each stats row with its profile so a
        // missing profile (defensive — never happens through normal flows)
        // doesn't NRE the DTO.
        var baseQuery = db.PlayerStats
            .AsNoTracking()
            .Where(s => s.GamesPlayed >= minGames)
            .Join(
                db.PlayerProfiles.AsNoTracking(),
                stats => stats.PlayerId,
                profile => profile.PlayerId,
                (stats, profile) => new LeaderboardRowProjection
                {
                    PlayerId = profile.PlayerId,
                    DisplayName = profile.DisplayName,
                    AvatarColor = profile.AvatarColor,
                    GamesPlayed = stats.GamesPlayed,
                    GamesWon = stats.GamesWon,
                    TotalScore = stats.TotalScore,
                    HighestSingleGameScore = stats.HighestSingleGameScore,
                    LongestWinStreak = stats.LongestWinStreak,
                    WinRate = stats.GamesPlayed > 0
                        ? (double)stats.GamesWon / stats.GamesPlayed
                        : 0.0,
                });

        var total = await baseQuery.CountAsync(ct);

        var ordered = sort switch
        {
            LeaderboardSort.TotalScore => baseQuery
                .OrderByDescending(r => r.TotalScore)
                .ThenByDescending(r => r.GamesWon)
                .ThenBy(r => r.PlayerId),
            LeaderboardSort.WinRate => baseQuery
                .OrderByDescending(r => r.WinRate)
                .ThenByDescending(r => r.GamesPlayed)
                .ThenBy(r => r.PlayerId),
            LeaderboardSort.LongestStreak => baseQuery
                .OrderByDescending(r => r.LongestWinStreak)
                .ThenByDescending(r => r.GamesWon)
                .ThenBy(r => r.PlayerId),
            LeaderboardSort.HighestScore => baseQuery
                .OrderByDescending(r => r.HighestSingleGameScore)
                .ThenByDescending(r => r.GamesWon)
                .ThenBy(r => r.PlayerId),
            _ => baseQuery
                .OrderByDescending(r => r.GamesWon)
                .ThenByDescending(r => r.WinRate)
                .ThenBy(r => r.PlayerId),
        };

        var page = await ordered
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        var rows = new List<LeaderboardRow>(page.Count);
        for (var i = 0; i < page.Count; i++)
        {
            var p = page[i];
            rows.Add(new LeaderboardRow(
                Rank: offset + i + 1,
                PlayerId: p.PlayerId,
                DisplayName: p.DisplayName,
                AvatarColor: p.AvatarColor,
                GamesPlayed: p.GamesPlayed,
                GamesWon: p.GamesWon,
                WinRate: p.WinRate,
                TotalScore: p.TotalScore,
                HighestSingleGameScore: p.HighestSingleGameScore,
                LongestWinStreak: p.LongestWinStreak));
        }

        return new LeaderboardResponse(total, rows);
    }

    /// <summary>
    /// Internal flat projection used between the EF Core query and the
    /// final DTO mapping. Exposed at type-level so the EF translator can
    /// pick it up; <c>Select(r =&gt; new LeaderboardRowProjection { … })</c>
    /// requires a parameterless ctor with set-accessors.
    /// </summary>
    private sealed class LeaderboardRowProjection
    {
        public string PlayerId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string AvatarColor { get; set; } = string.Empty;
        public int GamesPlayed { get; set; }
        public int GamesWon { get; set; }
        public long TotalScore { get; set; }
        public int HighestSingleGameScore { get; set; }
        public int LongestWinStreak { get; set; }
        public double WinRate { get; set; }
    }
}
