using System.Net;
using System.Net.Http.Headers;
using Mahjong.Autotable.Api.Hosting;
using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Mahjong.Autotable.Api.Tests.Hosting;

public sealed class UnityStaticHostingTests : IAsyncLifetime
{
    private readonly string _fixtures = Path.Combine(AppContext.BaseDirectory, "hosting-test-fixtures", Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_fixtures, "unity", "Build"));
        await File.WriteAllTextAsync(Path.Combine(_fixtures, "index.html"), "<!doctype html><title>FABRICATED HOSTING TEST SHELL</title>");
        await File.WriteAllTextAsync(Path.Combine(_fixtures, "unity", "renderer-build.json"), "{\"fixture\":\"FABRICATED HOSTING TEST ONLY\"}");
        await File.WriteAllTextAsync(Path.Combine(_fixtures, "unity", "Build", "TEST.loader.js"), "// FABRICATED HOSTING TEST ONLY");
        await File.WriteAllTextAsync(Path.Combine(_fixtures, "unity", "Build", "TEST.framework.js"), "// FABRICATED HOSTING TEST ONLY");
        await File.WriteAllTextAsync(Path.Combine(_fixtures, "unity", "Build", "TEST.abcdef12.js"), "// FABRICATED HOSTING TEST ONLY");
        await File.WriteAllBytesAsync(Path.Combine(_fixtures, "unity", "Build", "TEST.wasm"), [0, 97, 115, 109, 1, 0, 0, 0]);
        await File.WriteAllBytesAsync(Path.Combine(_fixtures, "unity", "Build", "TEST.data"), [1, 2, 3, 4]);
        await StartAsync(new Dictionary<string, string?>());
    }

    private async Task StartAsync(Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _fixtures,
            EnvironmentName = "Development"
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.UseMiddleware<SecurityHeadersMiddleware>();
        _app.UseAutotableFiles(_fixtures, builder.Configuration, _app.Logger);
        _app.UseRouting();
        _app.MapGet("/api/probe", () => new { hostingTestOnly = true });
        _app.MapFallback(() => Microsoft.AspNetCore.Http.Results.Content("<html>FABRICATED SPA FALLBACK</html>", "text/html"));
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
        Directory.Delete(_fixtures, true);
    }

    [Theory]
    [InlineData("renderer-build.json", "application/json")]
    [InlineData("Build/TEST.loader.js", "application/javascript")]
    [InlineData("Build/TEST.framework.js", "application/javascript")]
    [InlineData("Build/TEST.abcdef12.js", "application/javascript")]
    [InlineData("Build/TEST.wasm", "application/wasm")]
    [InlineData("Build/TEST.data", "application/octet-stream")]
    public async Task UnityFiles_UseExactMimeAndRevalidateEvenForHashLikeNames(string relative, string contentType)
    {
        using var response = await _client!.GetAsync("/autotable/unity/" + relative);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(_fixtures, "unity", relative)),
            await response.Content.ReadAsByteArrayAsync());
        Assert.True(response.Headers.CacheControl!.NoCache);
        Assert.True(response.Headers.CacheControl.MustRevalidate);
        Assert.DoesNotContain("immutable", response.Headers.GetValues("Cache-Control").Single());
        Assert.DoesNotContain("Content-Encoding", response.Content.Headers.Select(header => header.Key));
        Assert.Contains("'wasm-unsafe-eval'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("'unsafe-eval'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.False(response.Headers.Contains("Cross-Origin-Opener-Policy"));
        Assert.False(response.Headers.Contains("Cross-Origin-Embedder-Policy"));
    }

    [Theory]
    [InlineData("/autotable/unity/missing.loader.js")]
    [InlineData("/autotable/unity/missing.wasm")]
    [InlineData("/autotable/unity/missing.data")]
    [InlineData("/autotable/unity/missing.json")]
    [InlineData("/autotable/unity/")]
    [InlineData("/autotable/unity")]
    public async Task MissingUnityArtifacts_NeverReachSpaOrDefaultFiles(string url)
    {
        using var response = await _client!.GetAsync(url);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl!.NoCache);
        Assert.True(response.Headers.CacheControl.MustRevalidate);
    }

    [Fact]
    public async Task EntireMissingBuildStill404sWhileShellAndApiWork()
    {
        Directory.Delete(Path.Combine(_fixtures, "unity"), true);
        using var missing = await _client!.GetAsync("/autotable/unity/renderer-build.json");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Empty(await missing.Content.ReadAsByteArrayAsync());
        Assert.Contains("FABRICATED HOSTING TEST SHELL", await _client.GetStringAsync("/autotable/"));
        Assert.Contains("\"hostingTestOnly\":true", await _client.GetStringAsync("/api/probe"));
    }

    [Fact]
    public async Task StaticFilesRetainHeadRangeAndConditionalRequests()
    {
        using var first = await _client!.GetAsync("/autotable/unity/Build/TEST.wasm");
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/autotable/unity/Build/TEST.wasm");
        using var head = await _client.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(8L, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/autotable/unity/Build/TEST.wasm");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var notModified = await _client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.True(notModified.Headers.CacheControl!.NoCache);
        Assert.True(notModified.Headers.CacheControl.MustRevalidate);
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "/autotable/unity/Build/TEST.wasm");
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 3);
        using var range = await _client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(new byte[] { 0, 97, 115, 109 }, await range.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/wasm", range.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "default-src 'self'; script-src 'self'")]
    public async Task IncompatibleCsp_IsDiagnosedButNeverWeakened(bool strict, string? custom)
    {
        _client!.Dispose();
        await _app!.DisposeAsync();
        var settings = new Dictionary<string, string?>
        {
            ["Security:CspStrict"] = strict.ToString(),
            ["Security:ContentSecurityPolicy"] = custom
        };
        await StartAsync(settings);
        Assert.NotNull(UnityStaticHosting.CspIncompatibility(_app!.Configuration));
        using var response = await _client!.GetAsync("/autotable/unity/Build/TEST.wasm");
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.DoesNotContain("'wasm-unsafe-eval'", policy);
        Assert.DoesNotContain("'unsafe-eval'", policy);
        Assert.Contains(custom ?? SecurityHeadersMiddleware.StrictCsp, policy);
    }

    [Theory]
    [InlineData("default-src 'self' 'wasm-unsafe-eval'")]
    [InlineData("script-src 'self' 'wasm-unsafe-eval'")]
    [InlineData("script-src 'self' 'unsafe-eval'")]
    [InlineData("object-src 'none'")]
    public void CompatibleCustomPolicy_IsNotSilentlyModified(string policy)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:ContentSecurityPolicy"] = policy,
            ["Security:CspStrict"] = "true"
        }).Build();
        Assert.Null(UnityStaticHosting.CspIncompatibility(configuration));
        Assert.Equal(policy, configuration["Security:ContentSecurityPolicy"]);
    }

    [Fact]
    public void ReportOnlyStrictPolicy_DoesNotEnforceAWasmBlock()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:CspStrict"] = "true",
            ["Security:CspReportOnly"] = "true"
        }).Build();
        Assert.Null(UnityStaticHosting.CspIncompatibility(configuration));
    }
}
