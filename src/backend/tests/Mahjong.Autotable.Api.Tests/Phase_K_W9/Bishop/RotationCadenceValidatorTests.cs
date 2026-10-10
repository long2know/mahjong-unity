using Mahjong.Autotable.Api.Auth;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W9.Bishop;

/// <summary>
/// Phase K Wave 9 — Bishop. Hard-asserted facts for
/// <see cref="RotationCadenceValidator"/>. The validator enforces a
/// boot-time invariant relating the JWKS cache TTL to the JWT
/// rotation grace period — misalignment causes mid-rotation
/// validation failures that we want to catch at start-up rather
/// than triage in production.
///
/// <list type="number">
///   <item>Default cadence (60s TTL, 600s grace) passes validation.</item>
///   <item>A grace of 0 is treated as "rotation not configured" and
///         exits silently.</item>
///   <item>A TTL that exceeds the half-grace ceiling throws
///         <see cref="InvalidOperationException"/>.</item>
///   <item>The thrown exception message names both the TTL and the
///         grace + cites the runbook doc reference.</item>
///   <item>A TTL exactly at the half-grace ceiling is accepted
///         (boundary check, not strict less-than).</item>
///   <item>Helper <see cref="RotationCadenceValidator.ComputeCeilingSeconds"/>
///         returns half the grace as a double.</item>
/// </list>
/// </summary>
public sealed class RotationCadenceValidatorTests
{
    private static AuthOptions OptionsFor(int graceSeconds) => new()
    {
        JwtSigningKey = "k0",
        JwtRsaKeys = new[] { "kid1:rsa-fake" },
        RotationGracePeriodSeconds = graceSeconds,
    };

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void Validate_DefaultCadence_Passes()
    {
        var validator = new RotationCadenceValidator(OptionsFor(600), TimeSpan.FromSeconds(60));
        validator.Validate();
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void Validate_GraceOfZero_ExitsSilently()
    {
        var validator = new RotationCadenceValidator(OptionsFor(0), TimeSpan.FromSeconds(5_000));
        validator.Validate();
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void Validate_NegativeGrace_ExitsSilently()
    {
        var validator = new RotationCadenceValidator(OptionsFor(-1), TimeSpan.FromSeconds(5_000));
        validator.Validate();
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void Validate_TtlExceedsHalfGrace_Throws()
    {
        var validator = new RotationCadenceValidator(OptionsFor(600), TimeSpan.FromSeconds(400));
        Assert.Throws<InvalidOperationException>(() => validator.Validate());
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void Validate_ThrownMessage_NamesTtlAndGraceAndDocPointer()
    {
        var validator = new RotationCadenceValidator(OptionsFor(120), TimeSpan.FromSeconds(120));
        var ex = Assert.Throws<InvalidOperationException>(() => validator.Validate());
        Assert.Contains("120", ex.Message);
        Assert.Contains("docs/jwt-rotation.md", ex.Message);
        Assert.Contains(RotationCadenceValidator.DocReference, ex.Message);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void Validate_TtlAtCeiling_Passes()
    {
        // Grace=600, ceiling=300; TTL=300 should be accepted.
        var validator = new RotationCadenceValidator(OptionsFor(600), TimeSpan.FromSeconds(300));
        validator.Validate();
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void Validate_TtlOneSecondAboveCeiling_Throws()
    {
        var validator = new RotationCadenceValidator(OptionsFor(600), TimeSpan.FromSeconds(301));
        Assert.Throws<InvalidOperationException>(() => validator.Validate());
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void ComputeCeilingSeconds_IsHalfGrace()
    {
        Assert.Equal(300.0, RotationCadenceValidator.ComputeCeilingSeconds(600));
        Assert.Equal(0.5, RotationCadenceValidator.ComputeCeilingSeconds(1));
        Assert.Equal(0.0, RotationCadenceValidator.ComputeCeilingSeconds(0));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void MaxTtlToGraceRatio_IsCanonicalHalf()
    {
        Assert.Equal(0.5, RotationCadenceValidator.MaxTtlToGraceRatio);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RotationCadenceValidator((AuthOptions?)null!, TimeSpan.FromSeconds(60)));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void AuthOptions_DefaultRotationGrace_Is600s()
    {
        var fresh = new AuthOptions();
        Assert.Equal(600, fresh.RotationGracePeriodSeconds);
    }
}
