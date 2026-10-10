using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Patterns;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Api;

/// <summary>
/// Phase J Wave 4 — <c>/api/changsha/pattern-ordering</c> endpoint contract tests
/// (Vasquez).
///
/// <para>Bishop's Phase J Wave 3 work surfaced the canonical
/// <see cref="WinPattern"/> display order as a wire endpoint so the frontend
/// (Hicks's result-modal chip strip) can sort <c>winResult.allPatterns</c>
/// without embedding a parallel copy of the table. The endpoint returns a
/// flat JSON dictionary keyed by camelCase wire names mapped to integer
/// ranks (lower = earlier); the same camelCase strings appear in
/// <c>winResult.allPatterns</c> across both SignalR and the autotable WS
/// transport (see <c>Program.cs</c>'s <c>WinPatternWireName</c>, which
/// mirrors <c>ChangshaToAutotableTranslator.WinPatternToWire</c> and
/// <c>ChangshaGameRuntime.WinPatternToWire</c>).</para>
///
/// <para>The Wave 3 commit landed without endpoint tests — a gap given that
/// any drift between (a) the static ordering table, (b) the runtime
/// emit-side wire names, and (c) the frontend's consumption is a silent
/// regression. These tests pin the wire contract end-to-end through the
/// real Minimal-API route handler so a future blind spot — Bishop adds a
/// new <see cref="WinPattern"/> value but forgets the ordering entry, or a
/// rename desyncs the wire-name mapper from the ordering table — fails
/// loudly at CI time instead of going unnoticed until a player sees a
/// chip stack rendered in the wrong order.</para>
///
/// <para><b>Test strategy.</b> Spin up a <see cref="WebApplicationFactory{TEntryPoint}"/>
/// over <c>Program</c> with the standard test-host configuration (per-instance
/// temp SQLite DB, snapshot persistence off), GET the endpoint, deserialize
/// to <c>Dictionary&lt;string,int&gt;</c>, and assert the three contracts.
/// The factory + ChangshaRuntimeOptions snapshot mirrors
/// <see cref="HealthEndpointTests.InitializeAsync"/> verbatim — same env,
/// same per-test temp DB.</para>
/// </summary>
public class PatternOrderingEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-pattern-ord-{Guid.NewGuid():N}.db");

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

    // ────────────────────────────────────────────────────────────────────
    //  1. 200 OK + flat camelCase → int dictionary shape
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-4")]
    public async Task PatternOrdering_ReturnsOk_WithFlatJsonMap()
    {
        // Phase J Wave 3 contract pin: the endpoint must return a flat JSON
        // object (not nested, not wrapped in `{ patterns: { … } }`) whose
        // values are JSON numbers — the frontend `Object.entries(...)` /
        // numeric `.sort((a, b) => order[a] - order[b])` flow depends on
        // that exact shape. Per-key checks live in test #2 below; this test
        // pins the shape so a future refactor that returns a wrapped or
        // string-typed payload still fails loudly here.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync("/api/changsha/pattern-ordering");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        // Every value must be a non-negative integer (lower = render first).
        // Every key must be a non-empty string whose first character is
        // lowercase — the wire convention shared with winResult.allPatterns.
        var entryCount = 0;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            entryCount++;
            Assert.False(string.IsNullOrWhiteSpace(prop.Name),
                "pattern-ordering keys must be non-empty strings.");
            Assert.True(char.IsLower(prop.Name[0]),
                $"pattern-ordering key '{prop.Name}' is not camelCase " +
                "(must start lowercase to match the wire format used by " +
                "winResult.allPatterns across both SignalR and the autotable WS).");
            Assert.Equal(JsonValueKind.Number, prop.Value.ValueKind);
            var rank = prop.Value.GetInt32();
            Assert.True(rank >= 0,
                $"pattern-ordering value for '{prop.Name}' is {rank} but must be ≥ 0 " +
                "(ChangshaPatternOrdering.AlphabeticalFallbackOrder is the sentinel tail).");
        }

        Assert.True(entryCount > 0,
            "pattern-ordering endpoint returned an empty object — the static " +
            "ChangshaPatternOrdering.Order table is non-empty by construction, " +
            "so this indicates the Minimal-API handler regressed.");

        // Cross-check the response size matches the static table. If Bishop
        // adds a new entry, both sides advance together; if the wire-name
        // mapper drops one silently this assertion catches it.
        Assert.Equal(ChangshaPatternOrdering.Order.Count, entryCount);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Every WinPattern enum value has an ordering entry (blind-spot guard)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-4")]
    public async Task PatternOrdering_AllWinPatternEnumValues_HaveAnOrderingEntry()
    {
        // Blind-spot guard for future Bishop work: a new WinPattern enum
        // value added without a corresponding ordering entry would silently
        // sort to the AlphabeticalFallbackOrder=999 tail. That's correct
        // defensive behaviour at runtime (no crash), but it also masks the
        // gap — Hicks would render a freshly-detected pattern in an
        // arbitrary location and the team would only notice via player
        // bug-reports. This test reflects over the enum at runtime and
        // requires every defined value to appear in the wire response, so
        // a missed ordering entry fails RED at CI.
        //
        // The wire-name mapper (`WinPatternWireName` in Program.cs) is the
        // production source of camelCase keys; we replicate the same
        // mapping here so the test fails for the right reason (missing
        // ordering entry) rather than for a mapper miss (also a valid
        // bug, but covered by test #1's camelCase + count check).
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync("/api/changsha/pattern-ordering");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var map = JsonSerializer.Deserialize<Dictionary<string, int>>(body)
            ?? throw new InvalidOperationException(
                "pattern-ordering response failed to deserialize as Dictionary<string,int>.");

        var missing = new List<string>();
        foreach (var pattern in Enum.GetValues<WinPattern>())
        {
            var wireName = WireName(pattern);
            if (!map.ContainsKey(wireName))
            {
                missing.Add($"{pattern} (wire='{wireName}')");
            }
        }

        Assert.True(missing.Count == 0,
            "The following WinPattern enum values are missing from the " +
            "/api/changsha/pattern-ordering response — Bishop owes an entry " +
            "in ChangshaPatternOrdering.Order (and a wire-name case in " +
            "Program.cs's WinPatternWireName if the default lowercase doesn't fit): " +
            $"[{string.Join(", ", missing)}]. The runtime fallback of " +
            $"AlphabeticalFallbackOrder={ChangshaPatternOrdering.AlphabeticalFallbackOrder} " +
            "silently sinks unknown patterns to the tail, which is correct but masks gaps.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Canonical order sanity — HeavenlyHand outranks AllPungs (and a second pair)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Api"), Trait("Wave", "Phase-J-4")]
    public async Task PatternOrdering_HeavenlyHand_OutranksAllPungs()
    {
        // Phase J Wave 3 canonical order (per Bishop's ChangshaPatternOrdering
        // docstring): Big Wins first (1=HeavenlyHand, 2=EarthlyHand, 3=Last
        // TileFromWall, 4=LastDiscardCatch, 5=KongReplacementWin, 8=Nine
        // Terminals), then bonus-structural (9=AllPungs, 11=SevenPairs),
        // then alphabetical-tail (100=FullFlush, 101=Standard). We pin two
        // representative pairs:
        //
        //   • HeavenlyHand (Big Win, slot 1) MUST be earlier than AllPungs
        //     (bonus-structural, slot 9). This is the canonical headline
        //     ordering — a Big Win always renders left of a baseline
        //     structural pattern in Hicks's chip strip.
        //
        //   • SevenPairs (slot 11) MUST be earlier than FullFlush (slot
        //     100, alphabetical tail). This pins the "bonus-structural
        //     comes before alphabetical-tail" tier boundary so a future
        //     refactor that flattens the two tiers (e.g., "sort everything
        //     alphabetically") still fails RED here.
        //
        // Asserting the actual integer values would be too strict — Bishop's
        // doc explicitly reserves slot numbers for future patterns
        // (RobbedKong=6, NineGates=7, AllConcealed=10, SelfDraw=12,
        // SingleWait=13), so the absolute scale may shift. The relative
        // ordering between named pairs is the stable contract.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();

        using var response = await client.GetAsync("/api/changsha/pattern-ordering");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var map = JsonSerializer.Deserialize<Dictionary<string, int>>(body)
            ?? throw new InvalidOperationException(
                "pattern-ordering response failed to deserialize as Dictionary<string,int>.");

        Assert.True(map.TryGetValue("heavenlyHand", out var heavenlyHand),
            "'heavenlyHand' missing from pattern-ordering — Big Win headline " +
            "key is required by Hicks's result-modal chip strip.");
        Assert.True(map.TryGetValue("allPungs", out var allPungs),
            "'allPungs' missing from pattern-ordering — bonus-structural key " +
            "is required.");
        Assert.True(map.TryGetValue("sevenPairs", out var sevenPairs),
            "'sevenPairs' missing from pattern-ordering.");
        Assert.True(map.TryGetValue("fullFlush", out var fullFlush),
            "'fullFlush' missing from pattern-ordering.");

        Assert.True(heavenlyHand < allPungs,
            $"Canonical order regression: HeavenlyHand (rank={heavenlyHand}) must " +
            $"render before AllPungs (rank={allPungs}). Big Wins always precede " +
            "bonus-structural patterns per Bishop's Wave 3 ordering spec.");

        Assert.True(sevenPairs < fullFlush,
            $"Canonical tier-boundary regression: SevenPairs (rank={sevenPairs}, " +
            $"bonus-structural tier) must render before FullFlush (rank={fullFlush}, " +
            "alphabetical-tail tier). A flattening of tiers would defeat the " +
            "result-modal's visual hierarchy.");
    }

    // ────────────────────────────────────────────────────────────────────
    //  Wire-name mirror — must match Program.cs's WinPatternWireName.
    //  Defensively kept as a switch with the same shape so a Bishop-side
    //  rename of a wire string fails the parity check in test #2 above.
    // ────────────────────────────────────────────────────────────────────

    private static string WireName(WinPattern p) => p switch
    {
        WinPattern.Standard => "standard",
        WinPattern.SevenPairs => "sevenPairs",
        WinPattern.AllPungs => "allPungs",
        WinPattern.FullFlush => "fullFlush",
        WinPattern.NineTerminals => "nineTerminals",
        WinPattern.HeavenlyHand => "heavenlyHand",
        WinPattern.EarthlyHand => "earthlyHand",
        WinPattern.LastTileFromWall => "lastTileFromWall",
        WinPattern.LastDiscardCatch => "lastDiscardCatch",
        WinPattern.KongReplacementWin => "kongReplacementWin",
        _ => p.ToString().ToLowerInvariant()
    };
}
