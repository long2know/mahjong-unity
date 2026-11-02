using System.Data;
using Mahjong.Autotable.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Data;

public static class DatabaseBootstrapper
{
    // Phase K Wave 23 — Vasquez (Rules Engineer / Tester). Process-wide
    // serialization gate around the entire InitializeAsync body.
    //
    // <para>Background: Apone's CI-noise iter2 memo
    // (`.squad/decisions/inbox/apone-db-providers-stuck.md`) caught the
    // db-providers Postgres matrix failing on every backend PR with
    // four parallel <c>__EFMigrationsHistory</c> races at the start of
    // the run, followed by <c>relation "ChangshaGames" already exists</c>
    // / <c>column ... already exists</c> errors. The root cause is
    // xUnit's default parallelism: each test class boots its own
    // <c>WebApplicationFactory&lt;Program&gt;</c> which lands here, and
    // four collections racing <c>Database.MigrateAsync</c> on the same
    // Postgres database all win partially, leaving the schema half-baked
    // for whichever collection's tests run after.</para>
    //
    // <para>The semaphore serializes bootstrap across the whole process.
    // In production this is a one-shot at startup — the lock is held for
    // a few hundred ms once and never again, so the cost is invisible.
    // In tests, every <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
    // boot queues here, which is exactly the contract Apone's memo §3
    // ("fixture-singleton lock around the initial migration apply")
    // asked for.</para>
    private static readonly SemaphoreSlim _bootstrapGate = new(initialCount: 1, maxCount: 1);

    public static async Task InitializeAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        await _bootstrapGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeCoreAsync(db, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _bootstrapGate.Release();
        }
    }

    private static async Task InitializeCoreAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        // Phase K Wave 23 — Vasquez. Test-isolation hook. The test
        // assembly's module initializer
        // (`tests/.../TestInfrastructure/PostgresTestDatabaseLifetime.cs`)
        // sets <c>MAT_TEST_RESET_DB=1</c> when running against the
        // per-process throwaway Postgres DB. We reset the schema on
        // every factory boot so each test class's <c>IAsyncLifetime</c>
        // starts from a clean state — fixing the data-pollution
        // failures (Leaderboard / Players / Audit row-count drift) that
        // surfaced on Apone's W22 PG migration PR.
        //
        // Guarded by env-var so production deploys NEVER trip this.
        // SQLite tests already get a fresh per-class temp file from
        // the existing IAsyncLifetime pattern, so the reset is a no-op
        // there (EnsureCreated against an empty file just creates).
        var resetForTests = string.Equals(
            Environment.GetEnvironmentVariable("MAT_TEST_RESET_DB"),
            "1",
            StringComparison.Ordinal);

        if (resetForTests && db.Database.IsNpgsql())
        {
            // Postgres path — drop+recreate the `public` schema in the
            // current database so every test class gets a clean slate.
            // Faster than DROP DATABASE + CREATE DATABASE because we
            // stay connected and Npgsql's pool keeps warm.
            await db.Database.ExecuteSqlRawAsync(
                "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;",
                cancellationToken).ConfigureAwait(false);
        }
        else if (resetForTests && db.Database.IsSqlServer())
        {
            // WP-C / #118 — Frost. SQL Server twin of the Postgres reset.
            // SQL Server has no drop-schema-cascade, and `dbo` cannot be
            // dropped, so we drop every foreign key first (to break
            // referential ordering) then every user table in the current
            // database. MigrateAsync below then re-applies the full
            // SqlServer migration set from a clean slate — the same
            // per-class isolation contract the throwaway Postgres DB gets
            // via SqlServerTestDatabaseLifetime. Gated on
            // MAT_TEST_RESET_DB so production deploys never trip it.
            await db.Database.ExecuteSqlRawAsync(
                """
                DECLARE @sql NVARCHAR(MAX) = N'';
                SELECT @sql += N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name)
                             + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
                FROM sys.foreign_keys AS fk
                JOIN sys.tables AS t ON fk.parent_object_id = t.object_id;
                SELECT @sql += N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name) + N';'
                FROM sys.tables AS t;
                IF (@sql <> N'') EXEC sp_executesql @sql;
                """,
                cancellationToken).ConfigureAwait(false);
        }

        if (db.Database.IsSqlite())
        {
            // SQLite — dev / single-replica default. EnsureCreated + the
            // CREATE-IF-NOT-EXISTS bootstrap below remain canonical so
            // existing dev DBs (which predate the migration set) keep
            // working without an out-of-band `dotnet ef database update`.
            await db.Database.EnsureCreatedAsync(cancellationToken);
            // Phase A (autotable-vendored pivot): drop the legacy 136-tile
            // TableSessions + TableSessionEvents tables on startup if a
            // pre-pivot SQLite database is still present. The EF Core entities
            // and `/api/tables/*` REST surface have been hard-deleted; only
            // Changsha-native tables remain. This bootstrap drop replaces a
            // formal migration because the project never wired EF Core
            // migrations (it relied on EnsureCreatedAsync + manual ALTERs).
            await DropLegacyTableSessionsAsync(db, cancellationToken);
            await EnsureSqliteChangshaTablesAsync(db, cancellationToken);
            await EnsureSqliteRoomBindingsAsync(db, cancellationToken);
            // Phase J Wave 5 — defensive SQLite-only bootstrap for the new
            // PlayerProfiles + PlayerStats tables. The EF Core migration
            // (AddPlayerProfileAndStats) is the canonical schema source; this
            // CREATE-IF-NOT-EXISTS pass keeps existing-DB Sqlite installs
            // working without requiring an out-of-band `dotnet ef database
            // update`, matching the existing Changsha-tables pattern above.
            await EnsureSqlitePlayerTablesAsync(db, cancellationToken);
            // Phase J Wave 7 — same belt-and-braces pattern for the replay
            // snapshot table. Canonical schema is the AddChangshaGameReplay
            // migration; this guard auto-bootstraps the new table on
            // existing prod DBs so the runtime's game-completion hook
            // (ChangshaGameRuntime.EmitGameCompletedAsync) never trips on
            // a missing table.
            await EnsureSqliteReplayTablesAsync(db, cancellationToken);
            // Phase J Wave 8 — auth identity + rule presets. The canonical
            // schema is the AddAuthAndRulePresets EF migration; this guard
            // bootstraps existing-prod SQLite databases that pre-date Wave 8
            // without requiring an out-of-band `dotnet ef database update`.
            await EnsureSqliteWave8TablesAsync(db, cancellationToken);
            // Phase J Wave 9 — CSP violation report sink (Apone, DevOps).
            // Canonical schema is the AddCspViolations EF migration; this
            // guard ensures existing SQLite DBs gain the table on boot so
            // POST /api/csp-report never trips a runtime "no such table".
            await EnsureSqliteCspViolationsAsync(db, cancellationToken);
            // Phase J Wave 9 — reconnect token rotation, append-only audit
            // log, and persisted chat backlog. Canonical schema is the
            // AddWave9ReconnectTokensAndChat migration; this guard keeps
            // existing SQLite DBs working without an out-of-band update,
            // adds the Role column to PlayerAuthSessions, and stamps a
            // SchemaVersion column onto ChangshaGameReplays for the v2
            // replay schema (defaulting legacy rows to 1).
            await EnsureSqliteWave9TablesAsync(db, cancellationToken);
            // Phase J Wave 10 — Tournament tables. Canonical schema is
            // the AddTournaments EF migration; this guard bootstraps
            // existing SQLite DBs that pre-date Wave 10 without
            // requiring an out-of-band `dotnet ef database update`.
            await EnsureSqliteWave10TablesAsync(db, cancellationToken);
            // Phase K Wave 1 — match-history denormalization + per-season
            // Elo rating tables (Bishop). Canonical schema is the
            // AddMatchHistoryAndRatings EF migration; this guard
            // bootstraps existing SQLite DBs that pre-date Wave-K-1
            // without requiring an out-of-band `dotnet ef database
            // update`. Also stamps the new TournamentMatches
            // forfeit columns onto pre-Wave-K Wave-10 rows.
            await EnsureSqlitePhaseK1TablesAsync(db, cancellationToken);
            // Phase K Wave 3 — Bishop. Adds the ChangshaGame voice +
            // owner columns, the PlayerOnboardingStatuses table, and
            // renames the Wave-2 deferral columns to the canonical
            // FromSeasonId / ToSeasonId / ResolvedAtUtc shape.
            // Canonical schema is the Phase_K_W3_VoiceAndOnboardingSchema
            // migration; this guard bootstraps existing-prod SQLite
            // DBs without an out-of-band `dotnet ef database update`.
            await EnsureSqlitePhaseK3TablesAsync(db, cancellationToken);
        }
        else
        {
            // Phase J Wave 7 — Apone (DevOps). Postgres + SqlServer go
            // through the canonical EF Core migration runner so deploys
            // are versioned, rollbackable, and recorded in the
            // `__EFMigrationsHistory` table. The provider-specific
            // migration sets live under Persistence/Migrations/Postgres
            // and Persistence/Migrations/SqlServer; the right one is
            // discovered automatically because `AddPersistence` registers
            // the provider's typed subclass of AppDbContext (the
            // PostgresAppDbContext / SqlServerAppDbContext shells).
            await db.Database.MigrateAsync(cancellationToken);
        }

        // Phase J Wave 8 — seed the "Classic Changsha" default preset on
        // every provider once the schema is up. Idempotent: the upsert is
        // gated on the canonical preset id so repeated boot cycles never
        // create duplicates and never overwrite a manually-tuned row that
        // an operator may have edited.
        await SeedClassicChangshaPresetAsync(db, cancellationToken);
    }

    private static async Task DropLegacyTableSessionsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var dropEvents = connection.CreateCommand();
            dropEvents.CommandText = "DROP TABLE IF EXISTS \"TableSessionEvents\";";
            await dropEvents.ExecuteNonQueryAsync(cancellationToken);

            await using var dropSessions = connection.CreateCommand();
            dropSessions.CommandText = "DROP TABLE IF EXISTS \"TableSessions\";";
            await dropSessions.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static Task EnsureSqliteRoomBindingsAsync(AppDbContext db, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "AutotableRoomBindings" (
                "RoomKey" TEXT NOT NULL CONSTRAINT "PK_AutotableRoomBindings" PRIMARY KEY,
                "RoomId" TEXT NOT NULL,
                "RuntimeGameId" TEXT NOT NULL,
                "CreatedUtc" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AutotableRoomBindings_RuntimeGameId"
                ON "AutotableRoomBindings" ("RuntimeGameId");
            """, cancellationToken);

    private static async Task EnsureSqliteChangshaTablesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using (var createGames = connection.CreateCommand())
            {
                createGames.CommandText = """
                    CREATE TABLE IF NOT EXISTS "ChangshaGames" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ChangshaGames" PRIMARY KEY,
                        "RuleSet" TEXT NOT NULL DEFAULT 'changsha-v1',
                        "Seed" INTEGER NOT NULL,
                        "StateJson" TEXT NOT NULL,
                        "StateVersion" INTEGER NOT NULL DEFAULT 1,
                        "CurrentHandNumber" INTEGER NOT NULL DEFAULT 1,
                        "CurrentRoundNumber" INTEGER NOT NULL DEFAULT 1,
                        "CreatedUtc" TEXT NOT NULL,
                        "UpdatedUtc" TEXT NOT NULL
                    );
                    """;
                await createGames.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var createEvents = connection.CreateCommand())
            {
                createEvents.CommandText = """
                    CREATE TABLE IF NOT EXISTS "ChangshaGameEvents" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_ChangshaGameEvents" PRIMARY KEY AUTOINCREMENT,
                        "GameId" TEXT NOT NULL,
                        "Sequence" INTEGER NOT NULL,
                        "EventType" TEXT NOT NULL,
                        "SeatIndex" INTEGER NOT NULL,
                        "TurnNumber" INTEGER NOT NULL,
                        "TileId" INTEGER NULL,
                        "Detail" TEXT NOT NULL,
                        "HandNumber" INTEGER NOT NULL DEFAULT 1,
                        "StateVersion" INTEGER NOT NULL,
                        "OccurredUtc" TEXT NOT NULL,
                        "PersistedUtc" TEXT NOT NULL,
                        CONSTRAINT "FK_ChangshaGameEvents_ChangshaGames_GameId" FOREIGN KEY ("GameId") REFERENCES "ChangshaGames" ("Id") ON DELETE CASCADE
                    );
                    """;
                await createEvents.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var createIndex = connection.CreateCommand();
            createIndex.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChangshaGameEvents_GameId_Sequence"
                ON "ChangshaGameEvents" ("GameId", "Sequence");
                """;
            await createIndex.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task EnsureSqlitePlayerTablesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using (var createProfiles = connection.CreateCommand())
            {
                createProfiles.CommandText = """
                    CREATE TABLE IF NOT EXISTS "PlayerProfiles" (
                        "PlayerId" TEXT NOT NULL CONSTRAINT "PK_PlayerProfiles" PRIMARY KEY,
                        "DisplayName" TEXT NOT NULL,
                        "AvatarColor" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "LastSeenAt" TEXT NOT NULL
                    );
                    """;
                await createProfiles.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var createStats = connection.CreateCommand())
            {
                // Drake (backend hotfix, 2026-05-27) — LastGameAt MUST be NULL.
                // The PlayerStats EF model declares it as `DateTime?`
                // (Players/PlayerStats.cs L18) and every EF migration +
                // model snapshot agrees: nullable. An earlier revision of
                // this bootstrap declared it `NOT NULL DEFAULT '0001-01-01
                // 00:00:00'`, which silently shadowed the model on any
                // pre-PlayerStats dev SQLite DB where EnsureCreatedAsync
                // was a no-op and the CREATE-IF-NOT-EXISTS pass was the
                // only thing landing the table. The result was a runtime
                // SqliteException 19 — "NOT NULL constraint failed:
                // PlayerStats.LastGameAt" — the first time a brand-new
                // profile got upserted (PlayerProfileService writes a
                // fresh PlayerStats row with LastGameAt=null until the
                // player completes their first game).
                createStats.CommandText = """
                    CREATE TABLE IF NOT EXISTS "PlayerStats" (
                        "PlayerId" TEXT NOT NULL CONSTRAINT "PK_PlayerStats" PRIMARY KEY,
                        "GamesPlayed" INTEGER NOT NULL DEFAULT 0,
                        "GamesWon" INTEGER NOT NULL DEFAULT 0,
                        "TotalScore" INTEGER NOT NULL DEFAULT 0,
                        "HighestSingleGameScore" INTEGER NOT NULL DEFAULT 0,
                        "LongestWinStreak" INTEGER NOT NULL DEFAULT 0,
                        "CurrentWinStreak" INTEGER NOT NULL DEFAULT 0,
                        "LastGameAt" TEXT NULL,
                        CONSTRAINT "FK_PlayerStats_PlayerProfiles_PlayerId" FOREIGN KEY ("PlayerId") REFERENCES "PlayerProfiles" ("PlayerId") ON DELETE CASCADE
                    );
                    """;
                await createStats.ExecuteNonQueryAsync(cancellationToken);
            }

            // Drake (backend hotfix, 2026-05-27) — remediation pass for
            // dev SQLite DBs that already had the buggy CREATE applied
            // (LastGameAt NOT NULL DEFAULT '0001-01-01 00:00:00'). SQLite
            // doesn't support ALTER COLUMN DROP NOT NULL, so we detect the
            // bad column via PRAGMA table_info and rebuild the table with
            // the SQLite-recommended table-rebuild pattern. Sentinel
            // '0001-01-01 00:00:00' values get mapped back to NULL on the
            // way through. Fresh-create DBs (which already land on the
            // correct nullable shape via EnsureCreatedAsync or the fixed
            // CREATE above) skip the rebuild entirely.
            var lastGameAtIsNotNull = false;
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(\"PlayerStats\");";
                await using var reader = await pragma.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var name = reader.GetString(1);
                    if (string.Equals(name, "LastGameAt", StringComparison.Ordinal))
                    {
                        // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk
                        lastGameAtIsNotNull = reader.GetInt32(3) != 0;
                        break;
                    }
                }
            }
            if (lastGameAtIsNotNull)
            {
                await using var rebuild = connection.CreateCommand();
                rebuild.CommandText = """
                    PRAGMA foreign_keys = OFF;
                    BEGIN TRANSACTION;
                    CREATE TABLE "PlayerStats_new" (
                        "PlayerId" TEXT NOT NULL CONSTRAINT "PK_PlayerStats" PRIMARY KEY,
                        "GamesPlayed" INTEGER NOT NULL DEFAULT 0,
                        "GamesWon" INTEGER NOT NULL DEFAULT 0,
                        "TotalScore" INTEGER NOT NULL DEFAULT 0,
                        "HighestSingleGameScore" INTEGER NOT NULL DEFAULT 0,
                        "LongestWinStreak" INTEGER NOT NULL DEFAULT 0,
                        "CurrentWinStreak" INTEGER NOT NULL DEFAULT 0,
                        "LastGameAt" TEXT NULL,
                        CONSTRAINT "FK_PlayerStats_PlayerProfiles_PlayerId" FOREIGN KEY ("PlayerId") REFERENCES "PlayerProfiles" ("PlayerId") ON DELETE CASCADE
                    );
                    INSERT INTO "PlayerStats_new" (
                        "PlayerId", "GamesPlayed", "GamesWon", "TotalScore",
                        "HighestSingleGameScore", "LongestWinStreak",
                        "CurrentWinStreak", "LastGameAt"
                    )
                    SELECT
                        "PlayerId", "GamesPlayed", "GamesWon", "TotalScore",
                        "HighestSingleGameScore", "LongestWinStreak",
                        "CurrentWinStreak",
                        CASE WHEN "LastGameAt" = '0001-01-01 00:00:00' THEN NULL ELSE "LastGameAt" END
                    FROM "PlayerStats";
                    DROP TABLE "PlayerStats";
                    ALTER TABLE "PlayerStats_new" RENAME TO "PlayerStats";
                    COMMIT;
                    PRAGMA foreign_keys = ON;
                    """;
                await rebuild.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// Phase J Wave 7 — defensive SQLite-only bootstrap for the
    /// ChangshaGameReplays table. Mirrors the
    /// <c>AddChangshaGameReplay</c> EF migration so existing prod DBs
    /// running on EnsureCreatedAsync semantics pick up the new table at
    /// startup without an out-of-band migration sweep. The unique GameId
    /// index lets the runtime upsert on completion (rare; only when
    /// hydration re-enters a completed game) without violating PK
    /// uniqueness.
    /// </summary>
    private static async Task EnsureSqliteReplayTablesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using (var createReplays = connection.CreateCommand())
            {
                createReplays.CommandText = """
                    CREATE TABLE IF NOT EXISTS "ChangshaGameReplays" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ChangshaGameReplays" PRIMARY KEY,
                        "GameId" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "EventsJson" TEXT NOT NULL
                    );
                    """;
                await createReplays.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var createIndex = connection.CreateCommand();
            createIndex.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChangshaGameReplays_GameId"
                ON "ChangshaGameReplays" ("GameId");
                """;
            await createIndex.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// Phase J Wave 8 — defensive SQLite-only bootstrap for the new auth +
    /// rule-preset tables: ChangshaRulePresets, PlayerAuthIdentities,
    /// EmailMagicLinkTokens, PlayerAuthSessions. Also adds the nullable
    /// <c>RulePresetId</c> column to ChangshaGames when the column doesn't
    /// already exist (PRAGMA table_info probe). Mirrors the
    /// AddAuthAndRulePresets EF migration so existing prod SQLite DBs pick
    /// up the new schema without an out-of-band migration sweep.
    /// </summary>
    private static async Task EnsureSqliteWave8TablesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using (var createPresets = connection.CreateCommand())
            {
                createPresets.CommandText = """
                    CREATE TABLE IF NOT EXISTS "ChangshaRulePresets" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ChangshaRulePresets" PRIMARY KEY,
                        "Name" TEXT NOT NULL,
                        "Description" TEXT NOT NULL DEFAULT '',
                        "HandLimit" INTEGER NOT NULL DEFAULT 4,
                        "MaxScorePerHand" INTEGER NOT NULL DEFAULT 0,
                        "AllowWashout" INTEGER NOT NULL DEFAULT 1,
                        "AllowKongRobbing" INTEGER NOT NULL DEFAULT 1,
                        "AllowConcealedKongPromotion" INTEGER NOT NULL DEFAULT 1,
                        "AllowSevenPairs" INTEGER NOT NULL DEFAULT 1,
                        "AllowChow" INTEGER NOT NULL DEFAULT 1,
                        "BotDecisionTimeoutMs" INTEGER NOT NULL DEFAULT 2000,
                        "CreatorPlayerId" TEXT NOT NULL DEFAULT 'system',
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL
                    );
                    """;
                await createPresets.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxPresets = connection.CreateCommand())
            {
                idxPresets.CommandText = """
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChangshaRulePresets_Name"
                    ON "ChangshaRulePresets" ("Name");
                    """;
                await idxPresets.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var createIdentities = connection.CreateCommand())
            {
                createIdentities.CommandText = """
                    CREATE TABLE IF NOT EXISTS "PlayerAuthIdentities" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_PlayerAuthIdentities" PRIMARY KEY,
                        "PlayerId" TEXT NOT NULL,
                        "Provider" TEXT NOT NULL,
                        "ProviderSubject" TEXT NOT NULL,
                        "Email" TEXT NULL,
                        "EmailVerified" INTEGER NOT NULL DEFAULT 0,
                        "CreatedAt" TEXT NOT NULL,
                        "LastUsedAt" TEXT NOT NULL,
                        CONSTRAINT "FK_PlayerAuthIdentities_PlayerProfiles_PlayerId" FOREIGN KEY ("PlayerId") REFERENCES "PlayerProfiles" ("PlayerId") ON DELETE CASCADE
                    );
                    """;
                await createIdentities.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxIdentitiesProv = connection.CreateCommand())
            {
                idxIdentitiesProv.CommandText = """
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_PlayerAuthIdentities_Provider_ProviderSubject"
                    ON "PlayerAuthIdentities" ("Provider", "ProviderSubject");
                    """;
                await idxIdentitiesProv.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxIdentitiesPlayer = connection.CreateCommand())
            {
                idxIdentitiesPlayer.CommandText = """
                    CREATE INDEX IF NOT EXISTS "IX_PlayerAuthIdentities_PlayerId"
                    ON "PlayerAuthIdentities" ("PlayerId");
                    """;
                await idxIdentitiesPlayer.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var createTokens = connection.CreateCommand())
            {
                createTokens.CommandText = """
                    CREATE TABLE IF NOT EXISTS "EmailMagicLinkTokens" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_EmailMagicLinkTokens" PRIMARY KEY,
                        "Token" TEXT NOT NULL,
                        "Email" TEXT NOT NULL,
                        "RequestedPlayerId" TEXT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "ExpiresAt" TEXT NOT NULL,
                        "ConsumedAt" TEXT NULL
                    );
                    """;
                await createTokens.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxTokens = connection.CreateCommand())
            {
                idxTokens.CommandText = """
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_EmailMagicLinkTokens_Token"
                    ON "EmailMagicLinkTokens" ("Token");
                    """;
                await idxTokens.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var createSessions = connection.CreateCommand())
            {
                createSessions.CommandText = """
                    CREATE TABLE IF NOT EXISTS "PlayerAuthSessions" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_PlayerAuthSessions" PRIMARY KEY,
                        "Token" TEXT NOT NULL,
                        "PlayerId" TEXT NOT NULL,
                        "IdentityId" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "ExpiresAt" TEXT NOT NULL,
                        "LastUsedAt" TEXT NOT NULL
                    );
                    """;
                await createSessions.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxSessionsToken = connection.CreateCommand())
            {
                idxSessionsToken.CommandText = """
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_PlayerAuthSessions_Token"
                    ON "PlayerAuthSessions" ("Token");
                    """;
                await idxSessionsToken.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxSessionsPlayer = connection.CreateCommand())
            {
                idxSessionsPlayer.CommandText = """
                    CREATE INDEX IF NOT EXISTS "IX_PlayerAuthSessions_PlayerId"
                    ON "PlayerAuthSessions" ("PlayerId");
                    """;
                await idxSessionsPlayer.ExecuteNonQueryAsync(cancellationToken);
            }

            // Add RulePresetId column to ChangshaGames if missing. PRAGMA
            // table_info returns one row per column; we scan for the name
            // and only ALTER when it isn't there. SQLite has no
            // ADD-COLUMN-IF-NOT-EXISTS so this probe-then-add pattern is
            // standard.
            var hasRulePresetId = false;
            await using (var probe = connection.CreateCommand())
            {
                probe.CommandText = "PRAGMA table_info(\"ChangshaGames\");";
                await using var reader = await probe.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    // Column name is index 1 in PRAGMA table_info output.
                    if (reader.GetString(1).Equals("RulePresetId", StringComparison.OrdinalIgnoreCase))
                    {
                        hasRulePresetId = true;
                        break;
                    }
                }
            }
            if (!hasRulePresetId)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"ChangshaGames\" ADD COLUMN \"RulePresetId\" TEXT NULL;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// Phase J Wave 9 — defensive SQLite-only bootstrap for the
    /// <c>CspViolations</c> append-only table. Mirrors the
    /// AddCspViolations EF migration so existing prod SQLite installs
    /// pick up the new table on boot without an out-of-band migration sweep.
    /// </summary>
    private static async Task EnsureSqliteCspViolationsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using (var createTable = connection.CreateCommand())
            {
                createTable.CommandText = """
                    CREATE TABLE IF NOT EXISTS "CspViolations" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_CspViolations" PRIMARY KEY AUTOINCREMENT,
                        "PlayerId" TEXT NULL,
                        "DocumentUri" TEXT NULL,
                        "Referrer" TEXT NULL,
                        "ViolatedDirective" TEXT NULL,
                        "EffectiveDirective" TEXT NULL,
                        "OriginalPolicy" TEXT NULL,
                        "Disposition" TEXT NULL,
                        "BlockedUri" TEXT NULL,
                        "SourceFile" TEXT NULL,
                        "LineNumber" INTEGER NULL,
                        "ColumnNumber" INTEGER NULL,
                        "ScriptSample" TEXT NULL,
                        "StatusCode" INTEGER NULL,
                        "UserAgent" TEXT NULL,
                        "RawJson" TEXT NOT NULL,
                        "ReceivedAt" TEXT NOT NULL
                    );
                    """;
                await createTable.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxReceived = connection.CreateCommand())
            {
                idxReceived.CommandText = """
                    CREATE INDEX IF NOT EXISTS "IX_CspViolations_ReceivedAt"
                    ON "CspViolations" ("ReceivedAt");
                    """;
                await idxReceived.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxDirective = connection.CreateCommand())
            {
                idxDirective.CommandText = """
                    CREATE INDEX IF NOT EXISTS "IX_CspViolations_EffectiveDirective"
                    ON "CspViolations" ("EffectiveDirective");
                    """;
                await idxDirective.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// Phase J Wave 9 — defensive SQLite-only bootstrap for the
    /// <c>ReconnectTokens</c>, <c>ReconnectAuditEntries</c>, and
    /// <c>ChatMessages</c> tables, plus the additive <c>Role</c> column on
    /// <c>PlayerAuthSessions</c> and <c>SchemaVersion</c> on
    /// <c>ChangshaGameReplays</c>. Mirrors the
    /// AddWave9ReconnectTokensAndChat EF migration so existing prod
    /// SQLite installs pick up the new tables / columns on boot without
    /// an out-of-band migration sweep. PRAGMA-probe-then-ALTER is the
    /// idiom for additive columns because SQLite has no
    /// <c>ADD COLUMN IF NOT EXISTS</c>.
    /// </summary>
    private static async Task EnsureSqliteWave9TablesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using (var createReconnect = connection.CreateCommand())
            {
                createReconnect.CommandText = """
                    CREATE TABLE IF NOT EXISTS "ReconnectTokens" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ReconnectTokens" PRIMARY KEY,
                        "Token" TEXT NOT NULL,
                        "PlayerId" TEXT NOT NULL,
                        "GameId" TEXT NOT NULL,
                        "SeatIndex" INTEGER NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "ExpiresAt" TEXT NOT NULL,
                        "ConsumedAt" TEXT NULL,
                        "RotatedFromTokenId" TEXT NULL
                    );
                    """;
                await createReconnect.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxToken = connection.CreateCommand())
            {
                idxToken.CommandText = """
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReconnectTokens_Token"
                    ON "ReconnectTokens" ("Token");
                    """;
                await idxToken.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxPlayerGame = connection.CreateCommand())
            {
                idxPlayerGame.CommandText = """
                    CREATE INDEX IF NOT EXISTS "IX_ReconnectTokens_PlayerId_GameId"
                    ON "ReconnectTokens" ("PlayerId", "GameId");
                    """;
                await idxPlayerGame.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var createAudit = connection.CreateCommand())
            {
                createAudit.CommandText = """
                    CREATE TABLE IF NOT EXISTS "ReconnectAuditEntries" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ReconnectAuditEntries" PRIMARY KEY,
                        "PlayerId" TEXT NOT NULL,
                        "OldTokenId" TEXT NOT NULL,
                        "NewTokenId" TEXT NOT NULL,
                        "Ipv4Hash" TEXT NOT NULL,
                        "UserAgentHash" TEXT NOT NULL,
                        "At" TEXT NOT NULL
                    );
                    """;
                await createAudit.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxAuditPlayer = connection.CreateCommand())
            {
                idxAuditPlayer.CommandText = """
                    CREATE INDEX IF NOT EXISTS "IX_ReconnectAuditEntries_PlayerId"
                    ON "ReconnectAuditEntries" ("PlayerId");
                    """;
                await idxAuditPlayer.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxAuditAt = connection.CreateCommand())
            {
                idxAuditAt.CommandText = """
                    CREATE INDEX IF NOT EXISTS "IX_ReconnectAuditEntries_At"
                    ON "ReconnectAuditEntries" ("At");
                    """;
                await idxAuditAt.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var createChat = connection.CreateCommand())
            {
                createChat.CommandText = """
                    CREATE TABLE IF NOT EXISTS "ChatMessages" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_ChatMessages" PRIMARY KEY,
                        "GameId" TEXT NOT NULL,
                        "PlayerId" TEXT NOT NULL,
                        "Body" TEXT NOT NULL,
                        "At" TEXT NOT NULL,
                        "Channel" TEXT NOT NULL DEFAULT 'table'
                    );
                    """;
                await createChat.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxChatGameAt = connection.CreateCommand())
            {
                idxChatGameAt.CommandText = """
                    CREATE INDEX IF NOT EXISTS "IX_ChatMessages_GameId_At"
                    ON "ChatMessages" ("GameId", "At");
                    """;
                await idxChatGameAt.ExecuteNonQueryAsync(cancellationToken);
            }

            // PlayerAuthSessions.Role — additive column for the Wave 9 admin
            // gate (DevLogin can stamp role="admin"; AuthCookieService
            // hands the value back via ResolveAsync). PRAGMA-probe so
            // re-runs are no-ops.
            var hasRole = false;
            await using (var probeRole = connection.CreateCommand())
            {
                probeRole.CommandText = "PRAGMA table_info(\"PlayerAuthSessions\");";
                await using var reader = await probeRole.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.GetString(1).Equals("Role", StringComparison.OrdinalIgnoreCase))
                    {
                        hasRole = true;
                        break;
                    }
                }
            }
            if (!hasRole)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"PlayerAuthSessions\" ADD COLUMN \"Role\" TEXT NULL;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            // ChangshaGameReplays.SchemaVersion — additive column for v2.
            // Default 1 so legacy rows keep their implicit version.
            var hasSchemaVersion = false;
            await using (var probeSchema = connection.CreateCommand())
            {
                probeSchema.CommandText = "PRAGMA table_info(\"ChangshaGameReplays\");";
                await using var reader = await probeSchema.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.GetString(1).Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase))
                    {
                        hasSchemaVersion = true;
                        break;
                    }
                }
            }
            if (!hasSchemaVersion)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"ChangshaGameReplays\" ADD COLUMN \"SchemaVersion\" INTEGER NOT NULL DEFAULT 1;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// Phase J Wave 8 — seeds the canonical "Classic Changsha" preset row.
    /// Idempotent: keyed off <see cref="ChangshaRulePreset.ClassicPresetId"/>
    /// so repeated boots are no-ops and don't overwrite hand-edited values.
    /// Runs on every provider once the table exists.
    /// </summary>
    private static async Task SeedClassicChangshaPresetAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var canonicalId = Guid.Parse(ChangshaRulePreset.ClassicPresetId);
        var exists = await db.ChangshaRulePresets.AnyAsync(p => p.Id == canonicalId, cancellationToken);
        if (exists) return;

        db.ChangshaRulePresets.Add(new ChangshaRulePreset
        {
            Id = canonicalId,
            Name = "Classic Changsha",
            Description = "Standard Changsha Mahjong house rules.",
            HandLimit = 4,
            MaxScorePerHand = 0,
            AllowWashout = true,
            AllowKongRobbing = true,
            AllowConcealedKongPromotion = true,
            AllowSevenPairs = true,
            AllowChow = true,
            BotDecisionTimeoutMs = 2000,
            CreatorPlayerId = "system",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Phase J Wave 10 — idempotent CREATE-IF-NOT-EXISTS pass for the
    /// Tournament/Registration/Match tables. SQLite-only; Postgres and
    /// SqlServer reach these tables through the canonical
    /// AddTournaments EF migration. The migration is the schema source
    /// of truth — this bootstrap exists so legacy dev SQLite DBs (which
    /// historically relied on EnsureCreatedAsync) keep working without
    /// an out-of-band `dotnet ef database update`.
    /// </summary>
    private static async Task EnsureSqliteWave10TablesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using (var createTournaments = connection.CreateCommand())
            {
                createTournaments.CommandText = """
                    CREATE TABLE IF NOT EXISTS "Tournaments" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_Tournaments" PRIMARY KEY,
                        "Name" TEXT NOT NULL,
                        "Format" TEXT NOT NULL,
                        "Status" TEXT NOT NULL,
                        "CreatedByPlayerId" TEXT NOT NULL,
                        "MaxPlayers" INTEGER NOT NULL,
                        "GamesPerMatch" INTEGER NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "StartedAt" TEXT NULL,
                        "CompletedAt" TEXT NULL
                    );
                    """;
                await createTournaments.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxStatus = connection.CreateCommand())
            {
                idxStatus.CommandText = """
                    CREATE INDEX IF NOT EXISTS "IX_Tournaments_Status"
                    ON "Tournaments" ("Status");
                    """;
                await idxStatus.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxCreated = connection.CreateCommand())
            {
                idxCreated.CommandText = """
                    CREATE INDEX IF NOT EXISTS "IX_Tournaments_CreatedAt"
                    ON "Tournaments" ("CreatedAt");
                    """;
                await idxCreated.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var createReg = connection.CreateCommand())
            {
                createReg.CommandText = """
                    CREATE TABLE IF NOT EXISTS "TournamentRegistrations" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_TournamentRegistrations" PRIMARY KEY,
                        "TournamentId" TEXT NOT NULL,
                        "PlayerId" TEXT NOT NULL,
                        "Seed" INTEGER NOT NULL,
                        "RegisteredAt" TEXT NOT NULL,
                        CONSTRAINT "FK_TournamentRegistrations_Tournaments" FOREIGN KEY ("TournamentId")
                            REFERENCES "Tournaments" ("Id") ON DELETE CASCADE
                    );
                    """;
                await createReg.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxRegUnique = connection.CreateCommand())
            {
                idxRegUnique.CommandText = """
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_TournamentRegistrations_Tournament_Player"
                    ON "TournamentRegistrations" ("TournamentId", "PlayerId");
                    """;
                await idxRegUnique.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxRegTour = connection.CreateCommand())
            {
                idxRegTour.CommandText = """
                    CREATE INDEX IF NOT EXISTS "IX_TournamentRegistrations_TournamentId"
                    ON "TournamentRegistrations" ("TournamentId");
                    """;
                await idxRegTour.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var createMatch = connection.CreateCommand())
            {
                createMatch.CommandText = """
                    CREATE TABLE IF NOT EXISTS "TournamentMatches" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_TournamentMatches" PRIMARY KEY,
                        "TournamentId" TEXT NOT NULL,
                        "Round" INTEGER NOT NULL,
                        "Player1Id" TEXT NOT NULL,
                        "Player2Id" TEXT NOT NULL,
                        "Player3Id" TEXT NULL,
                        "Player4Id" TEXT NULL,
                        "WinnerPlayerId" TEXT NULL,
                        "GameIdsJson" TEXT NOT NULL,
                        "Status" TEXT NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "CompletedAt" TEXT NULL,
                        CONSTRAINT "FK_TournamentMatches_Tournaments" FOREIGN KEY ("TournamentId")
                            REFERENCES "Tournaments" ("Id") ON DELETE CASCADE
                    );
                    """;
                await createMatch.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxMatchRound = connection.CreateCommand())
            {
                idxMatchRound.CommandText = """
                    CREATE INDEX IF NOT EXISTS "IX_TournamentMatches_Tournament_Round"
                    ON "TournamentMatches" ("TournamentId", "Round");
                    """;
                await idxMatchRound.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var idxMatchTour = connection.CreateCommand())
            {
                idxMatchTour.CommandText = """
                    CREATE INDEX IF NOT EXISTS "IX_TournamentMatches_TournamentId"
                    ON "TournamentMatches" ("TournamentId");
                    """;
                await idxMatchTour.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// Phase K Wave 1 — defensive SQLite-only bootstrap for the
    /// <c>PlayerGameHistory</c>, <c>PlayerRatings</c>, and
    /// <c>PlayerRatingHistory</c> tables, plus the forfeit columns added
    /// to <c>TournamentMatches</c>. Postgres + SqlServer reach the same
    /// schema through the canonical EF migration set (see
    /// Persistence/Migrations/{Postgres,SqlServer}/…AddMatchHistoryAndRatings).
    /// </summary>
    private static async Task EnsureSqlitePhaseK1TablesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE IF NOT EXISTS "PlayerGameHistory" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_PlayerGameHistory" PRIMARY KEY,
                        "PlayerId" TEXT NOT NULL,
                        "GameId" TEXT NOT NULL,
                        "SeatIndex" INTEGER NOT NULL,
                        "FinalScore" INTEGER NOT NULL,
                        "Won" INTEGER NOT NULL,
                        "StartedAt" TEXT NOT NULL,
                        "CompletedAt" TEXT NOT NULL,
                        "OpponentPlayerIdsCsv" TEXT NOT NULL,
                        "RulePresetId" TEXT NULL
                    );
                    """;
                await create.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var idx1 = connection.CreateCommand())
            {
                idx1.CommandText = """
                    CREATE INDEX IF NOT EXISTS "IX_PlayerGameHistory_PlayerId_CompletedAt"
                    ON "PlayerGameHistory" ("PlayerId", "CompletedAt");
                    """;
                await idx1.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var idx2 = connection.CreateCommand())
            {
                idx2.CommandText = """
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_PlayerGameHistory_PlayerId_GameId"
                    ON "PlayerGameHistory" ("PlayerId", "GameId");
                    """;
                await idx2.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var createRatings = connection.CreateCommand())
            {
                createRatings.CommandText = """
                    CREATE TABLE IF NOT EXISTS "PlayerRatings" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_PlayerRatings" PRIMARY KEY,
                        "PlayerId" TEXT NOT NULL,
                        "Season" TEXT NOT NULL,
                        "EloRating" INTEGER NOT NULL,
                        "GamesPlayed" INTEGER NOT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "LastUpdatedAt" TEXT NOT NULL
                    );
                    """;
                await createRatings.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var idxR1 = connection.CreateCommand())
            {
                idxR1.CommandText = """
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_PlayerRatings_PlayerId_Season"
                    ON "PlayerRatings" ("PlayerId", "Season");
                    """;
                await idxR1.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var idxR2 = connection.CreateCommand())
            {
                idxR2.CommandText = """
                    CREATE INDEX IF NOT EXISTS "IX_PlayerRatings_Season"
                    ON "PlayerRatings" ("Season");
                    """;
                await idxR2.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var createHist = connection.CreateCommand())
            {
                createHist.CommandText = """
                    CREATE TABLE IF NOT EXISTS "PlayerRatingHistory" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_PlayerRatingHistory" PRIMARY KEY,
                        "PlayerId" TEXT NOT NULL,
                        "Season" TEXT NOT NULL,
                        "EloRating" INTEGER NOT NULL,
                        "GamesPlayed" INTEGER NOT NULL,
                        "FrozenAt" TEXT NOT NULL
                    );
                    """;
                await createHist.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var idxH1 = connection.CreateCommand())
            {
                idxH1.CommandText = """
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_PlayerRatingHistory_PlayerId_Season"
                    ON "PlayerRatingHistory" ("PlayerId", "Season");
                    """;
                await idxH1.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var idxH2 = connection.CreateCommand())
            {
                idxH2.CommandText = """
                    CREATE INDEX IF NOT EXISTS "IX_PlayerRatingHistory_Season"
                    ON "PlayerRatingHistory" ("Season");
                    """;
                await idxH2.ExecuteNonQueryAsync(cancellationToken);
            }

            // Additive columns on TournamentMatches — guarded by a
            // PRAGMA table_info probe so a fresh schema (with the
            // columns already created via the EF model on first boot)
            // doesn't re-add and trip "duplicate column".
            var hasForfeitFlag = false;
            var hasForfeitPlayer = false;
            await using (var probe = connection.CreateCommand())
            {
                probe.CommandText = "PRAGMA table_info(\"TournamentMatches\");";
                await using var reader = await probe.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var name = reader.GetString(1);
                    if (string.Equals(name, "ForfeitedByDisconnect", StringComparison.Ordinal)) hasForfeitFlag = true;
                    if (string.Equals(name, "ForfeitedPlayerId", StringComparison.Ordinal)) hasForfeitPlayer = true;
                }
            }
            if (!hasForfeitFlag)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"TournamentMatches\" ADD COLUMN \"ForfeitedByDisconnect\" INTEGER NOT NULL DEFAULT 0;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!hasForfeitPlayer)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"TournamentMatches\" ADD COLUMN \"ForfeitedPlayerId\" TEXT NULL;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// Phase K Wave 3 — Bishop. Defensive SQLite bootstrap for the
    /// Wave-3 schema deltas:
    /// <list type="bullet">
    ///   <item>Adds <c>ChangshaGames.OwnerPlayerId</c> +
    ///         <c>ChangshaGames.VoiceEnabled</c> columns when missing.</item>
    ///   <item>Adds the <c>ReconnectAuditEntries.Detail</c> column
    ///         when missing (the Wave-2 EF migration shipped without
    ///         it).</item>
    ///   <item>Creates the <c>PlayerOnboardingStatuses</c> table when
    ///         missing.</item>
    ///   <item>Renames the Wave-2 <c>PlayerSeasonRolloverDeferrals</c>
    ///         columns from <c>FromSeason / ToSeason / DrainedAtUtc</c>
    ///         to <c>FromSeasonId / ToSeasonId / ResolvedAtUtc</c> when
    ///         the old shape is detected.</item>
    /// </list>
    /// Postgres + SqlServer reach the same schema through the canonical
    /// Phase_K_W3_VoiceAndOnboardingSchema EF migration; this guard
    /// stays SQLite-only because SQLite never invokes the migration
    /// runner.
    /// </summary>
    private static async Task EnsureSqlitePhaseK3TablesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            // ── ChangshaGames: add OwnerPlayerId + VoiceEnabled ────────
            var hasOwner = false;
            var hasVoiceEnabled = false;
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(\"ChangshaGames\");";
                await using var reader = await pragma.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var name = reader.GetString(1);
                    if (string.Equals(name, "OwnerPlayerId", StringComparison.Ordinal)) hasOwner = true;
                    else if (string.Equals(name, "VoiceEnabled", StringComparison.Ordinal)) hasVoiceEnabled = true;
                }
            }
            if (!hasOwner)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"ChangshaGames\" ADD COLUMN \"OwnerPlayerId\" TEXT NULL;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!hasVoiceEnabled)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"ChangshaGames\" ADD COLUMN \"VoiceEnabled\" INTEGER NOT NULL DEFAULT 0;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            // ── ReconnectAuditEntries: add Detail (Wave 2 model drift) ─
            var hasDetail = false;
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(\"ReconnectAuditEntries\");";
                await using var reader = await pragma.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var name = reader.GetString(1);
                    if (string.Equals(name, "Detail", StringComparison.Ordinal)) { hasDetail = true; break; }
                }
            }
            if (!hasDetail)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE \"ReconnectAuditEntries\" ADD COLUMN \"Detail\" TEXT NULL;";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }

            // ── PlayerOnboardingStatuses: create when missing ───────────
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE IF NOT EXISTS "PlayerOnboardingStatuses" (
                        "PlayerId" TEXT NOT NULL CONSTRAINT "PK_PlayerOnboardingStatuses" PRIMARY KEY,
                        "Completed" INTEGER NOT NULL,
                        "StepsCompleted" INTEGER NOT NULL,
                        "LastStepCompletedUtc" TEXT NULL,
                        "CreatedAt" TEXT NOT NULL,
                        "UpdatedAt" TEXT NOT NULL
                    );
                    """;
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            // ── PlayerSeasonRolloverDeferrals: rename Wave-2 columns ───
            // SQLite supports column renames since v3.25 (2018-09); the
            // schema's been pinned to >= 3.35 by the EF Core 9 toolchain
            // so the rename is safe. We only rename when the old column
            // shape is still present so a fresh-create DB (which already
            // lands on the new shape via EnsureCreatedAsync) is a no-op.
            var hasOldFromSeason = false;
            var hasOldToSeason = false;
            var hasOldDrainedAt = false;
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(\"PlayerSeasonRolloverDeferrals\");";
                await using var reader = await pragma.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var name = reader.GetString(1);
                    if (string.Equals(name, "FromSeason", StringComparison.Ordinal)) hasOldFromSeason = true;
                    else if (string.Equals(name, "ToSeason", StringComparison.Ordinal)) hasOldToSeason = true;
                    else if (string.Equals(name, "DrainedAtUtc", StringComparison.Ordinal)) hasOldDrainedAt = true;
                }
            }
            if (hasOldFromSeason)
            {
                await using var rename = connection.CreateCommand();
                rename.CommandText = "ALTER TABLE \"PlayerSeasonRolloverDeferrals\" RENAME COLUMN \"FromSeason\" TO \"FromSeasonId\";";
                await rename.ExecuteNonQueryAsync(cancellationToken);
            }
            if (hasOldToSeason)
            {
                await using var rename = connection.CreateCommand();
                rename.CommandText = "ALTER TABLE \"PlayerSeasonRolloverDeferrals\" RENAME COLUMN \"ToSeason\" TO \"ToSeasonId\";";
                await rename.ExecuteNonQueryAsync(cancellationToken);
            }
            if (hasOldDrainedAt)
            {
                await using var rename = connection.CreateCommand();
                rename.CommandText = "ALTER TABLE \"PlayerSeasonRolloverDeferrals\" RENAME COLUMN \"DrainedAtUtc\" TO \"ResolvedAtUtc\";";
                await rename.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }
}
