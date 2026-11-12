using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;

namespace Mahjong.Autotable.Api.Hosting;

public sealed record AppMount(PathString Path, IReadOnlyList<IPAddress> TrustedProxies)
{
    public static AppMount Read(IConfiguration configuration)
    {
        var raw = configuration["BASE_URL"] ?? "";
        PathString path = PathString.Empty;
        if (raw is not ("" or "/"))
        {
            if (!raw.StartsWith('/') || raw.StartsWith("//") || raw.Contains("//") ||
                Regex.IsMatch(raw, "%(?![0-9a-fA-F]{2})") ||
                Regex.IsMatch(raw, "%(?:2f|5c)", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("BASE_URL must be an absolute site path such as /mahjong, not a URL or ambiguous path.");
            var decoded = Uri.UnescapeDataString(raw);
            if (decoded.Any(char.IsControl) || decoded.IndexOfAny(['\\', '?', '#', ':', '%']) >= 0 ||
                decoded.Split('/').Any(segment => segment is "." or ".."))
                throw new InvalidOperationException("BASE_URL cannot contain traversal, controls, query/fragment delimiters or nested escapes.");
            path = new PathString(decoded.TrimEnd('/'));
        }
        var proxies = new List<IPAddress>();
        foreach (var item in configuration.GetSection("ReverseProxy:KnownProxies").GetChildren())
        {
            if (string.IsNullOrEmpty(item.Value)) continue;
            if (!IPAddress.TryParse(item.Value, out var address))
                throw new InvalidOperationException("ReverseProxy:KnownProxies entries must be explicit trusted IP addresses.");
            proxies.Add(address);
        }
        return new AppMount(path, proxies);
    }

    public void ConfigureProxyServices(IServiceCollection services)
    {
        if (TrustedProxies.Count == 0) return;
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var address in TrustedProxies) options.KnownProxies.Add(address);
        });
    }

    public void Use(IApplicationBuilder app)
    {
        if (TrustedProxies.Count > 0) app.UseForwardedHeaders();
        if (Path.HasValue) app.UsePathBase(Path);
        app.Use(async (context, next) =>
        {
            // /health remains available to the container's internal healthcheck.
            if (Path.HasValue && !context.Request.PathBase.HasValue && context.Request.Path != "/health")
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            // Only canonicalize the entry's trailing slash/escaping; the mount itself serves the UI.
            if ((context.Request.Path == "/" || !context.Request.Path.HasValue || context.Request.Path == "/index.html") &&
                (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
            {
                var canonical = context.Request.PathBase.Add("/").ToUriComponent();
                var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0];
                if (string.IsNullOrEmpty(raw))
                    raw = context.Request.PathBase.Add(context.Request.Path).ToUriComponent();
                if (!context.Request.Path.HasValue || context.Request.Path == "/index.html" || raw != canonical)
                {
                    context.Response.Redirect(canonical + context.Request.QueryString);
                    return;
                }
            }
            await next(context);
        });
    }

    public static string CookiePath(HttpRequest request) =>
        request.PathBase.HasValue ? request.PathBase.ToUriComponent() + "/" : "/";
}
