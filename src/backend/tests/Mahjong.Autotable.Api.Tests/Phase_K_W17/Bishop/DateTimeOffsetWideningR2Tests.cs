using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Data.Entities;
using Mahjong.Autotable.Api.Observability;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W17.Bishop;

/// <summary>
/// Phase K Wave 17 — Bishop. Behaviour tests for the W17
/// DateTimeOffset widening round 2 — the extension-based
/// projections in <see cref="DateTimeOffsetWideningR2"/>.
/// </summary>
public sealed class DateTimeOffsetWideningR2Tests
{
    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void WaveTag_IsWireStable()
    {
        Assert.Equal("phase-k-w17-r2", DateTimeOffsetWideningR2.WaveTag);
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void AsUtcOffset_UnspecifiedKind_TreatedAsUtc()
    {
        var dt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Unspecified);
        var off = dt.AsUtcOffset();
        Assert.Equal(TimeSpan.Zero, off.Offset);
        Assert.Equal(dt.Ticks, off.UtcDateTime.Ticks);
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void AsUtcOffset_UtcKind_ZeroOffset()
    {
        var dt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var off = dt.AsUtcOffset();
        Assert.Equal(TimeSpan.Zero, off.Offset);
        Assert.Equal(dt, off.UtcDateTime);
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void PlayerAuthIdentity_CreatedAtOffset_RoundTrips()
    {
        var id = new PlayerAuthIdentity
        {
            CreatedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        Assert.Equal(TimeSpan.Zero, id.CreatedAtOffset().Offset);
        Assert.Equal(id.CreatedAt, id.CreatedAtOffset().UtcDateTime);
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void PlayerAuthIdentity_LastUsedAtOffset_RoundTrips()
    {
        var id = new PlayerAuthIdentity
        {
            LastUsedAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        Assert.Equal(id.LastUsedAt, id.LastUsedAtOffset().UtcDateTime);
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void PlayerAuthSession_CreatedAtOffset_RoundTrips()
    {
        var s = new PlayerAuthSession
        {
            CreatedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        Assert.Equal(s.CreatedAt, s.CreatedAtOffset().UtcDateTime);
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void PlayerAuthSession_ExpiresAtOffset_RoundTrips()
    {
        var s = new PlayerAuthSession
        {
            ExpiresAt = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        Assert.Equal(s.ExpiresAt, s.ExpiresAtOffset().UtcDateTime);
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void PlayerAuthSession_LastUsedAtOffset_RoundTrips()
    {
        var s = new PlayerAuthSession
        {
            LastUsedAt = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc),
        };
        Assert.Equal(s.LastUsedAt, s.LastUsedAtOffset().UtcDateTime);
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ReconnectAuditEntry_AtOffset_RoundTrips()
    {
        var e = new ReconnectAuditEntry
        {
            At = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        Assert.Equal(e.At, e.AtOffset().UtcDateTime);
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void SignalRSequenceEntry_CreatedAtOffset_RoundTrips()
    {
        var e = new SignalRSequenceEntry
        {
            CreatedAt = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        Assert.Equal(e.CreatedAt, e.CreatedAtOffset().UtcDateTime);
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void SignalRSequenceEntry_ExpiresAtOffset_RoundTrips()
    {
        var e = new SignalRSequenceEntry
        {
            ExpiresAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        Assert.Equal(e.ExpiresAt, e.ExpiresAtOffset().UtcDateTime);
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void CacheAgeOffset_PositiveDelta_Returned()
    {
        var cached = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromHours(1), cached.CacheAgeOffset(now));
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void CacheAgeOffset_NegativeDelta_ClampsToZero()
    {
        var cached = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, cached.CacheAgeOffset(now));
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void NullPlayerAuthIdentity_Extension_Throws()
    {
        PlayerAuthIdentity? id = null;
        Assert.Throws<ArgumentNullException>(() => id!.CreatedAtOffset());
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void NullPlayerAuthSession_Extension_Throws()
    {
        PlayerAuthSession? s = null;
        Assert.Throws<ArgumentNullException>(() => s!.CreatedAtOffset());
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void NullReconnectAuditEntry_Extension_Throws()
    {
        ReconnectAuditEntry? e = null;
        Assert.Throws<ArgumentNullException>(() => e!.AtOffset());
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void NullSignalRSequenceEntry_Extension_Throws()
    {
        SignalRSequenceEntry? e = null;
        Assert.Throws<ArgumentNullException>(() => e!.CreatedAtOffset());
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void PerTenantJwksRotationPolicy_CreatedAtOffset_PropertyExists()
    {
        var p = typeof(PerTenantJwksRotationPolicy).GetProperty("CreatedAtOffset");
        Assert.NotNull(p);
        Assert.Equal(typeof(DateTimeOffset), p!.PropertyType);
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void ReplayRetentionPolicy_CreatedAtOffset_PropertyExists()
    {
        var p = typeof(Mahjong.Autotable.Api.Replays.ReplayRetentionPolicy)
            .GetProperty("CreatedAtOffset");
        Assert.NotNull(p);
        Assert.Equal(typeof(DateTimeOffset), p!.PropertyType);
    }

    [Fact, Trait("Category", "DateTimeOffset"), Trait("Wave", "Phase-K-17"), Trait("Lane", "Bishop")]
    public void SignalRRetentionPolicy_UpdatedAtOffset_PropertyExists()
    {
        var p = typeof(SignalRRetentionPolicy).GetProperty("UpdatedAtOffset");
        Assert.NotNull(p);
        Assert.Equal(typeof(DateTimeOffset), p!.PropertyType);
    }
}
