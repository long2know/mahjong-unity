using System.Runtime.CompilerServices;
using Npgsql;

namespace Mahjong.Autotable.Api.Tests.TestInfrastructure;

/// <summary>
/// Phase K Wave 23 — Vasquez (Rules Engineer / Tester). Test-process
/// Postgres database isolation hook.
///
/// <para><b>Problem.</b> Apone's CI-noise iter2 memo
/// (<c>.squad/decisions/inbox/apone-db-providers-stuck.md</c>) caught
/// the <c>db-providers</c> Postgres matrix failing on every backend PR.
/// All test classes pointed at one shared CI database
/// (<c>mahjong_autotable_ci</c>), so:</para>
/// <list type="number">
///   <item>Four parallel test collections raced
///         <c>Database.MigrateAsync</c> → <c>"__EFMigrationsHistory does not
///         exist"</c> at start, then <c>"relation already exists" /
///         "column already exists"</c> as the half-applied migrations
///         re-collided.</item>
///   <item>Data seeded by class A (Leaderboard / Players / Audit) leaked
///         into class B's row-count assertions → <c>"Expected: 2 Actual:
///         4"</c> drift.</item>
/// </list>
///
/// <para><b>Fix.</b> Two-part isolation barrier:</para>
/// <list type="number">
///   <item><b>Per-process throwaway database.</b> This module
///         initializer fires once per <c>dotnet test</c> invocation. If
///         the test process is running against Postgres, it connects to
///         the maintenance DB, creates a fresh
///         <c>mat_test_&lt;pid&gt;_&lt;short-guid&gt;</c> database, and
///         rewrites <c>ConnectionStrings__PostgreSql</c> so every
///         <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
///         in the process boots against the throwaway. A
///         <see cref="AppDomain.ProcessExit"/> handler drops the
///         throwaway on shutdown so we never leak DBs.</item>
///   <item><b>Per-boot schema reset.</b> Sets <c>MAT_TEST_RESET_DB=1</c>
///         which <see cref="Mahjong.Autotable.Api.Data.DatabaseBootstrapper"/>
///         (see the W23 hook) interprets as "drop and recreate
///         <c>public</c> schema before bootstrap". Combined with
///         <c>xunit.runner.json</c>'s
///         <c>parallelizeTestCollections: false</c> this gives every
///         test class a clean schema in its
///         <see cref="Xunit.IAsyncLifetime.InitializeAsync"/> without
///         data leaks from prior classes.</item>
/// </list>
///
/// <para>SQLite path: this initializer is a no-op. SQLite tests already
/// use the per-class temp-file pattern (each
/// <c>IAsyncLifetime.InitializeAsync</c> generates
/// <c>mahjong-&lt;suite&gt;-&lt;guid&gt;.db</c>) so cross-class
/// collisions are physically impossible.</para>
/// </summary>
internal static class PostgresTestDatabaseLifetime
{
    private static readonly object _gate = new();
    private static bool _initialized;
    private static string? _perProcessDbName;
    private static string? _maintenanceConnectionString;

    /// <summary>
    /// Module initializer — runs ONCE per test process, before any
    /// <c>[Fact]</c> executes. C# 9 <c>[ModuleInitializer]</c>: the CLR
    /// guarantees this fires before any method on the module is
    /// invoked, which means it precedes xUnit's discovery and test
    /// orchestration.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            EnsureInitialized();
        }
        catch (Exception ex)
        {
            // ModuleInitializer exceptions abort the entire test process
            // with a confusing stack — swallow + dump to stderr so the
            // failure is visible but the runner keeps moving. If the
            // throwaway-DB creation fails we'd rather see the
            // downstream test failures than mask them behind a
            // TypeInitializationException.
            Console.Error.WriteLine(
                $"[PostgresTestDatabaseLifetime] init failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void EnsureInitialized()
    {
        lock (_gate)
        {
            if (_initialized) return;
            _initialized = true;

            var provider = Environment.GetEnvironmentVariable("Persistence__Provider")
                ?? Environment.GetEnvironmentVariable("Persistence:Provider");
            if (!string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(provider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
            {
                // Not running the Postgres matrix cell — leave env alone.
                // SQLite tests rely on the existing per-class temp file
                // pattern (see e.g. HealthEndpointTests.InitializeAsync).
                return;
            }

            var original = Environment.GetEnvironmentVariable("ConnectionStrings__PostgreSql")
                ?? Environment.GetEnvironmentVariable("ConnectionStrings:PostgreSql");
            if (string.IsNullOrWhiteSpace(original))
            {
                Console.Error.WriteLine(
                    "[PostgresTestDatabaseLifetime] Persistence__Provider=Postgres but " +
                    "ConnectionStrings__PostgreSql is unset — refusing to fabricate " +
                    "a default; let AddPersistence surface the missing-config error.");
                return;
            }

            var builder = new NpgsqlConnectionStringBuilder(original);
            var baseDb = builder.Database ?? "postgres";

            // Throwaway DB name. Constrained to ≤ 63 chars (Postgres limit)
            // and lower-cased (Postgres folds unquoted identifiers; we
            // quote anyway to be safe, but lowercase is the convention).
            // Process-id + short guid gives uniqueness across parallel
            // `dotnet test` invocations on the same CI runner.
            var perProcess = $"mat_test_{Environment.ProcessId}_{Guid.NewGuid():N}";
            if (perProcess.Length > 63) perProcess = perProcess[..63];
            _perProcessDbName = perProcess.ToLowerInvariant();

            // Connect to the maintenance DB (the original config's DB,
            // typically `mahjong_autotable_ci` for CI) to issue
            // `CREATE DATABASE`. We can't issue CREATE DATABASE while
            // connected to the to-be-created DB, hence the explicit
            // separate connection.
            _maintenanceConnectionString = original;

            using (var admin = new NpgsqlConnection(_maintenanceConnectionString))
            {
                admin.Open();
                using var cmd = admin.CreateCommand();
                cmd.CommandText = $"CREATE DATABASE \"{_perProcessDbName}\"";
                cmd.ExecuteNonQuery();
            }

            // Re-point the connection string at the throwaway DB. Every
            // factory in the process reads ConnectionStrings__PostgreSql
            // at bootstrap time (see AddPersistence), so flipping it
            // here transparently routes everyone.
            builder.Database = _perProcessDbName;
            var newConnectionString = builder.ConnectionString;
            Environment.SetEnvironmentVariable("ConnectionStrings__PostgreSql", newConnectionString);

            // Signal to DatabaseBootstrapper that it should drop+recreate
            // the public schema on every factory boot so each test class
            // sees a clean slate. Production deploys never set this.
            Environment.SetEnvironmentVariable("MAT_TEST_RESET_DB", "1");

            // Cleanup hook — drop the throwaway DB so we don't leak it
            // across CI runs sharing a Postgres instance. Runs on normal
            // process exit; if the runner is SIGKILLed the DB stays
            // around, but CI tears the whole container down anyway.
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            Console.Out.WriteLine(
                $"[PostgresTestDatabaseLifetime] provisioned throwaway db " +
                $"'{_perProcessDbName}' for pid={Environment.ProcessId}.");
        }
    }

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        if (_perProcessDbName is null || _maintenanceConnectionString is null) return;
        try
        {
            // Terminate any lingering connections to the throwaway so
            // DROP DATABASE doesn't trip "database is being accessed by
            // other users" — happens when test process is shutting
            // down with pooled connections still open.
            NpgsqlConnection.ClearAllPools();

            using var admin = new NpgsqlConnection(_maintenanceConnectionString);
            admin.Open();

            using (var term = admin.CreateCommand())
            {
                term.CommandText = $@"
                    SELECT pg_terminate_backend(pid)
                    FROM pg_stat_activity
                    WHERE datname = '{_perProcessDbName.Replace("'", "''")}'
                      AND pid <> pg_backend_pid();";
                term.ExecuteNonQuery();
            }

            using var cmd = admin.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_perProcessDbName}\"";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[PostgresTestDatabaseLifetime] throwaway-DB cleanup failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
