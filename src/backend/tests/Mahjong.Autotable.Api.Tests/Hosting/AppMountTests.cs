using System.Net;
using Mahjong.Autotable.Api.Hosting;
using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Mahjong.Autotable.Api.Tests.Hosting;

public sealed class AppMountTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("/mahjong", "/mahjong")]
    [InlineData("/mahjong/", "/mahjong")]
    [InlineData("/games/mahjong/", "/games/mahjong")]
    [InlineData("/My Game", "/My%20Game")]
    [InlineData("/My%20Game/", "/My%20Game")]
    [InlineData("/m%61hjong", "/mahjong")]
    public void NormalizesRootTrailingSlashAndLegalEscapedPaths(string? input, string expected)
    {
        var mount = AppMount.Read(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["BASE_URL"] = input }).Build());
        Assert.Equal(expected, mount.Path.ToUriComponent());
    }

    [Theory]
    [InlineData("https://example.test/mahjong")]
    [InlineData("mahjong")]
    [InlineData("//other.test/path")]
    [InlineData("/a//b")]
    [InlineData("/a/../b")]
    [InlineData("/a/%2e%2e/b")]
    [InlineData("/a\\b")]
    [InlineData("/a?x=1")]
    [InlineData("/a#fragment")]
    [InlineData("/a%00")]
    [InlineData("/a%2fb")]
    [InlineData("/a%255cb")]
    [InlineData("/broken%2")]
    [InlineData("/a\nb")]
    public void InvalidMountFailsBeforeApplicationStartup(string input)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["BASE_URL"] = input }).Build();
        Assert.Contains("BASE_URL", Assert.Throws<InvalidOperationException>(() => AppMount.Read(config)).Message);
    }

    [Theory]
    [InlineData("", "/")]
    [InlineData("/mahjong", "/mahjong")]
    [InlineData("/games/mahjong/", "/games/mahjong")]
    [InlineData("/My%20Game", "/My%20Game")]
    public async Task ActualStaticAndEndpointRoutingUsesMountWithoutRewritingFiles(string configured, string publicBase)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "mount-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        const string script = "export const immutableSource = true;";
        await File.WriteAllTextAsync(Path.Combine(fixture, "index.html"), "<title>MOUNT TEST</title>");
        await File.WriteAllTextAsync(Path.Combine(fixture, "main.1234abcd.js"), script);
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["BASE_URL"] = configured });
        builder.Logging.ClearProviders(); builder.WebHost.UseTestServer();
        var mount = AppMount.Read(builder.Configuration);
        await using var app = builder.Build();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        mount.Use(app); app.UseRouting();
        app.UseAutotableFiles(fixture, builder.Configuration, app.Logger);
        app.MapGet("/api/mount", (HttpContext context) => new
        {
            path = context.Request.Path.Value, pathBase = context.Request.PathBase.ToUriComponent(),
            cookiePath = AppMount.CookiePath(context.Request)
        });
        app.MapGet("/health", () => "healthy");
        try
        {
            await app.StartAsync();
            using var client = app.GetTestClient();
            var prefix = publicBase == "/" ? "" : publicBase;
            using var landing = await client.GetAsync(prefix + "/?action=new-game");
            Assert.Equal(HttpStatusCode.Redirect, landing.StatusCode);
            Assert.Equal(prefix + "/autotable/?action=new-game", landing.Headers.Location!.OriginalString);
            Assert.Equal(script, await client.GetStringAsync(prefix + "/autotable/main.1234abcd.js"));
            using var headers = await client.GetAsync(prefix + "/autotable/main.1234abcd.js");
            Assert.Contains("report-uri " + prefix + "/api/csp-report",
                headers.Headers.GetValues("Content-Security-Policy").Single());
            var api = await client.GetStringAsync(prefix + "/api/mount");
            Assert.Contains("\"path\":\"/api/mount\"", api);
            Assert.Contains("\"pathBase\":\"" + prefix + "\"", api);
            Assert.Contains("\"cookiePath\":\"" + (prefix.Length == 0 ? "/" : prefix + "/") + "\"", api);
            Assert.Equal("healthy", await client.GetStringAsync("/health"));
            if (prefix.Length > 0) Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/mount")).StatusCode);
            if (prefix == "/mahjong")
            {
                var canonical = await app.GetTestServer().SendAsync(context =>
                {
                    context.Request.Method = "GET";
                    context.Request.Path = "/mahjong/autotable/";
                    context.Request.QueryString = new("?keep=one");
                    context.Features.Get<IHttpRequestFeature>()!.RawTarget = "/%6dahjong/autotable/?keep=one";
                });
                Assert.Equal(StatusCodes.Status302Found, canonical.Response.StatusCode);
                Assert.Equal("/mahjong/autotable/?keep=one", canonical.Response.Headers.Location);
            }
        }
        finally { Directory.Delete(fixture, true); }
    }

    [Theory]
    [InlineData("127.0.0.2", "https")]
    [InlineData("127.0.0.3", "http")]
    public async Task ForwardedSchemeIsAcceptedOnlyFromExplicitTrustedPeer(string remote, string expectedScheme)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:KnownProxies:0"] = "127.0.0.2"
        });
        builder.Logging.ClearProviders(); builder.WebHost.UseTestServer();
        var mount = AppMount.Read(builder.Configuration); mount.ConfigureProxyServices(builder.Services);
        await using var app = builder.Build();
        app.Use(async (context, next) => { context.Connection.RemoteIpAddress = IPAddress.Parse(remote); await next(context); });
        mount.Use(app); app.UseRouting();
        app.MapGet("/api/scheme", (HttpContext context) => context.Request.Scheme);
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/scheme");
        request.Headers.Add("X-Forwarded-Proto", "https");
        Assert.Equal(expectedScheme, await (await client.SendAsync(request)).Content.ReadAsStringAsync());
    }
}
