using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;

namespace Mahjong.Autotable.Api.Tests.TestInfrastructure;

/// <summary>
/// WP-C / #118 — Frost (Backend Dev, persistence). SQL Server twin of
/// <see cref="PostgresTestDatabaseLifetime"/>. Gives the <c>db-providers</c>
/// SqlServer matrix cell the same per-process isolation the Postgres cell
/// already had, so the full xUnit suite can run against a real
/// <c>mcr.microsoft.com/mssql/server</c> service without the parallel test
/// collections colliding on a single shared database.
///
/// <para><b>Problem (mirrors the Postgres story).</b> Every test class boots
/// its own <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// which lands in <see cref="Mahjong.Autotable.Api.Data.DatabaseBootstrapper"/>.
/// Pointed at one shared SQL Server database, parallel collections race
/// <c>Database.MigrateAsync</c> on <c>__EFMigrationsHistory</c> and leak
/// seeded rows across classes.</para>
///
/// <para><b>Fix — two-part isolation barrier:</b></para>
/// <list type="number">
///   <item><b>Per-process throwaway database.</b> This module initializer
///         fires once per <c>dotnet test</c> invocation. When the process
///         targets SQL Server it connects to <c>master</c>, creates a fresh
///         <c>mat_test_&lt;pid&gt;_&lt;short-guid&gt;</c> database, and
///         rewrites <c>ConnectionStrings__SqlServer</c> so every factory in
///         the process boots against the throwaway. A
///         <see cref="AppDomain.ProcessExit"/> handler force-drops it on
///         shutdown so we never leak databases across CI runs sharing a
///         SQL Server instance.</item>
///   <item><b>Per-boot schema reset.</b> Sets <c>MAT_TEST_RESET_DB=1</c>
///         which <see cref="Mahjong.Autotable.Api.Data.DatabaseBootstrapper"/>
///         interprets (for SQL Server) as "drop every FK + user table
///         before bootstrap". Combined with <c>xunit.runner.json</c>'s
///         <c>parallelizeTestCollections: false</c> this gives every test
///         class a clean schema in its
///         <see cref="Xunit.IAsyncLifetime.InitializeAsync"/>.</item>
/// </list>
///
/// <para>SQLite + Postgres paths: this initializer is a no-op (the provider
/// gate below only fires for <c>SqlServer</c>). It coexists with
/// <see cref="PostgresTestDatabaseLifetime"/> — the two module initializers
/// are mutually exclusive because the active <c>Persistence__Provider</c>
/// can only be one value per process.</para>
/// </summary>
internal static class SqlServerTestDatabaseLifetime
{
    private static readonly object _gate = new();
    private static bool _initialized;
    private static string? _perProcessDbName;
    private static string? _maintenanceConnectionString;

    /// <summary>
    /// Module initializer — runs ONCE per test process, before any
    /// <c>[Fact]</c> executes.
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
            // failure is visible but the runner keeps moving.
            Console.Error.WriteLine(
                $"[SqlServerTestDatabaseLifetime] init failed: {ex.GetType().Name}: {ex.Message}");
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
            if (!string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                // Not running the SqlServer matrix cell — leave env alone.
                return;
            }

            var original = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer")
                ?? Environment.GetEnvironmentVariable("ConnectionStrings:SqlServer");
            if (string.IsNullOrWhiteSpace(original))
            {
                Console.Error.WriteLine(
                    "[SqlServerTestDatabaseLifetime] Persistence__Provider=SqlServer but " +
                    "ConnectionStrings__SqlServer is unset — refusing to fabricate " +
                    "a default; let AddPersistence surface the missing-config error.");
                return;
            }

            var builder = new SqlConnectionStringBuilder(original);

            // Throwaway DB name. SQL Server identifiers allow up to 128 chars;
            // process-id + short guid gives uniqueness across parallel
            // `dotnet test` invocations on the same CI runner.
            var perProcess = $"mat_test_{Environment.ProcessId}_{Guid.NewGuid():N}";
            _perProcessDbName = perProcess;

            // Connect to `master` to issue CREATE DATABASE — we can't create
            // a database while connected to the to-be-created one.
            var maintenanceBuilder = new SqlConnectionStringBuilder(original)
            {
                InitialCatalog = "master",
            };
            _maintenanceConnectionString = maintenanceBuilder.ConnectionString;

            using (var admin = new SqlConnection(_maintenanceConnectionString))
            {
                admin.Open();
                using var cmd = admin.CreateCommand();
                cmd.CommandText = $"CREATE DATABASE [{_perProcessDbName}];";
                cmd.ExecuteNonQuery();
            }

            // Re-point the connection string at the throwaway DB. Every
            // factory in the process reads ConnectionStrings__SqlServer at
            // bootstrap time (see AddPersistence), so flipping it here
            // transparently routes everyone.
            builder.InitialCatalog = _perProcessDbName;
            Environment.SetEnvironmentVariable("ConnectionStrings__SqlServer", builder.ConnectionString);

            // Signal to DatabaseBootstrapper that it should drop every FK +
            // user table on each factory boot so each test class sees a
            // clean slate. Production deploys never set this.
            Environment.SetEnvironmentVariable("MAT_TEST_RESET_DB", "1");

            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            Console.Out.WriteLine(
                $"[SqlServerTestDatabaseLifetime] provisioned throwaway db " +
                $"'{_perProcessDbName}' for pid={Environment.ProcessId}.");
        }
    }

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        if (_perProcessDbName is null || _maintenanceConnectionString is null) return;
        try
        {
            // Clear pools so DROP DATABASE isn't blocked by lingering pooled
            // connections, then force SINGLE_USER to kick any stragglers.
            SqlConnection.ClearAllPools();

            using var admin = new SqlConnection(_maintenanceConnectionString);
            admin.Open();

            using var cmd = admin.CreateCommand();
            cmd.CommandText = $"""
                IF DB_ID(N'{_perProcessDbName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{_perProcessDbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{_perProcessDbName}];
                END
                """;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[SqlServerTestDatabaseLifetime] throwaway-DB cleanup failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
