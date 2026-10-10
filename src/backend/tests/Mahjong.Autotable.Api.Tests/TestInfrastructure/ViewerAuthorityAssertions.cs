using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Mahjong.Autotable.Api.Autotable;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.TestInfrastructure;

internal static class ViewerAuthorityAssertions
{
    public static long Expect(JsonElement envelope, string? roomId, int? seat, long minimumRevision = 0)
    {
        Assert.True(envelope.TryGetProperty("viewer", out var viewer), "Every Changsha envelope requires viewer authority.");
        Assert.Equal(JsonValueKind.Object, viewer.ValueKind);
        Assert.Equal(new[] { "revision", "roomId", "seat" },
            viewer.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
        Assert.Equal(roomId, viewer.GetProperty("roomId").GetString());
        var actualSeat = viewer.GetProperty("seat");
        if (seat.HasValue) Assert.Equal(seat.Value, actualSeat.GetInt32());
        else Assert.Equal(JsonValueKind.Null, actualSeat.ValueKind);
        var revision = viewer.GetProperty("revision").GetInt64();
        Assert.True(revision >= minimumRevision, $"Authority revision {revision} precedes {minimumRevision}.");
        return revision;
    }

    public static AutotableConnection GrantedConnection(
        AutotableConnectionManager manager, IChangshaGameRuntime runtime, string gameId, int seat)
    {
        return Assert.Single(Connections(manager),
            connection => runtime.TryGetSeatForConnection(gameId, connection.Id.ToString("N")) == seat);
    }

    public static AutotableConnection[] Connections(AutotableConnectionManager manager)
    {
        // Read transport IDs only to fence actual disconnects and serialized-send races.
        var field = typeof(AutotableConnectionManager).GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<ConcurrentDictionary<Guid, AutotableConnection>>(field.GetValue(manager)).Values.ToArray();
    }
}
