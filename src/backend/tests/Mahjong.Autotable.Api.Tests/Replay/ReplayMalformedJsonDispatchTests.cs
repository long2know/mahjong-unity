using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mahjong.Autotable.Api.Changsha.Replay;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Mahjong.Autotable.Api.Tests.Replay;

public class ReplayMalformedJsonDispatchTests(ITestOutputHelper output)
{
    private const string PrivateMarker = "private-replay-fixture-canary";
    private const string PublicNote = """Public text mentioning {"schemaVersion":3,"reconstruction":"not a format declaration"}.""";

    public static IEnumerable<object[]> MalformedStoredReplays()
    {
        foreach (var rowVersion in new[] { 1, 2 })
        foreach (var shape in new[]
        {
            "root-eof-schema-first", "root-eof-schema-last", "root-eof-escaped-fields",
            "eof-before-root-schema", "unterminated-private-value", "invalid-escape-after-private",
            "trailing-document", "array-private-prefix", "empty", "object-prefix",
            "truncated-v1", "truncated-v2"
        })
            yield return [rowVersion, shape];
    }

    [Theory]
    [MemberData(nameof(MalformedStoredReplays))]
    public async Task StaleLegacyColumn_ParseFailureReturnsBoundedErrorWithoutPayloadOrParserDetails(
        int rowVersion, string shape)
    {
        var payload = MalformedPayload(shape);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(payload));
        await using var fixture = await ReplayRuntimeFixture.Create();
        var logs = new ReplayLogCapture();
        fixture.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        var id = await Store(fixture, rowVersion, payload);

        using var response = await fixture.Client.GetAsync($"/api/games/{id}/replay");
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        var hasEvents = body.RootElement.TryGetProperty("events", out var events);
        var rawPayloadEchoed = hasEvents && events.ValueKind == JsonValueKind.String && events.GetString() == payload;
        output.WriteLine(JsonSerializer.Serialize(new
        {
            rowVersion, shape,
            status = (int)response.StatusCode,
            payloadBytes = Encoding.UTF8.GetByteCount(payload),
            responseBytes = Encoding.UTF8.GetByteCount(text),
            hasEvents, rawPayloadEchoed,
            privateMarkerExposed = text.Contains(PrivateMarker, StringComparison.Ordinal),
            reconstructionTokenExposed = text.Contains("reconstruction", StringComparison.Ordinal),
            parserExceptionLogged = logs.Entries.Any(entry => entry.Exception is not null)
        }));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, 256);
        Assert.Equal(new[] { "error", "gameId" },
            body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("invalid-replay-format", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(id.ToString(), body.RootElement.GetProperty("gameId").GetString());
        Assert.False(hasEvents);
        Assert.False(rawPayloadEchoed);
        Assert.DoesNotContain(PrivateMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain("reconstruction", text, StringComparison.OrdinalIgnoreCase);
        var warning = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Null(warning.Exception);
        Assert.InRange(Encoding.UTF8.GetByteCount(warning.Message), 1, 256);
        Assert.DoesNotContain(PrivateMarker, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("reconstruction", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task GenuineLegacy_WithFormatWordsInPublicText_PreservesStoredOrderAndDefaults(int version)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var payload = LegacyPayload(version);
        var id = await Store(fixture, version, payload);

        using var response = await fixture.Client.GetAsync($"/api/games/{id}/replay");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(version, body.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("reconstruction", out _));
        var events = body.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(new[] { 57, 1 }, events.Select(item => item.GetProperty("turn").GetInt32()));
        Assert.All(events, item =>
        {
            Assert.Equal(PublicNote, item.GetProperty("detail").GetString());
            Assert.Equal("unknown", item.GetProperty("source").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("durationMs").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("debugScore").ValueKind);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidCurrent_WithLateOrEscapedMetadata_ProjectsOnlyPublicEvents(bool escaped)
    {
        await using var fixture = await ReplayRuntimeFixture.Create();
        var current = CurrentEnvelope(metadataLast: true);
        var payload = escaped ? EscapedCurrentEnvelope(current) : current.ToJsonString();
        var id = await Store(fixture, 1, payload);

        using var response = await fixture.Client.GetAsync($"/api/games/{id}/replay");
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(PrivateMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain("reconstruction", text, StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(text);
        Assert.Equal(ChangshaGameReplay.CurrentSchemaVersion, body.RootElement.GetProperty("schemaVersion").GetInt32());
        var events = body.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(new long[] { 1, 2 }, events.Select(item => item.GetProperty("sequence").GetInt64()));
        Assert.Equal(new[] { 0, 1 }, events.Select(item => item.GetProperty("turn").GetInt32()));
    }

    private static string MalformedPayload(string shape)
    {
        var current = CurrentEnvelope(metadataLast: shape == "root-eof-schema-last");
        var json = current.ToJsonString();
        Assert.Contains(PrivateMarker, json, StringComparison.Ordinal);
        return shape switch
        {
            "root-eof-schema-first" or "root-eof-schema-last" => json[..^1],
            "root-eof-escaped-fields" => EscapedCurrentEnvelope(current)[..^1],
            "eof-before-root-schema" => PrefixBeforeRootSchema(current),
            "unterminated-private-value" => json[..(json.IndexOf(PrivateMarker, StringComparison.Ordinal) + PrivateMarker.Length)],
            "invalid-escape-after-private" => json[..^1] + ",\"note\":\"\\q\"}",
            "trailing-document" => json + "{}",
            "array-private-prefix" => "[" + current["reconstruction"]!.ToJsonString(),
            "empty" => "",
            "object-prefix" => "{",
            "truncated-v1" => LegacyPayload(1)[..^1],
            "truncated-v2" => LegacyPayload(2)[..^1],
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
    }

    private static JsonObject CurrentEnvelope(bool metadataLast)
    {
        var driver = new ReplayTestDriver(17);
        driver.Do(ReplayOperation.BindHumanSeat, new() { SeatIndex = 0, PlayerId = PrivateMarker });
        driver.Do(ReplayOperation.StartGame);
        driver.AssertVerified();
        var events = JsonNode.Parse(
            """[{"sequence":2,"turn":1,"action":"game-started"},{"sequence":1,"turn":0,"action":"game-created"}]""");
        var reconstruction = JsonSerializer.SerializeToNode(driver.Export(), ChangshaReplayStateCodec.RecordJson);
        return metadataLast
            ? new JsonObject
            {
                ["reconstruction"] = reconstruction,
                ["events"] = events,
                ["schemaVersion"] = ChangshaGameReplay.CurrentSchemaVersion
            }
            : new JsonObject
            {
                ["schemaVersion"] = ChangshaGameReplay.CurrentSchemaVersion,
                ["events"] = events,
                ["reconstruction"] = reconstruction
            };
    }

    private static string EscapedCurrentEnvelope(JsonObject current) =>
        "{\"reconstr\\u0075ction\":" + current["reconstruction"]!.ToJsonString()
        + ",\"events\":" + current["events"]!.ToJsonString()
        + ",\"\\u0073chemaVersion\":" + ChangshaGameReplay.CurrentSchemaVersion + "}";

    private static string PrefixBeforeRootSchema(JsonObject current)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WritePropertyName("reconstruction");
        current["reconstruction"]!.WriteTo(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string LegacyPayload(int version)
    {
        var events = JsonSerializer.SerializeToNode(new[]
        {
            new { turn = 57, phase = "EndHand", actor = -1, action = "draw-hand",
                tilesJson = "[]", timestampUtc = DateTime.UnixEpoch, detail = PublicNote },
            new { turn = 1, phase = "Setup", actor = -1, action = "dice-rolled",
                tilesJson = "[]", timestampUtc = DateTime.UnixEpoch.AddSeconds(1), detail = PublicNote }
        });
        return version == 1
            ? events!.ToJsonString()
            : new JsonObject { ["schemaVersion"] = 2, ["events"] = events, ["reconstruction"] = null }.ToJsonString();
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

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class ReplayLogCapture : ILoggerProvider, ILogger
    {
        internal ConcurrentQueue<LogEntry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(ChangshaReplayController).FullName ? this : NullLogger.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(new(logLevel, formatter(state, exception), exception));
        public void Dispose() { }
    }
}
