using Microsoft.AspNetCore.SignalR;
using Sentry;

namespace Mahjong.Autotable.Api.Observability;

/// <summary>
/// Phase J Wave 8 — Sentry breadcrumb pipe for SignalR hub method calls
/// (Apone, DevOps). Implements <see cref="IHubFilter"/> so every hub
/// method invocation emits a Sentry breadcrumb (category <c>signalr</c>,
/// level <c>info</c>) before delegation. Exceptions raised inside the
/// hub method are captured before being re-thrown so they reach both
/// SignalR's error handler and Sentry's event pipeline.
///
/// <para>The filter is a singleton — registered via
/// <c>builder.Services.AddSignalR(o =&gt; o.AddFilter&lt;SentryHubFilter&gt;())</c>
/// in <c>Program.cs</c>. When the Sentry SDK is not initialised
/// (<c>Sentry:Dsn</c> empty) the breadcrumb / capture calls are no-ops
/// because <see cref="SentrySdk.AddBreadcrumb(string,string,string,IDictionary{string,string}?,BreadcrumbLevel)"/>
/// and <see cref="SentrySdk.CaptureException"/> are disabled by the
/// uninitialised hub.</para>
/// </summary>
public sealed class SentryHubFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        // Skip breadcrumbs when Sentry hasn't been wired — short-circuit
        // avoids allocating the dictionary in the hot path of dev/test runs.
        if (SentrySdk.IsEnabled)
        {
            // Connection id is the per-tab identifier used by Bishop's
            // profile pipeline; it's already exposed in /metrics so it's
            // safe to surface here. Argument count (not values) keeps PII
            // out of the breadcrumb.
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hub"] = invocationContext.Hub.GetType().Name,
                ["method"] = invocationContext.HubMethodName,
                ["argCount"] = invocationContext.HubMethodArguments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["connectionId"] = invocationContext.Context.ConnectionId,
            };
            SentrySdk.AddBreadcrumb(
                $"{invocationContext.Hub.GetType().Name}.{invocationContext.HubMethodName}",
                category: "signalr",
                type: "default",
                data: data,
                level: BreadcrumbLevel.Info);
        }

        try
        {
            return await next(invocationContext).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Tag the event with the hub method so it's filterable in
            // the Sentry UI without parsing the stack trace.
            if (SentrySdk.IsEnabled)
            {
                SentrySdk.CaptureException(ex, scope =>
                {
                    scope.SetTag("signalr.hub", invocationContext.Hub.GetType().Name);
                    scope.SetTag("signalr.method", invocationContext.HubMethodName);
                });
            }
            throw;
        }
    }

    public async Task OnConnectedAsync(
        HubLifetimeContext context,
        Func<HubLifetimeContext, Task> next)
    {
        if (SentrySdk.IsEnabled)
        {
            SentrySdk.AddBreadcrumb(
                $"{context.Hub.GetType().Name} connected",
                category: "signalr",
                type: "default",
                data: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["connectionId"] = context.Context.ConnectionId,
                },
                level: BreadcrumbLevel.Info);
        }
        await next(context).ConfigureAwait(false);
    }

    public async Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        if (SentrySdk.IsEnabled)
        {
            SentrySdk.AddBreadcrumb(
                $"{context.Hub.GetType().Name} disconnected",
                category: "signalr",
                type: "default",
                data: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["connectionId"] = context.Context.ConnectionId,
                    ["hasError"] = (exception is not null).ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                level: exception is null ? BreadcrumbLevel.Info : BreadcrumbLevel.Warning);
        }
        await next(context, exception).ConfigureAwait(false);
    }
}
