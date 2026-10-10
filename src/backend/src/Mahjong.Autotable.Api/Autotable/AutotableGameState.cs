using System.Collections.Concurrent;
using System.Text.Json;

namespace Mahjong.Autotable.Api.Autotable;

/// <summary>
/// Per-game collaborative state store for the autotable WS relay.
///
/// <para><b>Phase C-relay role:</b> the .NET backend acts as a broadcast hub —
/// each connected bundle submits collection deltas (<c>things</c>, <c>seats</c>,
/// <c>nicks</c>, <c>match</c>, <c>mouse</c>, <c>sound</c>, <c>dice</c>) as an
/// <c>UPDATE</c> and applies the server's confirmation. This class stores the latest value of each
/// (kind, key) pair so that a late-joining bundle can replay the full state
/// on <c>JOINED</c>. The relay confirms accepted deltas to the sender and broadcasts
/// them to other connections in the same gameId — see <see cref="AutotableConnectionManager"/>.</para>
///
/// <para><b>Upstream parity (server/game.ts):</b> mirrors upstream's
/// <c>Map&lt;string, Map&lt;string|number, any&gt;&gt; collections</c> with the
/// same <c>ephemeral</c> / <c>unique</c> / <c>perPlayer</c> meta-collection
/// semantics. Ephemeral collections (<c>sound</c>, <c>dice</c>) are
/// broadcast-only — never stored — so a late joiner never replays a stale
/// dice-roll animation. <c>unique</c> field tracking is preserved for the
/// snapshot (so the bundle can re-establish slot uniqueness) but the relay
/// does not enforce conflicts in Phase C-relay (Phase D-backend will own
/// rules-level adjudication).</para>
///
/// <para><b>Concurrency:</b> instances are thread-safe via per-game lock around
/// <see cref="ApplyUpdate"/> and <see cref="Snapshot"/>. Broadcast to other
/// connections is performed outside the lock by the caller.</para>
/// </summary>
/// <summary>
/// Identifies who wrote a given collection entry into the per-game store. Used by
/// <see cref="AutotableGameState.ApplyUpdate(System.Collections.Generic.IEnumerable{CollectionEntry}, UpdateSource)"/>
/// to enforce runtime-vs-client precedence per Bishop Phase D-backend decision #1 — the Changsha
/// runtime is authoritative; client UPDATEs cannot overwrite a key the runtime owns.
/// </summary>
public enum UpdateSource
{
    /// <summary>The autotable bundle pushed this entry via inbound WS UPDATE. Advisory only.</summary>
    Client = 0,
    /// <summary>The Changsha rules engine pushed this entry via the translator. Authoritative.</summary>
    Runtime = 1
}

public sealed class AutotableGameState
{
    private readonly Dictionary<string, Dictionary<object, JsonElement>> _collections = new(StringComparer.Ordinal);
    // Source attribution per (kind, normalizedKey). Absent entries default to Client.
    private readonly Dictionary<string, Dictionary<object, UpdateSource>> _sources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ephemeralKinds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uniqueFields = new(StringComparer.Ordinal);
    private readonly HashSet<string> _perPlayerKinds = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public string GameId { get; }

    public AutotableGameState(string gameId)
    {
        GameId = gameId;
    }

    /// <summary>
    /// Applies the given entries to the stored collections and returns the
    /// list to broadcast. Ephemeral kinds are NOT stored but ARE included in
    /// the broadcast list. Entries with <c>value == null</c> remove the key
    /// (upstream's tombstone semantics).
    /// <para><b>Precedence (Bishop Phase D-backend §1):</b> when <paramref name="source"/>
    /// is <see cref="UpdateSource.Client"/>, an entry is REJECTED if the existing entry's
    /// source is <see cref="UpdateSource.Runtime"/> — the rules engine wins. Rejected entries
    /// are excluded from the returned broadcast list so peers don't see speculative client
    /// state. <see cref="UpdateSource.Runtime"/> always overwrites regardless of prior source.</para>
    /// </summary>
    /// <remarks>
    /// Mirrors upstream <c>server/game.ts:update()</c>. The meta-collections
    /// (<c>ephemeral</c>, <c>unique</c>, <c>perPlayer</c>) themselves are
    /// stored as ordinary collections so a late joiner re-receives the
    /// metadata declarations — that lets a non-first bundle still know which
    /// kinds are ephemeral, unique, or per-player. Their side-effect on the
    /// meta-tables takes effect immediately for subsequent entries in the
    /// same call.
    /// </remarks>
    public IReadOnlyList<CollectionEntry> ApplyUpdate(
        IEnumerable<CollectionEntry> entries,
        UpdateSource source = UpdateSource.Client)
    {
        if (entries is null) return Array.Empty<CollectionEntry>();

        var applied = new List<CollectionEntry>();
        lock (_lock)
        {
            foreach (var entry in entries)
            {
                if (entry is null || string.IsNullOrEmpty(entry.Kind)) continue;

                // Meta side-effects come first so a single transaction that
                // declares (ephemeral, X) and then writes to X correctly
                // treats X as ephemeral for the latter entry.
                ApplyMetaSideEffect(entry);

                // Ephemeral collections are broadcast but not stored.
                if (!_ephemeralKinds.Contains(entry.Kind))
                {
                    var collection = GetOrCreate(entry.Kind);
                    var sourceMap = GetOrCreateSourceMap(entry.Kind);
                    var normalizedKey = NormalizeKey(entry.Key);

                    // Client cannot overwrite a Runtime-owned entry — the rules
                    // engine has the authoritative state.
                    if (source == UpdateSource.Client
                        && sourceMap.TryGetValue(normalizedKey, out var existing)
                        && existing == UpdateSource.Runtime)
                    {
                        continue;
                    }

                    if (entry.Value is null
                        || (entry.Value is JsonElement je && je.ValueKind == JsonValueKind.Null))
                    {
                        collection.Remove(normalizedKey);
                        sourceMap.Remove(normalizedKey);
                    }
                    else
                    {
                        collection[normalizedKey] = CloneValue(entry.Value);
                        sourceMap[normalizedKey] = source;
                    }
                }

                applied.Add(entry);
            }
        }
        return applied;
    }

    /// <summary>
    /// Returns the full snapshot of all stored collections as a flat entry
    /// list suitable for an outbound <c>UPDATE { full: true }</c>.
    /// Ephemeral collections (which were never stored) are omitted.
    /// </summary>
    public IReadOnlyList<CollectionEntry> Snapshot()
    {
        lock (_lock)
        {
            var result = new List<CollectionEntry>();
            foreach (var (kind, collection) in _collections)
            {
                foreach (var (key, value) in collection)
                {
                    result.Add(new CollectionEntry(kind, key, value));
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="kind"/> has been declared ephemeral
    /// by a prior <c>['ephemeral', kind, true]</c> meta-side-effect. Lets
    /// callers (notably the runtime broadcast path) include ephemeral
    /// entries in a full snapshot since they are deliberately dropped by
    /// <see cref="Snapshot"/>.
    /// </summary>
    public bool IsEphemeral(string kind)
    {
        lock (_lock)
        {
            return _ephemeralKinds.Contains(kind);
        }
    }

    /// <summary>
    /// Test/diagnostic hook: returns the number of stored entries for a specific
    /// collection <paramref name="kind"/>. Lets integration tests wait on a
    /// deterministic per-kind count instead of an aggregate count that may already
    /// be satisfied by translator-emitted initial state.
    /// </summary>
    public int CountFor(string kind)
    {
        if (string.IsNullOrEmpty(kind)) return 0;
        lock (_lock)
        {
            return _collections.TryGetValue(kind, out var collection) ? collection.Count : 0;
        }
    }

    /// <summary>
    /// Removes <paramref name="playerId"/> from every per-player collection
    /// (<c>seats</c>, <c>nicks</c>, <c>mouse</c>, …) and returns the resulting
    /// tombstone entries to broadcast. Mirrors upstream <c>leave()</c>.
    /// </summary>
    public IReadOnlyList<CollectionEntry> RemovePlayerEntries(string playerId)
    {
        if (string.IsNullOrEmpty(playerId)) return Array.Empty<CollectionEntry>();

        var tombstones = new List<CollectionEntry>();
        lock (_lock)
        {
            foreach (var kind in _perPlayerKinds)
            {
                if (!_collections.TryGetValue(kind, out var collection)) continue;
                if (!collection.ContainsKey(playerId)) continue;
                collection.Remove(playerId);
                if (_sources.TryGetValue(kind, out var sourceMap))
                    sourceMap.Remove(playerId);
                tombstones.Add(new CollectionEntry(kind, playerId, null));
            }
        }
        return tombstones;
    }

    /// <summary>
    /// Test/diagnostic accessor: reports which source last wrote a given
    /// (kind, key) entry, or null if the entry is absent. Useful for runtime-vs-client
    /// precedence assertions and the per-viewer privacy filter (Runtime-owned `things`
    /// entries are filtered server-side; Client-owned passes through unchanged because
    /// the bundle that pushed them already saw the same data).
    /// </summary>
    public UpdateSource? GetSource(string kind, object key)
    {
        if (string.IsNullOrEmpty(kind)) return null;
        var normalizedKey = NormalizeKey(key);
        lock (_lock)
        {
            if (_sources.TryGetValue(kind, out var sourceMap) && sourceMap.TryGetValue(normalizedKey, out var s))
                return s;
            return null;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────

    private Dictionary<object, UpdateSource> GetOrCreateSourceMap(string kind)
    {
        if (!_sources.TryGetValue(kind, out var map))
        {
            map = new Dictionary<object, UpdateSource>();
            _sources[kind] = map;
        }
        return map;
    }

    private void ApplyMetaSideEffect(CollectionEntry entry)
    {
        if (entry.Kind == "ephemeral" && entry.Key is string ephemeralKind)
        {
            var truthy = IsTruthy(entry.Value);
            if (truthy) _ephemeralKinds.Add(ephemeralKind);
            else _ephemeralKinds.Remove(ephemeralKind);
        }
        else if (entry.Kind == "perPlayer" && entry.Key is string perPlayerKind)
        {
            var truthy = IsTruthy(entry.Value);
            if (truthy) _perPlayerKinds.Add(perPlayerKind);
            else _perPlayerKinds.Remove(perPlayerKind);
        }
        else if (entry.Kind == "unique" && entry.Key is string uniqueKind)
        {
            if (entry.Value is JsonElement je && je.ValueKind == JsonValueKind.String)
            {
                _uniqueFields[uniqueKind] = je.GetString() ?? string.Empty;
            }
            else if (entry.Value is string field)
            {
                _uniqueFields[uniqueKind] = field;
            }
        }
    }

    private Dictionary<object, JsonElement> GetOrCreate(string kind)
    {
        if (!_collections.TryGetValue(kind, out var collection))
        {
            collection = new Dictionary<object, JsonElement>();
            _collections[kind] = collection;
        }
        return collection;
    }

    /// <summary>
    /// Normalises keys so that integer keys parsed from the wire compare
    /// equal regardless of whether they arrived as <c>long</c>, <c>int</c>,
    /// or numeric <c>JsonElement</c>. The autotable bundle uses both numeric
    /// (<c>things[42]</c>) and string keys (<c>seats["abc123"]</c>) — we
    /// preserve that distinction but coerce all integral numerics to
    /// <c>long</c> internally so the dictionary lookup is stable.
    /// </summary>
    private static object NormalizeKey(object key) => key switch
    {
        int i => (long)i,
        long l => l,
        short s => (long)s,
        double d when d == Math.Truncate(d) => (long)d,
        string s => s,
        JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt64(out var l) => l,
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? string.Empty,
        _ => key
    };

    private static JsonElement CloneValue(object? value)
    {
        if (value is JsonElement je) return je.Clone();
        // Fallback path — re-serialise so the stored form is a JsonElement.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, AutotableJson.Options);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        JsonElement je => je.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => false,
            JsonValueKind.Number => je.TryGetDouble(out var d) && d != 0,
            JsonValueKind.String => !string.IsNullOrEmpty(je.GetString()),
            _ => true
        },
        _ => true
    };
}
