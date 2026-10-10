using Mahjong.Autotable.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PersistenceOptions>(configuration.GetSection("Persistence"));

        var provider = configuration.GetValue<string>("Persistence:Provider") ?? PersistenceProvider.Sqlite.ToString();

        // Phase J Wave 7 — Apone (DevOps). One concrete subclass of
        // AppDbContext is registered per provider so each provider's
        // migration set (Persistence/Migrations/{Sqlite,Postgres,SqlServer})
        // can be discovered independently — EF Core scopes migration
        // discovery by the runtime DbContext type, and the subclass
        // identity is what keeps the three sets from colliding. The
        // base AppDbContext is then aliased to whichever subclass is
        // active so the existing
        // `IServiceProvider.GetRequiredService<AppDbContext>()` call
        // sites across the codebase keep working unchanged.
        switch (provider.ToLowerInvariant())
        {
            case "postgresql":
            case "postgres":
                {
                    services.AddDbContext<PostgresAppDbContext>(options =>
                    {
                        // Phase J Wave 7 — Apone. Resolved lazily inside the
                        // option lambda so a missing connection string surfaces
                        // at DI-resolution time (clear startup stack trace),
                        // not at AddPersistence-registration time (which can
                        // happen long before the operator's k8s configmap is
                        // even rendered). Vasquez's DbProviderSwitchingTests
                        // pins this contract.
                        var connection = configuration.GetConnectionString("PostgreSql")
                            ?? throw new InvalidOperationException("ConnectionStrings:PostgreSql is required for Postgres provider.");
                        options.UseNpgsql(connection, npgsql =>
                        {
                            npgsql.MigrationsAssembly(typeof(PostgresAppDbContext).Assembly.GetName().Name);
                            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "public");
                        });
                    });
                    services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<PostgresAppDbContext>());
                    break;
                }

            case "sqlserver":
                {
                    services.AddDbContext<SqlServerAppDbContext>(options =>
                    {
                        var connection = configuration.GetConnectionString("SqlServer")
                            ?? throw new InvalidOperationException("ConnectionStrings:SqlServer is required for SqlServer provider.");
                        options.UseSqlServer(connection, sql =>
                        {
                            sql.MigrationsAssembly(typeof(SqlServerAppDbContext).Assembly.GetName().Name);
                        });
                    });
                    services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<SqlServerAppDbContext>());
                    break;
                }

            default:
                {
                    // Sqlite — the dev / single-replica default. Registers
                    // the SqliteAppDbContext subclass against its own
                    // migration set, plus aliases the legacy `AppDbContext`
                    // to it so design-time tooling (and existing tests
                    // that pre-date the Wave 7 subclass split) keep
                    // resolving without a code edit.
                    services.AddDbContext<SqliteAppDbContext>(options =>
                    {
                        var connection = configuration.GetConnectionString("Sqlite")
                            ?? "Data Source=data/mahjong-autotable.db";
                        options.UseSqlite(connection, sqlite =>
                        {
                            sqlite.MigrationsAssembly(typeof(SqliteAppDbContext).Assembly.GetName().Name);
                        });
                    });
                    services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<SqliteAppDbContext>());
                    break;
                }
        }

        return services;
    }
}
