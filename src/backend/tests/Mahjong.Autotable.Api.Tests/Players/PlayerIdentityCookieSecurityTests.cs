using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Auth;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Players;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Players;

/// <summary>
/// Burke — HTTP-surface contract for the durable identity credential.
///
/// <para>Pins the migration + cookie policy that closes the impersonation blocker:</para>
/// <list type="bullet">
///   <item>the cookie VALUE is a signed credential, never the public player id;</item>
///   <item>a raw legacy player id is not honoured — the caller is rotated onto a FRESH
///         identity, so publishing a victim's id grants nothing;</item>
///   <item>a tampered credential is likewise rotated, never echoed back;</item>
///   <item>legitimate reuse (reload, second tab) keeps one stable identity;</item>
///   <item>attributes: HttpOnly, SameSite=Lax, Path=/, sliding Max-Age, Secure on HTTPS and
///         under the explicit proxy-TLS policy knob.</item>
/// </list>
/// </summary>
public sealed class PlayerIdentityCookieSecurityTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        _factory = NewFactory(out _tempDb);
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try { if (_tempDb is not null && File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
        return Task.CompletedTask;
    }

    private static WebApplicationFactory<Program> NewFactory(
        out string tempDb,
        Action<IWebHostBuilderShim>? extra = null)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        tempDb = Path.Combine(dataDir, $"identity-security-{Guid.NewGuid():N}.db");
        var db = tempDb;
        var settings = new Dictionary<string, string>();
        extra?.Invoke(new IWebHostBuilderShim(settings));

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={db}");
            foreach (var kv in settings) b.UseSetting(kv.Key, kv.Value);
            b.ConfigureServices(s => s.Configure<ChangshaRuntimeOptions>(o =>
            {
                o.BotTurnDelayMs = 1;
                o.ClaimWindowTimeoutMs = 50;
                o.DealBatchDelayMs = 0;
                o.PersistSnapshots = false;
            }));
        });
    }

    /// <summary>Tiny setting bag so a test can add host settings without repeating the builder.</summary>
    public sealed class IWebHostBuilderShim
    {
        private readonly Dictionary<string, string> _settings;
        internal IWebHostBuilderShim(Dictionary<string, string> settings) => _settings = settings;
        public IWebHostBuilderShim Setting(string key, string value) { _settings[key] = value; return this; }
    }

    // ── 1. the cookie is a credential, not the identifier ────────────────────────

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-credential")]
    public async Task PostIdentity_CookieValue_IsSigned_AndIsNotThePublicPlayerId()
    {
        using var client = _factory!.CreateClient();
        using var response = await client.PostAsync("/api/identity", content: null);
        response.EnsureSuccessStatusCode();

        var playerId = await ReadPlayerIdAsync(response);
        var cookie = ReadIdentityCookieValue(response);

        Assert.NotNull(cookie);
        Assert.NotEqual(playerId, cookie);
        Assert.StartsWith(PlayerIdentityTokenProtector.SchemePrefix + ".", cookie);
        Assert.Equal(4, cookie!.Split('.').Length);

        // The credential verifies back to exactly the published public id.
        var protector = _factory.Services.GetRequiredService<PlayerIdentityTokenProtector>();
        var verdict = protector.Unprotect(cookie);
        Assert.True(verdict.IsValid);
        Assert.Equal(playerId, verdict.PlayerId);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-credential")]
    public async Task IdentityCookie_CarriesTheHardenedAttributeSet()
    {
        using var client = _factory!.CreateClient();
        using var response = await client.PostAsync("/api/identity", content: null);
        var setCookie = ReadIdentitySetCookieHeader(response);

        Assert.NotNull(setCookie);
        Assert.Contains("httponly", setCookie!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=", setCookie, StringComparison.OrdinalIgnoreCase);
        // Plain-HTTP request → no Secure flag (browsers would drop it and churn identity).
        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-credential")]
    public async Task IdentityCookie_IsSecure_OverHttps()
    {
        using var client = _factory!.CreateClient();
        client.BaseAddress = new Uri("https://localhost");

        using var response = await client.PostAsync("/api/identity", content: null);
        var setCookie = ReadIdentitySetCookieHeader(response);

        Assert.NotNull(setCookie);
        Assert.Contains("secure", setCookie!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-credential")]
    public async Task IdentityCookie_IsSecure_OnPlainHttp_WhenProxyTlsPolicyIsSet()
    {
        using var factory = NewFactory(out var db, x => x.Setting("Identity:RequireSecureCookie", "true"));
        try
        {
            using var client = factory.CreateClient();
            using var response = await client.PostAsync("/api/identity", content: null);
            var setCookie = ReadIdentitySetCookieHeader(response);

            Assert.NotNull(setCookie);
            Assert.Contains("secure", setCookie!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            factory.Dispose();
            try { if (File.Exists(db)) File.Delete(db); } catch { }
        }
    }

    // ── 2. impersonation: a public player id buys nothing ───────────────────────

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-impersonation")]
    public async Task RawLegacyPlayerIdCookie_IsNotHonoured_AndRotatesToAFreshIdentity()
    {
        // Step 1 — the victim obtains an identity. Their playerId is PUBLIC (it is returned in
        // this very body and broadcast in the seats/nicks wire keys).
        using var victimClient = _factory!.CreateClient();
        using var victimResponse = await victimClient.PostAsync("/api/identity", content: null);
        var victimPublicId = await ReadPlayerIdAsync(victimResponse);
        Assert.False(string.IsNullOrEmpty(victimPublicId));

        // Step 2 — the attacker replays it verbatim as their own cookie (Frost's exploit).
        using var attackerClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        attackerClient.DefaultRequestHeaders.Add(
            "Cookie", $"{PlayerIdentityService.CookieName}={victimPublicId}");

        using var attackerResponse = await attackerClient.PostAsync("/api/identity", content: null);
        attackerResponse.EnsureSuccessStatusCode();
        var attackerId = await ReadPlayerIdAsync(attackerResponse);

        Assert.NotEqual(victimPublicId, attackerId);

        // …and the attacker is re-issued a real (signed) credential for their OWN new identity,
        // never one for the victim.
        var reissued = ReadIdentityCookieValue(attackerResponse);
        Assert.NotNull(reissued);
        var verdict = _factory.Services.GetRequiredService<PlayerIdentityTokenProtector>()
            .Unprotect(reissued);
        Assert.True(verdict.IsValid);
        Assert.Equal(attackerId, verdict.PlayerId);
        Assert.NotEqual(victimPublicId, verdict.PlayerId);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-impersonation")]
    public async Task TamperedCredential_IsRejected_AndReissuedAsAFreshIdentity()
    {
        var identity = _factory!.Services.GetRequiredService<PlayerIdentityService>();
        var victimPublicId = Guid.NewGuid().ToString("N");
        var genuine = identity.Protect(victimPublicId);

        // Flip one character of the MAC field.
        var parts = genuine.Split('.');
        var macChars = parts[3].ToCharArray();
        macChars[0] = macChars[0] == 'A' ? 'B' : 'A';
        var tampered = string.Join('.', parts[0], parts[1], parts[2], new string(macChars));

        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"{PlayerIdentityService.CookieName}={tampered}");

        using var response = await client.PostAsync("/api/identity", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resolved = await ReadPlayerIdAsync(response);

        Assert.NotEqual(victimPublicId, resolved);
        Assert.NotNull(ReadIdentityCookieValue(response));           // reissued, not echoed
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-impersonation")]
    public async Task CredentialSignedByAForeignKey_IsRejected()
    {
        var foreign = new PlayerIdentityTokenProtector(
            new JwtSigningKeyProvider(
                new AuthOptions { JwtSigningKeys = new[] { Convert.ToBase64String(new byte[48]) } },
                NullLogger<JwtSigningKeyProvider>.Instance));

        var victimPublicId = Guid.NewGuid().ToString("N");
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{PlayerIdentityService.CookieName}={foreign.Protect(victimPublicId)}");

        using var response = await client.PostAsync("/api/identity", content: null);
        Assert.NotEqual(victimPublicId, await ReadPlayerIdAsync(response));
    }

    // ── 3. legitimate identity continuity ───────────────────────────────────────

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-reconnect")]
    public async Task SignedCredential_KeepsOneStableIdentity_AcrossRequests()
    {
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
        });

        using var first = await client.PostAsync("/api/identity", content: null);
        var firstId = await ReadPlayerIdAsync(first);

        using var second = await client.PostAsync("/api/identity", content: null);
        var secondId = await ReadPlayerIdAsync(second);

        Assert.Equal(firstId, secondId);
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-reconnect")]
    public async Task SameCredentialInASecondTab_ResolvesToTheSameIdentity()
    {
        var identity = _factory!.Services.GetRequiredService<PlayerIdentityService>();
        var playerId = Guid.NewGuid().ToString("N");
        var cookie = identity.Protect(playerId);

        foreach (var _ in Enumerable.Range(0, 2))
        {
            using var tab = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
            });
            tab.DefaultRequestHeaders.Add("Cookie", $"{PlayerIdentityService.CookieName}={cookie}");
            using var response = await tab.PostAsync("/api/identity", content: null);
            Assert.Equal(playerId, await ReadPlayerIdAsync(response));
        }
    }

    [Fact, Trait("Category", "Identity"), Trait("Contract", "identity-rotation")]
    public async Task CredentialSignedByAnOlderActiveKey_StillResolves_AndIsReSignedWithThePrimary()
    {
        // Boot a host with TWO keys: index 0 signs, index 1 is the previous key still in the
        // rotation window. A cookie minted under the old key must keep the player logged in.
        var oldKey = Convert.ToBase64String(Enumerable.Repeat((byte)7, 48).ToArray());
        var newKey = Convert.ToBase64String(Enumerable.Repeat((byte)9, 48).ToArray());

        using var factory = NewFactory(out var db, x => x
            .Setting("Authentication:JwtSigningKeys:0", newKey)
            .Setting("Authentication:JwtSigningKeys:1", oldKey));
        try
        {
            var playerId = Guid.NewGuid().ToString("N");
            var underOldKey = new PlayerIdentityTokenProtector(
                new JwtSigningKeyProvider(
                    new AuthOptions { JwtSigningKeys = new[] { oldKey } },
                    NullLogger<JwtSigningKeyProvider>.Instance))
                .Protect(playerId);

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                HandleCookies = false,
            });
            client.DefaultRequestHeaders.Add(
                "Cookie", $"{PlayerIdentityService.CookieName}={underOldKey}");

            using var response = await client.PostAsync("/api/identity", content: null);
            Assert.Equal(playerId, await ReadPlayerIdAsync(response));

            // Re-issued under the CURRENT primary key, so the identity survives the old key's retirement.
            var refreshed = ReadIdentityCookieValue(response);
            Assert.NotNull(refreshed);
            Assert.NotEqual(underOldKey, refreshed);
            var protector = factory.Services.GetRequiredService<PlayerIdentityTokenProtector>();
            var verdict = protector.Unprotect(refreshed);
            Assert.True(verdict.IsValid);
            Assert.True(verdict.SignedByPrimaryKey);
            Assert.Equal(playerId, verdict.PlayerId);
        }
        finally
        {
            factory.Dispose();
            try { if (File.Exists(db)) File.Delete(db); } catch { }
        }
    }

    // ── 4. no non-cookie source may name an identity ────────────────────────────

    [Theory, Trait("Category", "Identity"), Trait("Contract", "identity-impersonation")]
    [InlineData("/api/identity?playerId={0}")]
    [InlineData("/api/identity?player_id={0}")]
    [InlineData("/api/identity?pid={0}")]
    public async Task QueryStringCannotNameAnIdentity(string template)
    {
        var victimPublicId = Guid.NewGuid().ToString("N");
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        using var response = await client.PostAsync(string.Format(template, victimPublicId), content: null);
        response.EnsureSuccessStatusCode();

        Assert.NotEqual(victimPublicId, await ReadPlayerIdAsync(response));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static async Task<string> ReadPlayerIdAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("playerId").GetString() ?? string.Empty;
    }

    private static string? ReadIdentitySetCookieHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(
                PlayerIdentityService.CookieName + "=", StringComparison.Ordinal))
            : null;

    private static string? ReadIdentityCookieValue(HttpResponseMessage response)
    {
        var header = ReadIdentitySetCookieHeader(response);
        if (header is null) return null;
        var firstSegment = header.Split(';')[0];
        return firstSegment[(firstSegment.IndexOf('=') + 1)..];
    }
}
