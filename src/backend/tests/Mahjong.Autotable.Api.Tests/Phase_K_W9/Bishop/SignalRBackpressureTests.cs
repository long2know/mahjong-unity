using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W9.Bishop;

/// <summary>
/// Phase K Wave 9 — Bishop. Hard-asserted facts for the SignalR
/// backpressure broadcaster. Drives the broadcaster against a
/// captured-output hub stub so we can assert the rate-cap, age-drop,
/// monotonic-sequence, and reconnect-replay contracts without
/// standing up a real SignalR connection.
///
/// <list type="number">
///   <item>Published messages flow through to <c>SendCoreAsync</c>.</item>
///   <item>Sequence numbers are monotonic and start at 1.</item>
///   <item>Rate cap drops messages above
///         <c>MaxMessagesPerSecond</c>.</item>
///   <item>The replay buffer retains every published envelope up to
///         the retention depth.</item>
///   <item><c>ResumeFromAck</c> returns the subset newer than the
///         supplied sequence.</item>
///   <item><c>ResumeFromAck</c> drops envelopes older than the age
///         window.</item>
///   <item>Empty-group publish returns <c>false</c>.</item>
///   <item><c>CurrentSequence</c> tracks the latest stamped sequence.</item>
/// </list>
/// </summary>
public sealed class SignalRBackpressureTests
{
    private sealed class CapturedSend
    {
        public required string Group { get; init; }
        public required string Method { get; init; }
        public required object?[] Args { get; init; }
    }

    private sealed class FakeHubClients : IHubClients
    {
        private readonly FakeClientProxy _proxy;
        public FakeHubClients(FakeClientProxy proxy) { _proxy = proxy; }
        public IClientProxy All => _proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Client(string connectionId) => _proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
        public IClientProxy Group(string groupName)
        {
            _proxy.LastGroup = groupName;
            return _proxy;
        }
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;
        public IClientProxy User(string userId) => _proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
    }

    private sealed class FakeClientProxy : IClientProxy
    {
        public List<CapturedSend> Sends { get; } = new();
        public string LastGroup { get; set; } = string.Empty;

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Sends.Add(new CapturedSend { Group = LastGroup, Method = method, Args = args });
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHub : Microsoft.AspNetCore.SignalR.Hub { }

    private sealed class FakeHubContext : IHubContext<FakeHub>
    {
        public FakeClientProxy Proxy { get; } = new();
        public IHubClients Clients => new FakeHubClients(Proxy);
        public IGroupManager Groups => throw new NotSupportedException();
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-9")]
    public async Task PublishAsync_RoutesThroughHub()
    {
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(hub, NullLogger.Instance);
        var ok = await b.PublishAsync("g1", "DoThing", new { x = 1 });
        Assert.True(ok);
        Assert.Single(hub.Proxy.Sends);
        Assert.Equal("DoThing", hub.Proxy.Sends[0].Method);
        Assert.Equal("g1", hub.Proxy.Sends[0].Group);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-9")]
    public async Task SequenceNumbers_AreMonotonic()
    {
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(hub, NullLogger.Instance);
        await b.PublishAsync("g", "M", 1);
        await b.PublishAsync("g", "M", 2);
        await b.PublishAsync("g", "M", 3);
        Assert.Equal(3, b.CurrentSequence);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-9")]
    public async Task PublishAsync_RateCap_DropsMessagesAboveCeiling()
    {
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance,
            maxPerSecond: 5,
            maxAge: TimeSpan.FromSeconds(5),
            retentionDepth: 256);

        var accepted = 0;
        var dropped = 0;
        for (var i = 0; i < 20; i++)
        {
            var ok = await b.PublishAsync("burst-group", "Tick", i);
            if (ok) accepted++; else dropped++;
        }

        Assert.Equal(5, accepted);
        Assert.Equal(15, dropped);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-9")]
    public async Task ResumeFromAck_ReturnsAllEnvelopes_AboveLastAcked()
    {
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance,
            maxPerSecond: 1_000,
            maxAge: TimeSpan.FromSeconds(60));
        for (var i = 0; i < 5; i++)
            await b.PublishAsync("g", "M", i);

        var resumed = b.ResumeFromAck("g", lastAckedSequence: 2);
        Assert.Equal(3, resumed.Count);
        Assert.All(resumed, e => Assert.True(e.Sequence > 2));
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-9")]
    public async Task ResumeFromAck_DropsEnvelopes_OlderThanAgeWindow()
    {
        var hub = new FakeHubContext();
        // 1ms age window — every envelope is "ancient" by the time
        // we call ResumeFromAck.
        var b = new SignalRBackpressureBroadcaster<FakeHub>(
            hub, NullLogger.Instance,
            maxPerSecond: 1_000,
            maxAge: TimeSpan.FromMilliseconds(1));
        await b.PublishAsync("g", "M", 1);
        await Task.Delay(20);
        var resumed = b.ResumeFromAck("g", lastAckedSequence: 0);
        Assert.Empty(resumed);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-9")]
    public async Task ResumeFromAck_UnknownGroup_ReturnsEmpty()
    {
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(hub, NullLogger.Instance);
        await b.PublishAsync("g-real", "M", 1);
        var resumed = b.ResumeFromAck("g-other", lastAckedSequence: 0);
        Assert.Empty(resumed);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-9")]
    public async Task PublishAsync_EmptyGroup_ReturnsFalse()
    {
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(hub, NullLogger.Instance);
        var ok = await b.PublishAsync(string.Empty, "M", 1);
        Assert.False(ok);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-9")]
    public void Defaults_AreCanonicalValues()
    {
        Assert.Equal(30, SignalRBackpressureBroadcaster<FakeHub>.DefaultMaxMessagesPerSecond);
        Assert.Equal(5, SignalRBackpressureBroadcaster<FakeHub>.DefaultMaxMessageAgeSeconds);
        Assert.Equal(256, SignalRBackpressureBroadcaster<FakeHub>.DefaultRetainedMessageCount);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-9")]
    public async Task PublishAsync_StampsEnvelopeFields()
    {
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(hub, NullLogger.Instance);
        await b.PublishAsync("g", "M", new { x = 1 });
        var resumed = b.ResumeFromAck("g", lastAckedSequence: 0);
        Assert.Single(resumed);
        var env = resumed[0];
        Assert.Equal(1, env.Sequence);
        Assert.Equal("M", env.Method);
        Assert.NotNull(env.Payload);
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-9")]
    public void Constructor_NullHub_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SignalRBackpressureBroadcaster<FakeHub>(null!, NullLogger.Instance));
    }

    [Fact, Trait("Category", "Realtime"), Trait("Wave", "Phase-K-9")]
    public async Task BackpressureEnvelope_Sequence_StartsAtOne()
    {
        var hub = new FakeHubContext();
        var b = new SignalRBackpressureBroadcaster<FakeHub>(hub, NullLogger.Instance);
        await b.PublishAsync("g", "M", 1);
        var resumed = b.ResumeFromAck("g", lastAckedSequence: 0);
        Assert.Equal(1, resumed[0].Sequence);
    }
}
