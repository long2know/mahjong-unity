using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Sentry;
using Sentry.AspNetCore;
using Sentry.Extensibility;

namespace Mahjong.Autotable.Api.Observability;

/// <summary>
/// Phase J Wave 8 — Sentry SDK wiring (Apone, DevOps).
///
/// <para>The Sentry SDK is added unconditionally as a NuGet dependency
/// but only initialised when <c>Sentry:Dsn</c> is configured to a
/// non-empty value. With no DSN the SDK becomes a no-op: <c>SentrySdk.Init</c>
/// is never called and no network I/O is performed. This is the
/// contract the xUnit harness relies on — <c>WebApplicationFactory&lt;Program&gt;</c>
/// boots under <c>Development</c> with the default empty DSN, so every
/// integration test runs Sentry-free.</para>
///
/// <para>Configuration shape (<c>appsettings.json</c> § <c>Sentry</c>):</para>
/// <list type="bullet">
///   <item><b>Dsn</b> (string, default <c>""</c>) — Sentry project ingestion URL.
///         Empty disables the SDK entirely.</item>
///   <item><b>Environment</b> (string, default <c>null</c>) — overrides the
///         tag attached to every event. Falls back to <c>ASPNETCORE_ENVIRONMENT</c>
///         when null. Production deploys should set this explicitly to
///         distinguish blue/green slots, regional pods, etc.</item>
///   <item><b>SampleRate</b> (double, default <c>1.0</c>) — error-event
///         sample rate (0.0–1.0). 1.0 captures every unhandled exception.</item>
///   <item><b>TracesSampleRate</b> (double, default <c>0.0</c>) —
///         performance/tracing sample rate. Default off because tracing
///         spans add per-request overhead that dwarfs the SignalR hub's
///         tight latency budget.</item>
///   <item><b>EnableLogs</b> (bool, default <c>false</c>) — when true,
///         ingest <c>ILogger</c> entries at Warning+ as Sentry events.
///         Breadcrumbs at Information+ are always captured (when the
///         SDK is enabled at all).</item>
/// </list>
///
/// <para>The hub-method breadcrumb capture lives in
/// <see cref="SentryHubFilter"/>; <c>Program.cs</c> wires it via
/// <c>AddSignalR(o =&gt; o.AddFilter&lt;SentryHubFilter&gt;())</c>.
/// </para>
///
/// <para><b>Test no-op contract.</b> See
/// <c>SentryConfigurationTests.AddMahjongSentry_NoDsn_DoesNotEnableSdk</c>
/// — the <c>Sentry:Dsn</c>-unset branch returns false and never invokes
/// <c>UseSentry</c> on the host builder.</para>
/// </summary>
public static class SentryConfiguration
{
    /// <summary>Configuration key whose presence + non-empty value gates the SDK.</summary>
    public const string DsnConfigKey = "Sentry:Dsn";

    /// <summary>Configuration key for the explicit environment override.</summary>
    public const string EnvironmentConfigKey = "Sentry:Environment";

    /// <summary>Configuration key for the error-event sample rate (0.0–1.0).</summary>
    public const string SampleRateConfigKey = "Sentry:SampleRate";

    /// <summary>Configuration key for the performance/tracing sample rate (0.0–1.0).</summary>
    public const string TracesSampleRateConfigKey = "Sentry:TracesSampleRate";

    /// <summary>Configuration key for breadcrumb-from-ILogger ingestion.</summary>
    public const string EnableLogsConfigKey = "Sentry:EnableLogs";

    /// <summary>
    /// Wire Sentry into the web-host pipeline iff <c>Sentry:Dsn</c> is set.
    /// Safe to call unconditionally — when the DSN is absent the host
    /// builder is returned untouched so test/dev boots stay Sentry-free.
    /// </summary>
    /// <returns><c>true</c> when the SDK was initialised; <c>false</c> when
    /// the DSN was absent and Sentry remained a no-op.</returns>
    public static bool AddMahjongSentry(this IWebHostBuilder webHost, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(webHost);
        ArgumentNullException.ThrowIfNull(configuration);

        var dsn = configuration.GetValue<string?>(DsnConfigKey);
        if (string.IsNullOrWhiteSpace(dsn))
        {
            return false;
        }

        webHost.UseSentry(options =>
        {
            options.Dsn = dsn;

            var environment = configuration.GetValue<string?>(EnvironmentConfigKey);
            if (!string.IsNullOrWhiteSpace(environment))
            {
                options.Environment = environment;
            }

            var sampleRate = configuration.GetValue<double?>(SampleRateConfigKey);
            if (sampleRate.HasValue)
            {
                options.SampleRate = (float)Math.Clamp(sampleRate.Value, 0.0, 1.0);
            }

            var tracesRate = configuration.GetValue<double?>(TracesSampleRateConfigKey);
            options.TracesSampleRate = tracesRate.HasValue
                ? Math.Clamp(tracesRate.Value, 0.0, 1.0)
                : 0.0;

            // Send a release identifier so deploys can be correlated against
            // the gh-cr image tag set. BUILD_SHA is already the canonical
            // build identifier for /health and /metrics.
            var sha = System.Environment.GetEnvironmentVariable("BUILD_SHA");
            if (!string.IsNullOrWhiteSpace(sha))
            {
                options.Release = $"mahjong-autotable@{sha}";
            }

            // Default off so the JSON-console logger stays the canonical
            // log surface — operators can opt in per-environment.
            var enableLogs = configuration.GetValue<bool?>(EnableLogsConfigKey) ?? false;
            options.MinimumBreadcrumbLevel = LogLevel.Information;
            options.MinimumEventLevel = enableLogs ? LogLevel.Warning : LogLevel.Error;

            // Strip request bodies (may contain mahjong_pid cookie or
            // future credentials). Sentry's default already redacts
            // headers like Authorization / Cookie but the body path is
            // opt-in. Off here for safety.
            options.MaxRequestBodySize = RequestSize.None;
            options.SendDefaultPii = false;
            options.AttachStacktrace = true;

            options.SetBeforeBreadcrumb((crumb, _hint) => RedactBreadcrumb(crumb));
        });

        return true;
    }

    /// <summary>
    /// Trim cookie / Authorization data from breadcrumbs before they ship.
    /// Sentry already redacts common header names but a defensive sweep
    /// here ensures the <c>mahjong_pid</c> cookie value never leaves the
    /// process. The breadcrumb category convention is <c>http</c> for
    /// outbound calls and <c>signalr</c> for hub-method invocations.
    /// </summary>
    private static Breadcrumb? RedactBreadcrumb(Breadcrumb crumb)
    {
        if (crumb.Data is null || crumb.Data.Count == 0)
        {
            return crumb;
        }

        var needsRedact = false;
        foreach (var key in crumb.Data.Keys)
        {
            if (IsSensitiveKey(key))
            {
                needsRedact = true;
                break;
            }
        }
        if (!needsRedact) return crumb;

        var redacted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in crumb.Data)
        {
            redacted[kvp.Key] = IsSensitiveKey(kvp.Key) ? "[redacted]" : kvp.Value;
        }
        // Preserve all the same fields; the 6-arg constructor that
        // accepts a timestamp is internal-only in this Sentry version
        // so we use the 5-arg variant. The breadcrumb timestamp
        // defaults to "now", which is identical to its emission time
        // since BeforeBreadcrumb runs synchronously.
        return new Breadcrumb(
            crumb.Message ?? string.Empty,
            crumb.Type ?? "default",
            redacted,
            crumb.Category,
            crumb.Level);
    }

    private static bool IsSensitiveKey(string key) =>
        key.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            || key.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || key.Contains("set-cookie", StringComparison.OrdinalIgnoreCase)
            || key.Equals("mahjong_pid", StringComparison.OrdinalIgnoreCase);
}
