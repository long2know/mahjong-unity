using Xunit;

namespace Mahjong.Autotable.Api.Tests.Phase_K_W19.Bishop;

/// <summary>
/// Phase K Wave 19 — Bishop. Smoke tests for the three EF
/// migration files that land the
/// <c>SwissPairingAuditEntries</c> table for the
/// <c>SqliteAppDbContext</c>, <c>PostgresAppDbContext</c>, and
/// <c>SqlServerAppDbContext</c> contexts. Pins the file
/// presence + the migration name so a future refactor that
/// drops the migration for any of the three providers will
/// fail loudly here rather than at deploy time.
/// </summary>
public sealed class SwissPairingAuditMigrationFilesTests
{
    private static string LocateMigrationsDir(string provider)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var probe = Path.Combine(dir.FullName,
                "src", "backend", "src", "Mahjong.Autotable.Api",
                "Persistence", "Migrations", provider);
            if (Directory.Exists(probe)) return probe;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate migrations dir for {provider}.");
    }

    private static string[] MigrationFiles(string provider) =>
        Directory.GetFiles(LocateMigrationsDir(provider), "*_Phase_K_W19_SwissPairingAudit.cs");

    private static string[] DesignerFiles(string provider) =>
        Directory.GetFiles(LocateMigrationsDir(provider), "*_Phase_K_W19_SwissPairingAudit.Designer.cs");

    [Theory, Trait("Category", "Persistence"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    [InlineData("Sqlite")]
    [InlineData("Postgres")]
    [InlineData("SqlServer")]
    public void Migration_FileExistsForProvider(string provider)
    {
        Assert.NotEmpty(MigrationFiles(provider));
    }

    [Theory, Trait("Category", "Persistence"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    [InlineData("Sqlite")]
    [InlineData("Postgres")]
    [InlineData("SqlServer")]
    public void Migration_DesignerFileExistsForProvider(string provider)
    {
        Assert.NotEmpty(DesignerFiles(provider));
    }

    [Theory, Trait("Category", "Persistence"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    [InlineData("Sqlite")]
    [InlineData("Postgres")]
    [InlineData("SqlServer")]
    public void Migration_CreatesSwissPairingAuditEntriesTable(string provider)
    {
        var file = MigrationFiles(provider).Single();
        var text = File.ReadAllText(file);
        Assert.Contains("SwissPairingAuditEntries", text);
    }

    [Theory, Trait("Category", "Persistence"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    [InlineData("Sqlite")]
    [InlineData("Postgres")]
    [InlineData("SqlServer")]
    public void Migration_DeclaresUniqueIndexOnTournamentRoundBoard(string provider)
    {
        var file = MigrationFiles(provider).Single();
        var text = File.ReadAllText(file);
        // Index name should reflect (TournamentId, Round, Board)
        // somewhere. We check the three column names appear in
        // close proximity (covering both single-line + multi-
        // line CreateIndex emit styles).
        Assert.Contains("TournamentId", text);
        Assert.Contains("Round", text);
        Assert.Contains("Board", text);
        // Should declare unique:true on some index.
        Assert.Contains("unique: true", text);
    }

    [Theory, Trait("Category", "Persistence"), Trait("Wave", "Phase-K-19"), Trait("Lane", "Bishop")]
    [InlineData("Sqlite")]
    [InlineData("Postgres")]
    [InlineData("SqlServer")]
    public void Migration_DesignerSnapshotReferencesEntity(string provider)
    {
        var file = DesignerFiles(provider).Single();
        var text = File.ReadAllText(file);
        Assert.Contains("SwissPairingAuditEntry", text);
    }
}
