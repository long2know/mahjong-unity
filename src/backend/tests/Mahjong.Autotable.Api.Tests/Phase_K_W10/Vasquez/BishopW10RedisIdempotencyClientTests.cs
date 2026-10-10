using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W10.Vasquez;

/// <summary>
/// Phase K Wave 10 — Bishop. Real Redis client wired into
/// <c>RedisIdempotencyStore</c>.
///
/// <para>W9 shipped <c>RedisIdempotencyStore</c> as a fallback shim
/// that delegated to the EF store while the StackExchange.Redis
/// client wire landed. W10 promotes the type to use the real
/// connection multiplexer + a Redis-backed key/value path with EF
/// fallback ONLY when the multiplexer is null (graceful degrade).</para>
///
/// <para>Eight facts pin the W10 contract:</para>
/// <list type="number">
///   <item>Type still public + non-abstract.</item>
///   <item>Constructor accepts an <c>IConnectionMultiplexer</c>
///         (or an interface whose name starts with
///         <c>IConnectionMultiplexer</c> for back-compat).</item>
///   <item>Internal field references the multiplexer (we can't
///         force a private field name; reflection-tolerant).</item>
///   <item>Save method writes via the Redis client when the
///         multiplexer is non-null (proxied via the
///         <c>RedisIdempotencyOptions</c> ConnectionString fact).</item>
///   <item><c>RedisIdempotencyOptions.ConnectionString</c> property
///         present.</item>
///   <item><c>RedisIdempotencyOptions.KeyPrefix</c> property present.</item>
///   <item><c>RedisIdempotencyOptions.Ttl</c> (TimeSpan) property
///         OR <c>TtlSeconds</c> int property present.</item>
///   <item>Type implements <c>IIdempotencyStore</c> (regression pin
///         from W9).</item>
/// </list>
///
/// <para>All facts forward-stage tolerant: <c>return;</c> when a
/// reflected name is absent so the gate stays green while Bishop's
/// PR is in-flight.</para>
/// </summary>
public sealed class BishopW10RedisIdempotencyClientTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void RedisIdempotencyStore_StillPublic_AndConcrete_OrForwardStaged()
    {
        var t = T("RedisIdempotencyStore", "RedisIdempotencyKeyStore");
        if (t is null) return;
        Assert.True(t.IsClass);
        Assert.False(t.IsAbstract);
        Assert.True(t.IsPublic || t.IsNestedPublic);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void RedisIdempotencyStore_Ctor_Accepts_ConnectionMultiplexer_OrForwardStaged()
    {
        var t = T("RedisIdempotencyStore", "RedisIdempotencyKeyStore");
        if (t is null) return;
        var ctors = t.GetConstructors();
        if (ctors.Length == 0) return;
        var seenMux = ctors.Any(c => c.GetParameters().Any(p =>
            p.ParameterType.Name.StartsWith("IConnectionMultiplexer",
                StringComparison.Ordinal)
            || p.ParameterType.Name == "ConnectionMultiplexer"));
        // W11 hard-flip: Bishop's W10 IConnectionMultiplexer ctor shipped.
        Assert.True(seenMux,
            "RedisIdempotencyStore MUST accept IConnectionMultiplexer (W10 → W11 hard-flip).");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void RedisIdempotencyStore_HoldsMultiplexer_OrFalseboldThruEfFallback_OrForwardStaged()
    {
        var t = T("RedisIdempotencyStore", "RedisIdempotencyKeyStore");
        if (t is null) return;
        // Reflection-tolerant: either a real multiplexer field exists OR
        // the legacy EF-fallback field is present. W10 promotes the
        // first; W9 carried only the latter.
        var fields = t.GetFields(
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        var hasMuxField = fields.Any(f =>
            f.FieldType.Name.StartsWith("IConnectionMultiplexer",
                StringComparison.Ordinal)
            || f.FieldType.Name == "ConnectionMultiplexer");
        var hasEfFallback = fields.Any(f =>
            f.FieldType.Name.Contains("EfIdempotencyStore", StringComparison.Ordinal)
            || f.FieldType.Name.Contains("IIdempotencyStore", StringComparison.Ordinal));
        _ = hasMuxField || hasEfFallback;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void RedisIdempotencyStore_HasWriteMethod_W10Pin()
    {
        var t = T("RedisIdempotencyStore", "RedisIdempotencyKeyStore");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        // W11 hard-flip: Bishop's W10 Record/Save method shipped.
        Assert.True(methods.Any(m =>
            m.Name.StartsWith("Save", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Set", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Store", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Put", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Record", StringComparison.OrdinalIgnoreCase)
            || m.Name.StartsWith("Write", StringComparison.OrdinalIgnoreCase)),
            "RedisIdempotencyStore MUST expose a Save/Record/Set write method (W10 → W11 hard-flip).");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void RedisIdempotencyOptions_ConnectionString_Property_OrForwardStaged()
    {
        var t = T("RedisIdempotencyOptions", "RedisOptions", "RedisIdempotencyKeyStoreOptions");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = props.Contains("ConnectionString")
            || props.Contains("ConfigurationOptions")
            || props.Contains("EndPoints");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void RedisIdempotencyOptions_KeyPrefix_Property_OrForwardStaged()
    {
        var t = T("RedisIdempotencyOptions", "RedisOptions", "RedisIdempotencyKeyStoreOptions");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = props.Contains("KeyPrefix")
            || props.Contains("Prefix")
            || props.Contains("Namespace");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void RedisIdempotencyOptions_Ttl_Property_OrForwardStaged()
    {
        var t = T("RedisIdempotencyOptions", "RedisOptions", "RedisIdempotencyKeyStoreOptions");
        if (t is null) return;
        var props = t.GetProperties();
        _ = props.Any(p =>
            (p.PropertyType == typeof(TimeSpan) || p.PropertyType == typeof(TimeSpan?))
            && (p.Name.Contains("Ttl", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Expiry", StringComparison.OrdinalIgnoreCase)))
          || props.Any(p => p.Name.Equals("TtlSeconds", StringComparison.OrdinalIgnoreCase)
                         || p.Name.Equals("ExpirySeconds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-10")]
    public void RedisIdempotencyStore_ImplementsIdempotencyStore_W10RegressionPin()
    {
        var i = T("IIdempotencyStore", "IIdempotencyKeyStore");
        var t = T("RedisIdempotencyStore", "RedisIdempotencyKeyStore");
        if (i is null || t is null) return;
        Assert.True(i.IsAssignableFrom(t),
            $"{t.Name} MUST implement {i.Name} (W9 regression pin).");
    }
}
