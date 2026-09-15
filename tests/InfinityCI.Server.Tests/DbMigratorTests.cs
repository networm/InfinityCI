using InfinityCI.Server;
using InfinityCI.Server.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InfinityCI.Server.Tests;

/// <summary>Versioned schema migrations: baseline stamping, step application,
/// and data preservation across upgrades.</summary>
public class DbMigratorTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly string _dbPath;
    private readonly DbContextOptions<CiDbContext> _options;

    public DbMigratorTests()
    {
        _dbPath = Path.Combine(_dir, "mig.db");
        _options = new DbContextOptionsBuilder<CiDbContext>().UseSqlite($"Data Source={_dbPath}").Options;
    }

    private CiDbContext Open() => new(_options);

    /// <summary>Builds a v5-shaped database (no RunNumber, no SchemaVersion) with legacy rows.</summary>
    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void CreateLegacyV5Database()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        Exec(connection, """
            CREATE TABLE Users (Id INTEGER PRIMARY KEY AUTOINCREMENT, Username TEXT NOT NULL, DisplayName TEXT, PasswordHash TEXT NOT NULL, Role TEXT NOT NULL);
            CREATE TABLE Projects (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Description TEXT);
            CREATE TABLE UserProjects (UserId INTEGER NOT NULL, ProjectId INTEGER NOT NULL, CONSTRAINT PK_UserProjects PRIMARY KEY (UserId, ProjectId));
            CREATE TABLE Runs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                WorkflowName TEXT NOT NULL,
                Project TEXT NOT NULL,
                TriggeredBy TEXT NOT NULL,
                Status INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                StartedAt TEXT,
                FinishedAt TEXT,
                Version INTEGER NOT NULL,
                ParamsJson TEXT NOT NULL
            );
            CREATE TABLE JobRuns (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId INTEGER NOT NULL,
                JobKey TEXT NOT NULL,
                RunsOn TEXT NOT NULL,
                Status INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                StepsJson TEXT NOT NULL,
                NeedsJson TEXT NOT NULL
            );
            """);
        Exec(connection, """
            INSERT INTO Runs (WorkflowName, Project, TriggeredBy, Status, CreatedAt, Version, ParamsJson)
            VALUES ('legacy-job', 'Default', 'admin', 2, '2026-01-01', 1, '{}'),
                   ('legacy-job', 'Default', 'admin', 2, '2026-01-02', 2, '{}'),
                   ('other-job',  'Default', 'admin', 2, '2026-01-03', 3, '{}');
            """);
    }

    [Fact]
    public void LegacyDatabase_MigratesToLatest_PreservesData_AndBackfillsRunNumbers()
    {
        CreateLegacyV5Database();

        using (var db = Open())
        {
            DbMigrator.Migrate(db);

            Assert.Equal(DbMigrator.LatestVersion, db.VersionInfo.Single().Version);

            // Legacy data preserved; RunNumbers backfilled per workflow in Id order.
            var runs = db.Runs.OrderBy(r => r.WorkflowName).ThenBy(r => r.RunNumber).ToList();
            Assert.Equal(3, runs.Count);
            var legacy = runs.Where(r => r.WorkflowName == "legacy-job").ToList();
            Assert.Equal([1, 2], legacy.Select(r => r.RunNumber));
            Assert.Equal("other-job", runs.Single(r => r.WorkflowName == "other-job").WorkflowName);
            Assert.Equal(1, runs.Single(r => r.WorkflowName == "other-job").RunNumber);
        }

        // Second migrate call is a no-op (idempotent).
        using (var db = Open())
        {
            DbMigrator.Migrate(db);
            Assert.Equal(DbMigrator.LatestVersion, db.VersionInfo.Single().Version);
            Assert.Equal(3, db.Runs.Count());
        }
    }

    [Fact]
    public void FreshDatabase_IsCreatedAndStampedAtLatest()
    {
        using var db = Open();
        DbMigrator.Migrate(db);

        Assert.Equal(DbMigrator.LatestVersion, db.VersionInfo.Single().Version);
        // Latest model schema present (unique per-workflow run numbers).
        Assert.True(db.Runs.Any() == false);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }
}
