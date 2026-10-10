using Mahjong.Autotable.Api.Auth;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W12.Bishop;

/// <summary>
/// Phase K Wave 12 — Bishop. Hard-asserted contract for the
/// JWT staged-rotation policy (<see cref="JwtStagedRotationPolicy"/>).
///
/// <list type="number">
///   <item><see cref="JwtStagedRotationPolicy.DefaultOverlapDays"/>
///         equals 30.</item>
///   <item><see cref="AuthOptions.RotationOverlapDays"/> default
///         is 30.</item>
///   <item><see cref="AuthOptions.RotationStartUtc"/> default
///         is null.</item>
///   <item>OverlapWindowEndsAtUtc is null when
///         RotationStartUtc is null.</item>
///   <item>OverlapWindowEndsAtUtc = RotationStartUtc +
///         OverlapDays.</item>
///   <item>IsWithinOverlapWindow returns true between start
///         and end.</item>
///   <item>IsWithinOverlapWindow returns false before start.</item>
///   <item>IsWithinOverlapWindow returns false after end.</item>
///   <item>RemainingOverlapDays = 0 when window closed.</item>
///   <item>RemainingOverlapDays counts down through the
///         window.</item>
/// </list>
/// </summary>
public sealed class JwtStagedRotationFacts
{
    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void DefaultOverlapDays_Is30()
    {
        Assert.Equal(30, JwtStagedRotationPolicy.DefaultOverlapDays);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void AuthOptions_DefaultsOverlapDaysTo30()
    {
        var opts = new AuthOptions();
        Assert.Equal(30, opts.RotationOverlapDays);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void AuthOptions_DefaultsRotationStartToNull()
    {
        var opts = new AuthOptions();
        Assert.Null(opts.RotationStartUtc);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void OverlapWindowEndsAtUtc_NullWhenRotationStartUnset()
    {
        var policy = new JwtStagedRotationPolicy(new AuthOptions());
        Assert.Null(policy.OverlapWindowEndsAtUtc);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void OverlapWindowEndsAtUtc_IsStartPlusDays()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var policy = new JwtStagedRotationPolicy(new AuthOptions
        {
            RotationStartUtc = start,
            RotationOverlapDays = 30,
        });
        Assert.Equal(start.AddDays(30), policy.OverlapWindowEndsAtUtc);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void IsWithinOverlapWindow_TrueBetweenStartAndEnd()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var policy = new JwtStagedRotationPolicy(new AuthOptions
        {
            RotationStartUtc = start,
            RotationOverlapDays = 30,
        });
        Assert.True(policy.IsWithinOverlapWindow(start.AddDays(15)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void IsWithinOverlapWindow_FalseBeforeStart()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var policy = new JwtStagedRotationPolicy(new AuthOptions
        {
            RotationStartUtc = start,
            RotationOverlapDays = 30,
        });
        Assert.False(policy.IsWithinOverlapWindow(start.AddDays(-1)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void IsWithinOverlapWindow_FalseAfterEnd()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var policy = new JwtStagedRotationPolicy(new AuthOptions
        {
            RotationStartUtc = start,
            RotationOverlapDays = 30,
        });
        Assert.False(policy.IsWithinOverlapWindow(start.AddDays(40)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void RemainingOverlapDays_ZeroWhenWindowClosed()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var policy = new JwtStagedRotationPolicy(new AuthOptions
        {
            RotationStartUtc = start,
            RotationOverlapDays = 30,
        });
        Assert.Equal(0, policy.RemainingOverlapDays(start.AddDays(100)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void RemainingOverlapDays_CountsDownThroughWindow()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var policy = new JwtStagedRotationPolicy(new AuthOptions
        {
            RotationStartUtc = start,
            RotationOverlapDays = 30,
        });
        var early = policy.RemainingOverlapDays(start.AddDays(5));
        var late = policy.RemainingOverlapDays(start.AddDays(20));
        Assert.True(early > late);
    }
}
