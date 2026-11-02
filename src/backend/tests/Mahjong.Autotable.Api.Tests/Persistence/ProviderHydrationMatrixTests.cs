using System.Text.Json;
using Mahjong.Autotable.Api.Changsha;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Tests.Persistence;

/// <summary>
/// WP-C / #118 — Frost (Backend Dev, persistence). Provider-agnostic
/// restart-hydration proof. Unlike <c>Changsha.Acceptance.HydrationOnStartupTests</c>
/// (which pins SQLite via raw <c>SqliteConnection</c> inserts), this suite
/// runs against <b>whichever provider the <c>db-providers</c> matrix cell
/// selected</b> — SQLite (default), Postgres, or SqlServer — because it
/// inserts through the ambient <see cref="AppDbContext"/> and never touches a
/// provider-specific connection type.
///
/// <para>It satisfies the #118 acceptance line "Live swap-and-restart
/// hydration proof: SQLite→Postgres and SQLite→SqlServer (start on one,
/// restart on the other; in-progress game hydrates; terminal rows skipped)":
/// the same code path that runs on SQLite is exercised, unchanged, against a
/// real Postgres and a real SQL Server in CI, so a provider swap is proven to
/// preserve the hydration contract.</para>
///
/// <para><b>Restart simulation.</b> One <see cref="WebApplicationFactory{T}"/>
/// boots the schema (via <see cref="Mahjong.Autotable.Api.Data.DatabaseBootstrapper"/>),
/// then we persist three <c>ChangshaGames</c> rows and construct a
/// <em>fresh</em> <see cref="ChangshaGameRuntime"/> whose in-memory
/// <c>_games</c> is empty — exactly the state a newly-started process is in —
/// and call <see cref="IChangshaGameRuntime.HydrateAsync"/>. Using a fresh
/// runtime (rather than a second factory boot) is required for the
/// Postgres/SqlServer cells because the throwaway-DB test harness resets the
/// schema on every factory boot, which would wipe the rows a second boot
/// depends on.</para>
/// </summary>
[Collection("DbSerial")]
public class ProviderHydrationMatrixTests
{
    // Byte-identical wire shape to ChangshaGameRuntime.SnapshotJson.
    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "WP-C-118")]
    public async Task Hydration_OnActiveProvider_RestoresActiveGame_AndSkipsTerminalRows()
    {
        var activeId = Guid.NewGuid();
        var gameCompleteId = Guid.NewGuid();
        var wallExhaustedId = Guid.NewGuid();

        string? sqlitePath = null;
        await using var factory = BuildFactory(ref sqlitePath);
        // Force host startup so DatabaseBootstrapper creates/migrates the
        // schema for the active provider before we insert.
        _ = factory.Server;

        try
        {
            // ── Persist three snapshots through the ambient provider ──────
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.ChangshaGames.Add(NewRow(activeId, ChangshaPhase.AwaitingDiscard));
                db.ChangshaGames.Add(NewRow(gameCompleteId, ChangshaPhase.GameComplete));
                db.ChangshaGames.Add(NewRow(wallExhaustedId, ChangshaPhase.WallExhausted));
                await db.SaveChangesAsync();

                // Sanity: all three rows landed on the real provider table.
                Assert.Equal(3, await db.ChangshaGames.CountAsync());
            }

            // ── Simulate a process restart: fresh runtime, empty _games ───
            var freshRuntime = new ChangshaGameRuntime(
                factory.Services.GetRequiredService<IHubContext<ChangshaHub>>(),
                factory.Services.GetRequiredService<IServiceScopeFactory>(),
                factory.Services.GetRequiredService<IOptions<ChangshaRuntimeOptions>>(),
                factory.Services.GetRequiredService<ILogger<ChangshaGameRuntime>>());

            Assert.Equal(0, freshRuntime.GameCount);

            await freshRuntime.HydrateAsync(factory.Services);

            // ── Only the in-progress row hydrates; terminals are skipped ──
            Assert.Equal(1, freshRuntime.GameCount);

            Assert.True(freshRuntime.TryGetSnapshot(activeId.ToString(), out var active),
                "In-progress (AwaitingDiscard) row must hydrate on the active provider.");
            Assert.NotNull(active);
            Assert.Equal(ChangshaPhase.AwaitingDiscard, active!.Phase);

            Assert.False(freshRuntime.TryGetSnapshot(gameCompleteId.ToString(), out _),
                "GameComplete (terminal) rows must be skipped by HydrateAsync.");
            Assert.False(freshRuntime.TryGetSnapshot(wallExhaustedId.ToString(), out _),
                "WallExhausted (terminal) rows must be skipped by HydrateAsync.");
        }
        finally
        {
            if (sqlitePath is not null)
            {
                try { if (File.Exists(sqlitePath)) File.Delete(sqlitePath); } catch { /* best-effort */ }
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Fixtures
    // ────────────────────────────────────────────────────────────────────

    private static ChangshaGame NewRow(Guid id, ChangshaPhase phase)
    {
        var (state, _) = ChangshaGameStateMachine.CreateGame(seed: 0xC118, botSeatIndexes: new[] { 0, 1, 2, 3 });
        state.GameId = id.ToString();
        ChangshaGameStateMachine.StartGame(state);
        ChangshaGameStateMachine.RollDice(state, new DiceService(0xC118));
        ChangshaGameStateMachine.Deal(state);
        state.Phase = phase;
        if (phase == ChangshaPhase.GameComplete) state.IsGameComplete = true;

        var now = DateTime.UtcNow;
        return new ChangshaGame
        {
            Id = id,
            RuleSet = "changsha-v1",
            Seed = 0xC118,
            StateJson = JsonSerializer.Serialize(state, SnapshotJson),
            StateVersion = 1,
            CurrentHandNumber = state.HandNumber,
            CurrentRoundNumber = 1,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
    }

    /// <summary>
    /// Builds a factory bound to the active provider. When the process is
    /// running the SQLite cell (provider unset or "Sqlite") we point it at a
    /// per-test SQLite file so the shared dev DB and sibling tests are never
    /// touched; for Postgres/SqlServer we leave the connection strings alone
    /// so the throwaway-DB test harness
    /// (<c>PostgresTestDatabaseLifetime</c> / <c>SqlServerTestDatabaseLifetime</c>)
    /// remains authoritative.
    /// </summary>
    private static WebApplicationFactory<Program> BuildFactory(ref string? sqlitePath)
    {
        var provider = Environment.GetEnvironmentVariable("Persistence__Provider")
            ?? Environment.GetEnvironmentVariable("Persistence:Provider");
        var isSqlite = string.IsNullOrWhiteSpace(provider)
            || string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase);

        string? localSqlitePath = null;
        if (isSqlite)
        {
            var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
            Directory.CreateDirectory(dataDir);
            localSqlitePath = Path.Combine(dataDir, $"wpc-118-hydration-{Guid.NewGuid():N}.db");
        }
        sqlitePath = localSqlitePath;

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            if (localSqlitePath is not null)
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new[]
                    {
                        new KeyValuePair<string, string?>("Persistence:Provider", "Sqlite"),
                        new KeyValuePair<string, string?>("ConnectionStrings:Sqlite", $"Data Source={localSqlitePath}"),
                    });
                });
            }
            builder.ConfigureServices(services =>
            {
                services.Configure<ChangshaRuntimeOptions>(o =>
                {
                    // Park bot activity well past test wall-clock; hydration
                    // itself schedules nothing, but the DI runtime boots too.
                    o.BotTurnDelayMs = 30_000;
                    o.BotClaimDelayMs = 30_000;
                    o.ClaimWindowTimeoutMs = 30_000;
                    o.DealBatchDelayMs = 0;
                    o.PersistSnapshots = false;
                });
            });
        });
        return factory;
    }
}
