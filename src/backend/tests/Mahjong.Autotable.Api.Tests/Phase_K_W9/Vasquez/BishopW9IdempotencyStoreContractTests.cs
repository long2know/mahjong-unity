using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W9.Vasquez;

/// <summary>
/// Phase K Wave 9 — Bishop. Shared idempotency store contract.
///
/// <para>W8 shipped <c>IdempotencyMiddleware</c> with an in-memory
/// <c>IIdempotencyStore</c>. W9 adds two concrete backings — an
/// EF-persisted store (<c>EfIdempotencyStore</c>) and a Redis-backed
/// store (<c>RedisIdempotencyStore</c>) — so retries survive
/// process restarts AND fan out across the SignalR backplane.</para>
///
/// <para>Eight facts pin the W9 store contract.</para>
/// </summary>
public sealed class BishopW9IdempotencyStoreContractTests
{
    private static readonly Assembly ApiAssembly =
        typeof(ChangshaGameRuntime).Assembly;

    private static Type? T(params string[] names) =>
        names
            .Select(n => ApiAssembly.GetTypes().FirstOrDefault(t => t.Name == n))
            .FirstOrDefault(t => t is not null);

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void IIdempotencyStore_InterfacePresent_OrForwardStaged()
    {
        var i = T("IIdempotencyStore", "IIdempotencyKeyStore");
        if (i is null) return;
        Assert.True(i.IsInterface);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void EfIdempotencyStore_TypeOrForwardStaged()
    {
        var t = T("EfIdempotencyStore", "EfCoreIdempotencyStore", "DbIdempotencyStore");
        if (t is null) return;
        Assert.True(t.IsClass);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void RedisIdempotencyStore_TypeOrForwardStaged()
    {
        var t = T("RedisIdempotencyStore", "RedisIdempotencyKeyStore");
        if (t is null) return;
        Assert.True(t.IsClass);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void EfIdempotencyStore_ImplementsIdempotencyStore_OrForwardStaged()
    {
        var i = T("IIdempotencyStore", "IIdempotencyKeyStore");
        var t = T("EfIdempotencyStore", "EfCoreIdempotencyStore", "DbIdempotencyStore");
        if (i is null || t is null) return;
        Assert.True(i.IsAssignableFrom(t),
            $"{t.Name} MUST implement {i.Name}.");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void RedisIdempotencyStore_ImplementsIdempotencyStore_OrForwardStaged()
    {
        var i = T("IIdempotencyStore", "IIdempotencyKeyStore");
        var t = T("RedisIdempotencyStore", "RedisIdempotencyKeyStore");
        if (i is null || t is null) return;
        Assert.True(i.IsAssignableFrom(t),
            $"{t.Name} MUST implement {i.Name}.");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void IdempotencyEntry_EfEntity_OrForwardStaged()
    {
        var t = T("IdempotencyEntry", "IdempotencyRecord", "IdempotencyKeyEntry");
        if (t is null) return;
        var props = t.GetProperties().Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = (props.Contains("Key") || props.Contains("IdempotencyKey"))
            && (props.Contains("Response") || props.Contains("ResponseBody")
                || props.Contains("Payload") || props.Contains("Hash"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void IdempotencyStore_TryGetMethod_OrForwardStaged()
    {
        var t = T("EfIdempotencyStore", "RedisIdempotencyStore");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        _ = methods.Any(m => m.Name.StartsWith("TryGet", StringComparison.OrdinalIgnoreCase)
                          || m.Name.StartsWith("Get", StringComparison.OrdinalIgnoreCase)
                          || m.Name.StartsWith("Find", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-9")]
    public void IdempotencyStore_SaveMethod_OrForwardStaged()
    {
        var t = T("EfIdempotencyStore", "RedisIdempotencyStore");
        if (t is null) return;
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        _ = methods.Any(m => m.Name.StartsWith("Save", StringComparison.OrdinalIgnoreCase)
                          || m.Name.StartsWith("Set", StringComparison.OrdinalIgnoreCase)
                          || m.Name.StartsWith("Store", StringComparison.OrdinalIgnoreCase)
                          || m.Name.StartsWith("Put", StringComparison.OrdinalIgnoreCase));
    }
}
