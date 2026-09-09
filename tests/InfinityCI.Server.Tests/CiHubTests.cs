using InfinityCI.Core;
using InfinityCI.Server;
using InfinityCI.Server.Builds;
using InfinityCI.Server.Hubs;
using InfinityCI.Server.Jobs;
using InfinityCI.Server.Storage;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace InfinityCI.Server.Tests;

/// <summary>End-to-end tests for the SignalR hub over a real hosted server.</summary>
public class CiHubTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly WebApplicationFactory<Program> _factory;

    public CiHubTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "jobs"));
        var slowCommand = OperatingSystem.IsWindows() ? "ping -n 6 127.0.0.1 > nul" : "sleep 5";
        File.WriteAllText(Path.Combine(_dir, "jobs", "hub-job.yml"), $"""
            name: hub-job
            steps:
              - name: slow
                command: {slowCommand}
              - name: late
                command: echo beta
            """);
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("InfinityCI:DataDir", _dir));
    }

    private BuildQueueService Queue() => _factory.Services.GetRequiredService<BuildQueueService>();

    /// <summary>HubConnection routed through the test server (TestServer has no WebSockets → long polling).</summary>
    private HubConnection CreateHubClient()
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/ci"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            // Match the server's AddJsonProtocol enum-as-string converter.
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()))
            .Build();
    }

    private async Task<Build> WaitRunningAsync(long buildId)
    {
        var repo = _factory.Services.GetRequiredService<IServiceScopeFactory>()
            .CreateScope().ServiceProvider.GetRequiredService<BuildRepository>();
        await TestEnv.WaitUntilAsync(async () =>
        {
            var fresh = await repo.GetAsync(buildId);
            return fresh is { Status: BuildStatus.Running };
        }, TimeSpan.FromSeconds(30));
        return (await repo.GetAsync(buildId))!;
    }

    [Fact]
    public async Task SubscribeDuringRunningBuild_ReceivesSnapshotAndLiveStream()
    {
        await Queue().Ready;
        var build = await Queue().TriggerAsync("hub-job");
        await WaitRunningAsync(build.Id); // subscribe mid-run so live events must flow

        var connection = CreateHubClient();
        await connection.StartAsync();
        try
        {
            var liveLines = new List<LogLine>();
            connection.On<long, long, string>("logAppended", (buildId, offset, text) =>
            {
                if (buildId == build.Id)
                    liveLines.Add(new LogLine(offset, text));
            });
            var terminal = new TaskCompletionSource();
            connection.On<Build>("buildUpdated", b =>
            {
                if (b.Id == build.Id && b.IsTerminal)
                    terminal.TrySetResult();
            });

            var snapshot = await connection.InvokeAsync<BuildSubscription>("SubscribeBuild", build.Id, 0L);

            // Mid-run snapshot: still running, with the slow step's output backfilled.
            Assert.Equal(build.Id, snapshot.Build.Id);
            Assert.Equal(BuildStatus.Running, snapshot.Build.Status);
            Assert.Contains(snapshot.Lines, l => l.Text.Contains("[server]"));

            await TestEnv.WaitUntilAsync(() => terminal.Task.IsCompleted, TimeSpan.FromSeconds(30));

            // "beta" is only logged after the slow step finishes, so it must have
            // arrived through the live stream rather than the snapshot backfill.
            Assert.Contains(liveLines, l => l.Text.Contains("beta"));
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task SubscribeDashboard_ReturnsRecentBuilds()
    {
        var options = _factory.Services.GetRequiredService<CiServerOptions>();
        Assert.Equal(_dir, options.DataDir);
        var jobStore = _factory.Services.GetRequiredService<JobStore>();
        Assert.NotNull(jobStore.TryGet("hub-job"));
        await Queue().Ready;
        var build = await Queue().TriggerAsync("hub-job");
        await WaitRunningAsync(build.Id);

        var connection = CreateHubClient();
        await connection.StartAsync();
        try
        {
            var builds = await connection.InvokeAsync<List<Build>>("SubscribeDashboard");
            var found = Assert.Single(builds, b => b.Id == build.Id);
            Assert.Equal("hub-job", found.JobName);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    public void Dispose()
    {
        _factory.Dispose();
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
