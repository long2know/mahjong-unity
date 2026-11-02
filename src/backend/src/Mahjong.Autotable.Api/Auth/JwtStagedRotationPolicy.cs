namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 12 — Bishop. Staged JWT rotation policy.
/// The W10/W11 surface supports rotation rehearsal +
/// cadence validation; W12 adds the explicit STAGED rotation
/// window contract documented in <c>docs/jwt-rotation.md §13</c>.
///
/// <list type="bullet">
///   <item>During the overlap window (default 30 days), the
///         <see cref="JwtSigningKeyProvider"/> still mints new
///         tokens with the active signer (index 0) but the
///         validator accepts any key in
///         <see cref="JwtSigningKeyProvider.AllKeys"/> /
///         <see cref="JwtSigningKeyProvider.AllRsaKeys"/>.</item>
///   <item>The policy is purely informational at the validation
///         layer — the validator already round-trips through the
///         <c>kid</c> header to pick a key. The policy surfaces
///         the configured overlap window so operators can audit
///         it via the cadence validator + the JWKS endpoint's
///         response headers (W13 forward).</item>
///   <item>An optional <see cref="RotationStartUtc"/> stamp
///         lets the policy compute
///         <see cref="OverlapWindowEndsAtUtc"/> + the
///         <see cref="IsWithinOverlapWindow"/> predicate. When
///         unset, the policy treats the process as outside any
///         rotation window — keys are still validated, but the
///         "staged rotation" terminology doesn't apply.</item>
/// </list>
///
/// <para>This class is intentionally tiny — the actual key
/// material lives in <see cref="JwtSigningKeyProvider"/>. The
/// policy is the seam future surfaces will hook (cadence
/// validator, JWKS response headers, ops dashboard).</para>
/// </summary>
public sealed class JwtStagedRotationPolicy
{
    /// <summary>Default overlap window in days. Matches the
    /// canonical <c>docs/jwt-rotation.md §13</c> guidance.</summary>
    public const int DefaultOverlapDays = 30;

    private readonly int _overlapDays;
    private readonly DateTime? _rotationStartUtc;

    public JwtStagedRotationPolicy(AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _overlapDays = options.RotationOverlapDays > 0
            ? options.RotationOverlapDays
            : DefaultOverlapDays;
        _rotationStartUtc = options.RotationStartUtc;
    }

    /// <summary>Configured overlap window in days.</summary>
    public int OverlapDays => _overlapDays;

    /// <summary>UTC timestamp when the current staged rotation
    /// window opened. Null when no rotation is in progress.</summary>
    public DateTime? RotationStartUtc => _rotationStartUtc;

    /// <summary>UTC timestamp when the overlap window closes.
    /// Null when <see cref="RotationStartUtc"/> is unset.</summary>
    public DateTime? OverlapWindowEndsAtUtc =>
        _rotationStartUtc is { } start
            ? start.AddDays(_overlapDays)
            : null;

    /// <summary>
    /// Phase K Wave 16 — Bishop. <see cref="DateTimeOffset"/>
    /// flavour of <see cref="RotationStartUtc"/>. Returns the
    /// rotation-start instant with a UTC offset so call sites
    /// using <c>DateTimeOffset</c> for time-zone-safe math (e.g.
    /// the multi-tenant rotation pipeline) don't have to round-
    /// trip through <see cref="DateTime"/>. Null when no
    /// rotation is in progress.
    /// </summary>
    public DateTimeOffset? RotationStartUtcOffset =>
        _rotationStartUtc is { } start
            ? new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc))
            : null;

    /// <summary>
    /// Phase K Wave 16 — Bishop. <see cref="DateTimeOffset"/>
    /// flavour of <see cref="OverlapWindowEndsAtUtc"/>. Useful
    /// for surfaces that already speak <c>DateTimeOffset</c>
    /// (the per-tenant rotation policy uses
    /// <c>DateTimeOffset</c> for its complete-utc column).
    /// </summary>
    public DateTimeOffset? OverlapWindowEndsAtOffset =>
        OverlapWindowEndsAtUtc is { } end
            ? new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Utc))
            : null;

    /// <summary>True when <paramref name="utcNow"/> falls
    /// inside the staged rotation overlap window.</summary>
    public bool IsWithinOverlapWindow(DateTime utcNow)
    {
        if (_rotationStartUtc is not { } start) return false;
        var end = start.AddDays(_overlapDays);
        return utcNow >= start && utcNow <= end;
    }

    /// <summary>
    /// Phase K Wave 16 — Bishop. <see cref="DateTimeOffset"/>
    /// overload of <see cref="IsWithinOverlapWindow(DateTime)"/>.
    /// Normalises the input to UTC before delegating so callers
    /// can supply any offset.
    /// </summary>
    public bool IsWithinOverlapWindow(DateTimeOffset utcNow) =>
        IsWithinOverlapWindow(utcNow.UtcDateTime);

    /// <summary>Days remaining in the current overlap window.
    /// Returns 0 when no rotation is in progress or the window
    /// has already closed. Surfaced for the W13
    /// operator-dashboard banner.</summary>
    public int RemainingOverlapDays(DateTime utcNow)
    {
        if (_rotationStartUtc is not { } start) return 0;
        var end = start.AddDays(_overlapDays);
        var remaining = (end - utcNow).TotalDays;
        if (remaining < 0) return 0;
        return (int)Math.Ceiling(remaining);
    }

    /// <summary>
    /// Phase K Wave 16 — Bishop. <see cref="DateTimeOffset"/>
    /// overload of <see cref="RemainingOverlapDays(DateTime)"/>.
    /// Delegates to the <see cref="DateTime"/> impl after
    /// normalising to UTC.
    /// </summary>
    public int RemainingOverlapDays(DateTimeOffset utcNow) =>
        RemainingOverlapDays(utcNow.UtcDateTime);
}
