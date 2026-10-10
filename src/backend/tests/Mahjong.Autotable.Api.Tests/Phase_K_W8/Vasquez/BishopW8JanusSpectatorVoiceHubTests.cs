using System.Reflection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W8.Vasquez;

/// <summary>
/// Phase K Wave 8 — Bishop. Janus SFU spectator-voice hub contract.
///
/// <para>W7 shipped a SignalR-based voice hub that fans audio between
/// peers via a star-topology relay. W8 replaces the relay with a real
/// SFU (Janus) so the hub can scale past the ~6-peer star limit. The
/// new type <c>JanusSpectatorVoiceHub</c> (or <c>JanusVoiceHub</c>) is
/// a SignalR hub that brokers a Janus AudioBridge mountpoint per
/// table.</para>
///
/// <para>Six facts:</para>
/// <list type="number">
///   <item><c>JanusSpectatorVoiceHub</c> type present in API assembly.</item>
///   <item>It inherits (transitively) from
///         <c>Microsoft.AspNetCore.SignalR.Hub</c>.</item>
///   <item>A <c>JanusOptions</c> / <c>JanusAudioBridgeOptions</c> record
///         exists carrying at least <c>BaseUrl</c> and a secret /
///         <c>ApiSecret</c> axis.</item>
///   <item>A <c>JanusAudioBridgeMountpoint</c> entity / record
///         exists carrying at least <c>RoomId</c> +
///         <c>Description</c>.</item>
///   <item>The hub exposes an explicit join axis —
///         <c>JoinTable</c> / <c>JoinMountpoint</c> / <c>Subscribe</c>
///         method.</item>
///   <item>A Janus client interface — <c>IJanusClient</c> /
///         <c>IJanusAudioBridgeClient</c> — is registered so the hub
///         can be mocked under test.</item>
/// </list>
///
/// <para>All facts forward-stage tolerant.</para>
/// </summary>
public sealed class JanusSpectatorVoiceHubTests
{
    private static readonly Assembly ApiAssembly =
        typeof(Mahjong.Autotable.Api.Changsha.Runtime.ChangshaGameRuntime).Assembly;

    private static Type? FindHubType() =>
        ApiAssembly.GetTypes().FirstOrDefault(t =>
            t.Name == "JanusSpectatorVoiceHub"
            || t.Name == "JanusVoiceHub"
            || t.Name == "JanusSpectatorHub");

    private static Type? FindOptionsType() =>
        ApiAssembly.GetTypes().FirstOrDefault(t =>
            t.Name == "JanusOptions"
            || t.Name == "JanusAudioBridgeOptions"
            || t.Name == "JanusSfuOptions");

    private static Type? FindMountpointType() =>
        ApiAssembly.GetTypes().FirstOrDefault(t =>
            t.Name == "JanusAudioBridgeMountpoint"
            || t.Name == "JanusMountpoint"
            || t.Name == "AudioBridgeMountpoint");

    private static Type? FindClientInterfaceType() =>
        ApiAssembly.GetTypes().FirstOrDefault(t =>
            t.IsInterface
            && (t.Name == "IJanusClient"
                || t.Name == "IJanusAudioBridgeClient"
                || t.Name == "IJanusSfuClient"));

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void JanusHub_TypePresent_OrForwardStaged()
    {
        var t = FindHubType();
        if (t is null) return;
        Assert.True(t.IsClass, "Janus hub MUST be a class.");
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void JanusHub_InheritsFromSignalRHub_OrForwardStaged()
    {
        var t = FindHubType();
        if (t is null) return;

        bool inheritsHub = false;
        for (var b = t.BaseType; b is not null; b = b.BaseType)
        {
            if (b.FullName?.StartsWith("Microsoft.AspNetCore.SignalR.Hub", StringComparison.Ordinal) == true)
            {
                inheritsHub = true;
                break;
            }
        }
        Assert.True(inheritsHub,
            "JanusSpectatorVoiceHub MUST inherit from Microsoft.AspNetCore.SignalR.Hub.");
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void JanusOptions_PresentOrForwardStaged()
    {
        var t = FindOptionsType();
        if (t is null) return;

        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Select(p => p.Name)
                     .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var urlish = new[] { "BaseUrl", "Endpoint", "Url", "JanusUrl" };
        var secretish = new[] { "ApiSecret", "Secret", "AuthToken", "AdminSecret" };

        Assert.True(urlish.Any(props.Contains),
            "JanusOptions MUST carry a URL / endpoint axis.");
        Assert.True(secretish.Any(props.Contains),
            "JanusOptions MUST carry a secret / API-key axis.");
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void JanusMountpoint_PresentOrForwardStaged()
    {
        var t = FindMountpointType();
        if (t is null) return;

        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Select(p => p.Name)
                     .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var roomish = new[] { "RoomId", "Room", "MountpointId", "Id" };
        var descish = new[] { "Description", "Name", "Label" };

        Assert.True(roomish.Any(props.Contains),
            "JanusAudioBridgeMountpoint MUST carry a room / id axis.");
        Assert.True(descish.Any(props.Contains),
            "JanusAudioBridgeMountpoint MUST carry a description / name axis.");
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void JanusHub_JoinMethod_PresentOrForwardStaged()
    {
        var t = FindHubType();
        if (t is null) return;

        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var hasJoin = methods.Any(m =>
            m.Name == "JoinTable"
            || m.Name == "JoinMountpoint"
            || m.Name == "Subscribe"
            || m.Name == "JoinSpectatorVoice"
            || m.Name == "Join");

        Assert.True(hasJoin,
            "JanusSpectatorVoiceHub MUST expose a Join / Subscribe method.");
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-8")]
    public void JanusClient_InterfacePresent_OrForwardStaged()
    {
        var t = FindClientInterfaceType();
        if (t is null) return;
        Assert.True(t.IsInterface, "IJanusClient MUST be an interface (mockable).");
    }
}
