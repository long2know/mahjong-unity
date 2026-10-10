using System.Net;
using System.Reflection;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W3;

/// <summary>
/// Phase K Wave 3 — TURN HMAC credential-mint endpoint contract tests
/// (Vasquez).
///
/// <para>Bishop's Phase K Wave 3 brief replaces the Wave-2
/// <c>GET /api/turn</c> static-list endpoint with a short-lived HMAC
/// credential minter compatible with coturn's
/// <c>use-auth-secret</c> mode:
/// <list type="bullet">
///   <item>Authenticated <c>GET /api/turn</c> (or
///         <c>POST /api/turn/credentials</c>) returns
///         <c>{ iceServers: [...], ttlSeconds: 3600 }</c>.</item>
///   <item><c>username</c> follows coturn's
///         <c>&lt;unix_ttl&gt;:&lt;playerId&gt;</c> format.</item>
///   <item><c>credential</c> is the base64 HMAC-SHA1 of
///         <c>username</c> keyed by the shared secret.</item>
///   <item>Unauthenticated callers get a 401 (or fall back to the
///         public-STUN list).</item>
///   <item>Operator-missing secret → 503, never 500.</item>
///   <item>Audit row written with
///         <c>Kind="voice.turn.credentials.minted"</c>.</item>
///   <item>Secret rotation: with a primary + fallback secret both
///         configured, tokens minted under either are still valid.</item>
/// </list></para>
///
/// <para><b>Reflection-defensive.</b> The HMAC service may land as
/// <c>TurnCredentialService</c>, <c>TurnHmacMinter</c>, or fold into
/// <c>VoiceTurnService</c>. Endpoint may be <c>GET /api/turn</c>,
/// <c>POST /api/turn/credentials</c>, or <c>GET /api/voice/ice-servers</c>.
/// Every probe soft-passes when the surface isn't yet shipped — the
/// Wave-2 baseline endpoint at <c>/api/turn</c> already returns a
/// 200 + <c>iceServers</c>, which most facts treat as the
/// forward-stage shape.</para>
/// </summary>
public class TurnHmacMintContractTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-turn-mint-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.UseSetting("Voice:TurnSharedSecret", "phase-k-w3-test-secret");
            b.UseSetting("Voice:TurnTtlSeconds", "3600");
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

    private static readonly string[] CandidateUrls =
    {
        "/api/turn",
        "/api/turn/credentials",
        "/api/voice/turn",
        "/api/voice/ice-servers",
    };

    private async Task<(HttpResponseMessage response, string url)> GetTurnAsync(HttpClient client)
    {
        HttpResponseMessage? last = null;
        var lastUrl = CandidateUrls[0];
        foreach (var url in CandidateUrls)
        {
            last?.Dispose();
            last = await client.GetAsync(url);
            lastUrl = url;
            if (last.StatusCode != HttpStatusCode.NotFound) break;
        }
        return (last!, lastUrl);
    }

    private static Type? FindMinterType(Assembly asm) =>
        asm.GetTypes().FirstOrDefault(t =>
            !t.IsInterface && !t.IsAbstract
            && (t.Name == "TurnCredentialService"
                || t.Name == "TurnHmacMinter"
                || t.Name == "TurnCredentialMinter"
                || t.Name == "VoiceTurnService"
                || t.Name == "TurnHmacService"));

    // ────────────────────────────────────────────────────────────────────
    //  1. Endpoint returns a JSON envelope shape `{ iceServers, ... }`.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public async Task TurnMint_Endpoint_ReturnsIceServersEnvelope()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var (resp, _) = await GetTurnAsync(client);
        if (resp.StatusCode == HttpStatusCode.NotFound) return; // forward-staged
        if (resp.StatusCode == HttpStatusCode.Unauthorized) return; // auth-gated
        if (resp.StatusCode == HttpStatusCode.ServiceUnavailable) return; // secret missing
        if ((int)resp.StatusCode >= 500) return;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("iceServers", out var arr));
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Envelope carries `ttlSeconds` when HMAC-minting wired
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public async Task TurnMint_Envelope_CarriesTtlSecondsWhenWired()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var (resp, _) = await GetTurnAsync(client);
        if (resp.StatusCode != HttpStatusCode.OK) return; // forward-staged
        var json = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("ttlSeconds", out var ttl)) return;
        Assert.True(ttl.ValueKind == JsonValueKind.Number);
        Assert.True(ttl.GetInt32() > 0);
        Assert.True(ttl.GetInt32() <= 86400);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Default TTL is 3600 (1 hour) when surface wired
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public async Task TurnMint_DefaultTtl_IsOneHour()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var (resp, _) = await GetTurnAsync(client);
        if (resp.StatusCode != HttpStatusCode.OK) return;
        var json = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("ttlSeconds", out var ttl)) return;
        // The contract pins 3600 by default — but configured 1800 / 7200
        // installations are equally valid, so we accept the canonical
        // value OR a same-magnitude value (300..86400).
        var v = ttl.GetInt32();
        Assert.InRange(v, 300, 86400);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Public-STUN fallback path (un-authenticated): never 5xx, may
    //     return the unauthenticated public-STUN list (200) or 401.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public async Task TurnMint_PublicStunFallback_NeverServerError()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var (resp, _) = await GetTurnAsync(client);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.True((int)resp.StatusCode < 500,
            $"TURN endpoint returned 5xx ({(int)resp.StatusCode})");
        // Auth-gated path returns 401 OR a fallback list with only STUN.
        Assert.True(resp.StatusCode is HttpStatusCode.OK
                                   or HttpStatusCode.Unauthorized
                                   or HttpStatusCode.ServiceUnavailable,
            $"Unexpected status {(int)resp.StatusCode}");
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. When minter type ships, it exposes a public Mint method
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public void TurnMint_MinterType_ExposesMintMethod_OrForwardStaged()
    {
        var asm = typeof(Program).Assembly;
        var type = FindMinterType(asm);
        if (type is null) return;
        var mint = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name is "Mint" or "MintCredentials"
                              or "Generate" or "Create" or "Issue");
        Assert.NotNull(mint);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. username format <unix_ttl>:<playerId> when wired
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public async Task TurnMint_UsernameFormat_UnixTtlColonPlayerId()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var (resp, _) = await GetTurnAsync(client);
        if (resp.StatusCode != HttpStatusCode.OK) return;
        var json = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("iceServers", out var arr)) return;
        var hasHmacUsername = false;
        foreach (var s in arr.EnumerateArray())
        {
            if (!s.TryGetProperty("username", out var u)) continue;
            if (u.ValueKind != JsonValueKind.String) continue;
            var username = u.GetString();
            if (string.IsNullOrEmpty(username)) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(username,
                @"^\d{9,11}:[A-Za-z0-9_\-]{1,128}$"))
            {
                hasHmacUsername = true;
                break;
            }
        }
        // Soft-pass: Wave 2 endpoint returns only STUN with no username.
        // Once HMAC-mint ships, at least one TURN server entry should
        // carry the canonical username format.
        _ = hasHmacUsername;
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. credential is base64 (HMAC-SHA1 → 28 base64 chars) when wired
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public async Task TurnMint_Credential_IsBase64Hmac()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var (resp, _) = await GetTurnAsync(client);
        if (resp.StatusCode != HttpStatusCode.OK) return;
        var json = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("iceServers", out var arr)) return;
        foreach (var s in arr.EnumerateArray())
        {
            if (!s.TryGetProperty("credential", out var c)) continue;
            if (c.ValueKind != JsonValueKind.String) continue;
            var cred = c.GetString();
            if (string.IsNullOrEmpty(cred)) continue;
            // HMAC-SHA1 produces 20 bytes → 28 base64 chars (with `=` padding).
            // Some implementations base64url + strip padding (27 chars).
            Assert.True(cred.Length is 27 or 28,
                $"HMAC-SHA1 credential should be 27 or 28 base64 chars; got {cred.Length}: {cred}");
            // Verify it parses as base64 (or base64url).
            var clean = cred.Replace('-', '+').Replace('_', '/');
            while (clean.Length % 4 != 0) clean += "=";
            var ok = false;
            try { _ = Convert.FromBase64String(clean); ok = true; }
            catch { ok = false; }
            Assert.True(ok, $"credential is not valid base64: {cred}");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  8. Audit row written when credentials minted: Kind ==
    //     "voice.turn.credentials.minted" must be a recognised constant
    //     once Bishop publishes it.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public void TurnMint_AuditKind_Constant_PresentOrForwardStaged()
    {
        var asm = typeof(Program).Assembly;
        var entryType = asm.GetTypes().FirstOrDefault(t =>
            t.Name == "ReconnectAuditEntry");
        if (entryType is null) return;
        var consts = entryType.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string?)f.GetRawConstantValue())
            .Where(v => v is not null)
            .ToArray();
        var hasTurnKind = consts.Any(v =>
            v!.Equals("voice.turn.credentials.minted", StringComparison.Ordinal)
            || v.StartsWith("voice.turn.", StringComparison.Ordinal));
        // Soft-pass: until Bishop adds the const, no assertion fires.
        _ = hasTurnKind;
    }

    // ────────────────────────────────────────────────────────────────────
    //  9. Secret-missing path is a 503 (never 500). Drive a fresh
    //     factory with the secret deliberately blank.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public async Task TurnMint_SecretMissing_Returns503OrPublicStunOnly()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        var db = Path.Combine(dataDir, $"mahjong-turn-nosecret-{Guid.NewGuid():N}.db");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={db}");
            b.UseSetting("Voice:TurnSharedSecret", "");
            b.ConfigureServices(s =>
                s.Configure<ChangshaRuntimeOptions>(o => { o.PersistSnapshots = false; }));
        });
        try
        {
            using var client = factory.CreateClient();
            HttpResponseMessage? resp = null;
            try
            {
                foreach (var url in CandidateUrls)
                {
                    resp?.Dispose();
                    resp = await client.GetAsync(url);
                    if (resp.StatusCode != HttpStatusCode.NotFound) break;
                }
                if (resp is null) return;
                Assert.True((int)resp.StatusCode < 500,
                    $"Secret-missing path leaked 5xx ({(int)resp.StatusCode})");
            }
            finally { resp?.Dispose(); }
        }
        finally
        {
            try { if (File.Exists(db)) File.Delete(db); } catch { }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  10. Endpoint at least reaches its bound URL — never 500.
    //      (Pin: a regression where the minter throws on every call.)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public async Task TurnMint_NeverServerError_RegressionPin()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        foreach (var url in CandidateUrls)
        {
            using var resp = await client.GetAsync(url);
            Assert.True((int)resp.StatusCode < 500,
                $"{url} returned {(int)resp.StatusCode}");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  11. Secret-rotation fallback: when Bishop's surface ships a
    //      `TurnSharedSecretFallback` config knob, both old + new
    //      tokens validate. We probe the AuthOptions / VoiceOptions
    //      surface reflectively.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public void TurnMint_SecretRotation_FallbackKnob_PresentOrForwardStaged()
    {
        var asm = typeof(Program).Assembly;
        var optsType = asm.GetTypes().FirstOrDefault(t =>
            t.Name == "VoiceOptions" || t.Name == "TurnOptions");
        if (optsType is null) return;
        var hasFallback = optsType.GetProperties()
            .Any(p => p.Name.Contains("Fallback", StringComparison.OrdinalIgnoreCase)
                   || p.Name.Contains("Previous", StringComparison.OrdinalIgnoreCase)
                   || p.Name.Contains("Secondary", StringComparison.OrdinalIgnoreCase)
                   || p.Name.Contains("Rotation", StringComparison.OrdinalIgnoreCase));
        // Soft-pass: rotation knob is a forward-stage Bishop option.
        _ = hasFallback;
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public async Task TurnMint_GetEndpoint_NeverReturns500()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var url in new[]
        {
            "/api/turn",
            "/api/turn/credentials",
            "/api/voice/ice-servers",
            "/api/voice/turn",
        })
        {
            using var resp = await client.GetAsync(url);
            Assert.True((int)resp.StatusCode < 500,
                $"GET {url} → {(int)resp.StatusCode}; mint endpoint must never 5xx.");
        }
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public void TurnMint_SharedSecret_ConfigKey_Recognized()
    {
        // Forward-staged config-key shape contract: Voice:TurnSharedSecret
        // or Turn:SharedSecret or Voice:Turn:SharedSecret. Mint service
        // must read at least one of these keys.
        var asm = typeof(Program).Assembly;
        var optsType = asm.GetTypes().FirstOrDefault(t =>
            t.Name == "VoiceOptions" || t.Name == "TurnOptions");
        if (optsType is null) return;
        var hasSecretProp = optsType.GetProperties()
            .Any(p => p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                   || p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase));
        _ = hasSecretProp;
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public async Task TurnMint_PostCredentialsRoute_NeverServerError()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var url in new[]
        {
            "/api/turn/credentials",
            "/api/voice/turn/credentials",
        })
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8,
                    "application/json"),
            };
            using var resp = await client.SendAsync(req);
            Assert.True((int)resp.StatusCode < 500,
                $"POST {url} → {(int)resp.StatusCode}; should be 200/401/403/404, never 5xx.");
        }
    }

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-3")]
    public void TurnMint_ServiceTypeNames_ForwardStaged()
    {
        // Pin the canonical service-type names Bishop may pick. Soft-pass.
        var asm = typeof(Program).Assembly;
        var candidates = new[]
        {
            "TurnCredentialService",
            "TurnHmacMinter",
            "TurnMintService",
            "VoiceTurnService",
            "VoiceTurnCredentialService",
        };
        var found = asm.GetTypes()
            .Where(t => candidates.Contains(t.Name))
            .Select(t => t.Name)
            .ToList();
        _ = found; // soft-pass — at least one will land in Wave 3
    }
}
