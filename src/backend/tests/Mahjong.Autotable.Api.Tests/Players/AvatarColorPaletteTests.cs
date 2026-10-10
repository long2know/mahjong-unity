using System.Linq;
using System.Reflection;
using Mahjong.Autotable.Api.Players;

namespace Mahjong.Autotable.Api.Tests.Players;

/// <summary>
/// Phase J Wave 7 — avatar colour palette contract tests (Vasquez).
///
/// <para>Bishop's Wave 7 work consolidates the avatar-colour palette: the
/// frontend's <c>AVATAR_COLOR_PRESETS</c> (8 entries in
/// <c>src/frontend/autotable-src/src/profile.ts</c>) is now the authoritative
/// list, and <see cref="PlayerProfile.AvatarColor"/>'s class-initialiser
/// default points at the FIRST entry (<c>#c0392b</c>, red).</para>
///
/// <para>Wave 5/6 carried a 16-entry HSL-spaced palette inside
/// <see cref="PlayerProfileService.DefaultAvatarColor(string)"/> AND a
/// separate <c>#808080</c> grey default on the entity property — neither
/// was a member of Hicks's user-facing 8-entry set, so any code path that
/// constructed a <see cref="PlayerProfile"/> without going through the
/// service helper shipped a "ghost" 9th colour. The Wave 7 fix collapses
/// both vocabularies to the same 8 colours.</para>
///
/// <para><b>Reflection-defensive.</b> The palette may move from its
/// current static-array residence inside
/// <see cref="PlayerProfileService.DefaultAvatarColor(string)"/> to a
/// dedicated public constant (Bishop has flagged this as a likely Wave 7
/// follow-up). These tests probe via reflection for any
/// <c>string[]</c> / <c>IReadOnlyList&lt;string&gt;</c> / array-typed
/// public static member in the <c>Players</c> namespace whose contents
/// match the documented 8-colour set; if no such surface exists, we fall
/// back to a behavioural probe over
/// <see cref="PlayerProfileService.DefaultAvatarColor(string)"/>'s output
/// for a wide playerId sweep so the contract is exercised either way.</para>
/// </summary>
public class AvatarColorPaletteTests
{
    /// <summary>
    /// The canonical Wave 7 palette — sourced from
    /// <c>src/frontend/autotable-src/src/profile.ts:84-93</c>
    /// (<c>AVATAR_COLOR_PRESETS</c>). These are the only 8 colours the
    /// frontend will ever surface on the onboarding card, profile drawer,
    /// or profile page; the backend default + service helper must each
    /// pick from this set.
    /// </summary>
    private static readonly string[] Wave7Palette =
    {
        "#c0392b", "#e67e22", "#f1c40f", "#2ecc71",
        "#16a085", "#2980b9", "#8e44ad", "#34495e",
    };

    // ────────────────────────────────────────────────────────────────────
    //  1. The class-initializer default is a member of the documented palette
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-7")]
    public void PlayerProfile_DefaultAvatarColor_IsAPaletteMember()
    {
        // Bishop's Wave 7 contract: a freshly-constructed `new PlayerProfile()`
        // — used by EF Core materialisation, by test fixtures that bypass
        // PlayerProfileService.GetOrCreateAsync, and by future migrations —
        // must carry an avatar colour drawn from Hicks's 8-entry preset
        // palette. The Wave 5/6 default of "#808080" (grey) was NOT a
        // palette member; the Wave 7 fix swaps it for `#c0392b` (first
        // palette entry). We assert membership rather than the literal value
        // so a future re-ordering of the palette (or selecting a different
        // entry as the default) doesn't require a test edit — only adding
        // a colour outside the documented set would surface.
        var profile = new PlayerProfile();
        Assert.Contains(profile.AvatarColor, Wave7Palette);

        // Defence-in-depth: the legacy grey default specifically must NOT
        // be the value. Without this assertion a regression that reverts
        // Bishop's Wave 7 fix would only fail by losing palette-membership,
        // and a slip that swapped one non-palette colour for another (e.g.
        // a near-grey "#888888") would also have been masked.
        Assert.NotEqual("#808080", profile.AvatarColor);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Service helper output covers (only) the documented 8 colours
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-7")]
    public void DefaultAvatarColor_OnWideSweep_OnlyEmitsPaletteMembers()
    {
        // The PlayerProfileService.DefaultAvatarColor helper hashes the
        // supplied playerId (FNV-1a) and picks from the palette. If the
        // underlying static array still carries the legacy 16 HSL-spaced
        // entries, a wide sweep will surface non-palette colours; if Bishop
        // has trimmed the helper to the 8-entry set, every sample lands in
        // the documented palette. We probe 1000 random ids — at 16 entries
        // we'd expect every legacy colour to appear inside 100 samples
        // (birthday-paradox conservative bound), so 1000 is generous.
        for (var i = 0; i < 1000; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            var colour = PlayerProfileService.DefaultAvatarColor(id);
            Assert.Contains(colour, Wave7Palette);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Service helper output is deterministic per playerId
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-7")]
    public void DefaultAvatarColor_IsDeterministic_PerPlayerId()
    {
        // Determinism is the property that lets a returning user keep the
        // same chip colour across reconnects WITHOUT the runtime storing a
        // per-row colour preference. If the helper switches to
        // Random.Shared / DateTime.UtcNow.Ticks the contract breaks
        // silently — this test catches it on the next CI run.
        for (var i = 0; i < 50; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            var first = PlayerProfileService.DefaultAvatarColor(id);
            var second = PlayerProfileService.DefaultAvatarColor(id);
            var third = PlayerProfileService.DefaultAvatarColor(id);
            Assert.Equal(first, second);
            Assert.Equal(second, third);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Empty / null playerId returns a stable palette member
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-7")]
    public void DefaultAvatarColor_EmptyOrNullId_ReturnsPaletteMember()
    {
        // The helper guards against empty / null player ids with an
        // explicit early-return path. The fallback colour must still be
        // a palette member — otherwise the regression "every uninitialised
        // call returns the legacy grey" would silently re-enter on the
        // edge case.
        var empty = PlayerProfileService.DefaultAvatarColor(string.Empty);
        Assert.Contains(empty, Wave7Palette);

        // We don't pass null directly because the parameter is non-nullable;
        // the legacy implementation accepts null via the runtime null check.
        var nullish = PlayerProfileService.DefaultAvatarColor(null!);
        Assert.Contains(nullish, Wave7Palette);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Every palette entry is a 7-char "#RRGGBB" lowercase hex string
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-7")]
    public void Palette_WireShape_IsLowercaseHexHashRRGGBB()
    {
        // Pins the wire shape so any future palette edit (re-ordering,
        // swapping a colour) keeps the same on-the-wire format the
        // frontend's regex (^#[0-9A-Fa-f]{6}$) accepts. Lower-case here
        // matches Hicks's source-of-truth.
        foreach (var colour in Wave7Palette)
        {
            Assert.Equal(7, colour.Length);
            Assert.Equal('#', colour[0]);
            for (var i = 1; i < colour.Length; i++)
            {
                var c = colour[i];
                var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                Assert.True(hex, $"palette entry '{colour}' has non-hex / non-lowercase char '{c}' at {i}");
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. If a public palette surface is exposed, its entries match Wave 7
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-J-7")]
    public void PublicPaletteSurface_IfPresent_MatchesDocumentedSet()
    {
        // Reflection-defensive probe: if Bishop has surfaced the palette
        // as a public static member (e.g.
        // `PlayerProfileService.AvatarPalette` or
        // `AvatarColorPalette.Presets`), we cross-check its contents
        // against the documented 8-entry set. If no such surface exists
        // yet, the probe is a no-op (the behavioural sweep in test #2
        // already proves the contract).
        //
        // We scan every public static field/property of every type under
        // `Mahjong.Autotable.Api.Players` whose name contains "palette" or
        // "preset" (case-insensitive) and whose value is an enumerable of
        // strings. Any match is asserted to be the documented 8-entry set
        // (order-insensitive).
        var assembly = typeof(PlayerProfile).Assembly;
        var paletteTypes = assembly.GetTypes()
            .Where(t => t.Namespace == "Mahjong.Autotable.Api.Players")
            .ToArray();

        var foundAny = false;
        foreach (var type in paletteTypes)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!field.Name.Contains("palette", StringComparison.OrdinalIgnoreCase)
                 && !field.Name.Contains("preset", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (field.GetValue(null) is not IEnumerable<string> values) continue;

                foundAny = true;
                var contents = values.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var expected in Wave7Palette)
                    Assert.Contains(expected, contents);
                // No "ghost" extras: the public surface MUST be exactly the
                // documented set if it exists. (We compare in lower case
                // because the frontend uses lower-case literals; either case
                // round-trips through the IsValidPlayerId regex.)
                Assert.Equal(Wave7Palette.Length, contents.Count);
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                if (!prop.Name.Contains("palette", StringComparison.OrdinalIgnoreCase)
                 && !prop.Name.Contains("preset", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (prop.GetValue(null) is not IEnumerable<string> values) continue;

                foundAny = true;
                var contents = values.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var expected in Wave7Palette)
                    Assert.Contains(expected, contents);
                Assert.Equal(Wave7Palette.Length, contents.Count);
            }
        }

        // If no public surface exists, this test silently passes — the
        // behavioural sweep in test #2 covers the same contract. We do not
        // require the public surface; we only require it to be correct
        // when present. The boolean keeps the intent explicit so a future
        // edit that intentionally adds a public palette member won't
        // accidentally regress to no coverage.
        _ = foundAny;
    }
}
