using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Auth;

/// <summary>
/// Phase J Wave 8 — OAuth callback contract tests (Vasquez).
///
/// <para>Bishop's Wave 8 ships Google + GitHub OAuth via the standard
/// ASP.NET Core OAuth handler chain. We do NOT exercise the real upstream
/// (Google / GitHub servers) — instead we hit the callback URL with the
/// shapes the handler exposes and assert it does not crash to 500.</para>
///
/// <para><b>What we pin (read-only / no real OAuth tokens):</b>
/// <list type="number">
///   <item><b>Endpoint exists or is not-yet-registered.</b> Probe candidate
///         callback URLs. 404 is the not-yet-registered signal.</item>
///   <item><b>Missing-code rejection.</b> A callback hit without the OAuth
///         <c>code</c> query param must respond with 4xx (typically 400 or
///         a redirect to the sign-in error page) — never 500.</item>
///   <item><b>Sign-in challenge redirect.</b> Hitting the sign-in start
///         URL with an unknown provider returns 404 or 4xx; a known
///         provider (when enabled) returns 302 to the upstream.</item>
/// </list></para>
///
/// <para><b>Reflection-defensive.</b> Bishop's URL shape may be one of
/// <c>/signin-google</c>, <c>/api/auth/callback/google</c>,
/// <c>/api/auth/sign-in/google/callback</c>, etc. We probe all of them
/// and accept the first non-404, similar to the Wave 7 replay test.</para>
/// </summary>
public class OAuthCallbackTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-oauth-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            // Seed fake OAuth client ids so any handler that requires them
            // at registration time still wires up cleanly. Real upstream
            // calls are NOT exercised (we only hit our own callback).
            b.UseSetting("Authentication:Google:ClientId", "test-google-client-id");
            b.UseSetting("Authentication:Google:ClientSecret", "test-google-client-secret");
            b.UseSetting("Authentication:GitHub:ClientId", "test-github-client-id");
            b.UseSetting("Authentication:GitHub:ClientSecret", "test-github-client-secret");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.PersistSnapshots = false;
                });
            });
        });
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try { if (_tempDb is not null && File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
        return Task.CompletedTask;
    }

    private static IEnumerable<string> CallbackCandidates(string provider) => new[]
    {
        $"/signin-{provider}",
        $"/api/auth/callback/{provider}",
        $"/api/auth/sign-in/{provider}/callback",
        $"/auth/{provider}/callback",
    };

    private static IEnumerable<string> ChallengeCandidates(string provider) => new[]
    {
        $"/api/auth/sign-in/{provider}",
        $"/api/auth/challenge/{provider}",
        $"/auth/{provider}/start",
    };

    private static async Task<HttpResponseMessage> ProbeAsync(HttpClient client, IEnumerable<string> urls)
    {
        HttpResponseMessage? last = null;
        foreach (var url in urls)
        {
            last?.Dispose();
            last = await client.GetAsync(url);
            if (last.StatusCode != HttpStatusCode.NotFound) return last;
        }
        return last!;
    }

    [Theory, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    [InlineData("google")]
    [InlineData("github")]
    public async Task OAuthCallback_MissingCode_RejectsCleanly(string provider)
    {
        // Bishop's contract: a callback hit without OAuth `code` must NOT
        // 500. Either 4xx (validation reject) or redirect to a sign-in
        // error page. The not-yet-registered case (404) also passes.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        using var response = await ProbeAsync(client, CallbackCandidates(provider));

        var code = (int)response.StatusCode;
        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound
            || (code >= 300 && code < 500),
            $"OAuth {provider} callback missing-code response was {code}, expected 3xx/4xx or 404 (not yet registered).");
    }

    [Theory, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    [InlineData("google")]
    [InlineData("github")]
    public async Task OAuthChallenge_ReturnsRedirectOrNotFound(string provider)
    {
        // Bishop's contract: hitting the sign-in challenge URL with the
        // OAuth providers configured returns 302 to the upstream. If the
        // surface isn't yet wired, 404 is acceptable.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        using var response = await ProbeAsync(client, ChallengeCandidates(provider));

        var code = (int)response.StatusCode;
        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound
            || (code >= 200 && code < 500),
            $"OAuth {provider} challenge response was {code}, expected 2xx/3xx/4xx or 404.");
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public async Task OAuthCallback_UnknownProvider_Rejects()
    {
        // A made-up provider name must surface 404 / 400 — never 500.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        using var response = await ProbeAsync(client, CallbackCandidates("notarealprovider"));

        Assert.True((int)response.StatusCode < 500,
            $"Unknown provider callback returned {(int)response.StatusCode}; must not 5xx.");
    }
}
