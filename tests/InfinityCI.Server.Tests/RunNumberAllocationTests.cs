using InfinityCI.Core;
using InfinityCI.Server.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InfinityCI.Server.Tests;

public class RunNumberAllocationTests
{
    [Fact]
    public async Task AddRunAsync_Allocates_PerWorkflow_Sequence()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "infinityci-tests", Guid.NewGuid().ToString("N"), "alloc.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var options = new DbContextOptionsBuilder<CiDbContext>().UseSqlite($"Data Source={dbPath}").Options;
        using var db = new CiDbContext(options);
        db.Database.EnsureCreated();

        var repo = new RunRepository(db);
        var run1 = await repo.AddRunAsync(new Run { WorkflowName = "w", Project = "p", TriggeredBy = "t", Status = RunStatus.Running, CreatedAt = DateTimeOffset.UtcNow });
        var run2 = await repo.AddRunAsync(new Run { WorkflowName = "w", Project = "p", TriggeredBy = "t", Status = RunStatus.Running, CreatedAt = DateTimeOffset.UtcNow });
        var other = await repo.AddRunAsync(new Run { WorkflowName = "other", Project = "p", TriggeredBy = "t", Status = RunStatus.Running, CreatedAt = DateTimeOffset.UtcNow });

        Assert.Equal(1, run1.RunNumber);
        Assert.Equal(2, run2.RunNumber);
        Assert.Equal(1, other.RunNumber);
    }

    public void Dispose() { }
}
