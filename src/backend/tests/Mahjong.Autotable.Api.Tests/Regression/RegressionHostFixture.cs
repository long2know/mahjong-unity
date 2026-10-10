using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Regression;

/// <summary>
/// Phase K Wave 5 — Vasquez. Shared <see cref="WebApplicationFactory{TEntryPoint}"/>
/// host for the cross-wave regression class.
///
/// <para><b>Why a CollectionFixture?</b> Wave-4 ran the regression
/// class as a per-class <see cref="IAsyncLifetime"/>, which under high
/// xunit parallelism (8+ cores) intermittently surfaced an
/// <c>ObjectDisposedException</c> on
/// <c>IServiceProvider</c> during <c>InitializeAsync</c>. The flake
/// was narrow enough that <c>maxParallelThreads=2</c> dampened it,
/// but the workaround pinned wall-clock cost.</para>
///
/// <para><b>Wave 5 fix.</b> This fixture owns the factory + temp DB
/// lifecycle as a SHARED instance across every test that consumes the
/// <c>regression-host</c> collection. The factory is constructed ONCE
/// (<see cref="InitializeAsync"/>) and disposed ONCE
/// (<see cref="DisposeAsync"/>); per-test work just calls
/// <see cref="CreateClient"/>. The disposal race goes away by
/// construction — there's no concurrent
/// construct-and-tear-down anymore.</para>
///
/// <para>Together with the collection definition below, this lets the
/// regression suite run at default xunit parallelism (no
/// <c>xunit.runner.json</c> override needed) while staying green.</para>
/// </summary>
public sealed class RegressionHostFixture : IAsyncLifetime
{
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public string TempDb { get; private set; } = null!;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        TempDb = Path.Combine(dataDir, $"mahjong-reg-w5-{Guid.NewGuid():N}.db");
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // Wave 8 CSP only lands in Production — the regression class
            // pins prod-shape headers, so we keep this environment.
            b.UseEnvironment("Production");
            // Phase L — Drake. Prod hardening: JwtSigningKeyProvider now
            // refuses to boot in Production with an ephemeral random
            // HMAC key. Supply a stable test key so the factory starts.
            // See docs/jwt-rotation.md §7.
            b.UseSetting("Auth:JwtSigningKeys:0", "test-prod-stable-jwt-key-aaaaaaaaaaaaaaaaaaaaaaaa");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={TempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.PersistSnapshots = false;
                });
            });
        });
        _ = Factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Factory.Dispose(); } catch { /* tolerate double-dispose race */ }
        try { if (File.Exists(TempDb)) File.Delete(TempDb); } catch { }
        return Task.CompletedTask;
    }

    public HttpClient CreateClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });
}

/// <summary>
/// xunit collection definition binding all consumers of the
/// regression host into a single test collection so the
/// <see cref="RegressionHostFixture"/> is shared across them.
/// </summary>
[CollectionDefinition(Name)]
public sealed class RegressionHostCollection : ICollectionFixture<RegressionHostFixture>
{
    public const string Name = "regression-host";
}
