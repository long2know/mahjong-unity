using Mahjong.Autotable.Api.Auth;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W12.Bishop;

/// <summary>
/// Phase K Wave 12 — Bishop. Hard-asserted contract for the
/// OAuth introspect rate-limiter
/// (<see cref="OAuthIntrospectRateLimiter"/>).
///
/// <list type="number">
///   <item><see cref="IOAuthIntrospectRateLimiter"/> exists.</item>
///   <item><see cref="OAuthIntrospectRateLimitOptions"/> default
///         RateLimitPerClient = 100.</item>
///   <item><see cref="OAuthIntrospectRateLimitOptions"/> default
///         WindowSeconds = 60.</item>
///   <item>First call returns Allowed=true.</item>
///   <item>Remaining decrements per call.</item>
///   <item>Exceeding the cap returns Allowed=false.</item>
///   <item>RetryAfter is &gt; 0 on a deny.</item>
///   <item>The limiter surfaces RequestsPerWindow + WindowSeconds
///         so the controller can stamp the response headers.</item>
///   <item>Separate client ids have independent buckets.</item>
///   <item>Window-elapsed call clears the bucket
///         (sliding-window behaviour).</item>
/// </list>
/// </summary>
public sealed class OAuthIntrospectRateLimitFacts
{
    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void Interface_Exists()
    {
        Assert.NotNull(typeof(IOAuthIntrospectRateLimiter));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void Options_DefaultsCanonical()
    {
        var opts = new OAuthIntrospectRateLimitOptions();
        Assert.Equal(100, opts.RateLimitPerClient);
        Assert.Equal(60, opts.WindowSeconds);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void FirstCall_Allowed()
    {
        var limiter = new OAuthIntrospectRateLimiter(capacity: 5, windowSeconds: 60);
        var verdict = limiter.TryAcquire("client-a", DateTimeOffset.UtcNow);
        Assert.True(verdict.Allowed);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void Remaining_Decrements()
    {
        var limiter = new OAuthIntrospectRateLimiter(capacity: 5, windowSeconds: 60);
        var now = DateTimeOffset.UtcNow;
        var first = limiter.TryAcquire("client-a", now);
        var second = limiter.TryAcquire("client-a", now);
        Assert.True(second.Remaining < first.Remaining);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void Exceeding_Cap_Denies()
    {
        var limiter = new OAuthIntrospectRateLimiter(capacity: 2, windowSeconds: 60);
        var now = DateTimeOffset.UtcNow;
        var a = limiter.TryAcquire("client-a", now);
        var b = limiter.TryAcquire("client-a", now);
        var c = limiter.TryAcquire("client-a", now);
        Assert.True(a.Allowed);
        Assert.True(b.Allowed);
        Assert.False(c.Allowed);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void Deny_Includes_RetryAfter()
    {
        var limiter = new OAuthIntrospectRateLimiter(capacity: 1, windowSeconds: 30);
        var now = DateTimeOffset.UtcNow;
        limiter.TryAcquire("client-a", now);
        var denied = limiter.TryAcquire("client-a", now);
        Assert.False(denied.Allowed);
        Assert.True(denied.RetryAfterSeconds > 0);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void Limiter_Surfaces_LimitAndWindow()
    {
        var limiter = new OAuthIntrospectRateLimiter(capacity: 7, windowSeconds: 45);
        Assert.Equal(7, limiter.RequestsPerWindow);
        Assert.Equal(45, limiter.WindowSeconds);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void Separate_Clients_HaveIndependentBuckets()
    {
        var limiter = new OAuthIntrospectRateLimiter(capacity: 1, windowSeconds: 60);
        var now = DateTimeOffset.UtcNow;
        var aFirst = limiter.TryAcquire("client-a", now);
        var bFirst = limiter.TryAcquire("client-b", now);
        Assert.True(aFirst.Allowed);
        Assert.True(bFirst.Allowed);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-12")]
    public void Window_Elapsed_ClearsBucket()
    {
        var limiter = new OAuthIntrospectRateLimiter(capacity: 1, windowSeconds: 10);
        var start = DateTimeOffset.UtcNow;
        limiter.TryAcquire("client-a", start);
        var denied = limiter.TryAcquire("client-a", start);
        Assert.False(denied.Allowed);
        var afterWindow = limiter.TryAcquire("client-a", start.AddSeconds(15));
        Assert.True(afterWindow.Allowed);
    }
}
