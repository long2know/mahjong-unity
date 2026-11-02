using Mahjong.Autotable.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Mahjong.Autotable.Api.Persistence;

// Phase J Wave 7 — Apone (DevOps). SQLite twin of
// `PostgresDesignTimeDbContextFactory`. Lets
// `dotnet ef migrations add ... --context SqliteAppDbContext` work
// from a clean checkout without spinning up the full host. See
// PostgresDesignTimeDbContextFactory.cs for the rationale.
internal sealed class SqliteDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SqliteAppDbContext>
{
    public SqliteAppDbContext CreateDbContext(string[] args)
    {
        var basePath = Directory.GetCurrentDirectory();
        var config = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connection = config.GetConnectionString("Sqlite")
            ?? "Data Source=data/mahjong-autotable.db";

        var optionsBuilder = new DbContextOptionsBuilder<SqliteAppDbContext>()
            .UseSqlite(connection, sqlite =>
            {
                sqlite.MigrationsAssembly(typeof(SqliteAppDbContext).Assembly.GetName().Name);
            });

        return new SqliteAppDbContext(optionsBuilder.Options);
    }
}
