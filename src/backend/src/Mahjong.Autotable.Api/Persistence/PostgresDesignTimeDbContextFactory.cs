using Mahjong.Autotable.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Mahjong.Autotable.Api.Persistence;

// Phase J Wave 7 — Apone (DevOps). Design-time factory so
// `dotnet ef migrations add ... --context PostgresAppDbContext` can
// instantiate the context without spinning up the full host. EF Core's
// migrations CLI discovers `IDesignTimeDbContextFactory<TContext>`
// implementations by reflection. Reads the connection string from the
// usual configuration ladder (appsettings.json + env overrides) so
// devs running the CLI locally don't need a hand-crafted secrets file.
//
// Stays internal — only EF tooling consumes it; production registration
// goes through `ServiceCollectionExtensions.AddPersistence`.
internal sealed class PostgresDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PostgresAppDbContext>
{
    public PostgresAppDbContext CreateDbContext(string[] args)
    {
        var basePath = Directory.GetCurrentDirectory();
        var config = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connection = config.GetConnectionString("PostgreSql")
            ?? "Host=localhost;Port=5432;Database=mahjong_autotable;Username=mahjong;Password=mahjong";

        var optionsBuilder = new DbContextOptionsBuilder<PostgresAppDbContext>()
            .UseNpgsql(connection, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(PostgresAppDbContext).Assembly.GetName().Name);
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "public");
            });

        return new PostgresAppDbContext(optionsBuilder.Options);
    }
}
