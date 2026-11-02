using Mahjong.Autotable.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Mahjong.Autotable.Api.Persistence;

// Phase J Wave 7 — Apone (DevOps). SQL Server twin of
// `PostgresDesignTimeDbContextFactory`. See that file for the
// rationale and contract.
internal sealed class SqlServerDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SqlServerAppDbContext>
{
    public SqlServerAppDbContext CreateDbContext(string[] args)
    {
        var basePath = Directory.GetCurrentDirectory();
        var config = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connection = config.GetConnectionString("SqlServer")
            ?? "Server=localhost,1433;Database=mahjong_autotable;User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=true";

        var optionsBuilder = new DbContextOptionsBuilder<SqlServerAppDbContext>()
            .UseSqlServer(connection, sql =>
            {
                sql.MigrationsAssembly(typeof(SqlServerAppDbContext).Assembly.GetName().Name);
            });

        return new SqlServerAppDbContext(optionsBuilder.Options);
    }
}
