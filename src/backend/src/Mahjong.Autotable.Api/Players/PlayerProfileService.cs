using System.Text.RegularExpressions;
using Mahjong.Autotable.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Players;

/// <summary>
/// Phase J Wave 5 — service for reading/writing <see cref="PlayerProfile"/>
/// and <see cref="PlayerStats"/>. Singleton-scoped because it is consumed by
/// other singletons (the runtime and the hub); it uses an
/// <see cref="IServiceScopeFactory"/> to open a fresh scoped <see cref="AppDbContext"/>
/// per call. Every DB write is wrapped in a single <c>SaveChangesAsync</c>
/// so callers see all-or-nothing semantics.
/// </summary>
public sealed class PlayerProfileService
{
    private static readonly Regex AvatarColorPattern = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PlayerProfileService> _logger;

    public PlayerProfileService(IServiceScopeFactory scopeFactory, ILogger<PlayerProfileService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns a stable default display name keyed off the player id. We
    /// hash the id (FNV-1a) and select from a 16-entry palette so the same
    /// player always sees the same "Player-XYZ" assignment until they pick
    /// their own name. Keeps the lobby readable when nobody has customised
    /// their profile yet.
    /// </summary>
    public static string DefaultDisplayName(string playerId)
    {
        if (string.IsNullOrEmpty(playerId)) return "Player";
        var hash = Fnv1aHash(playerId);
        return $"Player-{(hash & 0xFFFFFF):X6}";
    }

    /// <summary>
    /// Returns a stable default avatar colour keyed off the player id.
    /// Hashed pick from Hicks's 8-entry preset palette (the same set
    /// surfaced in <c>src/frontend/autotable-src/src/profile.ts</c>
    /// as <c>AVATAR_COLOR_PRESETS</c>) so the colour is deterministic
    /// (a reconnect with the same id reproduces the same chip colour)
    /// AND is guaranteed to be a palette member — the frontend renders
    /// the same colour on the lobby chip, profile card, and onboarding
    /// preview without any extra mapping. Phase J Wave 7 trimmed the
    /// helper from the legacy 16-entry HSL palette to this canonical
    /// 8-entry set; the previous wider palette emitted "ghost" colours
    /// that didn't match anything the user could pick by hand.
    /// </summary>
    public static string DefaultAvatarColor(string playerId)
    {
        // Mirrors AVATAR_COLOR_PRESETS in
        // src/frontend/autotable-src/src/profile.ts (Hicks, Wave 5).
        // Order is the canonical palette order; pick is FNV-hash modulo
        // length so adding/removing an entry deterministically reshuffles
        // every player's default (acceptable — defaults are advisory and
        // overridden by any prior UpdateAvatarColor call).
        string[] palette =
        {
            "#c0392b", "#e67e22", "#f1c40f", "#2ecc71",
            "#16a085", "#2980b9", "#8e44ad", "#34495e",
        };
        if (string.IsNullOrEmpty(playerId)) return palette[0];
        var hash = Fnv1aHash(playerId);
        return palette[hash % (uint)palette.Length];
    }

    private static uint Fnv1aHash(string s)
    {
        unchecked
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            var hash = offset;
            foreach (var c in s)
            {
                hash ^= c;
                hash *= prime;
            }
            return hash;
        }
    }

    /// <summary>
    /// Returns the existing <see cref="PlayerProfile"/> for <paramref name="playerId"/>
    /// or creates one (with default display name + avatar colour) on first
    /// access. Always touches <c>LastSeenAt</c> so the lobby UI can show
    /// "recently online" indicators in a future wave.
    /// </summary>
    public Task<PlayerProfile> GetOrCreateAsync(string playerId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(playerId)) throw new ArgumentException("playerId required", nameof(playerId));

        var now = DateTime.UtcNow;
        return UpsertProfileAsync(
            playerId,
            onCreate: profile =>
            {
                profile.DisplayName = DefaultDisplayName(playerId);
                profile.AvatarColor = DefaultAvatarColor(playerId);
                profile.CreatedAt = now;
                profile.LastSeenAt = now;
            },
            onExisting: profile => profile.LastSeenAt = DateTime.UtcNow,
            ct);
    }

    /// <summary>
    /// Returns the <see cref="PlayerStats"/> row for the player, creating a
    /// fresh zeroed one if the player has never played (defensive — should
    /// only happen if the profile was created out-of-band).
    /// </summary>
    public async Task<PlayerStats> GetStatsAsync(string playerId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(playerId)) throw new ArgumentException("playerId required", nameof(playerId));

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stats = await db.PlayerStats.FirstOrDefaultAsync(s => s.PlayerId == playerId, ct);
        if (stats is null)
        {
            stats = new PlayerStats { PlayerId = playerId };
            db.PlayerStats.Add(stats);
            await db.SaveChangesAsync(ct);
        }
        return stats;
    }

    /// <summary>
    /// Updates <see cref="PlayerProfile.DisplayName"/> after trimming and
    /// length-validating (1–32 chars, no leading/trailing whitespace).
    /// Throws <see cref="ArgumentException"/> on invalid input — callers
    /// at the hub layer translate this to a <c>HubException</c>.
    /// </summary>
    public Task<PlayerProfile> UpdateDisplayNameAsync(string playerId, string displayName, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(playerId)) throw new ArgumentException("playerId required", nameof(playerId));
        if (displayName is null) throw new ArgumentException("displayName required", nameof(displayName));
        var trimmed = displayName.Trim();
        if (trimmed.Length is < 1 or > 32)
        {
            throw new ArgumentException("displayName must be 1–32 characters after trimming.", nameof(displayName));
        }
        if (trimmed != displayName)
        {
            // Reject leading/trailing whitespace explicitly so the rejection
            // is visible — the actual stored value is `trimmed`.
            throw new ArgumentException("displayName must not have leading or trailing whitespace.", nameof(displayName));
        }

        var now = DateTime.UtcNow;
        return UpsertProfileAsync(
            playerId,
            onCreate: profile =>
            {
                profile.DisplayName = trimmed;
                profile.AvatarColor = DefaultAvatarColor(playerId);
                profile.CreatedAt = now;
                profile.LastSeenAt = now;
            },
            onExisting: profile =>
            {
                profile.DisplayName = trimmed;
                profile.LastSeenAt = DateTime.UtcNow;
            },
            ct);
    }

    /// <summary>
    /// Updates <see cref="PlayerProfile.AvatarColor"/>. Accepts only
    /// <c>#RRGGBB</c> (lower or upper case). Throws
    /// <see cref="ArgumentException"/> for any other shape.
    /// </summary>
    public Task<PlayerProfile> UpdateAvatarColorAsync(string playerId, string avatarColor, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(playerId)) throw new ArgumentException("playerId required", nameof(playerId));
        if (string.IsNullOrEmpty(avatarColor) || !AvatarColorPattern.IsMatch(avatarColor))
        {
            throw new ArgumentException("avatarColor must be a #RRGGBB hex string.", nameof(avatarColor));
        }

        var now = DateTime.UtcNow;
        return UpsertProfileAsync(
            playerId,
            onCreate: profile =>
            {
                profile.DisplayName = DefaultDisplayName(playerId);
                profile.AvatarColor = avatarColor;
                profile.CreatedAt = now;
                profile.LastSeenAt = now;
            },
            onExisting: profile =>
            {
                profile.AvatarColor = avatarColor;
                profile.LastSeenAt = DateTime.UtcNow;
            },
            ct);
    }

    /// <summary>
    /// Race-safe upsert shared by <see cref="GetOrCreateAsync"/>,
    /// <see cref="UpdateDisplayNameAsync"/>, and
    /// <see cref="UpdateAvatarColorAsync"/>.
    ///
    /// <para>Drake (backend hotfix, 2026-05-29) — the original SELECT-then-INSERT
    /// pattern in these methods could race when two concurrent requests for the
    /// same persistent player id (e.g. <c>POST /api/identity</c> + the
    /// <c>ChangshaHub.OnConnectedAsync</c> "ensure profile on first connect"
    /// branch, or two browser tabs onboarding together) both observed
    /// <c>FirstOrDefault → null</c> and both called <c>PlayerProfiles.Add</c>,
    /// causing the second <c>SaveChangesAsync</c> to throw
    /// <c>DbUpdateException</c> → <c>SqliteException 19 — UNIQUE constraint
    /// failed: PlayerProfiles.PlayerId</c> in live play. The upsert flow below
    /// catches the unique-violation, discards the losing scope (so the dangling
    /// tracked inserts are dropped with it), and re-enters the loop to take the
    /// "update existing row" branch with the row the winning request just
    /// committed. Exactly one retry — if even the post-retry SELECT misses, the
    /// next save's exception bubbles up so a genuine schema problem isn't
    /// masked.</para>
    /// </summary>
    private async Task<PlayerProfile> UpsertProfileAsync(
        string playerId,
        Action<PlayerProfile> onCreate,
        Action<PlayerProfile> onExisting,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.PlayerProfiles.FirstOrDefaultAsync(p => p.PlayerId == playerId, ct);
            if (profile is null)
            {
                profile = new PlayerProfile { PlayerId = playerId };
                onCreate(profile);
                db.PlayerProfiles.Add(profile);

                // Pair the profile with an empty stats row so subsequent
                // RecordGameCompleted calls don't need a "create on first game"
                // branch — keeps the hot-path single-query. Defensive AnyAsync
                // skips the insert if a concurrent GetStatsAsync already
                // landed the stats row (rare but possible — stats has no FK
                // problem either way because we're adding the profile in the
                // same SaveChanges).
                var statsExist = await db.PlayerStats.AnyAsync(s => s.PlayerId == playerId, ct);
                if (!statsExist)
                {
                    db.PlayerStats.Add(new PlayerStats { PlayerId = playerId });
                }
            }
            else
            {
                onExisting(profile);
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return profile;
            }
            catch (DbUpdateException ex) when (attempt == 0 && IsUniqueViolation(ex))
            {
                _logger.LogDebug(
                    ex,
                    "UpsertProfileAsync lost an insert race for {PlayerId}; retrying via update branch.",
                    playerId);
                // Drop this scope (and its tracked Add() entries) and loop —
                // the next iteration's FirstOrDefault will find the row the
                // other caller just committed and take the existing-row path.
            }
        }
    }

    /// <summary>
    /// Cross-provider unique-constraint-violation predicate. Returns true when
    /// the innermost exception identifies a UNIQUE / PRIMARY KEY violation on
    /// SQLite (<c>SqliteErrorCode == 19</c>), Postgres (<c>SqlState == "23505"</c>),
    /// or SQL Server (<c>Number == 2627 or 2601</c>) — covering every database
    /// provider this codebase ships against (see
    /// <c>Persistence/ServiceCollectionExtensions.cs</c>). Used by
    /// <see cref="UpsertProfileAsync"/> to recognise the "another request just
    /// inserted the same PlayerId" race so we can re-fetch instead of failing.
    /// </summary>
    // Visibility note (Drake, 2026-06-03 thorough-audit pass): bumped from
    // `private static` to `internal static` so the cross-provider parity
    // unit test (`IsUniqueViolationCrossProviderTests`) can drive the
    // predicate directly with a synthetic provider exception for each of
    // SQLite, Postgres, and SqlServer — verifying the
    // "fix lands once, works everywhere" claim of the upsert hotfix
    // without needing a live Postgres / SqlServer in CI. The
    // <c>InternalsVisibleTo</c> entry for
    // <c>Mahjong.Autotable.Api.Tests</c> is already declared in the API
    // project's csproj (line 51), so no project-file change is needed.
    internal static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case Microsoft.Data.Sqlite.SqliteException sqlite when sqlite.SqliteErrorCode == 19:
                    return true;
                case Npgsql.PostgresException pg when pg.SqlState == "23505":
                    return true;
                case Microsoft.Data.SqlClient.SqlException sql when sql.Number == 2627 || sql.Number == 2601:
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Records the completion of a game. <paramref name="finalScores"/> is
    /// keyed by <c>PlayerId</c> (per-seat sums already collapsed by the
    /// caller); <paramref name="winners"/> is the set of <c>PlayerId</c>s
    /// tied at the top score. PlayerIds prefixed with <c>bot-</c> are
    /// filtered (bots have no profile). All updates happen in a single
    /// <c>SaveChangesAsync</c>; DB exceptions are logged and swallowed so a
    /// stats failure can never break game completion.
    /// </summary>
    public async Task RecordGameCompletedAsync(
        IReadOnlyDictionary<string, int> finalScores,
        IReadOnlySet<string> winners,
        CancellationToken ct = default)
    {
        if (finalScores is null || finalScores.Count == 0) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;

            foreach (var (playerId, score) in finalScores)
            {
                if (string.IsNullOrEmpty(playerId)) continue;
                if (playerId.StartsWith("bot-", StringComparison.Ordinal)) continue;

                var stats = await db.PlayerStats.FirstOrDefaultAsync(s => s.PlayerId == playerId, ct);
                if (stats is null)
                {
                    stats = new PlayerStats { PlayerId = playerId };
                    db.PlayerStats.Add(stats);

                    // Guarantee a profile row exists too so the stats FK
                    // resolves on cascade-aware providers.
                    var profileExists = await db.PlayerProfiles.AnyAsync(p => p.PlayerId == playerId, ct);
                    if (!profileExists)
                    {
                        db.PlayerProfiles.Add(new PlayerProfile
                        {
                            PlayerId = playerId,
                            DisplayName = DefaultDisplayName(playerId),
                            AvatarColor = DefaultAvatarColor(playerId),
                            CreatedAt = now,
                            LastSeenAt = now,
                        });
                    }
                }

                stats.GamesPlayed += 1;
                stats.TotalScore += score;
                if (score > stats.HighestSingleGameScore) stats.HighestSingleGameScore = score;

                if (winners.Contains(playerId))
                {
                    stats.GamesWon += 1;
                    stats.CurrentWinStreak += 1;
                    if (stats.CurrentWinStreak > stats.LongestWinStreak)
                        stats.LongestWinStreak = stats.CurrentWinStreak;
                }
                else
                {
                    stats.CurrentWinStreak = 0;
                }
                stats.LastGameAt = now;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recording game-completed stats failed; counters may be stale.");
        }
    }
}
