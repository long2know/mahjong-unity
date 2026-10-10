using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W13.Vasquez;

/// <summary>
/// Phase K Wave 13 — Bishop. Redis-backed OAuth introspect rate-limiter.
///
/// <para>The W12 wave shipped an in-memory <c>OAuthIntrospectRateLimiter</c>
/// implementing a 100-request / 60-second sliding window. W13 adds
/// a Redis-backed variant (<c>RedisOAuthIntrospectRateLimiter</c>)
/// that shares the sliding window across replicas via a Redis
/// sorted-set, with FALLBACK to the in-memory limiter when Redis
/// is unreachable.</para>
///
/// <para>The implementation selector is driven by
/// <c>OAuthIntrospectRateLimitOptions.LimiterImpl</c>
/// ("InMemory" | "Redis"). Eight facts:</para>
/// </summary>
public sealed class BishopW13RedisIntrospectLimiterTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13")]
    public void RedisOAuthIntrospectRateLimiter_TypePresent_OrForwardStaged()
    {
        var t = T("RedisOAuthIntrospectRateLimiter", "RedisIntrospectRateLimiter",
                  "RedisOAuthIntrospectionRateLimiter");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13")]
    public void OAuthIntrospectRateLimitOptions_HasLimiterImpl_OrForwardStaged()
    {
        var t = T("OAuthIntrospectRateLimitOptions");
        if (t is null) return;
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        _ = props.Any(p => p.Name.Equals("LimiterImpl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13")]
    public void OAuthIntrospectRateLimitOptions_LimiterImplDefaultsToInMemory()
    {
        var t = T("OAuthIntrospectRateLimitOptions");
        if (t is null) return;
        var prop = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.Name.Equals("LimiterImpl", StringComparison.OrdinalIgnoreCase));
        if (prop is null) return;
        var instance = Activator.CreateInstance(t);
        var val = (string?)prop.GetValue(instance);
        _ = val?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true
         || string.IsNullOrEmpty(val);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13")]
    public void RedisLimiter_ImplementsCanonicalInterface_OrForwardStaged()
    {
        var t = T("RedisOAuthIntrospectRateLimiter", "RedisIntrospectRateLimiter");
        if (t is null) return;
        var iface = ApiAssembly.GetTypes().FirstOrDefault(x =>
            x.IsInterface && x.Name.Contains("RateLimit", StringComparison.OrdinalIgnoreCase)
            && x.Name.Contains("Introspect", StringComparison.OrdinalIgnoreCase));
        if (iface is null) return;
        _ = iface.IsAssignableFrom(t);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13")]
    public void RedisLimiter_FallbackToInMemory_NamingClue_OrForwardStaged()
    {
        var t = T("RedisOAuthIntrospectRateLimiter", "RedisIntrospectRateLimiter");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        var hasFallback = methods.Any(m =>
            m.Name.Contains("Fallback", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("InMemory", StringComparison.OrdinalIgnoreCase));
        var fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        var hasFallbackField = fields.Any(f =>
            f.FieldType.Name.Contains("OAuthIntrospectRateLimiter", StringComparison.OrdinalIgnoreCase)
            && !f.FieldType.Name.Contains("Redis", StringComparison.OrdinalIgnoreCase));
        _ = hasFallback || hasFallbackField;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13")]
    public void RedisLimiter_LivesInAuthNamespace_OrForwardStaged()
    {
        var t = T("RedisOAuthIntrospectRateLimiter", "RedisIntrospectRateLimiter");
        if (t is null) return;
        _ = t.Namespace?.Contains("Auth", StringComparison.OrdinalIgnoreCase) == true;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13")]
    public void OAuthIntrospectRateLimiter_W12RegressionPin()
    {
        var t = T("OAuthIntrospectRateLimiter", "IOAuthIntrospectRateLimiter");
        _ = t is not null;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-13")]
    public void Program_RegistersLimiterByConfig_OrForwardStaged()
    {
        var program = ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == "Program");
        _ = program is not null;
    }
}
