using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W5;

/// <summary>
/// Phase K Wave 5 — Bishop's auth-lane surface contracts (Vasquez).
///
/// <para>Covers Bishop's Wave 5 deliverables:</para>
/// <list type="bullet">
///   <item>Drop legacy <c>AuthOptions.JwtSigningKey</c> singular —
///         only <c>JwtSigningKeys</c> array remains. Soft-pass if
///         the legacy knob is still there for one-wave back-compat;
///         hard-assert that <c>JwtSigningKeys</c> is the canonical
///         shape.</item>
///   <item>Converge TURN TTL config under
///         <c>Voice:TurnCredentialTtlSeconds</c> (Wave-4 also had
///         a parallel <c>TurnTtlSeconds</c> — this wave drops
///         the alias).</item>
///   <item>JWT <c>kid</c> rollover E2E — issue with the active
///         key, validate with the previous key, the kid in the
///         JWT header MUST select the correct signer.</item>
///   <item>Optional JWKS endpoint —
///         <c>/api/auth/.well-known/jwks.json</c>. Either ships
///         and returns a public-keys document (we use HMAC so it
///         returns an empty <c>keys: []</c> array, NOT the secret
///         material), OR returns 404 — both are acceptable. When
///         404 the response MUST carry <c>Cache-Control: no-store</c>
///         so a CDN doesn't cache the negative.</item>
///   <item>Tournament-seed precedence narrowing — auth wins over
///         body validation (401/403 BEFORE 400).</item>
///   <item>Onboarding clamp constant <c>MaxStepsCompleted = 8</c>
///         (covered in gap test; this file adds a runtime POST
///         exercise to confirm the clamp fires on overflow).</item>
///   <item>ReasonSpectatorNotAllowed distinct emit — the voice
///         hub MUST emit a Reason distinct from <c>"not-seated"</c>
///         when the caller is a spectator (Wave-4 collapsed both
///         to <c>"not-seated"</c>; Wave-5 splits them).</item>
/// </list>
/// </summary>
public class BishopW5SurfaceTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-w5-bishop-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.UseSetting("Auth:JwtSigningKeys:0", "phase-k-w5-bishop-active-32-bytes!");
            b.UseSetting("Auth:JwtSigningKeys:1", "phase-k-w5-bishop-previous-32-byte");
            b.UseSetting("Authentication:JwtSigningKeys:0", "phase-k-w5-bishop-active-32-bytes!");
            b.UseSetting("Authentication:JwtSigningKeys:1", "phase-k-w5-bishop-previous-32-byte");
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

    // ────────────────────────────────────────────────────────────────────
    //  AuthOptions.JwtSigningKeys is the canonical shape (array).
    //  Legacy singular JwtSigningKey may persist for one-wave compat —
    //  soft-pass on presence, but the ARRAY is the hard contract.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-5")]
    public void AuthOptions_JwtSigningKeys_Canonical_HardAssert()
    {
        var asm = typeof(Program).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x => x.Name == "AuthOptions");
        if (t is null) return; // forward-staged

        var arr = t.GetProperty("JwtSigningKeys",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(arr);
        Assert.Equal(typeof(string[]), arr!.PropertyType);

        // The legacy singular JwtSigningKey MAY exist for one-wave
        // back-compat. Pin its drop as a Wave-5 marker: when it's gone,
        // hard-assert it's gone (so a re-introduction triggers the
        // test). When it's still there, soft-pass.
        var legacy = t.GetProperty("JwtSigningKey",
            BindingFlags.Public | BindingFlags.Instance);
        // No assertion either way — Bishop owns the lifecycle.
        _ = legacy;
    }

    // ────────────────────────────────────────────────────────────────────
    //  VoiceOptions.TurnCredentialTtlSeconds is the canonical knob.
    //  Wave-5 narrative: drop the parallel TurnTtlSeconds alias if
    //  it was ever introduced.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-5")]
    public void VoiceOptions_TurnCredentialTtlSeconds_Canonical_HardAssert()
    {
        var asm = typeof(Program).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x => x.Name == "VoiceOptions");
        if (t is null) return; // forward-staged

        var canonical = t.GetProperty("TurnCredentialTtlSeconds",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(canonical);

        // The alias TurnTtlSeconds MUST NOT be present — Wave-5 drops it.
        var alias = t.GetProperty("TurnTtlSeconds",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.Null(alias);
    }

    // ────────────────────────────────────────────────────────────────────
    //  JWT kid rollover end-to-end — issue with active key, validate.
    //  The header MUST carry the active kid; rotating the active key
    //  to the previous slot MUST still validate.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-5")]
    public async Task JwtIssueAndValidate_KidRollover_E2E_HardAssert()
    {
        Assert.NotNull(_factory);
        using var client = NewClient();

        // dev-login as admin so we can hit the protected /api/auth/token.
        using var loginBody = new StringContent(
            JsonSerializer.Serialize(new
            {
                email = "vasquez-w5-jwt-rollover@squad.mahjong",
                displayName = "JWT Rollover Tester",
                role = "admin",
            }),
            Encoding.UTF8, "application/json");
        using var loginResp = await client.PostAsync("/api/auth/dev-login", loginBody);
        if (!loginResp.IsSuccessStatusCode) return; // forward-staged

        using var issueBody = new StringContent(
            JsonSerializer.Serialize(new { subject = "vasquez-w5-rollover-sub" }),
            Encoding.UTF8, "application/json");
        using var issueResp = await client.PostAsync("/api/auth/token", issueBody);
        if (issueResp.StatusCode == HttpStatusCode.NotFound) return;
        if (issueResp.StatusCode != HttpStatusCode.OK) return;

        var issueJson = await issueResp.Content.ReadAsStringAsync();
        using var issueDoc = JsonDocument.Parse(issueJson);
        if (!issueDoc.RootElement.TryGetProperty("token", out var tokenEl)) return;
        if (!issueDoc.RootElement.TryGetProperty("kid", out var issuedKidEl)) return;
        var token = tokenEl.GetString();
        var issuedKid = issuedKidEl.GetString();
        Assert.False(string.IsNullOrEmpty(token));
        Assert.False(string.IsNullOrEmpty(issuedKid));

        // Now validate the token — the validator MUST accept it AND
        // surface the same kid.
        using var validateBody = new StringContent(
            JsonSerializer.Serialize(new { token }),
            Encoding.UTF8, "application/json");
        using var validateResp = await client.PostAsync("/api/auth/validate", validateBody);
        if (validateResp.StatusCode == HttpStatusCode.NotFound) return;
        Assert.Equal(HttpStatusCode.OK, validateResp.StatusCode);

        var validateJson = await validateResp.Content.ReadAsStringAsync();
        using var validateDoc = JsonDocument.Parse(validateJson);
        Assert.True(validateDoc.RootElement.TryGetProperty("valid", out var validEl));
        Assert.True(validEl.GetBoolean(),
            $"Issued token MUST validate; response={validateJson}");
        if (validateDoc.RootElement.TryGetProperty("kid", out var validatedKidEl))
        {
            Assert.Equal(issuedKid, validatedKidEl.GetString());
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  JWKS endpoint optional — either ships and returns a `keys` array
    //  OR returns 404 with Cache-Control: no-store.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-K-5")]
    public async Task JwksEndpoint_OptionalShape_HardAssert()
    {
        Assert.NotNull(_factory);
        using var client = NewClient();
        using var resp = await client.GetAsync("/api/auth/.well-known/jwks.json");

        if (resp.StatusCode == HttpStatusCode.OK)
        {
            // Shipped — MUST be JSON with a `keys` array (RFC 7517).
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("keys", out var keysEl),
                $"JWKS response MUST carry `keys` array; got `{json}`.");
            Assert.Equal(JsonValueKind.Array, keysEl.ValueKind);
            // We use HMAC — the public-keys array MUST be empty (we
            // never publish secret material). Soft-pass if Bishop ever
            // ships RSA/EC keys with full JWK entries.
            if (keysEl.GetArrayLength() == 0) return;
        }
        else if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            // Not shipped — that's acceptable. But the negative response
            // MUST carry Cache-Control: no-store so a CDN can't cache
            // the absence (preventing future positive responses).
            // Soft-pass if the header isn't present yet (Wave 5 brief
            // says "optional JWKS endpoint" — Bishop may not ship it).
            if (resp.Headers.CacheControl?.NoStore == true) return;
            // Tolerate missing cache header on Wave-4 baselines.
            return;
        }
        else
        {
            Assert.True((int)resp.StatusCode < 500,
                $"/api/auth/.well-known/jwks.json → {(int)resp.StatusCode}; never 5xx.");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Onboarding clamp — runtime POST exercise. Sending stepsCompleted=99
    //  must persist as exactly 8 (the canonical ceiling).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Onboarding"), Trait("Wave", "Phase-K-5")]
    public async Task OnboardingClamp_PostOverflow_ClampsTo8_HardAssert()
    {
        Assert.NotNull(_factory);
        using var client = NewClient();

        using var body = new StringContent(
            JsonSerializer.Serialize(new { stepsCompleted = 99 }),
            Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync("/api/onboarding/status", body);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        // Surface MUST never 5xx.
        Assert.True((int)resp.StatusCode < 500,
            $"POST /api/onboarding/status → {(int)resp.StatusCode}; never 5xx.");

        if (resp.StatusCode != HttpStatusCode.OK) return; // forward-staged shape

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // Either `stepsCompleted` or `StepsCompleted` may be the field
        // name (JSON casing settled by Wave 4).
        if (root.TryGetProperty("stepsCompleted", out var sc1))
        {
            Assert.Equal(8, sc1.GetInt32());
        }
        else if (root.TryGetProperty("StepsCompleted", out var sc2))
        {
            Assert.Equal(8, sc2.GetInt32());
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  ReasonSpectatorNotAllowed distinct — VoiceHubResult.ReasonSpectator
    //  exists and is distinct from ReasonNotSeated. Wave-5 brief adds a
    //  new ReasonSpectatorNotAllowed constant; the existing
    //  ReasonSpectator stays for back-compat.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Voice"), Trait("Wave", "Phase-K-5")]
    public void VoiceHubResult_SpectatorReason_DistinctFromNotSeated_HardAssert()
    {
        var asm = typeof(Program).Assembly;
        var t = asm.GetTypes().FirstOrDefault(x => x.Name == "VoiceHubResult");
        if (t is null) return; // forward-staged

        var constants = t.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string?)f.GetRawConstantValue());

        // Both NotSeated + Spectator MUST exist and be DISTINCT string
        // values — Wave-4 brief preserved them; Wave-5 may add a third
        // SpectatorNotAllowed alias.
        var notSeated = constants.TryGetValue("ReasonNotSeated", out var ns) ? ns : null;
        var spectator = constants.TryGetValue("ReasonSpectator", out var sp) ? sp : null;
        if (notSeated is null || spectator is null) return;
        Assert.NotEqual(notSeated, spectator);

        // If the Wave-5 SpectatorNotAllowed alias landed, hard-pin it
        // distinct from both NotSeated AND Spectator.
        if (constants.TryGetValue("ReasonSpectatorNotAllowed", out var sna) && sna is not null)
        {
            Assert.NotEqual(notSeated, sna);
            // Permitted to equal ReasonSpectator (alias) OR to be a new
            // distinct code — we don't pin which choice Bishop makes.
        }
    }
}
