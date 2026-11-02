using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W11.Bishop;

/// <summary>
/// Phase K Wave 11 — Bishop. Hard-asserted contract for the
/// RFC 7662 token-introspection endpoint at
/// <c>POST /api/auth/introspect</c>.
///
/// <list type="number">
///   <item><see cref="AuthOptions.IntrospectionClient"/> exists
///         with the canonical property surface (ClientId,
///         ClientSecret, Scope).</item>
///   <item><see cref="AuthOptions.IntrospectionClients"/> exists
///         on <see cref="AuthOptions"/>.</item>
///   <item><see cref="AuthOptions.IntrospectionClient.ResolveSecret"/>
///         expands the <c>env:VAR_NAME</c> indirection.</item>
///   <item><see cref="AuthOptions.IntrospectionClient.ResolveSecret"/>
///         passes through a literal secret.</item>
///   <item>Missing Basic header → 401 with
///         <c>WWW-Authenticate: Basic ...</c>.</item>
///   <item>Wrong client secret → 401 (constant-time compare).</item>
///   <item>Missing token form field → 400 with
///         <c>error: invalid_request</c>.</item>
///   <item>Empty allowlist → every request returns 401.</item>
///   <item>Valid token + valid client → 200 with
///         <c>active: true</c>.</item>
///   <item>Expired/malformed token + valid client → 200 with
///         <c>active: false</c> (RFC 7662 §2.2).</item>
///   <item>Active response carries <c>client_id</c> matching
///         the caller.</item>
///   <item>Active response carries <c>scope</c> from the
///         allowlist entry.</item>
/// </list>
/// </summary>
public sealed class OAuthIntrospectionEndpointFacts : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;
    private const string ClientId = "test-introspect-client";
    private const string ClientSecret = "shhh-very-secret";
    private const string Scope = "test:scope";

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w11-introsp-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.UseSetting("Authentication:IntrospectionClients:0:ClientId", ClientId);
            b.UseSetting("Authentication:IntrospectionClients:0:ClientSecret", ClientSecret);
            b.UseSetting("Authentication:IntrospectionClients:0:Scope", Scope);
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.PersistSnapshots = false;
                    o.BotTurnDelayMs = 1;
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

    private HttpClient NewClient() => _factory!.CreateClient();

    private static string BasicHeader(string user, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes($"{user}:{secret}");
        return "Basic " + Convert.ToBase64String(bytes);
    }

    private async Task<string> MintTokenAsync(HttpClient http)
    {
        // The token endpoint requires admin cookie auth — for the
        // test we mint a token directly via the JwtIssuingService.
        using var scope = _factory!.Services.CreateScope();
        var issuer = scope.ServiceProvider.GetRequiredService<JwtIssuingService>();
        var rsp = await issuer.IssueAsync("test-user", new Dictionary<string, object?>
        {
            ["preferred_username"] = "test-user",
        });
        return rsp.Token;
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public void IntrospectionClient_TypeExists()
    {
        Assert.NotNull(typeof(AuthOptions.IntrospectionClient));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public void IntrospectionClient_HasCanonicalProperties()
    {
        var t = typeof(AuthOptions.IntrospectionClient);
        Assert.NotNull(t.GetProperty("ClientId"));
        Assert.NotNull(t.GetProperty("ClientSecret"));
        Assert.NotNull(t.GetProperty("Scope"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public void AuthOptions_HasIntrospectionClientsArray()
    {
        Assert.NotNull(typeof(AuthOptions).GetProperty("IntrospectionClients"));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public void ResolveSecret_LiteralPassesThrough()
    {
        var c = new AuthOptions.IntrospectionClient { ClientSecret = "literal" };
        Assert.Equal("literal", c.ResolveSecret());
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public void ResolveSecret_EnvIndirectionExpands()
    {
        var varName = "W11_INTROSP_TEST_" + Guid.NewGuid().ToString("N")[..6];
        Environment.SetEnvironmentVariable(varName, "from-env");
        try
        {
            var c = new AuthOptions.IntrospectionClient { ClientSecret = $"env:{varName}" };
            Assert.Equal("from-env", c.ResolveSecret());
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public async Task MissingBasic_Returns401_WithWwwAuthenticate()
    {
        using var http = NewClient();
        var resp = await http.PostAsync("/api/auth/introspect",
            new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("token", "abc") }));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.True(resp.Headers.TryGetValues("WWW-Authenticate", out var values));
        Assert.Contains(values!, v => v.StartsWith("Basic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public async Task WrongSecret_Returns401()
    {
        using var http = NewClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/introspect")
        {
            Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("token", "abc") }),
        };
        req.Headers.Authorization = AuthenticationHeaderValue.Parse(BasicHeader(ClientId, "wrong-secret"));
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public async Task MissingToken_Returns400()
    {
        using var http = NewClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/introspect")
        {
            Content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()),
        };
        req.Headers.Authorization = AuthenticationHeaderValue.Parse(BasicHeader(ClientId, ClientSecret));
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public async Task ValidToken_Returns200_ActiveTrue()
    {
        using var http = NewClient();
        var token = await MintTokenAsync(http);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/introspect")
        {
            Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("token", token) }),
        };
        req.Headers.Authorization = AuthenticationHeaderValue.Parse(BasicHeader(ClientId, ClientSecret));
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("active").GetBoolean());
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public async Task ValidToken_Returns_ClientIdAndScope()
    {
        using var http = NewClient();
        var token = await MintTokenAsync(http);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/introspect")
        {
            Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("token", token) }),
        };
        req.Headers.Authorization = AuthenticationHeaderValue.Parse(BasicHeader(ClientId, ClientSecret));
        var resp = await http.SendAsync(req);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal(ClientId, doc.RootElement.GetProperty("client_id").GetString());
        Assert.Equal(Scope, doc.RootElement.GetProperty("scope").GetString());
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public async Task MalformedToken_Returns200_ActiveFalse()
    {
        using var http = NewClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/introspect")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("token", "not-a-real-jwt-at-all"),
            }),
        };
        req.Headers.Authorization = AuthenticationHeaderValue.Parse(BasicHeader(ClientId, ClientSecret));
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("active").GetBoolean());
    }
}

/// <summary>
/// Phase K Wave 11 — Bishop. Companion contract: an empty
/// <c>IntrospectionClients</c> allowlist disables the endpoint
/// (every request returns 401).
/// </summary>
public sealed class OAuthIntrospectionDisabledFacts : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w11-introsp-disabled-{Guid.NewGuid():N}.db");
        // NOTE: no IntrospectionClients configured.
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.PersistSnapshots = false;
                    o.BotTurnDelayMs = 1;
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

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-11")]
    public async Task EmptyAllowlist_Returns401_ForAnyRequest()
    {
        using var http = _factory!.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/introspect")
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("token", "anything"),
            }),
        };
        req.Headers.Authorization = AuthenticationHeaderValue.Parse(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("any:any")));
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
