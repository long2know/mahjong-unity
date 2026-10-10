using Mahjong.Autotable.Api.Auth;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W16.Bishop;

/// <summary>
/// Phase K Wave 16 — Bishop. Behaviour tests for the
/// <see cref="DateTimeOffset"/> overloads added to
/// <see cref="JwtStagedRotationPolicy"/>.
/// </summary>
public sealed class DateTimeOffsetWideningTests
{
    private static JwtStagedRotationPolicy MakePolicy(DateTime? start, int overlap = 30) =>
        new(new AuthOptions
        {
            RotationStartUtc = start,
            RotationOverlapDays = overlap,
        });

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void IsWithinOverlapWindow_DateTimeOffset_TrueInsideWindow()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var p = MakePolicy(start, overlap: 30);
        var probe = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        Assert.True(p.IsWithinOverlapWindow(probe));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void IsWithinOverlapWindow_DateTimeOffset_FalseBeforeWindow()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var p = MakePolicy(start, overlap: 30);
        var probe = new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero);
        Assert.False(p.IsWithinOverlapWindow(probe));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void IsWithinOverlapWindow_DateTimeOffset_FalseAfterWindow()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var p = MakePolicy(start, overlap: 30);
        var probe = new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero);
        Assert.False(p.IsWithinOverlapWindow(probe));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void IsWithinOverlapWindow_DateTimeOffsetWithNonUtcOffset_NormalisesCorrectly()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var p = MakePolicy(start, overlap: 30);
        // 2026-06-10 10:00 +08:00 == 2026-06-10 02:00 UTC → inside window
        var probe = new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.FromHours(8));
        Assert.True(p.IsWithinOverlapWindow(probe));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void IsWithinOverlapWindow_DateTimeOffset_NoRotationStart_ReturnsFalse()
    {
        var p = MakePolicy(start: null);
        Assert.False(p.IsWithinOverlapWindow(DateTimeOffset.UtcNow));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void RemainingOverlapDays_DateTimeOffset_MatchesDateTimeVersion()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var p = MakePolicy(start, overlap: 30);
        var now = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(p.RemainingOverlapDays(now.UtcDateTime), p.RemainingOverlapDays(now));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void RemainingOverlapDays_DateTimeOffset_ZeroWhenPastWindow()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var p = MakePolicy(start, overlap: 30);
        var now = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, p.RemainingOverlapDays(now));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void RotationStartUtcOffset_PreservesUtc()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var p = MakePolicy(start);
        var off = p.RotationStartUtcOffset;
        Assert.NotNull(off);
        Assert.Equal(TimeSpan.Zero, off!.Value.Offset);
        Assert.Equal(start, off.Value.UtcDateTime);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void RotationStartUtcOffset_NullWhenStartAbsent()
    {
        var p = MakePolicy(start: null);
        Assert.Null(p.RotationStartUtcOffset);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void OverlapWindowEndsAtOffset_NullWhenStartAbsent()
    {
        var p = MakePolicy(start: null);
        Assert.Null(p.OverlapWindowEndsAtOffset);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void OverlapWindowEndsAtOffset_MatchesDateTimeVersion()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var p = MakePolicy(start, overlap: 30);
        Assert.Equal(p.OverlapWindowEndsAtUtc, p.OverlapWindowEndsAtOffset?.UtcDateTime);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void DateTimeOverloads_StillWork_BackwardCompat()
    {
        // Ensures the W16 widening did NOT remove the W12-W15
        // DateTime members.
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var p = MakePolicy(start, overlap: 30);
        Assert.True(p.IsWithinOverlapWindow(start.AddDays(15)));
        Assert.Equal(15, p.RemainingOverlapDays(start.AddDays(15)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void DateTimeOffsetOverloads_RoundTripExact_ForUtc()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var p = MakePolicy(start, overlap: 30);
        var probe = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(p.IsWithinOverlapWindow(probe.UtcDateTime), p.IsWithinOverlapWindow(probe));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void DateTimeOffsetOverloads_RoundTripExact_ForNegativeOffset()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var p = MakePolicy(start, overlap: 30);
        var probe = new DateTimeOffset(2026, 6, 15, 5, 0, 0, TimeSpan.FromHours(-7));
        Assert.Equal(p.IsWithinOverlapWindow(probe.UtcDateTime), p.IsWithinOverlapWindow(probe));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-16"), Trait("Lane", "Bishop")]
    public void RemainingOverlapDays_DateTimeOffset_PositiveInsideWindow()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var p = MakePolicy(start, overlap: 30);
        var now = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
        Assert.True(p.RemainingOverlapDays(now) > 0);
    }
}
