using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Mahjong.Autotable.Api.RateLimiting;

/// <summary>
/// Phase J Wave 6 — production rate limiting (Apone, DevOps).
///
/// <para>Wires two named policies on top of <c>Microsoft.AspNetCore.RateLimiting</c>
/// (the framework's built-in middleware since .NET 7). Both policies are
/// partitioned by client IP via <see cref="RateLimitPartition"/> so the
/// stated quotas ("10 req / min / IP", "300 req / min / IP burst 30")
/// apply per remote caller, not as a single global bucket.</para>
///
/// <list type="bullet">
///   <item><see cref="AnonymousPolicy"/> (<c>fixed-window-anonymous</c>) —
///     guards low-volume unauthenticated mutations such as profile creation.
///     10 requests per IP per minute, fixed window.</item>
///   <item><see cref="ApiPolicy"/> (<c>token-bucket-api</c>) — generous
///     allowance for the rest of <c>/api/**</c>. 30-token bucket
///     replenishing at 5 tokens/sec, so the steady-state ceiling is
///     ~300 req/min/IP with a 30-request burst.</item>
/// </list>
///
/// <para><b>Test / dev safety.</b> The middleware only registers when
/// <c>RateLimiting:Enabled</c> is <c>true</c> in configuration. The base
/// <c>appsettings.json</c> sets it to <c>false</c> (so <c>Development</c>
/// and the xUnit <c>WebApplicationFactory</c> harness — which runs under
/// the <c>Development</c> environment — are unaffected). The
/// <c>appsettings.Production.json</c> override flips it to <c>true</c>.</para>
///
/// <para><b>What's not rate limited.</b> The Docker / k8s probe surface
/// (<c>/health</c>, <c>/api/health</c>), the Prometheus scrape surface
/// (<c>/metrics</c>), and the long-lived transport surfaces
/// (<c>/hubs/changsha</c> SignalR + <c>/autotable/ws</c> raw WebSocket).
/// Endpoints opt into rate limiting via
/// <see cref="Microsoft.AspNetCore.Builder.RateLimiterEndpointConventionBuilderExtensions.RequireRateLimiting{TBuilder}(TBuilder, string)"/>
/// in <c>Program.cs</c>; everything else is explicitly off-policy.</para>
///
/// <para>See <c>docs/deployment.md</c> § "Rate limiting" for the operator
/// runbook (how to disable for stress tests, how the 429 response is
/// shaped, how the policies map to endpoints).</para>
/// </summary>
public static class RateLimitingExtensions
{
    /// <summary>Policy name for unauthenticated mutating endpoints (currently the future <c>POST /api/identity</c> profile-create surface).</summary>
    public const string AnonymousPolicy = "fixed-window-anonymous";

    /// <summary>Policy name applied to the rest of <c>/api/**</c> (controllers + minimal-API endpoints).</summary>
    public const string ApiPolicy = "token-bucket-api";

    /// <summary>
    /// Phase K Wave 4 — Bishop. Per-IP policy for the unauthenticated
    /// <c>POST /api/auth/validate</c> token-introspection endpoint.
    /// Fixed-window limiter capped at 100 requests / minute / IP — the
    /// brief target. Tight enough to deter a brute-force signature
    /// attack while loose enough to support legitimate machine-to-
    /// machine validation traffic.
    /// </summary>
    public const string AuthValidatePolicy = "fixed-window-auth-validate";

    /// <summary>
    /// Configuration key whose boolean value gates the entire middleware.
    /// Default <c>false</c> in <c>appsettings.json</c>; flipped to
    /// <c>true</c> by <c>appsettings.Production.json</c>.
    /// </summary>
    public const string EnabledConfigKey = "RateLimiting:Enabled";

    /// <summary>
    /// Registers <see cref="Microsoft.AspNetCore.RateLimiting"/> services
    /// and the two named policies, gated by
    /// <see cref="EnabledConfigKey"/>. Safe to call unconditionally — if
    /// the gate is off no middleware is wired, but the policy names still
    /// resolve so calls to <c>.RequireRateLimiting("…")</c> in
    /// <c>Program.cs</c> don't crash a Development boot.
    /// </summary>
    /// <returns><c>true</c> when the middleware was registered (the
    /// caller should also call <c>app.UseRateLimiter()</c> on the
    /// resulting pipeline); <c>false</c> when the gate is off.</returns>
    public static bool AddMahjongRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool?>(EnabledConfigKey) ?? false;
        if (!enabled)
        {
            return false;
        }

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = static (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }
                context.HttpContext.Response.ContentType = "application/json";
                return new ValueTask(context.HttpContext.Response.WriteAsync(
                    "{\"error\":\"too_many_requests\"}", cancellationToken));
            };

            options.AddPolicy(AnonymousPolicy, httpContext =>
            {
                var key = ResolvePartitionKey(httpContext);
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });
            });

            options.AddPolicy(ApiPolicy, httpContext =>
            {
                var key = ResolvePartitionKey(httpContext);
                return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 30,
                    TokensPerPeriod = 5,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });
            });

            // Phase K Wave 4 — Bishop. Validate endpoint: 100 / min / IP.
            // Fixed window (rather than token bucket) so the burst
            // semantics are predictable for downstream M2M callers.
            options.AddPolicy(AuthValidatePolicy, httpContext =>
            {
                var key = ResolvePartitionKey(httpContext);
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });
            });
        });

        return true;
    }

    private static string ResolvePartitionKey(HttpContext context)
    {
        // Honor X-Forwarded-For when set by a trusted reverse proxy. Kestrel
        // populates Connection.RemoteIpAddress with the *proxy* IP unless
        // ForwardedHeaders middleware has been wired — in that case
        // RemoteIpAddress is the real client. The XFF fallback here keeps the
        // partition key stable even when forwarded-headers config slips.
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var comma = forwarded.IndexOf(',');
            return (comma >= 0 ? forwarded[..comma] : forwarded).Trim();
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
