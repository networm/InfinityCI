using InfinityCI.Core;
using InfinityCI.Server;
using InfinityCI.Server.Agents;
using InfinityCI.Server.Builds;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace InfinityCI.Server.Tests;

/// <summary>End-to-end tests for the build queue and process execution engine.</summary>
public class BuildQueueServiceTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly CiServerOptions _options;
    private readonly ServiceProvider _provider;
    private readonly JobStore _jobStore;
    private readonly BuildEvents _events = new(NullLogger<BuildEvents>.Instance);
    private readonly BuildQueueService _queue;

    public BuildQueueServiceTests()
    {
        _options = new CiServerOptions { DataDir = _dir };
        Directory.CreateDirectory(_options.JobsDir);

        var services = new ServiceCollection();
        services.AddDbContext<CiDbContext>(db => db.UseSqlite(_options.DbConnectionString));
        services.AddScoped<BuildRepository>();
        _provider = services.BuildServiceProvider();

        _jobStore = new JobStore(Options.Create(_options), NullLogger<JobStore>.Instance);
        var logStore = new BuildLogStore(_options, _events);
        var agentRegistry = new AgentRegistry(NullLogger<AgentRegistry>.Instance);
        _queue = new BuildQueueService(
            Options.Create(_options),
            _jobStore,
            logStore,
            _events,
            agentRegistry,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BuildQueueService>.Instance);

        new CiDbContext(new DbContextOptionsBuilder<CiDbContext>().UseSqlite(_options.DbConnectionString).Options)
            .Database.EnsureCreated();
    }

    private async Task StartAsync()
    {
        await _jobStore.StartAsync(CancellationToken.None);
        await _queue.StartAsync(CancellationToken.None);
        await _queue.Ready; // recovery must finish before triggering, as the REST API would guarantee
    }

    private void WriteJob(string yaml) =>
        File.WriteAllText(Path.Combine(_options.JobsDir, $"job-{Guid.NewGuid():N}.yml"), yaml);

    private async Task<Build> TriggerAndWaitAsync(string jobName, TimeSpan? timeout = null)
    {
        var build = await _queue.TriggerAsync(jobName);
        await TestEnv.WaitUntilAsync(async () =>
        {
            var fresh = await Repo().GetAsync(build.Id);
            return fresh is { IsTerminal: true };
        }, timeout ?? TimeSpan.FromSeconds(60));
        return (await Repo().GetAsync(build.Id))!;
    }

    private BuildRepository Repo()
    {
        var scope = _provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<BuildRepository>();
    }

    [Fact]
    public async Task SuccessfulJob_CompletesWithPerStepResults()
    {
        WriteJob("""
            name: ok-job
            steps:
              - name: first
                command: echo one
              - name: second
                command: echo two
            """);
        await StartAsync();

        var build = await TriggerAndWaitAsync("ok-job");

        Assert.Equal(BuildStatus.Success, build.Status);
        Assert.Equal(0, build.ExitCode);
        Assert.Equal(2, build.Steps.Count);
        Assert.All(build.Steps, s => Assert.Equal(BuildStepStatus.Success, s.Status));
        // Offsets progress monotonically and each step captured some output.
        Assert.True(build.Steps[0].StartOffset <= build.Steps[0].EndOffset);
        Assert.True(build.Steps[1].StartOffset >= build.Steps[0].EndOffset);

        var lines = await new BuildLogStore(_options, _events).ReadAfterAsync(build.Id, 0);
        Assert.Contains(lines, l => l.Text.Contains("one"));
        Assert.Contains(lines, l => l.Text.Contains("two"));
    }

    [Fact]
    public async Task FailingStep_FailsBuild_AndSkipsRemaining()
    {
        WriteJob("""
            name: fail-job
            steps:
              - name: warmup
                command: echo ok
              - name: explode
                command: exit 3
              - name: never-runs
                command: echo unreachable
            """);
        await StartAsync();

        var build = await TriggerAndWaitAsync("fail-job");

        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.Equal(3, build.ExitCode);
        Assert.Equal(BuildStepStatus.Success, build.Steps[0].Status);
        Assert.Equal(BuildStepStatus.Failed, build.Steps[1].Status);
        Assert.Equal(BuildStepStatus.Skipped, build.Steps[2].Status);
    }

    [Fact]
    public async Task ContinueOnError_StepFailureDoesNotFailBuild()
    {
        WriteJob("""
            name: continue-job
            steps:
              - name: tolerated
                command: exit 3
                continue_on_error: true
              - name: still-runs
                command: echo reached
            """);
        await StartAsync();

        var build = await TriggerAndWaitAsync("continue-job");

        Assert.Equal(BuildStatus.Success, build.Status);
        Assert.Equal(BuildStepStatus.Failed, build.Steps[0].Status);
        Assert.Equal(BuildStepStatus.Success, build.Steps[1].Status);
    }

    [Fact]
    public async Task CancelRunningBuild_MarksBuildAndStepCancelled()
    {
        var longCommand = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30";
        WriteJob($$"""
            name: slow-job
            steps:
              - name: slow
                command: {{longCommand}}
            """);
        await StartAsync();

        var build = await _queue.TriggerAsync("slow-job");
        await TestEnv.WaitUntilAsync(async () =>
        {
            var fresh = await Repo().GetAsync(build.Id);
            return fresh is { Status: BuildStatus.Running };
        }, TimeSpan.FromSeconds(30));

        Assert.True(await _queue.TryCancelAsync(build.Id));
        var finished = await TriggerAndWaitWhenTerminal(build.Id);

        Assert.True(finished.Status == BuildStatus.Cancelled,
            $"Expected Cancelled but was {finished.Status} (exit={finished.ExitCode}, steps=[{string.Join(", ", finished.Steps.Select(s => $"{s.Name}:{s.Status}:{s.ExitCode}"))}])");
        Assert.Equal(BuildStepStatus.Cancelled, finished.Steps[0].Status);
    }

    private async Task<Build> TriggerAndWaitWhenTerminal(long buildId)
    {
        await TestEnv.WaitUntilAsync(async () =>
        {
            var fresh = await Repo().GetAsync(buildId);
            return fresh is { IsTerminal: true };
        }, TimeSpan.FromSeconds(60));
        return (await Repo().GetAsync(buildId))!;
    }

    [Fact]
    public async Task TriggerUnknownJob_Throws()
    {
        await StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _queue.TriggerAsync("no-such-job"));
    }

    [Fact]
    public async Task EnvironmentVariables_AreInjectedIntoProcess()
    {
        var command = OperatingSystem.IsWindows() ? "echo %MY_JOB_VAR%" : "echo $MY_JOB_VAR";
        WriteJob($$"""
            name: env-job
            env:
              MY_JOB_VAR: job-value
            steps:
              - name: capture
                command: {{command}}
            """);
        await StartAsync();

        var build = await TriggerAndWaitAsync("env-job");
        Assert.Equal(BuildStatus.Success, build.Status);

        var lines = await new BuildLogStore(_options, _events).ReadAfterAsync(build.Id, 0);
        Assert.Contains(lines, l => l.Text.Contains("job-value"));
    }

    public void Dispose()
    {
        _provider.Dispose();
        _jobStore.Dispose();
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
