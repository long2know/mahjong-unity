using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mahjong.Autotable.Api.Changsha.Replay;

internal static class ChangshaReplayStateCodec
{
    internal const string CodecId = "changsha-full-snapshot-json-sha256-v2";
    internal const string ProfileId = "changsha-current-spec-pure-2026-09-14";

    internal static JsonSerializerOptions SnapshotJson { get; } = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static JsonSerializerOptions RecordJson { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private static readonly Lazy<ReplayEngineIdentity> Identity = new(() => new(
        FileHash(typeof(ChangshaGameStateMachine).Assembly.Location),
        FileHash(typeof(object).Assembly.Location),
        ProfileId, CodecId, ChangshaGameStateMachine.RngAlgorithmId));

    internal static ReplayEngineIdentity EngineIdentity => Identity.Value;
    internal static string Serialize(ChangshaGameState state) => JsonSerializer.Serialize(state, SnapshotJson);
    internal static string Hash(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    internal static ReplayCheckpoint Checkpoint(ChangshaGameState state) =>
        new(Hash(Serialize(state)), state.Phase, state.HandNumber, state.TurnNumber,
            state.StateVersion, state.EventSequence);
    internal static ChangshaGameState RoundTrip(ChangshaGameState state) =>
        JsonSerializer.Deserialize<ChangshaGameState>(Serialize(state), SnapshotJson)
        ?? throw new InvalidOperationException("A state snapshot cannot be null.");
    internal static T CopyRecord<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, RecordJson), RecordJson)
        ?? throw new InvalidOperationException("A replay record cannot be null.");
    internal static string SerializeRecord<T>(T value) => JsonSerializer.Serialize(value, RecordJson);

    private static string FileHash(string path) =>
        string.IsNullOrEmpty(path)
            ? throw new InvalidOperationException("Replay requires an identifiable loaded assembly.")
            : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
