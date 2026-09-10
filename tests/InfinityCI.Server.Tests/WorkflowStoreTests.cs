using InfinityCI.Core;
using InfinityCI.Server;
using InfinityCI.Server.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace InfinityCI.Server.Tests;

public class WorkflowStoreTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly CiServerOptions _options;

    public WorkflowStoreTests()
    {
        _options = new CiServerOptions { DataDir = _dir };
        Directory.CreateDirectory(_options.JobsDir);
    }

    private WorkflowStore CreateStore() => new(Options.Create(_options), new WorkflowGitStore(Options.Create(_options), NullLogger<WorkflowGitStore>.Instance), NullLogger<WorkflowStore>.Instance);

    private void WriteWorkflow(string fileName, string yaml) =>
        File.WriteAllText(Path.Combine(_options.JobsDir, fileName), yaml);

    [Fact]
    public async Task StartAsync_LoadsValidWorkflows_SkipsInvalidOnes()
    {
        WriteWorkflow("good.yml", "name: good\njobs:\n  a:\n    steps:\n      - command: echo hi");
        WriteWorkflow("bad.yml", "name: broken\njobs: {}");

        var store = CreateStore();
        await store.StartAsync(CancellationToken.None);

        var workflow = Assert.Single(store.Workflows);
        Assert.Equal("good", workflow.Name);
        Assert.NotNull(store.TryGet("GOOD"));
    }

    [Fact]
    public async Task StartAsync_EmptyDir_WritesSampleWorkflow()
    {
        var store = CreateStore();
        await store.StartAsync(CancellationToken.None);

        Assert.Single(store.Workflows);
        Assert.NotNull(store.TryGet("hello-workflow"));
    }

    [Fact]
    public void Save_ValidatesAndWritesFile_ThenReloads()
    {
        var store = CreateStore();

        var workflow = store.Save("save-test", "name: save-test\njobs:\n  a:\n    steps:\n      - command: echo");
        Assert.Equal("save-test", workflow.Name);

        // Name mismatch is rejected.
        Assert.Throws<WorkflowYamlException>(() => store.Save("other", "name: save-test\njobs:\n  a:\n    steps:\n      - command: echo"));

        Assert.NotNull(store.TryGet("save-test"));
        Assert.NotNull(store.TryGetRawYaml("save-test"));
    }

    [Fact]
    public async Task FileSystemChange_HotReloads()
    {
        var store = CreateStore();
        await store.StartAsync(CancellationToken.None);

        WriteWorkflow("added.yml", "name: added\njobs:\n  a:\n    steps:\n      - command: echo hi");
        await TestEnv.WaitUntilAsync(() => store.TryGet("added") is not null, TimeSpan.FromSeconds(5));

        File.Delete(Path.Combine(_options.JobsDir, "added.yml"));
        await TestEnv.WaitUntilAsync(() => store.TryGet("added") is null, TimeSpan.FromSeconds(5));
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
