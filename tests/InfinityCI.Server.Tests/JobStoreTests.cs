using InfinityCI.Server;
using InfinityCI.Server.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace InfinityCI.Server.Tests;

public class JobStoreTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly CiServerOptions _options;

    public JobStoreTests()
    {
        _options = new CiServerOptions { DataDir = _dir };
        Directory.CreateDirectory(_options.JobsDir);
    }

    private JobStore CreateStore() => new(Options.Create(_options), NullLogger<JobStore>.Instance);

    private void WriteJob(string fileName, string yaml) =>
        File.WriteAllText(Path.Combine(_options.JobsDir, fileName), yaml);

    [Fact]
    public async Task StartAsync_LoadsValidJobs_SkipsInvalidOnes()
    {
        WriteJob("good.yml", "name: good-job\nsteps:\n  - command: echo hi");
        WriteJob("bad.yml", "steps: []");

        var store = CreateStore();
        await store.StartAsync(CancellationToken.None);

        var job = Assert.Single(store.Jobs);
        Assert.Equal("good-job", job.Name);
        Assert.NotNull(store.TryGet("GOOD-JOB")); // lookup is case-insensitive
    }

    [Fact]
    public async Task StartAsync_EmptyDir_WritesSampleJob()
    {
        var store = CreateStore();
        await store.StartAsync(CancellationToken.None);

        Assert.Single(store.Jobs);
        Assert.NotNull(store.TryGet("hello-build"));
    }

    [Fact]
    public async Task FileSystemChange_HotReloads()
    {
        var store = CreateStore();
        await store.StartAsync(CancellationToken.None);

        WriteJob("added.yml", "name: added-job\nsteps:\n  - command: echo hi");
        await TestEnv.WaitUntilAsync(() => store.TryGet("added-job") is not null, TimeSpan.FromSeconds(5));

        File.Delete(Path.Combine(_options.JobsDir, "added.yml"));
        await TestEnv.WaitUntilAsync(() => store.TryGet("added-job") is null, TimeSpan.FromSeconds(5));
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
