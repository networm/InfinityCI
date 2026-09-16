using InfinityCI.Core;
using InfinityCI.Server;
using InfinityCI.Server.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using InfinityCI.Server.Realtime;

namespace InfinityCI.Server.Tests;

public class WorkflowStoreTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly CiServerOptions _options;

    public WorkflowStoreTests()
    {
        _options = new CiServerOptions { DataDir = _dir, CreateSampleWorkflow = false };
    }

    private WorkflowStore CreateStore() => new(Options.Create(_options), new WorkflowGitStore(Options.Create(_options), NullLogger<WorkflowGitStore>.Instance), new ChangeEvents(NullLogger<ChangeEvents>.Instance), NullLogger<WorkflowStore>.Instance);

    private void WriteWorkflow(string workflowName, string yaml)
    {
        var dir = Path.Combine(_options.DataDir, workflowName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, WorkflowStore.ConfigFileName), yaml);
    }

    /// <summary>Removes the sample workflow's config so Reload skips it
    /// (the directory's .git may contain read-only objects — leave it alone).</summary>
    private void RemoveSample()
    {
        var config = Path.Combine(_options.DataDir, "hello-workflow", WorkflowStore.ConfigFileName);
        if (File.Exists(config))
            File.Delete(config);
    }

    [Fact]
    public async Task StartAsync_LoadsValidWorkflows_SkipsInvalidOnes()
    {
        WriteWorkflow("good", "name: good\njobs:\n  a:\n    steps:\n      - command: echo hi");
        // Invalid workflows are skipped at load time (jobs: empty).
        WriteWorkflow("broken", "name: broken\njobs: {}");

        var store = CreateStore();
        await store.StartAsync(CancellationToken.None);

        var workflow = Assert.Single(store.Workflows);
        Assert.Equal("good", workflow.Name);
        Assert.NotNull(store.TryGet("GOOD"));
    }

    [Fact]
    public async Task StartAsync_SampleWorkflowFlagDisabled_DoesNotWriteSample()
    {
        _options.CreateSampleWorkflow = false;
        var store = CreateStore();
        await store.StartAsync(CancellationToken.None);

        Assert.Empty(store.Workflows);
    }

    [Fact]
    public async Task StartAsync_SampleWorkflowEnabled_WritesSampleWorkflow()
    {
        _options.CreateSampleWorkflow = true;
        var store = CreateStore();
        await store.StartAsync(CancellationToken.None);

        var workflow = Assert.Single(store.Workflows);
        Assert.Equal("hello-workflow", workflow.Name);
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
        // Config lives in the task's own directory, under its own git repo.
        Assert.True(Directory.Exists(Path.Combine(_options.DataDir, "save-test", ".git")));
    }

    [Fact]
    public async Task FileSystemChange_HotReloads()
    {
        var store = CreateStore();
        await store.StartAsync(CancellationToken.None);

        WriteWorkflow("added", "name: added\njobs:\n  a:\n    steps:\n      - command: echo hi");
        await TestEnv.WaitUntilAsync(() => store.TryGet("added") is not null, TimeSpan.FromSeconds(5));

        File.Delete(Path.Combine(_options.DataDir, "added", "workflow.yml"));
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
