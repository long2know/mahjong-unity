using System.Data.Common;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Mahjong.Autotable.Api.Data;
using Mahjong.Autotable.Api.Players;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Players;

/// <summary>
/// Phase K Wave 23 — Drake (Persistence). Schema-drift detection for the
/// SQLite bootstrap that backs <see cref="PlayerProfile"/> /
/// <see cref="PlayerStats"/>.
///
/// <para><b>Why this exists.</b> The dev / single-replica deploy path
/// runs <c>EnsureCreatedAsync()</c> + the hand-rolled
/// <c>DatabaseBootstrapper.EnsureSqlitePlayerTablesAsync</c> instead of
/// <c>Database.MigrateAsync()</c> (the EF migration set is the canonical
/// source on Postgres / SqlServer, but SQLite legacy DBs predate any
/// migrations and rely on the bootstrap to land/upgrade the tables).
/// The hand-rolled <c>CREATE TABLE</c> is therefore a parallel definition
/// of the schema — any drift between it and the EF model is invisible to
/// the build and only surfaces at runtime as a <c>SqliteException 19</c>
/// (e.g. Drake's earlier <c>c369c54</c> "LastGameAt NOT NULL" hotfix).</para>
///
/// <para><b>What this asserts.</b> After booting a fresh
/// <see cref="WebApplicationFactory{Program}"/> against a brand-new
/// SQLite file (so <c>EnsureCreatedAsync</c> + the bootstrap both run),
/// for every property on <see cref="PlayerProfile"/> + <see cref="PlayerStats"/>:
/// <list type="bullet">
///   <item>The column exists in the SQLite table.</item>
///   <item>The NOT-NULL bit matches the EF nullability (the column type
///         <c>DateTime?</c> ↔ <c>notnull=0</c>; non-nullable string ↔
///         <c>notnull=1</c>).</item>
///   <item>The primary-key column on the SQLite side matches the EF
///         <c>HasKey</c> declaration.</item>
/// </list>
/// Plus we assert the cross-table foreign key on
/// <c>PlayerStats.PlayerId → PlayerProfiles.PlayerId</c> exists with
/// <c>ON DELETE CASCADE</c> — the EF model declares the cascade and the
/// bootstrap CREATE has to honour it for the orphan-cleanup behaviour to
/// hold.</para>
///
/// <para><b>Failure mode.</b> If a future patch updates one side without
/// the other (e.g. adds a new column to <see cref="PlayerProfile"/> but
/// forgets the bootstrap CREATE), this test fails with a clear
/// "column X expected NOT NULL but found nullable" message — the same
/// kind of signal Drake's earlier <c>c369c54</c> would have caught at
/// CI time instead of in live play.</para>
/// </summary>
[Collection("DbSerial")]
[Trait("Category", "Persistence"), Trait("Wave", "Phase-K-Drake-Audit")]
public class PlayerTablesSchemaBootstrapTests : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private string? _tempDb;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(dataDir);
        // Brand-new DB file — guarantees both EnsureCreatedAsync AND the
        // hand-rolled bootstrap exercise the CREATE-TABLE path. If we
        // re-used an existing DB the IF-NOT-EXISTS guard would short-
        // circuit the bootstrap and a drift would silently pass.
        _tempDb = Path.Combine(dataDir, $"mahjong-schema-drift-{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_tempDb}");
            b.ConfigureServices(s =>
            {
                s.Configure<ChangshaRuntimeOptions>(o =>
                {
                    o.BotTurnDelayMs = 1;
                    o.BotClaimDelayMs = 1;
                    o.ClaimWindowTimeoutMs = 50;
                    o.DealBatchDelayMs = 0;
                    o.PersistSnapshots = false;
                });
            });
        });
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try { if (_tempDb is not null && File.Exists(_tempDb)) File.Delete(_tempDb); } catch { }
        return Task.CompletedTask;
    }

    private sealed record SqliteColumn(string Name, string Type, bool NotNull, bool PrimaryKey);
    private sealed record SqliteForeignKey(string Table, string From, string To, string OnDelete);

    // Phase L — Frost (Backend). SQLite-provider guard for the db-providers
    // matrix. The three schema-drift assertions below introspect the live
    // table via raw SQLite `PRAGMA table_info` / `PRAGMA foreign_key_list`
    // and validate the hand-rolled SQLite bootstrap
    // (DatabaseBootstrapper.EnsureSqlitePlayerTablesAsync). Under the
    // Postgres matrix cell (Persistence__Provider=Postgres) the factory binds
    // Npgsql — env vars override the UseSetting Sqlite connection string — and
    // `PRAGMA` is a Postgres syntax error (Npgsql 42601). The Postgres schema
    // comes from the EF migration set, not this bootstrap, so these tests are
    // meaningless there. We gate them to the SQLite provider with a deliberate
    // early `return;` (the suite's zero-skip convention — no Assert.Skip).
    private static bool RunningOnSqlite()
    {
        var provider = Environment.GetEnvironmentVariable("Persistence__Provider")
            ?? Environment.GetEnvironmentVariable("Persistence:Provider");
        // Unset defaults to SQLite (see Persistence/ServiceCollectionExtensions).
        return string.IsNullOrWhiteSpace(provider)
            || string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<string, SqliteColumn>> ReadSqliteColumnsAsync(string tableName)
    {
        Assert.NotNull(_factory);
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = db.Database.GetDbConnection();
        var openedByUs = connection.State != System.Data.ConnectionState.Open;
        if (openedByUs) await connection.OpenAsync();
        try
        {
            var result = new Dictionary<string, SqliteColumn>(StringComparer.Ordinal);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info(\"{tableName}\");";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk
                var name = reader.GetString(1);
                var type = reader.GetString(2);
                var notNull = reader.GetInt32(3) != 0;
                var pkPosition = reader.GetInt32(5);
                result[name] = new SqliteColumn(name, type, notNull, pkPosition > 0);
            }
            return result;
        }
        finally
        {
            if (openedByUs) await connection.CloseAsync();
        }
    }

    private async Task<IReadOnlyList<SqliteForeignKey>> ReadSqliteForeignKeysAsync(string tableName)
    {
        Assert.NotNull(_factory);
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = db.Database.GetDbConnection();
        var openedByUs = connection.State != System.Data.ConnectionState.Open;
        if (openedByUs) await connection.OpenAsync();
        try
        {
            var fks = new List<SqliteForeignKey>();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA foreign_key_list(\"{tableName}\");";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                // PRAGMA foreign_key_list columns:
                //   id, seq, table, from, to, on_update, on_delete, match
                fks.Add(new SqliteForeignKey(
                    Table: reader.GetString(2),
                    From: reader.GetString(3),
                    To: reader.GetString(4),
                    OnDelete: reader.GetString(6)));
            }
            return fks;
        }
        finally
        {
            if (openedByUs) await connection.CloseAsync();
        }
    }

    private IEntityType GetEntityType(Type clrType)
    {
        Assert.NotNull(_factory);
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = db.Model.FindEntityType(clrType)
            ?? throw new InvalidOperationException($"EF model has no entity for {clrType.FullName}");
        return entity;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. PlayerProfiles schema matches the EF model
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlayerProfiles_Sqlite_Schema_MatchesEfModel()
    {
        if (!RunningOnSqlite()) return; // SQLite-only PRAGMA schema check; skipped on the Postgres matrix cell.

        var entity = GetEntityType(typeof(PlayerProfile));
        var liveColumns = await ReadSqliteColumnsAsync("PlayerProfiles");

        Assert.NotEmpty(liveColumns);

        foreach (var prop in entity.GetProperties())
        {
            var colName = prop.GetColumnName();
            Assert.True(liveColumns.ContainsKey(colName),
                $"PlayerProfiles missing column {colName} (declared in EF model)");

            var live = liveColumns[colName];
            // EF "IsNullable=false" ↔ SQLite "notnull=1". Drift here is
            // exactly the c369c54 class of bug — silent NOT-NULL mismatch
            // that only crashes on first insert.
            Assert.Equal(!prop.IsNullable, live.NotNull);
        }

        // PK is PlayerId (declared HasKey(x => x.PlayerId) in AppDbContext).
        var pkColumns = liveColumns.Values.Where(c => c.PrimaryKey).Select(c => c.Name).ToList();
        Assert.Equal(new[] { "PlayerId" }, pkColumns);
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. PlayerStats schema matches the EF model — INCLUDING the
    //     nullable LastGameAt column (regression guard for c369c54)
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlayerStats_Sqlite_Schema_MatchesEfModel()
    {
        if (!RunningOnSqlite()) return; // SQLite-only PRAGMA schema check; skipped on the Postgres matrix cell.

        var entity = GetEntityType(typeof(PlayerStats));
        var liveColumns = await ReadSqliteColumnsAsync("PlayerStats");

        Assert.NotEmpty(liveColumns);

        foreach (var prop in entity.GetProperties())
        {
            var colName = prop.GetColumnName();
            Assert.True(liveColumns.ContainsKey(colName),
                $"PlayerStats missing column {colName} (declared in EF model)");

            var live = liveColumns[colName];
            Assert.Equal(!prop.IsNullable, live.NotNull);
        }

        // PK is PlayerId. SQLite reports PK position 1 for the first PK
        // member; we only want the column name.
        var pkColumns = liveColumns.Values.Where(c => c.PrimaryKey).Select(c => c.Name).ToList();
        Assert.Equal(new[] { "PlayerId" }, pkColumns);

        // Hard pin for the c369c54 regression: LastGameAt MUST be nullable.
        // If a future bootstrap edit reintroduces NOT NULL on this column,
        // this assertion fails with a precise localised message before any
        // runtime insert can hit the SqliteException 19 path.
        Assert.False(liveColumns["LastGameAt"].NotNull,
            "PlayerStats.LastGameAt must remain nullable (c369c54 regression guard).");
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. PlayerStats → PlayerProfiles FK with ON DELETE CASCADE
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlayerStats_ForeignKey_To_PlayerProfiles_IsCascadeDelete()
    {
        if (!RunningOnSqlite()) return; // SQLite-only PRAGMA foreign-key check; skipped on the Postgres matrix cell.

        // The EF model declares:
        //   .HasOne<PlayerProfile>().WithOne()
        //   .HasForeignKey<PlayerStats>(x => x.PlayerId)
        //   .OnDelete(DeleteBehavior.Cascade)
        // The hand-rolled bootstrap CREATE must mirror this — otherwise
        // a future "delete profile to wipe stats" admin action leaves
        // orphaned PlayerStats rows that violate the one-to-one invariant.
        var fks = await ReadSqliteForeignKeysAsync("PlayerStats");
        Assert.Single(fks);
        var fk = fks[0];
        Assert.Equal("PlayerProfiles", fk.Table);
        Assert.Equal("PlayerId", fk.From);
        Assert.Equal("PlayerId", fk.To);
        // SQLite normalises the ON DELETE action to all-caps in
        // foreign_key_list output; assert against the canonical form.
        Assert.Equal("CASCADE", fk.OnDelete);
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. Both tables are reachable + insertable end-to-end
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlayerProfile_Insert_OnFreshBootstrap_DoesNotThrow()
    {
        // Smoke test — the bootstrap and EF model are aligned enough that
        // a real round-trip through the service works on a brand-new file.
        // This is the kind of test that would have failed loudly on
        // c369c54 ("NOT NULL constraint failed: PlayerStats.LastGameAt")
        // before any user could hit it in production.
        Assert.NotNull(_factory);
        var svc = _factory!.Services.GetRequiredService<PlayerProfileService>();
        var pid = "schema-test-" + Guid.NewGuid().ToString("N");

        var profile = await svc.GetOrCreateAsync(pid);
        Assert.Equal(pid, profile.PlayerId);

        // Re-fetch + confirm the stats row exists with LastGameAt=null
        // (the exact shape that previously crashed).
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stats = await db.PlayerStats.AsNoTracking().FirstOrDefaultAsync(s => s.PlayerId == pid);
        Assert.NotNull(stats);
        Assert.Null(stats!.LastGameAt);
    }
}
