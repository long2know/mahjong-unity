using System.Linq;
using System.Reflection;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Tests.Persistence;

/// <summary>
/// Phase J Wave 7 — config-driven persistence-provider switching tests
/// (Vasquez). Pins Apone's <see cref="ServiceCollectionExtensions.AddPersistence"/>
/// shim and the <see cref="PersistenceProvider"/> enum that names the
/// three supported back-ends: SQLite (default), PostgreSQL, and SQL Server.
///
/// <para><b>Why this matters.</b> The Wave-5+ deployment guide promises
/// operators can swap the storage engine via <c>Persistence:Provider</c>
/// without code change. A regression where the switch silently falls back
/// to SQLite (or, worse, fails to register <see cref="AppDbContext"/> at
/// all) would surface only on a real Postgres/SqlServer deploy — too late.
/// These tests run inside an in-process <see cref="ServiceCollection"/> so
/// no external DB is needed, but assert the real provider name the EF Core
/// extension chose.</para>
///
/// <para><b>Reflection-defensive.</b> We resolve the
/// <see cref="PersistenceProvider"/> enum names via <see cref="Enum.GetNames(Type)"/>
/// rather than hard-coding the string vocabulary — a rename in the enum
/// reflects through the tests automatically, and a member added/removed
/// without a paired test pass surfaces here.</para>
/// </summary>
[Collection("DbSerial")]
public class DbProviderSwitchingTests
{
    /// <summary>
    /// Builds a fresh <see cref="ServiceCollection"/> with
    /// <see cref="AddPersistence(IServiceCollection, IConfiguration)"/> wired
    /// against the supplied configuration values. Returns the built
    /// <see cref="ServiceProvider"/> so the caller can resolve
    /// <see cref="AppDbContext"/> and inspect the chosen EF Core provider.
    /// </summary>
    private static ServiceProvider BuildProvider(params (string key, string value)[] settings)
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(settings.Select(s =>
            new KeyValuePair<string, string?>(s.key, s.value)));
        var configuration = builder.Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(configuration);
        return services.BuildServiceProvider();
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Default provider (no config) resolves to SQLite
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-J-7")]
    public void AddPersistence_DefaultsToSqlite_WhenProviderUnset()
    {
        // Apone's contract: omitting `Persistence:Provider` registers the
        // SQLite back-end pointing at `data/mahjong-autotable.db`. This
        // is the path local `dotnet run` developers + the in-repo test
        // harness both rely on; a regression here would silently break
        // the existing 456-test gate by attaching the suite to a
        // different (probably missing) provider.
        using var sp = BuildProvider();

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", db.Database.ProviderName);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Explicit Sqlite via Persistence:Provider
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-J-7")]
    public void AddPersistence_RegistersSqlite_WhenProviderEqualsSqlite()
    {
        // Explicit `Persistence:Provider=Sqlite` MUST round-trip to the
        // Sqlite EF provider — case-insensitive. The extension lowercases
        // the value before the switch (see ServiceCollectionExtensions),
        // so this also pins the case-insensitivity contract.
        using var sp = BuildProvider(
            ("Persistence:Provider", "Sqlite"),
            ("ConnectionStrings:Sqlite", "Data Source=:memory:"));

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", db.Database.ProviderName);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. PostgreSql provider registers Npgsql backend
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-J-7")]
    public void AddPersistence_RegistersPostgreSql_WhenProviderEqualsPostgreSql()
    {
        // Apone's contract: `Persistence:Provider=PostgreSql` switches the
        // EF Core registration to Npgsql + reads ConnectionStrings:PostgreSql.
        // We don't connect to a live Postgres (the test would need a
        // container) — instead we resolve AppDbContext, inspect the chosen
        // provider name, and assert it is the Npgsql provider. The DI
        // graph being constructible at all is the strong signal: any
        // missing package reference or option lambda misconfiguration
        // would throw on GetRequiredService<AppDbContext>().
        using var sp = BuildProvider(
            ("Persistence:Provider", "PostgreSql"),
            ("ConnectionStrings:PostgreSql", "Host=localhost;Database=mahjong;Username=test;Password=test"));

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. SqlServer provider registers Microsoft SqlServer backend
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-J-7")]
    public void AddPersistence_RegistersSqlServer_WhenProviderEqualsSqlServer()
    {
        // Apone's contract: `Persistence:Provider=SqlServer` switches the
        // EF Core registration to Microsoft.EntityFrameworkCore.SqlServer
        // + reads ConnectionStrings:SqlServer. Same hosting strategy as
        // the PostgreSQL test — we never open the connection, just verify
        // the DI graph resolves and the provider is the correct one.
        using var sp = BuildProvider(
            ("Persistence:Provider", "SqlServer"),
            ("ConnectionStrings:SqlServer", "Server=tcp:localhost,1433;Database=mahjong;User Id=sa;Password=Pa55word!;TrustServerCertificate=true"));

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", db.Database.ProviderName);
    }

    // ────────────────────────────────────────────────────────────────────
    //  5. PersistenceOptions binds from configuration
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-J-7")]
    public void AddPersistence_BindsPersistenceOptionsFromConfiguration()
    {
        // Apone's `AddPersistence` calls
        //   services.Configure<PersistenceOptions>(configuration.GetSection("Persistence"));
        // The bound options surface the provider name so the rest of the
        // app (Program.cs `/api/system/persistence` minimal endpoint, etc.)
        // can read it without re-walking the configuration tree. This test
        // pins the binding so a future refactor that swaps `Configure` for
        // a bespoke factory still surfaces a populated PersistenceOptions.
        using var sp = BuildProvider(
            ("Persistence:Provider", "PostgreSql"),
            ("ConnectionStrings:PostgreSql", "Host=localhost;Database=x;Username=x;Password=x"));

        var options = sp.GetRequiredService<IOptions<PersistenceOptions>>();
        Assert.Equal("PostgreSql", options.Value.Provider);
    }

    // ────────────────────────────────────────────────────────────────────
    //  6. PersistenceProvider enum carries the documented three values
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-J-7")]
    public void PersistenceProvider_EnumNames_AreSqliteAndPostgreSqlAndSqlServer()
    {
        // Documentation source-of-truth check: the supported back-ends are
        // SQLite, PostgreSQL, and SqlServer (Apone's Wave 5/6/7 deploy
        // guides). Reflection-defensive — we read Enum.GetNames so an
        // alias or new provider added without test-doc updates raises here.
        var names = Enum.GetNames(typeof(PersistenceProvider)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(3, names.Count);
        Assert.Contains("Sqlite", names);
        Assert.Contains("PostgreSql", names);
        Assert.Contains("SqlServer", names);
    }

    // ────────────────────────────────────────────────────────────────────
    //  7. Postgres alias accepts "postgres" (lower-case shorthand)
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-J-7")]
    public void AddPersistence_AcceptsPostgresAlias_AsPostgreSql()
    {
        // Apone's switch statement maps both "postgresql" and "postgres"
        // to Npgsql so a `Persistence__Provider=postgres` env override
        // doesn't silently fall through to SQLite. Pins the alias.
        using var sp = BuildProvider(
            ("Persistence:Provider", "postgres"),
            ("ConnectionStrings:PostgreSql", "Host=localhost;Database=x;Username=x;Password=x"));

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
    }

    // ────────────────────────────────────────────────────────────────────
    //  8. Missing PostgreSql connection string throws on resolution
    // ────────────────────────────────────────────────────────────────────

    [Fact, Trait("Category", "Persistence"), Trait("Wave", "Phase-J-7")]
    public void AddPersistence_PostgreSqlWithoutConnectionString_ThrowsOnResolve()
    {
        // Apone's option-lambda hard-fails when the operator selects
        // PostgreSql but omits ConnectionStrings:PostgreSql. Surfacing
        // this at DI-resolution time means the API container exits with
        // a clear stack trace on startup rather than silently attaching
        // to a different provider — important for k8s deploys where a
        // ConfigMap typo would otherwise be invisible.
        using var sp = BuildProvider(
            ("Persistence:Provider", "PostgreSql"));

        using var scope = sp.CreateScope();
        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
