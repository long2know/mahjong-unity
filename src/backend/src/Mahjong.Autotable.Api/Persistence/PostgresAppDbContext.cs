using Mahjong.Autotable.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Persistence;

// Phase J Wave 7 — Apone (DevOps). Postgres twin of SqliteAppDbContext.
// See SqliteAppDbContext.cs for the rationale. Migrations targeting
// Postgres live under Persistence/Migrations/Postgres/ and carry
// `[DbContext(typeof(PostgresAppDbContext))]`.
public sealed class PostgresAppDbContext(DbContextOptions<PostgresAppDbContext> options)
    : AppDbContext(options)
{
}
