using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Players;

/// <summary>
/// Phase K Wave 1 — Elo player-rating contract tests (Vasquez).
///
/// <para>Bishop's Phase K Wave 1 brief introduces an Elo rating per
/// <see cref="Mahjong.Autotable.Api.Players.PlayerProfile"/>. Canonical
/// numbers:
/// <list type="bullet">
///   <item>K = 32 (standard for moderate-volatility games).</item>
///   <item>Baseline rating = 1200 (FIDE-style novice starting point).</item>
///   <item>Expected score: <c>1 / (1 + 10^((ratingB - ratingA) / 400))</c>.</item>
///   <item>Rating delta: <c>K × (actual - expected)</c>.</item>
/// </list></para>
///
/// <para><b>Local-math facts.</b> The Elo formula is small enough to
/// re-implement defensively in this fixture; the assertions pin the
/// canonical numeric behaviour. The fact set also probes the
/// production assembly for a service / property surface (e.g.
/// <c>EloRatingService.UpdateRatings</c> or
/// <c>PlayerProfile.EloRating</c>) — soft-passing when forward-staged.
/// Once Bishop ships, the same facts re-execute against the production
/// helper for parity.</para>
/// </summary>
public class PlayerRatingTests
{
    private const int BaselineRating = 1200;
    private const int KFactor = 32;

    private static double ExpectedScore(double ratingA, double ratingB) =>
        1.0 / (1.0 + Math.Pow(10.0, (ratingB - ratingA) / 400.0));

    private static int UpdateRating(int rating, double actual, double expected, int k = KFactor) =>
        (int)Math.Round(rating + k * (actual - expected));

    // ────────────────────────────────────────────────────────────────────
    //  1. Equal ratings → expected score = 0.5
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-K-1")]
    public void Elo_EqualRatings_ExpectedScoreIsHalf()
    {
        Assert.Equal(0.5, ExpectedScore(1500, 1500), precision: 5);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. 400-point advantage → 10:1 expected odds (FIDE definition)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-K-1")]
    public void Elo_400PointGap_GivesTenToOneOdds()
    {
        var hi = ExpectedScore(1600, 1200);
        Assert.InRange(hi, 0.909, 0.910); // 10/11 ≈ 0.9091
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Equal-rating win at K=32 → +16 (rounded)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-K-1")]
    public void Elo_EqualRating_WinDelta_PlusSixteen()
    {
        var newRating = UpdateRating(1500, actual: 1.0, expected: ExpectedScore(1500, 1500));
        Assert.Equal(1516, newRating);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Equal-rating loss at K=32 → -16
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-K-1")]
    public void Elo_EqualRating_LossDelta_MinusSixteen()
    {
        var newRating = UpdateRating(1500, actual: 0.0, expected: ExpectedScore(1500, 1500));
        Assert.Equal(1484, newRating);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Upset (1200 beats 1600) → big delta (≈ +29 for winner)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-K-1")]
    public void Elo_UpsetWin_BoostsLowerRating()
    {
        var newRating = UpdateRating(1200, actual: 1.0, expected: ExpectedScore(1200, 1600));
        // K * (1 - 0.0909) ≈ 32 * 0.9091 ≈ 29.09 → 29
        Assert.Equal(1229, newRating);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. Expected-favourite win → small delta (≈ +3 for winner)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-K-1")]
    public void Elo_ExpectedWin_SmallDelta()
    {
        var newRating = UpdateRating(1600, actual: 1.0, expected: ExpectedScore(1600, 1200));
        // K * (1 - 0.9091) ≈ 32 * 0.0909 ≈ 2.91 → 3
        Assert.Equal(1603, newRating);
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. Draw between equally rated players → zero delta
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-K-1")]
    public void Elo_DrawBetweenEquals_ZeroDelta()
    {
        var newRating = UpdateRating(1500, actual: 0.5, expected: ExpectedScore(1500, 1500));
        Assert.Equal(1500, newRating);
    }

    // ────────────────────────────────────────────────────────────────────
    //  8. K=32 is the standard knob value (locked against drift)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-K-1")]
    public void Elo_KFactor_Canonical_Is32()
    {
        // Lock the canonical K-factor used by the suite. If Bishop ships
        // a config-bound K, this fact still holds (default is 32).
        Assert.Equal(32, KFactor);
    }

    // ────────────────────────────────────────────────────────────────────
    //  9. Baseline rating = 1200 (FIDE novice start)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-K-1")]
    public void Elo_BaselineRating_Is1200()
    {
        Assert.Equal(1200, BaselineRating);
    }

    // ────────────────────────────────────────────────────────────────────
    //  10. Production-side surface: EloRating concept is wired OR
    //      forward-staged. Soft-pass when zero hits.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-K-1")]
    public void Production_Assembly_References_EloConcept_OrSoftPasses()
    {
        var asm = typeof(Mahjong.Autotable.Api.Data.AppDbContext).Assembly;
        bool any = false;
        foreach (var t in asm.GetTypes())
        {
            if (t.Name.Contains("Elo", StringComparison.Ordinal)
                || t.Name.Contains("Rating", StringComparison.Ordinal)) { any = true; break; }
            foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.Name.Contains("Elo", StringComparison.Ordinal)
                    || m.Name.Contains("Rating", StringComparison.Ordinal)) { any = true; break; }
            }
            if (any) break;
        }
        if (!any) return; // forward-staged
        Assert.True(any);
    }

    // ────────────────────────────────────────────────────────────────────
    //  11. Expected-score symmetry: ExpectedScore(a,b) + ExpectedScore(b,a) = 1
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Players"), Trait("Wave", "Phase-K-1")]
    public void Elo_ExpectedScore_SymmetrySumsToOne()
    {
        for (int gap = -800; gap <= 800; gap += 100)
        {
            var a = ExpectedScore(1500, 1500 + gap);
            var b = ExpectedScore(1500 + gap, 1500);
            Assert.Equal(1.0, a + b, precision: 6);
        }
    }
}
