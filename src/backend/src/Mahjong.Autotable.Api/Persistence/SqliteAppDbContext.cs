using Mahjong.Autotable.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Persistence;

// Phase J Wave 7 — Apone (DevOps). Provider-specific subclass of
// AppDbContext. Exists so `dotnet ef migrations add ... --context
// SqliteAppDbContext --output-dir Persistence/Migrations/Sqlite` produces
// migrations whose `[DbContext(typeof(SqliteAppDbContext))]` annotation
// uniquely identifies the SQLite migration set; the Postgres + SQL Server
// twins live alongside this one. The base AppDbContext owns the model;
// these derived classes contribute nothing except the typed options
// constructor so EF Core can wire them up via DI.
//
// At runtime exactly one subclass is registered (per
// `Persistence:Provider`), and AppDbContext is aliased to that subclass
// so all existing `IServiceProvider.GetRequiredService<AppDbContext>()`
// call sites keep working unchanged.
public sealed class SqliteAppDbContext(DbContextOptions<SqliteAppDbContext> options)
    : AppDbContext(options)
{
}
