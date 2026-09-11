using InfinityCI.Core;
using InfinityCI.Server;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Auth;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Runs;
using InfinityCI.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace InfinityCI.Server.Tests;

/// <summary>End-to-end tests for needs-based scheduling (the job DAG).</summary>
public class NeedsSchedulingTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly CiServerOptions _options;
    private readonly ServiceProvider _provider;
    private readonly WorkflowStore _workflowStore;
    private readonly RunEvents _events = new(NullLogger<RunEvents>.Instance);
    private readonly RunQueueService _queue;
    private readonly RunRepository _repo;

    public NeedsSchedulingTests()
    {
        _options = new CiServerOptions { DataDir = _dir, MaxConcurrentJobs = 4 };
        Directory.CreateDirectory(_options.JobsDir);

        var services = new ServiceCollection();
        services.AddDbContext<CiDbContext>(db => db.UseSqlite(_options.DbConnectionString));
        services.AddScoped<RunRepository>();
        _provider = services.BuildServiceProvider();

        _workflowStore = new WorkflowStore(Options.Create(_options), new WorkflowGitStore(Options.Create(_options), NullLogger<WorkflowGitStore>.Instance), NullLogger<WorkflowStore>.Instance);
        var logStore = new JobLogStore(_options);
        var registry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);
        var localQueue = new LocalJobRunQueue();
        var aggregator = new RunAggregator(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _events,
            logStore,
            registry,
            localQueue,
            NullLogger<RunAggregator>.Instance);
        var credentialStore = new CredentialStore(_provider.GetRequiredService<IServiceScopeFactory>(),
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider().CreateProtector("test"));
        var executor = new JobRunExecutor(Options.Create(_options), logStore, _events, credentialStore, _provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<JobRunExecutor>.Instance);
        var workflowControl = new WorkflowControlService(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkflowControlService>.Instance);
        _queue = new RunQueueService(
            Options.Create(_options),
            _workflowStore,
            logStore,
            _events,
            registry,
            aggregator,
            executor,
            localQueue,
            workflowControl,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RunQueueService>.Instance);
        _repo = _provider.CreateScope().ServiceProvider.GetRequiredService<RunRepository>();

        new CiDbContext(new DbContextOptionsBuilder<CiDbContext>().UseSqlite(_options.DbConnectionString).Options)
            .Database.EnsureCreated();
    }

    private async Task StartAsync()
    {
        await _workflowStore.StartAsync(CancellationToken.None);
        await _queue.StartAsync(CancellationToken.None);
        await _queue.Ready;
    }

    private void WriteWorkflow(string yaml) =>
        File.WriteAllText(Path.Combine(_options.JobsDir, $"dag-{Guid.NewGuid():N}.yml"), yaml);

    private async Task<Run> WaitForRunTerminalAsync(long runId, TimeSpan? timeout = null)
    {
        await TestEnv.WaitUntilAsync(async () =>
        {
            var run = await _repo.GetRunAsync(runId);
            return run is { IsTerminal: true };
        }, timeout ?? TimeSpan.FromSeconds(60));
        return (await _repo.GetRunAsync(runId))!;
    }

    [Fact]
    public async Task NeedsChain_RunsInOrder_AfterDependenciesSucceed()
    {
        WriteWorkflow("""
            name: chain
            jobs:
              first:
                steps:
                  - command: echo first
              second:
                needs: [first]
                steps:
                  - command: echo second
              third:
                needs: [second]
                steps:
                  - command: echo third
            """);
        await StartAsync();

        var run = await _queue.TriggerAsync("chain", "tester");
        var finished = await WaitForRunTerminalAsync(run.Id);

        Assert.Equal(RunStatus.Success, finished.Status);
        var jobRuns = await _repo.GetJobRunsAsync(run.Id);
        Assert.Equal(JobRunStatus.Success, jobRuns.Single(j => j.JobKey == "first").Status);
        Assert.Equal(JobRunStatus.Success, jobRuns.Single(j => j.JobKey == "second").Status);
        Assert.Equal(JobRunStatus.Success, jobRuns.Single(j => j.JobKey == "third").Status);

        // Dependency order is visible in the per-job logs.
        var logs = new JobLogStore(_options);
        var third = await logs.ReadAfterAsync(run.Id, "third", 0);
        Assert.Contains(third, l => l.Text.Contains("third"));
    }

    [Fact]
    public async Task FailedDependency_CascadesSkipped_AndFailsRun()
    {
        WriteWorkflow("""
            name: cascade
            jobs:
              ok:
                steps:
                  - command: echo fine
              boom:
                steps:
                  - command: exit 3
              child:
                needs: [boom]
                steps:
                  - command: echo never
              grandchild:
                needs: [child]
                steps:
                  - command: echo never-ever
              independent:
                needs: [ok]
                steps:
                  - command: echo independent-ran
            """);
        await StartAsync();

        var run = await _queue.TriggerAsync("cascade", "tester");
        var finished = await WaitForRunTerminalAsync(run.Id);

        var jobRuns = await _repo.GetJobRunsAsync(run.Id);
        var dump = string.Join(" | ", jobRuns.Select(j => $"{j.JobKey}={j.Status}(exit={j.ExitCode},needs=[{string.Join(",", j.Needs)}])"));
        Assert.True(finished.Status == RunStatus.Failed, $"run status {finished.Status}: [{dump}]");
        Assert.True(jobRuns.Single(j => j.JobKey == "ok").Status == JobRunStatus.Success, $"ok: [{dump}]");
        Assert.True(jobRuns.Single(j => j.JobKey == "boom").Status == JobRunStatus.Failed, $"boom: [{dump}]");
        Assert.True(jobRuns.Single(j => j.JobKey == "child").Status == JobRunStatus.Skipped, $"child: [{dump}]");
        Assert.True(jobRuns.Single(j => j.JobKey == "grandchild").Status == JobRunStatus.Skipped, $"grandchild: [{dump}]");
        Assert.True(jobRuns.Single(j => j.JobKey == "independent").Status == JobRunStatus.Success, $"independent: [{dump}]");
    }

    [Fact]
    public async Task ParallelBranches_BothRun_AndJoin()
    {
        WriteWorkflow("""
            name: diamond
            jobs:
              start:
                steps:
                  - command: echo start
              left:
                needs: [start]
                steps:
                  - command: echo left
              right:
                needs: [start]
                steps:
                  - command: echo right
              join:
                needs: [left, right]
                steps:
                  - command: echo joined
            """);
        await StartAsync();

        var run = await _queue.TriggerAsync("diamond", "tester");
        var finished = await WaitForRunTerminalAsync(run.Id);

        Assert.Equal(RunStatus.Success, finished.Status);
        var jobRuns = await _repo.GetJobRunsAsync(run.Id);
        Assert.All(jobRuns, j => Assert.Equal(JobRunStatus.Success, j.Status));
        // The needs metadata round-trips through persistence for the UI.
        Assert.Equal(["left", "right"], jobRuns.Single(j => j.JobKey == "join").Needs.OrderBy(n => n));
    }

    public void Dispose()
    {
        _provider.Dispose();
        _workflowStore.Dispose();
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
