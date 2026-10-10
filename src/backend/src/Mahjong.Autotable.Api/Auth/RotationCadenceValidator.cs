using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 9 — Bishop. Startup invariant validator that pins
/// the relationship between the JWKS cache TTL and the JWT
/// rotation grace period. Misaligned values risk a window of
/// validation failure mid-rotation:
///
/// <para>If <c>JwksCacheService.DefaultTtl &gt; RotationGracePeriod / 2</c>,
/// downstream verifiers cached against the old kid list will fail
/// validation during the rotation grace window. The W9 invariant is:
/// <c>JwksCacheTtlSeconds &lt;= RotationGracePeriodSeconds / 2</c>.
/// The factor-of-2 ratio is the canonical Nyquist-style margin —
/// downstream verifiers refresh at least twice during the grace
/// window, so even a worst-case stale cache catches the new kid
/// before the old keys are evicted.</para>
///
/// <para>The validator runs as a hosted service that performs its
/// check at startup. On failure it throws
/// <see cref="InvalidOperationException"/> with an operator-friendly
/// message pointing at the docs section so the host aborts the
/// boot — better an explicit failure than a silent
/// production-incident class.</para>
///
/// <para>See <c>docs/jwt-rotation.md §11 "TTL discipline"</c> for the
/// operator runbook.</para>
/// </summary>
public interface IRotationCadenceValidator
{
    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the
    /// configured TTL / grace ratio violates the invariant.
    /// Returns silently when the configuration is valid.
    /// </summary>
    void Validate();
}

/// <summary>
/// Phase K Wave 9 — Bishop. Concrete implementation of
/// <see cref="IRotationCadenceValidator"/>. Pulls the active TTL
/// from <see cref="JwksCacheService.DefaultTtl"/> and the grace
/// window from <see cref="AuthOptions.RotationGracePeriodSeconds"/>.
/// </summary>
public sealed class RotationCadenceValidator : IRotationCadenceValidator
{
    /// <summary>Operator-facing doc reference embedded in the
    /// thrown exception so a startup-failure log line points
    /// directly at the runbook.</summary>
    public const string DocReference = "docs/jwt-rotation.md §11 (TTL discipline)";

    /// <summary>The canonical ratio — TTL must be no greater than
    /// half the rotation grace period. See class summary for
    /// rationale.</summary>
    public const double MaxTtlToGraceRatio = 0.5;

    private readonly TimeSpan _jwksTtl;
    private readonly int _rotationGraceSeconds;

    public RotationCadenceValidator(AuthOptions options, TimeSpan? jwksTtl = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _jwksTtl = jwksTtl ?? JwksCacheService.DefaultTtl;
        _rotationGraceSeconds = options.JwtRsaKeys?.Length > 0
            ? options.RotationGracePeriodSeconds
            : Math.Max(options.RotationGracePeriodSeconds, 1);
    }

    public RotationCadenceValidator(IOptions<AuthOptions> options, TimeSpan? jwksTtl = null)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), jwksTtl)
    {
    }

    public void Validate()
    {
        var ttlSeconds = _jwksTtl.TotalSeconds;
        // The half-grace ceiling is the canonical invariant. A
        // grace window of zero is treated as "no rotation
        // configured" and exits silently — operators running
        // without a rotation plan are out of scope for this check.
        if (_rotationGraceSeconds <= 0) return;

        var ceiling = _rotationGraceSeconds * MaxTtlToGraceRatio;
        if (ttlSeconds > ceiling)
        {
            throw new InvalidOperationException(
                $"JWKS cache TTL ({ttlSeconds:F0}s) exceeds half the JWT rotation grace " +
                $"period ({_rotationGraceSeconds}s / 2 = {ceiling:F0}s). " +
                $"Mid-rotation, downstream verifiers will see validation failures for " +
                $"up to {ttlSeconds:F0}s. " +
                $"Either lower the JWKS cache TTL (JwksCacheService.DefaultTtl), " +
                $"or raise Auth:JwtRsaKeys:RotationGracePeriodSeconds. " +
                $"See {DocReference}.");
        }
    }

    /// <summary>
    /// Phase K Wave 9 — Bishop. Helper exposed for the contract
    /// test so it can compute the ceiling deterministically
    /// without instantiating the validator.
    /// </summary>
    public static double ComputeCeilingSeconds(int rotationGraceSeconds) =>
        rotationGraceSeconds * MaxTtlToGraceRatio;
}
