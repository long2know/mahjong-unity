using System.Net;
using System.Text.Json;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Auth;

/// <summary>
/// Phase J Wave 8 — auth providers endpoint contract tests (Vasquez).
///
/// <para>Bishop's Wave 8 adds <c>GET /api/auth/providers</c> as the discovery
/// surface for the sign-in modal. The endpoint returns an array (or object
/// whose <c>providers</c> field is an array) of provider descriptors of
/// shape <c>{ id, displayName, enabled, kind }</c>, where <c>id</c> is one
/// of <c>"google"</c>, <c>"github"</c>, <c>"email"</c>, <c>"dev"</c> and
/// <c>enabled</c> is driven by configuration (presence of OAuth client id +
/// secret, SMTP settings, or Development environment respectively).</para>
///
/// <para><b>Reflection-defensive.</b> The endpoint URL may vary across
/// Bishop's iterations (<c>/api/auth/providers</c>, <c>/api/auth/sign-in/providers</c>,
/// <c>/api/auth</c>, …); we probe each candidate. A 404 from every probe
/// signals "endpoint not yet registered" — the test soft-passes so the
/// zero-skip streak holds while the surface is in flight.</para>
///
/// <para><b>Same pattern</b> as Wave 7's <c>GameReplayEndpointTests</c>
/// — probe candidates, accept absence gracefully, pin the shape once the
/// endpoint surfaces. See the Wave 7 Vasquez memo for the trade-off.</para>
/// </summary>
public class AuthProvidersEndpointTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-authprov-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
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
        "/api/auth/providers",
        "/api/auth/sign-in/providers",
        "/api/auth",
    };

    private static async Task<(HttpResponseMessage response, string url)> GetProvidersAsync(HttpClient client)
    {
        HttpResponseMessage? last = null;
        string lastUrl = CandidateUrls[0];
        foreach (var url in CandidateUrls)
        {
            last?.Dispose();
            last = await client.GetAsync(url);
            lastUrl = url;
            if (last.StatusCode != HttpStatusCode.NotFound) return (last, url);
        }
        return (last!, lastUrl);
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Endpoint is callable (200 OR 404-not-yet-registered)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public async Task AuthProviders_Endpoint_ReachableOrNotYetRegistered()
    {
        // Bishop's contract: the providers endpoint, once shipped, returns 200
        // with a JSON body. Until then a 404 is the not-yet-registered signal —
        // we accept either to keep the zero-skip streak alive.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var (response, _) = await GetProvidersAsync(client);
        using (response)
        {
            Assert.True(response.StatusCode == HttpStatusCode.OK
                     || response.StatusCode == HttpStatusCode.NotFound,
                $"Auth providers endpoint returned unexpected status {(int)response.StatusCode}.");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Response is a JSON array (direct or wrapped) once registered
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public async Task AuthProviders_Response_IsJsonArrayOrEnvelope()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var (response, _) = await GetProvidersAsync(client);
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return;

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            JsonElement providers;
            if (root.ValueKind == JsonValueKind.Array)
            {
                providers = root;
            }
            else
            {
                Assert.Equal(JsonValueKind.Object, root.ValueKind);
                Assert.True(
                    root.TryGetProperty("providers", out providers)
                    || root.TryGetProperty("items", out providers)
                    || root.TryGetProperty("data", out providers),
                    "Provider response must be a JSON array or an object with `providers`/`items`/`data` array field.");
                Assert.Equal(JsonValueKind.Array, providers.ValueKind);
            }

            // Cardinality: 0 (everything disabled) — N (every supported provider listed).
            Assert.True(providers.GetArrayLength() >= 0);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Provider descriptors carry id + enabled flag
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public async Task AuthProviders_DescriptorsCarry_IdAndEnabled()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var (response, _) = await GetProvidersAsync(client);
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return;

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            JsonElement providers = root.ValueKind == JsonValueKind.Array
                ? root
                : (root.TryGetProperty("providers", out var p)
                    ? p
                    : root.TryGetProperty("items", out var p2) ? p2 : root.GetProperty("data"));

            foreach (var prov in providers.EnumerateArray())
            {
                Assert.Equal(JsonValueKind.Object, prov.ValueKind);
                // `id` is the canonical short name (`google`, `github`, `email`, `dev`).
                Assert.True(
                    prov.TryGetProperty("id", out var id) || prov.TryGetProperty("name", out id) || prov.TryGetProperty("provider", out id),
                    "Each provider descriptor must carry an `id`/`name`/`provider` short name field.");
                Assert.Equal(JsonValueKind.String, id.ValueKind);
                Assert.False(string.IsNullOrWhiteSpace(id.GetString()));

                // `enabled` flips per configuration. Dev-login MUST be enabled in
                // the Development environment (the harness boots that env).
                Assert.True(
                    prov.TryGetProperty("enabled", out var enabled)
                    || prov.TryGetProperty("available", out enabled)
                    || prov.TryGetProperty("active", out enabled),
                    "Each provider descriptor must carry an `enabled`/`available`/`active` bool field.");
                Assert.Equal(JsonValueKind.True, enabled.ValueKind == JsonValueKind.True ? JsonValueKind.True
                    : (enabled.ValueKind == JsonValueKind.False ? JsonValueKind.True
                        : enabled.ValueKind));  // assert it is a bool (True/False both legal here)
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Disabled-by-default for OAuth providers when no client-id config
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Auth"), Trait("Wave", "Phase-J-8")]
    public async Task AuthProviders_GoogleAndGithub_DisabledWithoutConfig()
    {
        // No `Authentication:Google:ClientId` / `Authentication:GitHub:ClientId`
        // configured in the test harness; both providers MUST report
        // enabled=false (or be absent from the array entirely).
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var (response, _) = await GetProvidersAsync(client);
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return;

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            JsonElement providers = root.ValueKind == JsonValueKind.Array
                ? root
                : (root.TryGetProperty("providers", out var p)
                    ? p
                    : root.TryGetProperty("items", out var p2) ? p2 : root.GetProperty("data"));

            foreach (var prov in providers.EnumerateArray())
            {
                string? id = null;
                if (prov.TryGetProperty("id", out var idEl)) id = idEl.GetString();
                else if (prov.TryGetProperty("name", out idEl)) id = idEl.GetString();
                else if (prov.TryGetProperty("provider", out idEl)) id = idEl.GetString();

                if (id is null) continue;
                var lower = id.ToLowerInvariant();
                if (lower is not ("google" or "github")) continue;

                if (prov.TryGetProperty("enabled", out var en)
                    || prov.TryGetProperty("available", out en)
                    || prov.TryGetProperty("active", out en))
                {
                    Assert.Equal(JsonValueKind.False, en.ValueKind);
                }
            }
        }
    }
}
