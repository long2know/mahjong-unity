# EF Core migrations layout

> WP-C / #118 (Frost). Read this before running any `dotnet ef migrations` command.

The API supports three persistence providers, selected at runtime by
`Persistence:Provider` (see `../ServiceCollectionExtensions.cs`). Each provider
has its **own** `DbContext` subclass and its **own** migration set so the three
never collide:

| Provider  | Runtime context           | Migration set                          | How schema is applied            |
|-----------|---------------------------|----------------------------------------|----------------------------------|
| SQLite    | `SqliteAppDbContext`      | `Migrations/Sqlite/`                   | `EnsureCreated` + idempotent bootstrap (see `Data/DatabaseBootstrapper.cs`); dev/single-writer default |
| Postgres  | `PostgresAppDbContext`    | `Migrations/Postgres/`                 | `Database.MigrateAsync()` at startup, recorded in `__EFMigrationsHistory` |
| SqlServer | `SqlServerAppDbContext`   | `Migrations/SqlServer/`                | `Database.MigrateAsync()` at startup, recorded in `__EFMigrationsHistory` |

## Adding a migration

**Always pass an explicit provider `--context`** and route the output to the
matching folder so each provider set stays independent:

```bash
cd src/backend/src/Mahjong.Autotable.Api

dotnet ef migrations add <Name> --context SqliteAppDbContext     --output-dir Persistence/Migrations/Sqlite
dotnet ef migrations add <Name> --context PostgresAppDbContext   --output-dir Persistence/Migrations/Postgres
dotnet ef migrations add <Name> --context SqlServerAppDbContext  --output-dir Persistence/Migrations/SqlServer
```

After adding, the drift gate must be clean for every provider:

```bash
for ctx in SqliteAppDbContext PostgresAppDbContext SqlServerAppDbContext; do
  dotnet ef migrations has-pending-model-changes --context "$ctx"
done
```

CI enforces this via the `drift-gate` job in
`.github/workflows/db-providers.yml`.

## Dormant root migrations — DO NOT USE

The files directly in this folder (**not** in a provider sub-folder) target the
**base** `AppDbContext` and predate the Phase J Wave 7 provider split:

- `20260523031206_AddPlayerProfileAndStats.cs`
- `20260524000000_AddChangshaGameReplay.cs`
- `AppDbContextModelSnapshot.cs`

They are **dormant** and retained only for historical continuity. The runtime
never applies them. `AppDbContextModelSnapshot.cs` is a **regeneration trap**:
running `dotnet ef migrations add <Name> --context AppDbContext` (the bare base
context) would diff against this stale snapshot and emit a corrupt catch-up
migration. Never target the base `AppDbContext`; always use a provider context
from the table above.

## Concurrency / single-writer note

`ChangshaGame.StateVersion` is a plain `int` (not a DB rowversion/xmin
concurrency token). Write serialization is provided by the per-game in-process
`SemaphoreSlim` in the runtime, which is correct **only** for the single-writer
(single-container) deployment target. See `docs/database-providers.md` for the
no-multi-replica constraint.
