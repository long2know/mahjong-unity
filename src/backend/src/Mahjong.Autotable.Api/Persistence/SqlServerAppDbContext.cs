using Mahjong.Autotable.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Persistence;

// Phase J Wave 7 — Apone (DevOps). SQL Server twin of SqliteAppDbContext.
// See SqliteAppDbContext.cs for the rationale. Migrations targeting
// SQL Server live under Persistence/Migrations/SqlServer/ and carry
// `[DbContext(typeof(SqlServerAppDbContext))]`.
public sealed class SqlServerAppDbContext(DbContextOptions<SqlServerAppDbContext> options)
    : AppDbContext(options)
{
}
