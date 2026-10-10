using System.Reflection;
using Mahjong.Autotable.Api.Players;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Mahjong.Autotable.Api.Tests.Players;

/// <summary>
/// Phase K Wave 23 — Drake (Persistence). Cross-provider parity tests for
/// <see cref="PlayerProfileService.IsUniqueViolation"/> — the predicate that
/// recognises a UNIQUE / PK constraint violation on whichever database
/// provider this codebase is shipping against (SQLite, PostgreSQL, or
/// SQL Server).
///
/// <para><b>Why this matters.</b> The race-safe upsert hotfix
/// (<c>2df2e75 fix(persistence): PlayerProfiles.PlayerId UNIQUE race-safe
/// upsert</c>) intentionally lives in one place — <see cref="PlayerProfileService"/>
/// — and claims to recover correctly on every provider. The runtime
/// integration test (<c>GetOrCreate_IsRaceSafe_WhenCalledConcurrently_WithSameId</c>)
/// only exercises SQLite (the in-process test DB), so a regression that
/// breaks Postgres or SqlServer recognition would land silently in prod.
/// These unit tests synthesize a representative provider exception for
/// each backend, wrap it in a <see cref="DbUpdateException"/>, and assert
/// the predicate returns <c>true</c> for the canonical unique-violation
/// codes and <c>false</c> for anything else — pinning the contract
/// without needing a live Postgres / SqlServer in CI.</para>
///
/// <para><b>Codes covered:</b>
/// <list type="bullet">
///   <item><b>SQLite:</b> <see cref="SqliteException.SqliteErrorCode"/> <c>== 19</c>
///         (SQLITE_CONSTRAINT — UNIQUE / PRIMARY KEY violations both surface
///         under the umbrella code, with the constraint name in
///         <see cref="System.Exception.Message"/>).</item>
///   <item><b>PostgreSQL:</b> <c>SqlState == "23505"</c> (unique_violation
///         per the SQLSTATE class 23).</item>
///   <item><b>SQL Server:</b> <c>Number == 2627</c> (PRIMARY KEY violation)
///         or <c>Number == 2601</c> (UNIQUE index violation).</item>
/// </list></para>
///
/// <para><b>Synthesizing provider exceptions.</b> The three driver
/// exception types have internal-only constructors for most fields. We
/// use reflection to invoke them rather than throwing real DB errors —
/// keeps the test pure-in-memory, hermetic, and provider-agnostic
/// (no Postgres / SqlServer needed on the test runner).</para>
/// </summary>
[Trait("Category", "Players"), Trait("Wave", "Phase-K-Drake-Audit")]
public class IsUniqueViolationCrossProviderTests
{
    // ────────────────────────────────────────────────────────────────────
    //  Reflection helper — invoke PlayerProfileService.IsUniqueViolation
    // ────────────────────────────────────────────────────────────────────

    // The predicate is `internal static` (Drake bumped from `private` for
    // this test pass). InternalsVisibleTo for Mahjong.Autotable.Api.Tests
    // is wired in the API csproj, so a straight call works.
    private static bool Predicate(DbUpdateException ex) =>
        PlayerProfileService.IsUniqueViolation(ex);

    // ────────────────────────────────────────────────────────────────────
    //  SQLite — SqliteErrorCode == 19 (SQLITE_CONSTRAINT)
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sqlite_ConstraintErrorCode_19_IsRecognised()
    {
        // 19 (SQLITE_CONSTRAINT) covers UNIQUE and PRIMARY KEY violations.
        // The constraint name is encoded in the message text. This is the
        // exact shape the live race probe triggered (53/53 retries logged
        // "UNIQUE constraint failed: PlayerProfiles.PlayerId").
        var sqlite = new SqliteException(
            "SQLite Error 19: 'UNIQUE constraint failed: PlayerProfiles.PlayerId'.",
            errorCode: 19);
        var dbex = new DbUpdateException("save failed", sqlite);

        Assert.True(Predicate(dbex));
    }

    [Fact]
    public void Sqlite_NonConstraintErrorCode_IsNotRecognised()
    {
        // Code 5 (SQLITE_BUSY), code 8 (SQLITE_READONLY), code 14 (SQLITE_CANTOPEN)
        // are NOT constraint violations and must not be treated as a race
        // signal — they are operational errors and should bubble.
        foreach (var code in new[] { 1, 5, 8, 14 })
        {
            var sqlite = new SqliteException($"SQLite Error {code}: ...", errorCode: code);
            var dbex = new DbUpdateException("save failed", sqlite);
            Assert.False(Predicate(dbex), $"expected false for SQLite error {code}");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  PostgreSQL — SqlState == "23505" (unique_violation)
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Postgres_SqlState_23505_IsRecognised()
    {
        // Postgres SQLSTATE class 23 = "Integrity Constraint Violation";
        // 23505 specifically is "unique_violation" — what Postgres raises
        // when an INSERT collides with an existing PK or unique index.
        var pg = NewPostgresException(messageText: "duplicate key value violates unique constraint", sqlState: "23505");
        var dbex = new DbUpdateException("save failed", pg);

        Assert.True(Predicate(dbex));
    }

    [Fact]
    public void Postgres_OtherSqlState_IsNotRecognised()
    {
        // 23503 (foreign_key_violation), 23502 (not_null_violation), 22001
        // (string_data_right_truncation) are integrity / data errors that
        // should NOT be confused with a UNIQUE collision — the upsert
        // retry-loop would loop forever on a real bug.
        foreach (var state in new[] { "23502", "23503", "22001", "42P01" })
        {
            var pg = NewPostgresException(messageText: "...", sqlState: state);
            var dbex = new DbUpdateException("save failed", pg);
            Assert.False(Predicate(dbex), $"expected false for Postgres SqlState {state}");
        }
    }

    private static Npgsql.PostgresException NewPostgresException(string messageText, string sqlState)
    {
        // PostgresException(string messageText, string severity, string invariantSeverity, string sqlState)
        // is the 4-string ctor (NonPublic). Empirically verified: arg1 → MessageText,
        // arg4 → SqlState.
        var ctor = typeof(Npgsql.PostgresException).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            types: new[] { typeof(string), typeof(string), typeof(string), typeof(string) },
            modifiers: null)
            ?? throw new MissingMethodException("Npgsql.PostgresException(string,string,string,string) not found.");
        return (Npgsql.PostgresException)ctor.Invoke(new object?[] { messageText, "ERROR", "ERROR", sqlState });
    }

    // ────────────────────────────────────────────────────────────────────
    //  SQL Server — Number == 2627 (PK) or 2601 (UNIQUE index)
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void SqlServer_Number_2627_IsRecognised()
    {
        // 2627 = "Violation of PRIMARY KEY constraint" — the exact message
        // SqlServer raises when an INSERT collides with the PK on
        // PlayerProfiles.PlayerId under provider=sqlserver.
        var sqlEx = NewSqlException(number: 2627, message: "Violation of PRIMARY KEY constraint 'PK_PlayerProfiles'");
        var dbex = new DbUpdateException("save failed", sqlEx);

        Assert.True(Predicate(dbex));
    }

    [Fact]
    public void SqlServer_Number_2601_IsRecognised()
    {
        // 2601 = "Cannot insert duplicate key row in object with unique
        // index" — the alternate path a UNIQUE constraint (without it
        // being the PK) takes on SqlServer.
        var sqlEx = NewSqlException(number: 2601, message: "Cannot insert duplicate key row ... unique index");
        var dbex = new DbUpdateException("save failed", sqlEx);

        Assert.True(Predicate(dbex));
    }

    [Fact]
    public void SqlServer_OtherNumbers_AreNotRecognised()
    {
        // 547 (FK violation), 8152 (string-truncation), 50000 (RAISERROR)
        // are SqlServer numeric codes that must NOT be misclassified as
        // a unique violation.
        foreach (var number in new[] { 547, 8152, 50000, 1205 /* deadlock */ })
        {
            var sqlEx = NewSqlException(number: number, message: $"err {number}");
            var dbex = new DbUpdateException("save failed", sqlEx);
            Assert.False(Predicate(dbex), $"expected false for SqlServer Number {number}");
        }
    }

    private static Microsoft.Data.SqlClient.SqlException NewSqlException(int number, string message)
    {
        // SqlException itself has only internal ctors. The supported test
        // route is: build a SqlError via its internal 8-arg ctor, build a
        // SqlErrorCollection via its parameterless internal ctor + Add(),
        // then call the static internal CreateException(coll, server).
        var sqlErrType = typeof(Microsoft.Data.SqlClient.SqlError);
        var sqlErrCtor = sqlErrType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            types: new[]
            {
                typeof(int), typeof(byte), typeof(byte),
                typeof(string), typeof(string), typeof(string),
                typeof(int), typeof(Exception),
            },
            modifiers: null)
            ?? throw new MissingMethodException("SqlError(int,byte,byte,string,string,string,int,Exception) not found.");
        var sqlErr = sqlErrCtor.Invoke(new object?[]
        {
            number, (byte)14 /* severity */, (byte)1 /* state */,
            "test-server", message, "test-proc",
            1 /* line */, null /* inner */,
        })!;

        var collType = typeof(Microsoft.Data.SqlClient.SqlErrorCollection);
        var collCtor = collType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null)
            ?? throw new MissingMethodException("SqlErrorCollection() not found.");
        var coll = collCtor.Invoke(null)!;
        collType.GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(coll, new[] { sqlErr });

        var createException = typeof(Microsoft.Data.SqlClient.SqlException)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .First(m =>
            {
                if (m.Name != "CreateException") return false;
                var ps = m.GetParameters();
                return ps.Length == 2 && ps[0].ParameterType == collType && ps[1].ParameterType == typeof(string);
            });
        return (Microsoft.Data.SqlClient.SqlException)createException.Invoke(null, new object[] { coll, "9.0.0" })!;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Negative cases — non-provider inner exceptions don't false-match
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void DbUpdateException_WithGenericInner_IsNotRecognised()
    {
        // The race-safe upsert MUST NOT swallow an arbitrary
        // DbUpdateException — only ones whose innermost cause is one of
        // the three recognised provider exceptions. An InvalidOperation
        // (model misconfiguration), a TimeoutException (network), or a
        // bare Exception must all fail the predicate so the upsert loop
        // rethrows and the operator gets a real stack trace.
        Assert.False(Predicate(new DbUpdateException("save failed", new InvalidOperationException("model"))));
        Assert.False(Predicate(new DbUpdateException("save failed", new TimeoutException("network"))));
        Assert.False(Predicate(new DbUpdateException("save failed", new Exception("generic"))));
    }

    [Fact]
    public void DbUpdateException_WithNoInner_IsNotRecognised()
    {
        // Defensive — `DbUpdateException("msg")` with no inner exception
        // must return false (the loop short-circuits the inner walk
        // immediately, no NRE).
        Assert.False(Predicate(new DbUpdateException("save failed")));
    }

    [Fact]
    public void DbUpdateException_WithNestedSqliteInner_IsRecognised()
    {
        // The predicate walks the full InnerException chain — EF Core
        // frequently nests the driver exception under one or two layers
        // of its own wrapping. Synthesise a 2-deep chain to verify.
        var sqlite = new SqliteException("UNIQUE constraint failed", errorCode: 19);
        var middle = new InvalidOperationException("ef wrap", sqlite);
        var top = new DbUpdateException("save failed", middle);

        Assert.True(Predicate(top));
    }
}
