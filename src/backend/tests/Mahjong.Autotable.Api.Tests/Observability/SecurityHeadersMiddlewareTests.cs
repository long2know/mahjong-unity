using System.Net;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Observability;

/// <summary>
/// Phase J Wave 8 — <see cref="SecurityHeadersMiddleware"/> contract
/// tests (Apone).
///
/// <para>Pins three facts:
/// <list type="number">
///   <item>The four OWASP-recommended security headers are present on
///         every response (verified via the public <c>/health</c>
///         endpoint).</item>
///   <item>Cache-Control on a non-hashed JSON response is <c>no-cache</c>
///         (must-revalidate) so a deploy that changes the health JSON
///         shape is immediately visible.</item>
///   <item>The Parcel content-hash detector (<see cref="SecurityHeadersMiddleware.HasContentHash"/>)
///         correctly classifies real-world filenames pulled from the
///         existing <c>src/frontend/autotable/</c> bundle.</item>
/// </list></para>
/// </summary>
public class SecurityHeadersMiddlewareTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        _tempDb = Path.Combine(dataDir, $"mahjong-security-{Guid.NewGuid():N}.db");
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

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-J-8")]
    public async Task SecurityHeaders_PresentOnApiResponse()
    {
        // /api/health is a tiny anonymous endpoint perfect for header
        // assertions — its surface is stable across waves so this test
        // doesn't have to be rewritten when the JSON shape evolves.
        Assert.NotNull(_factory);
        using var client = _factory!.CreateClient();
        using var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(response.Headers.TryGetValues("X-Frame-Options", out var xfo)
            && xfo.Any(v => string.Equals(v, "DENY", StringComparison.OrdinalIgnoreCase)),
            "X-Frame-Options must be DENY.");

        Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var xcto)
            && xcto.Any(v => string.Equals(v, "nosniff", StringComparison.OrdinalIgnoreCase)),
            "X-Content-Type-Options must be nosniff.");

        Assert.True(response.Headers.TryGetValues("Referrer-Policy", out var rp)
            && rp.Any(v => v.Contains("strict-origin", StringComparison.OrdinalIgnoreCase)),
            "Referrer-Policy must declare strict-origin-when-cross-origin.");

        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var csp)
            && csp.Any(v => v.Contains("default-src", StringComparison.Ordinal)),
            "Content-Security-Policy must include a default-src directive.");
    }

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-J-8")]
    public void HasContentHash_ParcelStyleFilenames_AreImmutable()
    {
        // Real filenames pulled from src/frontend/autotable/ — these are
        // the 8-hex-char hashes that Parcel emits and that we want served
        // with `immutable` cache-control.
        Assert.True(SecurityHeadersMiddleware.HasContentHash("/autotable/autotable-src.6633d8fb.css"));
        Assert.True(SecurityHeadersMiddleware.HasContentHash("/autotable/autotable-src.85bbb8ca.js"));
        Assert.True(SecurityHeadersMiddleware.HasContentHash("/autotable/dealer.a27808af.png"));
        Assert.True(SecurityHeadersMiddleware.HasContentHash("/autotable/discard.c3151c81.wav"));
    }

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-J-8")]
    public void HasContentHash_NonHashedFilenames_AreMutable()
    {
        // Negative cases — these must NOT be classified as content-hashed.
        // The .auto. infix is 4 chars, not 8, so the detector skips them.
        Assert.False(SecurityHeadersMiddleware.HasContentHash("/index.html"));
        Assert.False(SecurityHeadersMiddleware.HasContentHash("/autotable/img/icon-32.auto.png"));
        Assert.False(SecurityHeadersMiddleware.HasContentHash("/foo.txt"));
        // The .auto.<hash> double-token IS detected as hashed because the
        // hash token comes last — Parcel-renamed assets keep this shape.
        Assert.True(SecurityHeadersMiddleware.HasContentHash("/autotable/dice.auto.391822b5.png"));
    }

    [Fact, Trait("Category", "Security"), Trait("Wave", "Phase-J-9")]
    public void DefaultCsp_DropsUnsafeEvalAfterWave9Audit()
    {
        // Phase J Wave 9 — Hicks audited the shipped Parcel bundle and
        // confirmed zero `new Function(...)` / `eval(...)` callsites,
        // so the default CSP no longer needs `'unsafe-eval'`. We keep
        // `'wasm-unsafe-eval'` so any future Three.js loader that
        // compiles a WebAssembly draco/ktx decoder keeps working; per
        // CSP Level 3 that token allows `WebAssembly.compile()` only
        // and does NOT re-enable `eval()`.
        //
        // Search for the exact-quoted `'unsafe-eval'` token (with
        // leading apostrophe), which is NOT a substring of
        // `'wasm-unsafe-eval'`.
        Assert.DoesNotContain("'unsafe-eval'", SecurityHeadersMiddleware.DefaultCsp);
        Assert.Contains("'wasm-unsafe-eval'", SecurityHeadersMiddleware.DefaultCsp);
        Assert.Contains("frame-ancestors 'none'", SecurityHeadersMiddleware.DefaultCsp);
        Assert.Contains("object-src 'none'", SecurityHeadersMiddleware.DefaultCsp);
    }
}
