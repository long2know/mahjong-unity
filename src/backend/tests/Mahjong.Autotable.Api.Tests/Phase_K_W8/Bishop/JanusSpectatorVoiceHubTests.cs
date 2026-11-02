using Mahjong.Autotable.Api.Voice;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W8.Bishop;

/// <summary>
/// Phase K Wave 8 — Bishop. Hard-asserted facts for the
/// <see cref="JanusSpectatorVoiceHub"/> + <see cref="JanusHealthProbe"/>
/// seams. These tests pin the parsing + deterministic-mountpoint
/// helpers without standing up a real Janus gateway — those are
/// driven by Vasquez's higher-level integration spec.
///
/// <list type="number">
///   <item>Mountpoint id is deterministic per table.</item>
///   <item>Mountpoint id stays in the 0..999_999 range.</item>
///   <item>Empty / null table id falls back gracefully.</item>
///   <item>JSON id-field extractor accepts both number + string ids.</item>
///   <item>JSON id-field extractor rejects malformed bodies.</item>
///   <item>Health probe reports endpoint-not-configured for empty config.</item>
///   <item>Health probe reports timeout for slow endpoints.</item>
///   <item>SpectatorVoiceHub is no longer sealed (so Janus can extend).</item>
/// </list>
/// </summary>
public sealed class JanusSpectatorVoiceHubTests
{
    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void ComputeMountpointId_IsDeterministic_ForSameTableId()
    {
        var a = JanusSpectatorVoiceHub.ComputeMountpointId("table-1234");
        var b = JanusSpectatorVoiceHub.ComputeMountpointId("table-1234");
        Assert.Equal(a, b);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void ComputeMountpointId_DiffersForDifferentTableIds()
    {
        var a = JanusSpectatorVoiceHub.ComputeMountpointId("table-A");
        var b = JanusSpectatorVoiceHub.ComputeMountpointId("table-B");
        Assert.NotEqual(a, b);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void ComputeMountpointId_FitsInSixDigitMountpointSpace()
    {
        var id = JanusSpectatorVoiceHub.ComputeMountpointId("table-XYZ");
        Assert.InRange(id, 0, 999_999);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void ComputeMountpointId_HandlesEmptyOrNullId()
    {
        var a = JanusSpectatorVoiceHub.ComputeMountpointId("");
        var b = JanusSpectatorVoiceHub.ComputeMountpointId(null!);
        Assert.InRange(a, 0, 999_999);
        Assert.InRange(b, 0, 999_999);
        Assert.Equal(a, b);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void ExtractIdField_NumericId_ReturnsLong()
    {
        var body = "{\"janus\":\"success\",\"data\":{\"id\":1234567890}}";
        var id = JanusSpectatorVoiceHub.ExtractIdField(body);
        Assert.Equal(1234567890L, id);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void ExtractIdField_StringId_ParsesToLong()
    {
        var body = "{\"janus\":\"success\",\"data\":{\"id\":\"42\"}}";
        var id = JanusSpectatorVoiceHub.ExtractIdField(body);
        Assert.Equal(42L, id);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void ExtractIdField_NonNumericString_ReturnsNull()
    {
        var body = "{\"janus\":\"success\",\"data\":{\"id\":\"not-a-number\"}}";
        var id = JanusSpectatorVoiceHub.ExtractIdField(body);
        Assert.Null(id);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void ExtractIdField_NoDataObject_ReturnsNull()
    {
        var body = "{\"janus\":\"error\"}";
        var id = JanusSpectatorVoiceHub.ExtractIdField(body);
        Assert.Null(id);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void ExtractIdField_MalformedJson_ReturnsNull()
    {
        Assert.Null(JanusSpectatorVoiceHub.ExtractIdField("not-json"));
        Assert.Null(JanusSpectatorVoiceHub.ExtractIdField(""));
        Assert.Null(JanusSpectatorVoiceHub.ExtractIdField("   "));
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public async Task HealthProbe_EmptyEndpoint_ReportsNotConfigured()
    {
        using var probe = new JanusHealthProbe("");
        var result = await probe.ProbeAsync();
        Assert.False(result.IsHealthy);
        Assert.Equal("endpoint-not-configured", result.Error);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public async Task HealthProbe_UnreachableEndpoint_ReportsHttpError()
    {
        // Use an unroutable IPv4 reserved by RFC 5737 (TEST-NET-1)
        // and rely on the 5-second timeout to short-circuit the
        // probe — never hits a real host.
        using var probe = new JanusHealthProbe("http://203.0.113.1:7088");
        var result = await probe.ProbeAsync();
        Assert.False(result.IsHealthy);
        Assert.NotNull(result.Error);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void SpectatorVoiceHub_IsNotSealed_SoJanusCanExtend()
    {
        // The Janus impl extends SpectatorVoiceHub; the base class
        // therefore MUST NOT be sealed. This fact protects the
        // un-seal change.
        Assert.False(typeof(SpectatorVoiceHub).IsSealed);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void JanusSpectatorVoiceHub_ExtendsSpectatorVoiceHub()
    {
        Assert.True(typeof(SpectatorVoiceHub).IsAssignableFrom(typeof(JanusSpectatorVoiceHub)),
            "JanusSpectatorVoiceHub must extend SpectatorVoiceHub so the /hubs/voice/spectator URL stays the same.");
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void JanusSpectatorVoiceHub_StubFallback_PeerIdPrefix_IsStable()
    {
        // The stub-fallback peer id prefix is part of the operator
        // log surface — protect it from accidental drift.
        Assert.Equal("stub-fallback-", JanusSpectatorVoiceHub.StubPeerIdPrefix);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void VoiceOptions_HasJanusEndpoint_Property()
    {
        var prop = typeof(VoiceOptions).GetProperty("JanusEndpoint");
        Assert.NotNull(prop);
        Assert.Equal(typeof(string), prop!.PropertyType);
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void VoiceOptions_HasSpectatorSfuImpl_Property()
    {
        var prop = typeof(VoiceOptions).GetProperty("SpectatorSfuImpl");
        Assert.NotNull(prop);
        Assert.Equal(typeof(string), prop!.PropertyType);
    }
}
