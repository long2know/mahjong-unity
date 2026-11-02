using System.Runtime.CompilerServices;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Observability;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 17 — Bishop. <em>DateTimeOffset widening,
/// round 2.</em> The W14 surface added DateTimeOffset overloads
/// to a handful of services + entities; W17 widens the round-2
/// cohort identified in the W16 hand-off memo §"Outstanding
/// DateTimeOffset gaps":
///
/// <list type="bullet">
///   <item><see cref="PlayerAuthIdentity"/> — Created /
///         LastUsed projections.</item>
///   <item><see cref="PlayerAuthSession"/> — Created / Expires
///         / LastUsed projections.</item>
///   <item><see cref="ReconnectAuditEntry"/> —
///         <c>At</c> projection.</item>
///   <item><see cref="SignalRSequenceEntry"/> — Created /
///         Expires projections.</item>
///   <item><see cref="OAuthDiscoveryDocument"/> +
///         <see cref="OAuthProviderHealthSnapshot"/> — cache-age
///         helpers (rendered as DateTimeOffset deltas so the
///         operator dashboard sees a stable type across the
///         multi-provider matrix).</item>
/// </list>
///
/// <para>The widening is intentionally extension-based: we
/// project the existing <see cref="DateTime"/> columns to
/// <see cref="DateTimeOffset"/> at the call site, leaving the
/// persisted shape unchanged. That keeps the migration set
/// narrow (no new columns) while still letting downstream
/// callers — admin dashboards, audit ledgers, OAuth health
/// renders — round-trip operator clocks through a single
/// offset-aware code path.</para>
///
/// <para>The conversion always treats the persisted
/// <see cref="DateTime"/> as UTC (the platform stamps all
/// columns with <see cref="DateTime.UtcNow"/>); the resulting
/// <see cref="DateTimeOffset"/> carries an explicit
/// <see cref="TimeSpan.Zero"/> offset so a downstream caller
/// re-rendering into a local tz never accidentally drops the
/// offset.</para>
/// </summary>
public static class DateTimeOffsetWideningR2
{
    /// <summary>Wire-stable identifier used by tests + audit
    /// dashboards to confirm the widening cohort. Reserved
    /// vocabulary for the W17 round-2 surface.</summary>
    public const string WaveTag = "phase-k-w17-r2";

    /// <summary>Centralised conversion. Treats the input as
    /// UTC even when the kind is unspecified so the offset is
    /// always <c>+00:00</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTimeOffset AsUtcOffset(this DateTime utc)
    {
        var kind = utc.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
            : utc.ToUniversalTime();
        return new DateTimeOffset(kind, TimeSpan.Zero);
    }

    // -------- PlayerAuthIdentity --------

    public static DateTimeOffset CreatedAtOffset(this PlayerAuthIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return identity.CreatedAt.AsUtcOffset();
    }

    public static DateTimeOffset LastUsedAtOffset(this PlayerAuthIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return identity.LastUsedAt.AsUtcOffset();
    }

    // -------- PlayerAuthSession --------

    public static DateTimeOffset CreatedAtOffset(this PlayerAuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.CreatedAt.AsUtcOffset();
    }

    public static DateTimeOffset ExpiresAtOffset(this PlayerAuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.ExpiresAt.AsUtcOffset();
    }

    public static DateTimeOffset LastUsedAtOffset(this PlayerAuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.LastUsedAt.AsUtcOffset();
    }

    // -------- ReconnectAuditEntry --------

    public static DateTimeOffset AtOffset(this ReconnectAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.At.AsUtcOffset();
    }

    // -------- SignalRSequenceEntry --------

    public static DateTimeOffset CreatedAtOffset(this SignalRSequenceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.CreatedAt.AsUtcOffset();
    }

    public static DateTimeOffset ExpiresAtOffset(this SignalRSequenceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.ExpiresAt.AsUtcOffset();
    }

    // -------- Generic cache-age helpers (OAuth discovery +
    //          provider health snapshots) --------

    /// <summary>Cache-age delta between the input UTC instant
    /// and <paramref name="nowOffset"/>. Returned as a positive
    /// <see cref="TimeSpan"/>; negative deltas (clock-skew /
    /// future timestamps) clamp to <see cref="TimeSpan.Zero"/>
    /// so the operator dashboard never renders a negative
    /// age.</summary>
    public static TimeSpan CacheAgeOffset(this DateTime cachedAtUtc, DateTimeOffset nowOffset)
    {
        var cachedOffset = cachedAtUtc.AsUtcOffset();
        var delta = nowOffset - cachedOffset;
        return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
    }
}
