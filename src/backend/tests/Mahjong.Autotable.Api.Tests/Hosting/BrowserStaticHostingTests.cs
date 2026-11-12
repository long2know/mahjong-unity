using System.Net;
using System.Net.Http.Headers;
using Mahjong.Autotable.Api.Hosting;
using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Mahjong.Autotable.Api.Tests.Hosting;

public sealed class BrowserStaticHostingTests : IAsyncLifetime
{
    private readonly string fixtures = Path.Combine(AppContext.BaseDirectory, "hosting-fixtures", Guid.NewGuid().ToString("N"));
    private WebApplication? app;
    private HttpClient? client;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(fixtures);
        await File.WriteAllTextAsync(Path.Combine(fixtures, "index.html"), "<title>STATIC TEST SHELL</title>");
        await File.WriteAllTextAsync(Path.Combine(fixtures, "table.1234abcd.js"), "export const test = true;");
        await File.WriteAllTextAsync(Path.Combine(fixtures, "web-build.json"), "{\"engine\":\"webgl2\"}");
        await File.WriteAllTextAsync(Path.Combine(fixtures, "COPYING"), "RETAINED TEST NOTICE");
        await File.WriteAllTextAsync(Path.Combine(fixtures, "UPSTREAM_SHA"), "TEST SOURCE ID");
        Directory.CreateDirectory(Path.Combine(fixtures, "unity"));
        await File.WriteAllTextAsync(Path.Combine(fixtures, "unity", "renderer-build.json"), "RETIRED TEST DATA");
        await File.WriteAllTextAsync(Path.Combine(fixtures, "unexpected.wasm"), "RETIRED TEST DATA");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = fixtures });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Security:CspStrict"] = "true" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        app = builder.Build();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseAutotableFiles(fixtures, builder.Configuration, app.Logger);
        app.MapGet("/api/probe", () => new { testOnly = true });
        await app.StartAsync();
        client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        if (app is not null) await app.DisposeAsync();
        Directory.Delete(fixtures, true);
    }

    [Theory]
    [InlineData("/", "text/html")]
    [InlineData("/table.1234abcd.js", "application/javascript")]
    [InlineData("/web-build.json", "application/json")]
    [InlineData("/COPYING", "text/plain")]
    [InlineData("/UPSTREAM_SHA", "text/plain")]
    public async Task BrowserFilesWorkWithStrictCspAndNoPlayer(string path, string mime)
    {
        using var response = await client!.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mime, response.Content.Headers.ContentType!.MediaType);
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.DoesNotContain("'wasm-unsafe-eval'", policy);
        Assert.DoesNotContain("'unsafe-eval'", policy);
        Assert.False(response.Headers.Contains("Cross-Origin-Opener-Policy"));
        Assert.False(response.Headers.Contains("Cross-Origin-Embedder-Policy"));
    }

    [Theory]
    [InlineData("/autotable/unity/renderer-build.json")]
    [InlineData("/autotable/unity/missing.wasm")]
    [InlineData("/autotable/unity/")]
    [InlineData("/autotable/unity")]
    [InlineData("/autotable/unexpected.wasm")]
    [InlineData("/autotable/missing.data")]
    [InlineData("/autotable/old.loader.js")]
    [InlineData("/autotable/old.framework.js")]
    [InlineData("/autotable/")]
    [InlineData("/unity/renderer-build.json")]
    [InlineData("/unexpected.wasm")]
    [InlineData("/missing.js")]
    [InlineData("/api/missing")]
    public async Task RetiredEngineFilesCannotBeServedEvenWhenPresent(string path)
    {
        using var response = await client!.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task BrowserFilesRetainConditionalHeadAndRangeSemantics()
    {
        const string path = "/table.1234abcd.js";
        using var first = await client!.GetAsync(path);
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var unchanged = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        using var range = new HttpRequestMessage(HttpMethod.Get, path);
        range.Headers.Range = new RangeHeaderValue(0, 5);
        using var partial = await client.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("export", await partial.Content.ReadAsStringAsync());
    }
}
