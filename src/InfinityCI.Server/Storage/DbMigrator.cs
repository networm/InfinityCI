using InfinityCI.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace InfinityCI.Server.Storage;

/// <summary>
/// Versioned schema migration runner. The database carries its schema version
/// in the SchemaVersion table; on startup pending steps from <see cref="Steps"/>
/// are applied in order. Fresh databases are created from the current EF model
/// and stamped with <see cref="LatestVersion"/> directly. Future schema changes
/// append a new step (fromVersion → toVersion) — never recreate the database,
/// so run history stays viewable.
/// </summary>
public static class DbMigrator
{
    /// <summary>Schema version of the model before RunNumber was introduced.</summary>
    public const int BaselineVersion = 5;
    public const int LatestVersion = 6;

    private sealed record MigrationStep(int FromVersion, int ToVersion, string Name, Action<CiDbContext> Apply);

    private static readonly List<MigrationStep> Steps =
    [
        new(5, 6, "add per-workflow RunNumber", db =>
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE Runs ADD COLUMN RunNumber INTEGER NOT NULL DEFAULT 0;");
            // Backfill: number runs 1..n per workflow in creation order (history preserved).
            db.Database.ExecuteSqlRaw("""
                UPDATE Runs SET RunNumber = (
                    SELECT COUNT(*) FROM Runs AS older
                    WHERE older.WorkflowName = Runs.WorkflowName AND older.Id <= Runs.Id
                );
                """);
            db.Database.ExecuteSqlRaw(
                "CREATE UNIQUE INDEX IX_Runs_WorkflowName_RunNumber ON Runs (WorkflowName, RunNumber);");
        }),
    ];

    /// <summary>Creates or migrates the database to <see cref="LatestVersion"/>.</summary>
    public static void Migrate(CiDbContext db)
    {
        db.Database.EnsureCreated(); // no-op when the database already exists

        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS VersionInfo (
                Id INTEGER NOT NULL PRIMARY KEY,
                Version INTEGER NOT NULL,
                AppliedUtc TEXT NOT NULL
            );
            """);

        var version = CurrentVersion(db);
        if (version == 0)
        {
            // Version 0 = no version row. A truly fresh database (EnsureCreated
            // built the latest schema) stamps directly at latest; a database from
            // before version tracking existed is baselined so its pending steps run.
            // Runs exists but lacks RunNumber → a pre-versioning (v5) database
            // whose pending steps must run; otherwise the schema is already latest.
            var isLegacy = RunsTableMissingRunNumber(db);
            Stamp(db, isLegacy ? BaselineVersion : LatestVersion);
            if (!isLegacy)
                return;
            version = BaselineVersion;
        }

        while (version < LatestVersion)
        {
            var step = Steps.FirstOrDefault(s => s.FromVersion == version)
                ?? throw new InvalidOperationException(
                    $"No migration step from schema version {version} (latest is {LatestVersion}).");
            using var transaction = db.Database.BeginTransaction();
            step.Apply(db);
            Stamp(db, step.ToVersion);
            transaction.Commit();
            version = step.ToVersion;
        }
    }

    private static int CurrentVersion(CiDbContext db) =>
        db.VersionInfo.Any() ? db.VersionInfo.Single().Version : 0;

    /// <summary>True when the Runs table exists but lacks RunNumber — a
    /// pre-versioning (v5) database. An absent table means a fresh database.</summary>
    private static bool RunsTableMissingRunNumber(CiDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Runs'";
            if (Convert.ToInt32(command.ExecuteScalar()) == 0)
                return false; // fresh database — no Runs table yet
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Runs') WHERE name = 'RunNumber'";
            return Convert.ToInt32(command.ExecuteScalar()) == 0; // table exists without the column → legacy
        }
    }

    private static void Stamp(CiDbContext db, int version)
    {
        var row = db.VersionInfo.FirstOrDefault();
        if (row is null)
        {
            db.VersionInfo.Add(new VersionInfo { Id = 1, Version = version, AppliedUtc = DateTimeOffset.UtcNow });
        }
        else
        {
            row.Version = version;
            row.AppliedUtc = DateTimeOffset.UtcNow;
        }
        db.SaveChanges();
    }
}
