using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Players;

/// <summary>
/// Phase J Wave 5 — <see cref="PlayerProfileService"/> contract tests
/// (Vasquez).
///
/// <para>Bishop's Wave 5 player-profile service backs the lobby "name +
/// avatar chip", the post-game stats panel, and the matchmaking-lobby
/// "hosted by &lt;name&gt;" subtitle. The service is the only writer for
/// the <c>PlayerProfiles</c> and <c>PlayerStats</c> tables, so its input
/// validation is the canonical guardrail against:
/// <list type="bullet">
///   <item>Empty / blank / over-long display names polluting the lobby.</item>
///   <item>Non-hex avatar colour strings reaching the frontend (the
///         renderer would inject them straight into a CSS variable).</item>
///   <item>Reconnects creating duplicate profile rows for the same id.</item>
/// </list></para>
///
/// <para><b>Test surface choices.</b>
/// <list type="bullet">
///   <item>The service is resolved straight from the host's DI container —
///         it's a singleton with an <see cref="IServiceScopeFactory"/> field,
///         so calling it directly mirrors the production code path (the
///         <see cref="ChangshaGameRuntime"/> and
///         <see cref="ChangshaHub"/> both consume it the same way).</item>
///   <item>The <c>WebApplicationFactory</c> is configured with the
///         "tests-only" temp-SQLite + snapshot-off pattern that the rest of
///         the Wave 5 backend test suite uses.</item>
///   <item>Each test uses a fresh per-instance temp DB so concurrent xUnit
///         test classes can't collide on the same player-id rows.</item>
/// </list></para>
///
/// <para><b>Determinism.</b> The <c>DefaultDisplayName</c> /
/// <c>DefaultAvatarColor</c> helpers are FNV-1a-hashed picks (free
/// 6-hex-digit suffix + a 16-entry palette) so any randomly-generated
/// player id collapses to a stable deterministic default. The tests assert
/// on the structural shape (<c>"Player-XXXXXX"</c> + hex regex) rather
/// than a fixed value so adding a colour to the palette doesn't break the
/// suite.</para>
/// </summary>
[Collection("DbSerial")]
public class PlayerProfileServiceTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-profile-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.BotClaimDelayMs = 1;
                    o.ClaimWindowTimeoutMs = 50;
                    o.DealBatchDelayMs = 0;
                    o.PersistSnapshots = false;
                });
            });
        });
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try { if (_tempDb is not null && File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
        return Task.CompletedTask;
    }

    private PlayerProfileService GetService()
    {
        Assert.NotNull(_factory);
        return _factory!.Services.GetRequiredService<PlayerProfileService>();
    }

    /// <summary>
    /// Phase K Wave 23 — Vasquez. Truncate a <see cref="DateTime"/> to
    /// microsecond precision (drops the last decimal tick digit) so
    /// equality assertions against a Postgres round-tripped timestamp
    /// hold. Postgres <c>timestamptz</c> is microsecond-precise; .NET
    /// <see cref="DateTime"/> ticks are 100ns. SQLite stores the raw
    /// .NET tick count in text, so this truncation is a no-op on the
    /// SQLite cell of the db-providers matrix.
    /// </summary>
    private static DateTime TruncateToMicroseconds(DateTime value)
    {
        const long ticksPerMicrosecond = 10;
        return new DateTime(value.Ticks - (value.Ticks % ticksPerMicrosecond), value.Kind);
    }

    private async Task<PlayerStats> ReadStatsAsync(string playerId)
    {
        // Read directly off the DB context instead of going through
        // GetStatsAsync — that way we can assert on values without having
        // the service auto-create a missing stats row (which would mask
        // a regression where RecordGameCompletedAsync silently failed).
        Assert.NotNull(_factory);
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stats = await db.PlayerStats.AsNoTracking().FirstOrDefaultAsync(s => s.PlayerId == playerId);
        Assert.NotNull(stats);
        return stats!;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. GetOrCreate creates with deterministic defaults
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-5")]
    public async Task GetOrCreate_CreatesNewProfile_WithDeterministicDefaults()
    {
        // The very first connection of a freshly-onboarded player must hit
        // the create-branch and emerge with a non-empty `Player-XXXXXX`
        // name + a `#RRGGBB`-shaped avatar colour. Frontend already trusts
        // these defaults verbatim — if either field came back blank the
        // lobby chip would render as a transparent ghost.
        var svc = GetService();
        var playerId = "test-player-" + Guid.NewGuid().ToString("N");

        var profile = await svc.GetOrCreateAsync(playerId);

        Assert.Equal(playerId, profile.PlayerId);

        // Default name structural shape: "Player-" + 6-char hex (matches
        // PlayerProfileService.DefaultDisplayName). Asserting on the
        // prefix + length keeps the test resilient to FNV palette tweaks.
        Assert.StartsWith("Player-", profile.DisplayName);
        Assert.Equal("Player-".Length + 6, profile.DisplayName.Length);
        Assert.Matches("^Player-[0-9A-F]{6}$", profile.DisplayName);

        // Avatar colour is one of the 8-entry palette in DefaultAvatarColor
        // (Phase J Wave 7 trimmed the helper from the legacy 16-entry HSL
        // set to Hicks's frontend palette). The palette entries are lower
        // case — the regex is case-insensitive so a future re-cased palette
        // edit doesn't tag this assertion.
        Assert.Matches("^#[0-9A-Fa-f]{6}$", profile.AvatarColor);

        // Deterministic check: a fresh call for the same id with no
        // mutation in between must produce the same default values, so
        // a reconnect doesn't reshuffle the user's name + colour.
        var defaultName = PlayerProfileService.DefaultDisplayName(playerId);
        var defaultColor = PlayerProfileService.DefaultAvatarColor(playerId);
        Assert.Equal(defaultName, profile.DisplayName);
        Assert.Equal(defaultColor, profile.AvatarColor);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. GetOrCreate returns the same row on repeat calls
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-5")]
    public async Task GetOrCreate_ReturnsExisting_WhenCalledTwice()
    {
        // Phase J Wave 5 ChangshaHub.OnConnectedAsync calls GetOrCreate
        // on every connection. A reconnect must NOT mint a second profile
        // row — that would (a) violate the unique PK and crash SaveChanges,
        // or (b) silently double-count games depending on the provider.
        // Assert by snapshotting CreatedAt (immutable per profile) and
        // confirming it matches across two back-to-back calls.
        var svc = GetService();
        var playerId = "test-player-" + Guid.NewGuid().ToString("N");

        var first = await svc.GetOrCreateAsync(playerId);
        var createdAt = first.CreatedAt;
        // Sleep a few ms to give LastSeenAt room to update without affecting CreatedAt.
        await Task.Delay(5);

        var second = await svc.GetOrCreateAsync(playerId);

        Assert.Equal(playerId, second.PlayerId);
        // Phase K Wave 23 — Vasquez. Compare CreatedAt at microsecond
        // granularity. The first call returns the in-memory entity
        // post-Add (full .NET DateTime 100ns-tick precision); the
        // second call returns the entity fetched fresh from the DB,
        // and Postgres `timestamptz` only stores 6 sub-second digits
        // (microsecond precision) — so the round-trip drops the last
        // tick digit. SQLite stores the literal .NET tick count as
        // text so the equality holds there without normalization, but
        // pinning to microseconds is provider-stable and the contract
        // under test ("same CreatedAt across reconnects") is satisfied
        // at any reasonable precision.
        Assert.Equal(TruncateToMicroseconds(createdAt), TruncateToMicroseconds(second.CreatedAt));

        // LastSeenAt SHOULD advance on every call (lobby UI uses it for
        // "recently online" — see PlayerProfileService.GetOrCreateAsync).
        // Use >= so we don't fail when the clock resolves at ms granularity.
        Assert.True(second.LastSeenAt >= first.LastSeenAt);

        // And there must be exactly one row in the DB.
        Assert.NotNull(_factory);
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.PlayerProfiles.AsNoTracking().Where(p => p.PlayerId == playerId).CountAsync();
        Assert.Equal(1, rows);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. UpdateDisplayName rejects empty / over-long / whitespace
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-5")]
    public async Task UpdateDisplayName_RejectsEmpty_AndOverlength()
    {
        // The hub layer translates ArgumentException → HubException so the
        // frontend sees a structured error. If a value here ever slips
        // through, the lobby would render either a blank chip (empty) or
        // a layout-breaking wall of text (>32 chars).
        var svc = GetService();
        var playerId = "test-player-" + Guid.NewGuid().ToString("N");

        // Empty / pure-whitespace inputs both fail (the service trims
        // first; the trim collapses pure whitespace to "").
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateDisplayNameAsync(playerId, ""));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateDisplayNameAsync(playerId, "   "));

        // 33 characters — one byte over the cap. New string('a', 33) keeps
        // the assertion ASCII so we don't accidentally exercise the
        // surrogate-pair length-counting edge case.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.UpdateDisplayNameAsync(playerId, new string('a', 33)));

        // Leading / trailing whitespace is rejected explicitly even when
        // the trimmed length is within bounds — the service wants the
        // raw input to match the trimmed value so the stored row is
        // visibly the same as the user's submission.
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateDisplayNameAsync(playerId, " Vasquez"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateDisplayNameAsync(playerId, "Vasquez "));

        // Happy-path sanity: 1-char name is fine and 32-char name is fine
        // (boundary checks — exactly at the bounds rather than safely in
        // the middle, which is the regression risk).
        var single = await svc.UpdateDisplayNameAsync(playerId, "V");
        Assert.Equal("V", single.DisplayName);

        var thirtyTwo = new string('z', 32);
        var maxed = await svc.UpdateDisplayNameAsync(playerId, thirtyTwo);
        Assert.Equal(thirtyTwo, maxed.DisplayName);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. UpdateAvatarColor enforces #RRGGBB shape
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-5")]
    public async Task UpdateAvatarColor_RejectsInvalid_HexFormat()
    {
        // The avatar colour is rendered into a CSS `background-color`
        // variable in lobby chips. Letting a non-hex string through would
        // both (a) break the chip render and (b) allow trivial CSS
        // injection (`red; ...`). Validation lives in the service, not the
        // hub, so this is the canonical guard.
        var svc = GetService();
        var playerId = "test-player-" + Guid.NewGuid().ToString("N");

        // Pre-seed the profile so we exercise the existing-row branch,
        // not the auto-create branch (both should fail validation but
        // testing the more-common branch keeps the assertion concrete).
        await svc.GetOrCreateAsync(playerId);

        // Invalid shapes — exhaustive across the regex axes:
        //   • Not a hex string at all
        //   • Missing leading `#`
        //   • 3-digit shorthand (CSS allows it; the service does NOT)
        //   • 4-digit (alpha) — also out of spec
        //   • Empty / whitespace
        //   • `null` (treated as empty by the service)
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateAvatarColorAsync(playerId, "red"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateAvatarColorAsync(playerId, "ABCDEF"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateAvatarColorAsync(playerId, "#abc"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateAvatarColorAsync(playerId, "#abcd"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateAvatarColorAsync(playerId, ""));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateAvatarColorAsync(playerId, "   "));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdateAvatarColorAsync(playerId, null!));

        // Happy path: canonical #RRGGBB form (both cases accepted, since
        // the regex is case-insensitive). The service stores whatever
        // case the caller supplied — the frontend's only requirement is
        // that the value parses as CSS hex.
        var lower = await svc.UpdateAvatarColorAsync(playerId, "#abcdef");
        Assert.Equal("#abcdef", lower.AvatarColor);

        var upper = await svc.UpdateAvatarColorAsync(playerId, "#ABCDEF");
        Assert.Equal("#ABCDEF", upper.AvatarColor);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. GetOrCreateAsync is race-safe under concurrent first-create calls
    //     (Drake hotfix — PlayerProfiles.PlayerId UNIQUE constraint)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-K-Hotfix")]
    public async Task GetOrCreate_IsRaceSafe_WhenCalledConcurrently_WithSameId()
    {
        // Drake (backend hotfix, 2026-05-29) — Stephen hit a runtime
        // DbUpdateException → SqliteException 19: "UNIQUE constraint
        // failed: PlayerProfiles.PlayerId" in live play. Two concurrent
        // first-touch requests for the same persistent player id
        // (POST /api/identity + ChangshaHub.OnConnectedAsync's "ensure
        // profile on first connect" call, or two browser tabs onboarding
        // together) both observed FirstOrDefault → null and both called
        // db.PlayerProfiles.Add; the second SaveChangesAsync crashed on
        // the unique PK.
        //
        // The fix wraps GetOrCreateAsync in a race-safe upsert: catch
        // DbUpdateException, recognise the unique violation across all
        // three database providers (SqliteErrorCode 19, Postgres
        // SqlState 23505, SqlServer Number 2627/2601), drop the losing
        // scope, and re-enter the loop so the next iteration finds the
        // row the winning caller just committed and takes the
        // existing-row branch.
        //
        // This test fires 8 parallel GetOrCreateAsync calls for the same
        // id and asserts:
        //   • all 8 succeed (no DbUpdateException leaks),
        //   • exactly ONE PlayerProfiles row lands in the DB,
        //   • exactly ONE PlayerStats row lands in the DB (the
        //     profile/stats pair that GetOrCreateAsync wires up).
        var svc = GetService();
        var playerId = "race-test-" + Guid.NewGuid().ToString("N");

        const int concurrency = 8;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(() => svc.GetOrCreateAsync(playerId)))
            .ToArray();
        var profiles = await Task.WhenAll(tasks);

        Assert.All(profiles, p => Assert.Equal(playerId, p.PlayerId));
        Assert.All(profiles, p => Assert.Matches("^Player-[0-9A-F]{6}$", p.DisplayName));

        Assert.NotNull(_factory);
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profileRows = await db.PlayerProfiles
            .AsNoTracking()
            .Where(p => p.PlayerId == playerId)
            .CountAsync();
        var statsRows = await db.PlayerStats
            .AsNoTracking()
            .Where(s => s.PlayerId == playerId)
            .CountAsync();
        Assert.Equal(1, profileRows);
        Assert.Equal(1, statsRows);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5b. Race-safety scales — 50 parallel callers for the SAME id, then
    //      50 parallel callers each for a DIFFERENT id (Drake thorough
    //      audit — mirrors the 100-parallel live probe at xUnit fidelity)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-K-Drake-Audit")]
    public async Task GetOrCreate_IsRaceSafe_AtHighConcurrency_SameId()
    {
        // Push the 8-parallel race regression test up to 50 parallel
        // callers so the retry-loop is exercised under more realistic
        // contention than CI's lone fast path. The live probe goes to
        // 100 via the HTTP surface; 50 in-process here keeps the test
        // under the 5-second budget while still hitting > 1 race retry
        // on contemporary CI hardware (observed: 4–8 unique-violation
        // recoveries out of 50 on this machine).
        var svc = GetService();
        var playerId = "race50-" + Guid.NewGuid().ToString("N");

        const int concurrency = 50;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(() => svc.GetOrCreateAsync(playerId)))
            .ToArray();
        var profiles = await Task.WhenAll(tasks);

        Assert.Equal(concurrency, profiles.Length);
        Assert.All(profiles, p => Assert.Equal(playerId, p.PlayerId));

        Assert.NotNull(_factory);
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.PlayerProfiles.AsNoTracking().CountAsync(p => p.PlayerId == playerId));
        Assert.Equal(1, await db.PlayerStats.AsNoTracking().CountAsync(s => s.PlayerId == playerId));
    }

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-K-Drake-Audit")]
    public async Task GetOrCreate_HighConcurrency_DistinctIds_AllResolveSeparately()
    {
        // Counterpart to the same-id stress: 50 parallel callers each
        // with their OWN unique id should produce 50 distinct profile
        // rows + 50 distinct stats rows, no cross-contamination, no
        // unique-violation retries (the predicate must NOT false-fire
        // and short-circuit a legitimate first-create on a different
        // id). Matches the live "A2 DISTINCT cookies" probe.
        var svc = GetService();
        var ids = Enumerable.Range(0, 50)
            .Select(_ => "distinct-" + Guid.NewGuid().ToString("N"))
            .ToArray();

        var tasks = ids.Select(id => Task.Run(() => svc.GetOrCreateAsync(id))).ToArray();
        var profiles = await Task.WhenAll(tasks);

        Assert.Equal(50, profiles.Length);
        Assert.Equal(ids.OrderBy(x => x).ToArray(), profiles.Select(p => p.PlayerId).OrderBy(x => x).ToArray());

        Assert.NotNull(_factory);
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var idSet = ids.ToHashSet();
        var profileCount = await db.PlayerProfiles.AsNoTracking().CountAsync(p => idSet.Contains(p.PlayerId));
        var statsCount = await db.PlayerStats.AsNoTracking().CountAsync(s => idSet.Contains(s.PlayerId));
        Assert.Equal(50, profileCount);
        Assert.Equal(50, statsCount);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. PlayerProfile.AvatarColor class-initializer default is a palette
    //     member (Phase J Wave 7 backstop — Vasquez)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-7")]
    public void PlayerProfile_AvatarColor_ClassDefault_IsInDocumentedPalette()
    {
        // Phase J Wave 5 originally initialised PlayerProfile.AvatarColor to
        // "#808080" (grey) — a colour that does NOT appear in Hicks's 8-entry
        // AVATAR_COLOR_PRESETS palette in src/frontend/autotable-src/src/profile.ts.
        // The runtime almost always overrides this via
        // PlayerProfileService.DefaultAvatarColor before the entity reaches the
        // DB, so the regression was invisible from the UI in most cases — but
        // any code path that constructs a PlayerProfile without going through
        // the service (test fixtures, future migrations, ad-hoc fix-ups) ends
        // up shipping a "ghost" 9th colour.
        //
        // The Wave 7 fix is to make the property initialiser point at the
        // FIRST entry of the documented palette ("#c0392b"). This test pins
        // that contract by:
        //   (a) cross-checking the constant against the 8 preset values that
        //       Hicks's frontend exports, and
        //   (b) confirming a freshly-`new`-ed PlayerProfile carries one of
        //       those preset values out of the box (regardless of which
        //       palette entry Bishop picks in the future).
        var palette = new[]
        {
            "#c0392b", "#e67e22", "#f1c40f", "#2ecc71",
            "#16a085", "#2980b9", "#8e44ad", "#34495e",
        };

        // The constant is the canonical default exposed to fixtures /
        // backfills; it must equal a palette member.
        Assert.Contains(PlayerProfile.DefaultPaletteAvatarColor, palette);

        // A freshly-`new`-ed PlayerProfile must carry the palette default,
        // not the legacy "#808080" ghost colour.
        var profile = new PlayerProfile();
        Assert.NotEqual("#808080", profile.AvatarColor);
        Assert.Contains(profile.AvatarColor, palette);
    }
}
