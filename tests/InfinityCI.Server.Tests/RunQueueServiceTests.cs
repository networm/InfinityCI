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

/// <summary>End-to-end tests for the parallel run engine.</summary>
public class RunQueueServiceTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly CiServerOptions _options;
    private readonly ServiceProvider _provider;
    private readonly WorkflowStore _workflowStore;
    private readonly RunEvents _events = new(NullLogger<RunEvents>.Instance);
    private readonly RunQueueService _queue;

    public RunQueueServiceTests()
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
        _queue = new RunQueueService(
            Options.Create(_options),
            _workflowStore,
            logStore,
            _events,
            registry,
            aggregator,
            executor,
            localQueue,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RunQueueService>.Instance);

        new CiDbContext(new DbContextOptionsBuilder<CiDbContext>().UseSqlite(_options.DbConnectionString).Options)
            .Database.EnsureCreated();
    }

    private async Task StartAsync()
    {
        await _workflowStore.StartAsync(CancellationToken.None);
        await _queue.StartAsync(CancellationToken.None);
        await _queue.Ready; // recovery must finish before triggering
    }

    private void WriteWorkflow(string yaml) =>
        File.WriteAllText(Path.Combine(_options.JobsDir, $"wf-{Guid.NewGuid():N}.yml"), yaml);

    private RunRepository Repo()
    {
        var scope = _provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<RunRepository>();
    }

    private async Task<Run> WaitForRunTerminalAsync(long runId, TimeSpan? timeout = null)
    {
        await TestEnv.WaitUntilAsync(async () =>
        {
            var run = await Repo().GetRunAsync(runId);
            return run is { IsTerminal: true };
        }, timeout ?? TimeSpan.FromSeconds(60));
        return (await Repo().GetRunAsync(runId))!;
    }

    [Fact]
    public async Task ParallelJobs_RunConcurrently_AndAggregateRunStatus()
    {
        // Each job sleeps ~2-3s; with parallel execution the run finishes in <5s
        // while sequential execution would need >4s of pure sleeping per job.
        var slow = OperatingSystem.IsWindows() ? "ping -n 4 127.0.0.1 > nul" : "sleep 3";
        WriteWorkflow($$"""
            name: parallel-job
            jobs:
              one:
                steps:
                  - name: sleep
                    command: {{slow}}
                  - name: echo
                    command: echo ONE-done
              two:
                steps:
                  - name: sleep
                    command: {{slow}}
                  - name: echo
                    command: echo TWO-done
            """);
        await StartAsync();

        var run = await _queue.TriggerAsync("parallel-job", "tester");
        var finished = await WaitForRunTerminalAsync(run.Id);
        var elapsed = (finished.FinishedAt!.Value - finished.StartedAt!.Value).TotalSeconds;

        Assert.Equal(RunStatus.Success, finished.Status);
        Assert.True(elapsed < 6, $"Jobs should run in parallel; elapsed {elapsed:F1}s suggests sequential execution.");

        var jobRuns = await Repo().GetJobRunsAsync(run.Id);
        Assert.Equal(2, jobRuns.Count);
        Assert.All(jobRuns, j => Assert.Equal(JobRunStatus.Success, j.Status));
        Assert.All(jobRuns, j =>
        {
            var dump = string.Join(" | ", j.Steps.Select(st => $"{st.Name}:{st.Status}:{st.StartLine}-{st.EndLine}"));
            var echo = j.Steps[1];
            Assert.True(echo.EndLine > echo.StartLine,
                $"echo step must capture its output line (job={j.JobKey}, steps=[{dump}])");
        });

        // Job logs are per-job files with their own line cursors.
        var logs = new JobLogStore(_options);
        var oneLines = await logs.ReadAfterAsync(run.Id, "one", 0);
        Assert.Contains(oneLines, l => l.Text.Contains("ONE-done"));
        Assert.DoesNotContain(oneLines, l => l.Text.Contains("TWO-done"));
    }

    [Fact]
    public async Task FailingJob_FailsRun_ButOtherJobStillSucceeds()
    {
        WriteWorkflow("""
            name: mixed-job
            jobs:
              ok:
                steps:
                  - command: echo fine
              boom:
                steps:
                  - command: exit 3
            """);
        await StartAsync();

        var run = await _queue.TriggerAsync("mixed-job", "tester");
        var finished = await WaitForRunTerminalAsync(run.Id);

        Assert.Equal(RunStatus.Failed, finished.Status);
        var jobRuns = await Repo().GetJobRunsAsync(run.Id);
        Assert.Equal(JobRunStatus.Success, jobRuns.Single(j => j.JobKey == "ok").Status);
        Assert.Equal(JobRunStatus.Failed, jobRuns.Single(j => j.JobKey == "boom").Status);
        Assert.Equal(3, jobRuns.Single(j => j.JobKey == "boom").ExitCode);
    }

    [Fact]
    public async Task RunWithTimestampedLogs_EmitsIsoTimestamps()
    {
        WriteWorkflow("""
            name: ts-job
            jobs:
              a:
                steps:
                  - command: echo stamped
            """);
        await StartAsync();

        var run = await _queue.TriggerAsync("ts-job", "tester");
        await WaitForRunTerminalAsync(run.Id);

        var lines = await new JobLogStore(_options).ReadAfterAsync(run.Id, "a", 0);
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.True(DateTimeOffset.TryParse(l.TimestampUtc, out _), "every line needs an ISO timestamp"));
    }

    [Fact]
    public async Task CancelRun_CancelsAllOpenJobs()
    {
        var slow = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30";
        WriteWorkflow($$"""
            name: cancel-job
            jobs:
              slowOne:
                steps:
                  - command: {{slow}}
              slowTwo:
                steps:
                  - command: {{slow}}
            """);
        await StartAsync();

        var run = await _queue.TriggerAsync("cancel-job", "tester");
        await TestEnv.WaitUntilAsync(async () =>
        {
            var jobRuns = await Repo().GetJobRunsAsync(run.Id);
            return jobRuns.All(j => j.Status == JobRunStatus.Running);
        }, TimeSpan.FromSeconds(30));

        await _queue.TryCancelRunAsync(run.Id);
        var finished = await WaitForRunTerminalAsync(run.Id);

        Assert.Equal(RunStatus.Cancelled, finished.Status);
        var jobRuns = await Repo().GetJobRunsAsync(run.Id);
        Assert.All(jobRuns, j => Assert.Equal(JobRunStatus.Cancelled, j.Status));
    }

    [Fact]
    public async Task TriggerUnknownWorkflow_Throws()
    {
        await StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _queue.TriggerAsync("no-such-workflow", "tester"));
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
