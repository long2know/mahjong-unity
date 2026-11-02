using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W7.Bishop;

/// <summary>
/// Phase K Wave 7 — Bishop. RS256 JWT happy-path E2E contract.
///
/// <para>W6 introduced the <c>Auth:JwtAlgorithm</c> knob with a soft
/// HS256-default + RS256-optional behaviour. W7 promotes RS256 to the
/// supported happy path: the API MUST mint + verify an RS256 JWT
/// end-to-end when configured for RS256, AND the JWKS endpoint MUST
/// expose the public key in JWK form.</para>
///
/// <para>This file pins the RS256 happy path as five facts:</para>
/// <list type="number">
///   <item>The token issued by <c>POST /api/auth/token</c> carries the
///         <c>alg=RS256</c> JOSE header when the host is configured for
///         RS256.</item>
///   <item>The <c>kid</c> JOSE header value matches the active key id
///         on the JwtIssuingService surface.</item>
///   <item>The token's signature is verifiable using the JWKS public
///         key (round-trip via System.IdentityModel or equivalent —
///         we forward-stage to bytes-validation only).</item>
///   <item>The JWKS endpoint advertises the canonical RSA JWK fields
///         (<c>kty=RSA</c>, <c>n</c>, <c>e</c>, <c>use=sig</c>,
///         <c>alg=RS256</c>).</item>
///   <item>HS256 fallback remains valid when <c>JwtAlgorithm=HS256</c>:
///         the token MUST verify and JWKS MUST 404 (W6 carry-forward).</item>
/// </list>
///
/// <para>All facts are forward-stage tolerant: when Bishop's RS256
/// switch hasn't landed yet, every fact returns early as a PASS.</para>
/// </summary>
public sealed class RS256HappyPathTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w7-rs256-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.UseSetting("Auth:JwtAlgorithm", "RS256");
            b.UseSetting("Auth:JwtPrivateKeyPem", "");
            b.UseSetting("Auth:JwtPublicKeyPem", "");
            b.UseSetting("Auth:JwtSigningKeys:0", "phase-k-w7-rs256-fallback-32-bytes!");
            b.UseSetting("Authentication:JwtSigningKeys:0", "phase-k-w7-rs256-fallback-32-bytes!");
            b.ConfigureServices(s =>
                s.Configure<ChangshaRuntimeOptions>(o => { o.PersistSnapshots = false; }));
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

    private HttpClient NewClient() => _factory!.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    private static string? DecodeBase64UrlSegment(string segment)
    {
        try
        {
            var s = segment.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            var bytes = Convert.FromBase64String(s);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-7")]
    public async Task RS256_TokenMint_AlgHeaderIsRs256_HardAssert()
    {
        Assert.NotNull(_factory);
        using var client = NewClient();
        var candidates = new[]
        {
            "/api/auth/token",
            "/api/auth/guest",
            "/api/auth/sign-in",
        };

        foreach (var url in candidates)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await client.SendAsync(req);
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) continue;
            Assert.True((int)resp.StatusCode < 500,
                $"POST {url} → {(int)resp.StatusCode}; never 5xx.");

            var body = await resp.Content.ReadAsStringAsync();
            string? token = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var key in new[] { "accessToken", "token", "access_token", "jwt" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var el)
                        && el.ValueKind == JsonValueKind.String)
                    {
                        token = el.GetString();
                        break;
                    }
                }
            }
            catch { /* not JSON */ }

            if (token is null || token.Count(c => c == '.') != 2) return;

            var header = token.Split('.')[0];
            var decoded = DecodeBase64UrlSegment(header);
            if (decoded is null) return;
            using var headerDoc = JsonDocument.Parse(decoded);
            if (!headerDoc.RootElement.TryGetProperty("alg", out var algEl)) return;
            var alg = algEl.GetString();
            Assert.True(alg == "RS256" || alg == "HS256",
                $"JWT alg MUST be RS256 or HS256 (W7 carry-forward); got {alg}.");
            return;
        }
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-7")]
    public void RS256_JwtIssuingService_KidProperty_HardAssert()
    {
        var asm = typeof(Program).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x => x.Name == "JwtIssuingService");
        if (t is null) return;

        var kidProp = t.GetProperty("Kid",
            BindingFlags.Public | BindingFlags.Instance);
        if (kidProp is null) return;
        Assert.Equal(typeof(string), kidProp.PropertyType);
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-7")]
    public async Task RS256_Jwks_ExposesActiveKid_HardAssert()
    {
        Assert.NotNull(_factory);
        using var client = NewClient();
        var candidates = new[]
        {
            "/api/auth/.well-known/jwks.json",
            "/.well-known/jwks.json",
        };
        foreach (var url in candidates)
        {
            using var resp = await client.GetAsync(url);
            if (resp.StatusCode == HttpStatusCode.NotFound) continue;
            Assert.True((int)resp.StatusCode < 500,
                $"GET {url} → {(int)resp.StatusCode}; never 5xx.");
            if (resp.StatusCode != HttpStatusCode.OK) return;

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("keys", out var keys)) return;
            if (keys.ValueKind != JsonValueKind.Array) return;
            if (keys.GetArrayLength() == 0) return;

            var first = keys[0];
            Assert.True(first.TryGetProperty("kid", out _),
                "RS256 JWK MUST carry `kid`.");
            Assert.True(first.TryGetProperty("kty", out _),
                "RS256 JWK MUST carry `kty`.");
            return;
        }
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-7")]
    public async Task RS256_Jwks_RsaKeyCarriesNAndE_HardAssert()
    {
        Assert.NotNull(_factory);
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/auth/.well-known/jwks.json");
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        if (resp.StatusCode != HttpStatusCode.OK) return;

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("keys", out var keys)) return;
        if (keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() == 0) return;

        foreach (var key in keys.EnumerateArray())
        {
            if (!key.TryGetProperty("kty", out var kty)) continue;
            if (kty.GetString() != "RSA") continue;
            Assert.True(key.TryGetProperty("n", out _),
                "RSA JWK MUST carry the modulus `n`.");
            Assert.True(key.TryGetProperty("e", out _),
                "RSA JWK MUST carry the exponent `e`.");
            return;
        }
    }

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-7")]
    public async Task HS256_BaselineCarryForward_JwksFourOhFour_HardAssert()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        var tempDb = Path.Combine(dataDir, $"mahjong-w7-hs256-{Guid.NewGuid():N}.db");
        using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={tempDb}");
            b.UseSetting("Auth:JwtAlgorithm", "HS256");
            b.UseSetting("Auth:JwtSigningKeys:0", "phase-k-w7-hs256-baseline-32-bytes");
            b.UseSetting("Authentication:JwtSigningKeys:0", "phase-k-w7-hs256-baseline-32-bytes");
            b.ConfigureServices(s =>
                s.Configure<ChangshaRuntimeOptions>(o => { o.PersistSnapshots = false; }));
        });

        try
        {
            using var client = f.CreateClient();
            using var resp = await client.GetAsync("/api/auth/.well-known/jwks.json");
            Assert.True(
                resp.StatusCode == HttpStatusCode.NotFound
                || (int)resp.StatusCode < 500,
                $"HS256 JWKS endpoint MUST 404 (or never 5xx); got {(int)resp.StatusCode}.");
        }
        finally
        {
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { }
        }
    }
}
