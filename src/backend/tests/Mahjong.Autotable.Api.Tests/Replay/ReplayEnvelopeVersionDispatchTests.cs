using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mahjong.Autotable.Api.Changsha.Replay;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Replay;

public class ReplayEnvelopeVersionDispatchTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> MalformedCurrentEvents()
    {
        foreach (var rowVersion in new[] { 1, 2 })
        foreach (var shape in new[] { "missing", "null", "object", "string", "number", "boolean" })
            yield return [rowVersion, shape];
    }

    public static IEnumerable<object[]> MalformedDeclaredSchemas()
    {
        foreach (var rowVersion in new[] { 1, 2 })
        foreach (var versionJson in new[] { "\"3\"", "null", "3.5", "2147483648", "true" })
            yield return [rowVersion, versionJson];
    }

    [Theory]
    [MemberData(nameof(MalformedCurrentEvents))]
    public async Task StaleLegacyColumn_ClaimedV3WithMalformedEvents_RejectsWithoutPrivateData(
        int rowVersion, string shape)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var payload = CurrentEnvelope();
        if (shape == "missing")
            payload.Remove("events");
        else
            payload["events"] = shape switch
            {
                "null" => null,
                "object" => new JsonObject(),
                "string" => JsonValue.Create("not-an-array"),
                "number" => JsonValue.Create(7),
                "boolean" => JsonValue.Create(true),
                _ => throw new ArgumentOutOfRangeException(nameof(shape))
            };
        var id = await Store(fixture, rowVersion, payload.ToJsonString());

        await AssertInvalidSchema(fixture, id);
    }

    [Theory]
    [MemberData(nameof(MalformedDeclaredSchemas))]
    public async Task StaleLegacyColumn_MalformedDeclaredSchema_RejectsWithoutDowngrade(
        int rowVersion, string versionJson)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var payload = CurrentEnvelope();
        payload["schemaVersion"] = JsonNode.Parse(versionJson);
        var id = await Store(fixture, rowVersion, payload.ToJsonString());

        await AssertInvalidSchema(fixture, id);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ValidV3_UsesDeclaredSchemaAndGlobalOrderWithoutPrivateData(int rowVersion)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var id = await Store(fixture, rowVersion, CurrentEnvelope().ToJsonString());

        using var response = await fixture.Client.GetAsync($"/api/games/{id}/replay");
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("\"reconstruction\"", text, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(text);
        Assert.Equal(3, body.RootElement.GetProperty("schemaVersion").GetInt32());
        var events = body.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(new long[] { 1, 2 }, events.Select(e => e.GetProperty("sequence").GetInt64()));
        Assert.Equal(new[] { 0, 1 }, events.Select(e => e.GetProperty("turn").GetInt32()));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    public async Task GenuineLegacyReadback_PreservesSchemaDefaultsAndStoredOrder(
        int rowVersion, int payloadVersion)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        const string eventsJson = """[{"turn":57,"action":"draw-hand"},{"turn":1,"action":"dice-rolled"}]""";
        var payload = payloadVersion == 1
            ? eventsJson
            : "{\"schemaVersion\":2,\"events\":" + eventsJson + "}";
        var id = await Store(fixture, rowVersion, payload);

        using var response = await fixture.Client.GetAsync($"/api/games/{id}/replay");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(payloadVersion, body.RootElement.GetProperty("schemaVersion").GetInt32());
        var events = body.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(new[] { 57, 1 }, events.Select(e => e.GetProperty("turn").GetInt32()));
        Assert.All(events, item =>
        {
            Assert.Equal("unknown", item.GetProperty("source").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("durationMs").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("debugScore").ValueKind);
        });
    }

    private static JsonObject CurrentEnvelope()
    {
        var driver = new ReplayTestDriver(17);
        driver.Do(ReplayOperation.StartGame);
        return new JsonObject
        {
            ["schemaVersion"] = ChangshaGameReplay.CurrentSchemaVersion,
            ["events"] = JsonNode.Parse(
                """[{"sequence":2,"turn":1,"action":"game-started"},{"sequence":1,"turn":0,"action":"game-created"}]"""),
            ["reconstruction"] = JsonSerializer.SerializeToNode(driver.Export(), ChangshaReplayStateCodec.RecordJson)
        };
    }

    private static async Task<Guid> Store(ReplayRuntimeFixture fixture, int rowVersion, string payload)
    {
        var id = Guid.NewGuid();
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ChangshaGameReplays.Add(new ChangshaGameReplay
        {
            GameId = id, SchemaVersion = rowVersion, EventsJson = payload, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task AssertInvalidSchema(ReplayRuntimeFixture fixture, Guid id)
    {
        using var response = await fixture.Client.GetAsync($"/api/games/{id}/replay");
        var text = await response.Content.ReadAsStringAsync();
        var exposesReconstruction = text.Contains("\"reconstruction\"", StringComparison.Ordinal);
        output.WriteLine("Status: {0}; private reconstruction exposed: {1}",
            (int)response.StatusCode, exposesReconstruction);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("invalid-replay-schema", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(id.ToString(), body.RootElement.GetProperty("gameId").GetString());
        Assert.False(exposesReconstruction);
        Assert.False(body.RootElement.TryGetProperty("events", out _));
    }
}
