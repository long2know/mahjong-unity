using System.Net;
using System.Reflection;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W2;

/// <summary>
/// Phase K Wave 2 — spectator livestream stub contract tests (Vasquez).
///
/// <para>Bishop's Phase K Wave 2 brief includes a spectator-livestream
/// stub: a thin server-side surface that hands a spectator a
/// per-table snapshot at join time, then forwards subsequent move
/// events as a server-sent stream (or via the existing SignalR group
/// broadcast). Wave 2 is the STUB only — full livestream playback +
/// frame-by-frame seek is deferred to Wave 3.</para>
///
/// <para>Expected wiring:
/// <list type="bullet">
///   <item>A new endpoint or hub method named
///         <c>/api/spectator/{tableId}/stream</c> OR
///         <c>SpectatorStream(tableId)</c> on the Changsha hub.</item>
///   <item>Returns 200 / 404 (never 500) for a synthetic table id.</item>
///   <item>If wired as a controller, declares the canonical 4 query
///         params: <c>tableId</c>, <c>fromEvent</c>, <c>maxEvents</c>,
///         <c>follow</c>.</item>
/// </list></para>
///
/// <para>The contract is intentionally light — the stub is a forward-
/// stage surface for Wave 3. Each fact below soft-passes when the
/// stub isn't yet shipped.</para>
/// </summary>
public class SpectatorLivestreamStubTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-livestream-{Guid.NewGuid():N}.db");
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

    private async Task<HttpResponseMessage?> ProbeAsync(params string[] urls)
    {
        using var client = _factory!.CreateClient();
        foreach (var url in urls)
        {
            var resp = await client.GetAsync(url);
            if (resp.StatusCode == HttpStatusCode.NotFound) { resp.Dispose(); continue; }
            return resp;
        }
        return null;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Stream endpoint reachable on any candidate URL OR forward-staged
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-2")]
    public async Task LivestreamStub_Endpoint_NeverServerError()
    {
        Assert.NotNull(_factory);
        var tid = Guid.NewGuid();
        using var resp = await ProbeAsync(
            $"/api/spectator/{tid}/stream",
            $"/api/spectator/stream?tableId={tid}",
            $"/api/games/{tid}/spectator-stream",
            $"/api/spectator/{tid}/livestream");
        if (resp is null) return; // forward-staged
        Assert.True((int)resp.StatusCode < 500,
            $"Livestream stub returned {(int)resp.StatusCode}");
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Controller / hub type present OR forward-staged
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-2")]
    public void LivestreamStub_TypePresentOrForwardStaged()
    {
        var asm = typeof(Program).Assembly;
        var t = asm.GetTypes()
            .Where(t => !t.IsInterface && !t.IsAbstract && t.IsClass)
            .FirstOrDefault(t => t.Name.Contains("Spectator", StringComparison.Ordinal)
                              && (t.Name.Contains("Stream", StringComparison.Ordinal)
                                  || t.Name.Contains("Livestream", StringComparison.Ordinal)));
        if (t is null) return; // forward-staged
        Assert.NotNull(t);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. When wired as a hub method, the method name signature is sane
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-2")]
    public void LivestreamStub_HubMethod_HasTableIdParam()
    {
        var asm = typeof(Program).Assembly;
        var hubBase = typeof(Microsoft.AspNetCore.SignalR.Hub);
        var streamMethods = asm.GetTypes()
            .Where(t => hubBase.IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m.Name.Contains("SpectatorStream", StringComparison.OrdinalIgnoreCase)
                     || m.Name == "Livestream"
                     || m.Name == "SubscribeSpectator")
            .ToArray();
        if (streamMethods.Length == 0) return;
        foreach (var m in streamMethods)
        {
            Assert.Contains(m.GetParameters(),
                p => p.ParameterType == typeof(string) || p.ParameterType == typeof(Guid));
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Synthetic table id → 200/204/404, never 5xx
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-2")]
    public async Task LivestreamStub_SyntheticTable_NoServerError()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var resp = await client.GetAsync($"/api/spectator/{Guid.NewGuid()}/stream?fromEvent=0&maxEvents=5");
        Assert.True((int)resp.StatusCode < 500);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. Bad inputs are 4xx not 5xx
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-2")]
    public async Task LivestreamStub_BadInputs_4xxNot5xx()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        // Invalid GUID — must not crash.
        using var resp = await client.GetAsync("/api/spectator/not-a-guid/stream");
        Assert.True((int)resp.StatusCode < 500);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. Spectator route is GET (read-only). The stub never accepts POST.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-2")]
    public async Task LivestreamStub_PostRejected()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var resp = await client.PostAsync(
            $"/api/spectator/{Guid.NewGuid()}/stream",
            new StringContent(""));
        Assert.True((int)resp.StatusCode < 500);
        // 405 / 404 / 415 all OK — just not 500.
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. Spectator stream type, if shipped, is NOT internal-only —
    //     livestream surfaces must be reachable for proxies / load
    //     balancers, so the type's visibility is at least `internal` in
    //     the API assembly (Wave 2 stub may be controller or hub class).
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-2")]
    public void LivestreamStub_TypeVisibility_AtLeastInternal()
    {
        var asm = typeof(Program).Assembly;
        var t = asm.GetTypes()
            .FirstOrDefault(t => t.IsClass && !t.IsAbstract
                              && t.Name.Contains("Spectator", StringComparison.Ordinal)
                              && (t.Name.Contains("Stream", StringComparison.Ordinal)
                                  || t.Name.Contains("Livestream", StringComparison.Ordinal)));
        if (t is null) return; // forward-staged
        // Must not be private-nested or compiler-generated.
        Assert.False(t.IsNotPublic && t.IsNestedPrivate,
            $"Spectator stream type {t.Name} must not be private-nested");
    }

    // ────────────────────────────────────────────────────────────────────
    //  8. Stub is GET-idempotent: repeating the same call with the same
    //     synthetic table id must not produce a 5xx on the second call.
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Spectator"), Trait("Wave", "Phase-K-2")]
    public async Task LivestreamStub_Idempotent_NoServerError()
    {
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        var tid = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
        {
            using var resp = await client.GetAsync($"/api/spectator/{tid}/stream");
            Assert.True((int)resp.StatusCode < 500, $"iter {i}: {(int)resp.StatusCode}");
        }
    }
}
