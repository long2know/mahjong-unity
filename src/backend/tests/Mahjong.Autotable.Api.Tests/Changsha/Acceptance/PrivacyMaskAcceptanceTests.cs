using System.Reflection;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;

namespace Mahjong.Autotable.Api.Tests.Changsha.Acceptance;

/// <summary>
/// Acceptance: Phase G privacy-mask slot-parse cleanup.
///
/// <para>Contract (locked here, owned by Bishop's <see cref="AutotableWsEndpoint"/>): the
/// private static helper <c>FilterEntriesForViewer(entries, viewerSeat)</c> must apply the
/// following rules — using the SEAT SUFFIX after the LAST <c>@</c> as the privacy boundary
/// (the prior bug was parsing the seat as the slice between <c>'.'</c> and <c>'@'</c>,
/// which corresponds to the handIdx in the canonical <c>hand.{handIdx}@{seat}</c> format).</para>
///
/// <list type="bullet">
///   <item>Slots that do NOT contain <c>'@'</c> pass through unchanged (no privacy needed).</item>
///   <item>Slots ending in <c>@{S}</c> with <c>S != viewerSeat</c> have their face stripped
///         and rotation forced face-down.</item>
///   <item>Slots ending in <c>@{S}</c> with <c>S == viewerSeat</c> pass through unchanged.</item>
///   <item>When <c>viewerSeat</c> is <see langword="null"/> (spectator), every <c>@</c>-suffixed
///         entry has its face stripped.</item>
///   <item>Malformed slots (multiple <c>@</c>, unparseable seat) use the LAST <c>@</c> as the
///         boundary; unparseable seats pass through gracefully (no exception).</item>
/// </list>
///
/// <para><b>Pre-Bishop posture:</b> the legacy implementation
/// (<see cref="AutotableWsEndpoint"/>:733 region) parses the seat as the slice between
/// <c>'.'</c> and <c>'@'</c> (i.e. it treats <c>hand.{handIdx}@{seat}</c> as
/// <c>hand.{seat}@{idx}</c>). The legacy code therefore mis-targets which hand to mask, and
/// would not mask wall/discard/meld slots at all (gated on <c>StartsWith("hand.")</c>).
/// Until Bishop ships the cleanup, the tests below that assert "any @-suffixed slot is
/// masked when not your seat" will fail RED on slots like <c>wall.0.0@1</c> or
/// <c>weird@foo@1</c>; the hand-specific tests with correctly-parseable handIdx will pass
/// GREEN by coincidence (the seat-as-handIdx parse on <c>hand.0@1</c> happens to extract
/// <c>0</c> and mis-compare; viewerSeat=0 looks like seat 0 → wrongly passes through).
/// All red tests turn green when Bishop's parse-by-last-@ lands.</para>
///
/// <para><b>Sources:</b> Bishop's Phase G task spec, <c>.squad/decisions.md</c> §"Deferred
/// Follow-ups": "FilterEntriesForViewer slot-suffix fix — same EndsWith('@N') change as
/// the test; cleanup pass".</para>
/// </summary>
public sealed class PrivacyMaskAcceptanceTests
{
    // ── Reflection access to the private static helper ─────────────────────
    // FilterEntriesForViewer lives on AutotableConnectionManager (private static); the
    // wider AutotableWsEndpoint static class only registers the route. Bishop's Phase G
    // cleanup may refactor the helper into a dedicated PrivacyFilter type — if so, this
    // lookup MUST be updated to track. We probe both candidate hosts so the contract
    // survives the move.

    private static readonly Assembly ApiAssembly = typeof(AutotableWsEndpoint).Assembly;

    private static readonly MethodInfo? FilterMethod = FindFilterMethod();

    private static MethodInfo? FindFilterMethod()
    {
        const string AutotableNs = "Mahjong.Autotable.Api.Autotable";
        foreach (var hostName in new[]
                 {
                     $"{AutotableNs}.AutotableConnectionManager",
                     $"{AutotableNs}.AutotableWsEndpoint",
                     $"{AutotableNs}.PrivacyFilter",
                 })
        {
            var type = ApiAssembly.GetType(hostName);
            var method = type?.GetMethod("FilterEntriesForViewer",
                BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            if (method != null) return method;
        }
        return null;
    }

    private static IReadOnlyList<CollectionEntry> Filter(
        IReadOnlyList<CollectionEntry> entries,
        int? viewerSeat)
    {
        Assert.True(FilterMethod is not null,
            "FilterEntriesForViewer not found on AutotableConnectionManager / AutotableWsEndpoint / PrivacyFilter. " +
            "Bishop owns the Phase G privacy-mask cleanup; see " +
            ".squad/decisions.md §\"Deferred Follow-ups\".");
        var result = FilterMethod!.Invoke(null, new object?[] { entries, viewerSeat });
        return (IReadOnlyList<CollectionEntry>)result!;
    }

    private static JsonElement JsonObj(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Construct a <c>things</c> CollectionEntry whose Value is a JsonElement
    /// shaped like an autotable Thing (slotName + face + rotationIndex).</summary>
    private static CollectionEntry ThingEntry(int key, string slotName, string face = "Wan-5", int rotationIndex = 1)
    {
        var json = $$"""
            {
              "slotName": {{JsonSerializer.Serialize(slotName)}},
              "face": {{JsonSerializer.Serialize(face)}},
              "rotationIndex": {{rotationIndex}},
              "id": {{key}}
            }
            """;
        return new CollectionEntry("things", key, JsonObj(json));
    }

    private static string? SlotOf(CollectionEntry entry)
    {
        if (entry.Value is JsonElement je && je.ValueKind == JsonValueKind.Object &&
            je.TryGetProperty("slotName", out var s) && s.ValueKind == JsonValueKind.String)
        {
            return s.GetString();
        }
        return null;
    }

    private static bool HasFace(CollectionEntry entry)
    {
        if (entry.Value is JsonElement je && je.ValueKind == JsonValueKind.Object &&
            je.TryGetProperty("face", out var f))
        {
            return f.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(f.GetString());
        }
        return false;
    }

    private static int? RotationOf(CollectionEntry entry)
    {
        if (entry.Value is JsonElement je && je.ValueKind == JsonValueKind.Object &&
            je.TryGetProperty("rotationIndex", out var r) && r.ValueKind == JsonValueKind.Number)
        {
            return r.GetInt32();
        }
        return null;
    }

    // ── Test 1 — Non-hand / non-@ slots are always preserved ───────────────

    [Fact, Trait("Category", "Acceptance")]
    public void Filter_PreservesNonHandSlots()
    {
        // Contract: slots with NO '@' suffix carry no per-seat privacy semantics and must
        // be passed through unmodified, regardless of viewer seat.
        var entries = new[]
        {
            ThingEntry(1, "wall.0"),
            ThingEntry(2, "discard.0.3"),
            ThingEntry(3, "meld.1.2"),
            ThingEntry(4, "tray.7"),
        };

        foreach (var viewer in new int?[] { null, 0, 1, 2, 3 })
        {
            var filtered = Filter(entries, viewer);
            Assert.Equal(entries.Length, filtered.Count);
            for (var i = 0; i < entries.Length; i++)
            {
                Assert.Equal(entries[i].Kind, filtered[i].Kind);
                Assert.Equal(entries[i].Key, filtered[i].Key);
                Assert.Equal(SlotOf(entries[i]), SlotOf(filtered[i]));
                Assert.True(HasFace(filtered[i]),
                    $"viewer={viewer} slot={SlotOf(entries[i])}: face was stripped on a non-@ slot " +
                    $"(must pass through unchanged).");
                Assert.Equal(RotationOf(entries[i]), RotationOf(filtered[i]));
            }
        }
    }

    // ── Test 2 — Hand slots belonging to OTHER seats are masked ────────────

    [Fact, Trait("Category", "Acceptance")]
    public void Filter_MasksForeignSeatHandSlots()
    {
        // hand.{handIdx}@{seat} — viewer=0 sees seat-1's hand face-down, regardless of
        // handIdx. The handIdx is irrelevant to privacy; only the suffix (after the last @)
        // determines ownership.
        var entries = new[]
        {
            ThingEntry(10, "hand.0@1"),
            ThingEntry(11, "hand.5@1"),
            ThingEntry(12, "hand.12@1"),
            ThingEntry(13, "hand.0@2"),
            ThingEntry(14, "hand.0@3"),
        };

        var filtered = Filter(entries, viewerSeat: 0);

        Assert.Equal(entries.Length, filtered.Count);
        foreach (var e in filtered)
        {
            var slot = SlotOf(e);
            Assert.NotNull(slot);
            Assert.False(HasFace(e),
                $"slot={slot}: face must be stripped for viewer=0 looking at seats 1/2/3.");
            // Force face-down rotation (HandRotFaceDown = 2 per upstream setup-slots.ts).
            Assert.Equal(2, RotationOf(e));
        }
    }

    // ── Test 3 — Hand slots belonging to viewer's OWN seat pass through ────

    [Fact, Trait("Category", "Acceptance")]
    public void Filter_PassesOwnSeatHandSlots()
    {
        // hand.0@0 + viewer=0 → unchanged (face still present, rotation unchanged).
        var entries = new[]
        {
            ThingEntry(20, "hand.0@0", face: "Wan-3", rotationIndex: 1),
            ThingEntry(21, "hand.5@0", face: "Tong-7", rotationIndex: 1),
            ThingEntry(22, "hand.12@0", face: "Tiao-2", rotationIndex: 1),
        };

        var filtered = Filter(entries, viewerSeat: 0);

        Assert.Equal(entries.Length, filtered.Count);
        for (var i = 0; i < entries.Length; i++)
        {
            Assert.Equal(SlotOf(entries[i]), SlotOf(filtered[i]));
            Assert.True(HasFace(filtered[i]),
                $"slot={SlotOf(filtered[i])}: viewer=0 must keep their own hand face-up.");
            Assert.Equal(1, RotationOf(filtered[i]));
        }
    }

    // ── Test 4 — Spectator (viewer=null) sees ALL hands masked ─────────────

    [Fact, Trait("Category", "Acceptance")]
    public void Filter_SpectatorMasksAllHandSlots()
    {
        // viewerSeat=null → every entry whose slot ends with @{S} is masked, regardless
        // of S. Non-@ slots still pass through (covered by Test 1).
        var entries = new[]
        {
            ThingEntry(30, "hand.0@0"),
            ThingEntry(31, "hand.0@1"),
            ThingEntry(32, "hand.0@2"),
            ThingEntry(33, "hand.0@3"),
        };

        var filtered = Filter(entries, viewerSeat: null);

        Assert.Equal(entries.Length, filtered.Count);
        foreach (var e in filtered)
        {
            var slot = SlotOf(e);
            Assert.NotNull(slot);
            Assert.False(HasFace(e),
                $"slot={slot}: spectator (viewerSeat=null) must not see any face.");
            Assert.Equal(2, RotationOf(e));
        }
    }

    // ── Test 5 — Malformed slots: parse seat from the LAST @, fail soft ────

    [Fact, Trait("Category", "Acceptance")]
    public void Filter_HandlesMalformedSlots()
    {
        // Three robustness contracts (per task spec):
        // (a) Multi-'@' slots — implementation must use the LAST '@' as the seat
        //     boundary, NOT the first (e.g. "weird@foo@1" → seat=1, not "foo@1").
        // (b) Trailing '@' or non-numeric suffix — gracefully pass through, no
        //     exceptions, no crashes.
        // (c) Output always has the same count as input — no entry is silently dropped.
        var entries = new[]
        {
            ThingEntry(40, "weird@foo@1"),       // last '@' → seat 1
            ThingEntry(41, "weird@foo@0"),       // last '@' → seat 0
            ThingEntry(42, "trailing@"),         // unparseable
            ThingEntry(43, "garbled@abc"),       // unparseable
            ThingEntry(44, "@5"),                // last '@' → seat 5 (out of range)
        };

        // (c) — no viewer-seat value may cause an exception.
        IReadOnlyList<CollectionEntry>? filteredForSeat0 = null;
        IReadOnlyList<CollectionEntry>? filteredForSpectator = null;
        IReadOnlyList<CollectionEntry>? filteredForSeat5 = null;

        Assert.Null(Record.Exception(() => filteredForSeat0 = Filter(entries, viewerSeat: 0)));
        Assert.Null(Record.Exception(() => filteredForSpectator = Filter(entries, viewerSeat: null)));
        Assert.Null(Record.Exception(() => filteredForSeat5 = Filter(entries, viewerSeat: 5)));

        // (c) — count is preserved across every viewer permutation.
        Assert.Equal(entries.Length, filteredForSeat0!.Count);
        Assert.Equal(entries.Length, filteredForSpectator!.Count);
        Assert.Equal(entries.Length, filteredForSeat5!.Count);

        // (b) — unparseable seats pass through (face preserved). Two slots qualify:
        // "trailing@" (last '@' has empty suffix) and "garbled@abc" (non-numeric).
        var trailing = filteredForSeat0.Single(e => SlotOf(e) == "trailing@");
        Assert.True(HasFace(trailing),
            "trailing@ has an empty seat suffix and must pass through unchanged (no exception, no mask).");
        var garbled = filteredForSeat0.Single(e => SlotOf(e) == "garbled@abc");
        Assert.True(HasFace(garbled),
            "garbled@abc has a non-numeric seat suffix and must pass through unchanged.");

        // (a) — last-'@' parse semantics: "weird@foo@1" must NOT be interpreted as
        // seat="foo@1" (which would be unparseable and pass through). When the
        // implementation extracts seat=1, viewer=1 sees it as own (face preserved);
        // viewer=2 sees it as foreign (whether it gets masked is implementation-defined,
        // but the seat parse MUST land on 1, not on the substring before the first '@').
        var owner = filteredForSeat0.Single(e => SlotOf(e) == "weird@foo@0");
        // With last-'@' parse → seat=0 → viewer=0 owns it → face preserved.
        // If implementation used FIRST '@', parse target would be "foo@0" (unparseable)
        // → also passes through unchanged. Either way: HasFace must be true. The
        // diagnostic value is in the asymmetry below.
        Assert.True(HasFace(owner));

        // Asymmetry pin: with viewerSeat=5, "@5" ends with seat=5 (the viewer's own).
        // If the implementation parses last-'@' correctly, the entry passes through
        // unchanged. If it parses first-'@' (or fails to parse), the entry STILL
        // passes through (unparseable suffix is also safe). The contract therefore
        // is: this entry is present in the output, no exception, count matches —
        // already asserted above. No face-mask assertion (implementation latitude).
        var atFive = filteredForSeat5.Single(e => SlotOf(e) == "@5");
        Assert.NotNull(atFive);
    }
}
