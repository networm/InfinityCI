using System.Net;
using System.Net.Http.Json;
using InfinityCI.Server;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace InfinityCI.Server.Tests;

/// <summary>
/// Real-time hub tests against a real hosted server: heartbeat ping, the
/// jobs/queue/projects groups and their change broadcasts. Queue items stay
/// put because the test workflow targets agent:&lt;label&gt; and no agent ever
/// connects.
/// </summary>
public class RealtimeHubTests : IDisposable
{
    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly WebApplicationFactory<Program> _factory;

    public RealtimeHubTests()
    {
        WriteWorkflow("rt-job", """
            name: rt-job
            project: Default
            jobs:
              build:
                steps:
                  - command: echo rt-ok
            """);
        WriteWorkflow("rt-queue-job", """
            name: rt-queue-job
            project: Default
            jobs:
              one:
                runs_on: agent:ci-test-label
                steps:
                  - command: echo 1
              two:
                runs_on: agent:ci-test-label
                steps:
                  - command: echo 2
            """);
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("InfinityCI:DataDir", _dir));
    }

    private void WriteWorkflow(string name, string yaml)
    {
        Directory.CreateDirectory(Path.Combine(_dir, name));
        File.WriteAllText(Path.Combine(_dir, name, "workflow.yml"), yaml);
    }

    /// <summary>Logs in over the test server and opens an authenticated hub
    /// connection. TestServer has no WebSocket support, so SSE/long polling only;
    /// the session cookie is attached manually because the test handler replaces
    /// the SignalR default (cookie-aware) pipeline.</summary>
    private async Task<HubConnection> ConnectAsync()
    {
        var loginClient = new HttpClient(_factory.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost") };
        var login = await loginClient.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" });
        login.EnsureSuccessStatusCode();
        var cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];

        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/ci", HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling, options =>
            {
                options.HttpMessageHandlerFactory = _ => new CookieHeaderHandler(cookie) { InnerHandler = _factory.Server.CreateHandler() };
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private sealed class CookieHeaderHandler(string cookie) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static async Task<T> ReceiveAsync<T>(TaskCompletionSource<T> source, TimeSpan timeout)
    {
        var done = await Task.WhenAny(source.Task, Task.Delay(timeout));
        Assert.True(done == source.Task, "Timed out waiting for the SignalR event.");
        return await source.Task;
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "admin" }).Wait();
        return client;
    }

    private async Task<HttpResponseMessage> AdminPostAsync(string url, object payload) =>
        await AdminClient().PostAsJsonAsync(url, payload);

    private Task<HttpResponseMessage> AdminGetAsync(string url) => AdminClient().GetAsync(url);

    [Fact]
    public async Task Ping_EchoesServerTime()
    {
        await using var connection = await ConnectAsync();
        var before = DateTimeOffset.UtcNow.AddMinutes(-5);
        var pong = await connection.InvokeAsync<DateTimeOffset>("Ping");
        Assert.InRange(pong, before, DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task SubscribeJobs_ReceivesSnapshotAndChangeEvents()
    {
        await using var connection = await ConnectAsync();
        var snapshot = await connection.InvokeAsync<List<WorkflowSummaryDto>>("SubscribeJobs");
        Assert.Contains(snapshot, j => j.Name == "rt-job");

        var changed = new TaskCompletionSource<(string Kind, string Name)>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string, string>("workflowChanged", (kind, name) => changed.TrySetResult((kind, name)));

        var create = await AdminPostAsync("/api/jobs", new
        {
            yaml = "name: rt-second\nproject: Default\njobs:\n  a:\n    steps:\n      - command: echo",
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var (kind, name) = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Added", kind);
        Assert.Equal("rt-second", name);
    }

    [Fact]
    public async Task QueueGroup_ReceivesSnapshotAndMovement()
    {
        await using var connection = await ConnectAsync();
        var snapshot = await connection.InvokeAsync<List<QueueItemDto>>("SubscribeQueue");
        Assert.Empty(snapshot);

        // queueUpdated is a payload-less signal; the client re-fetches the
        // visibility-filtered queue via REST.
        var changes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On("queueUpdated", () => { changes.TrySetResult(); });

        var trigger = await AdminPostAsync("/api/jobs/rt-queue-job/trigger", new { });
        trigger.EnsureSuccessStatusCode();

        // Both agent-targeted jobs wait for an agent that never comes.
        await changes.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = await (await AdminGetAsync("/api/queue")).Content.ReadFromJsonAsync<List<QueueItemDto>>();
        Assert.Equal(2, queued!.Count);
        Assert.All(queued, i =>
        {
            Assert.Equal("rt-queue-job", i.WorkflowName);
            Assert.Equal("ci-test-label", i.RequiredLabel);
        });

        // Cancelling the run drains the queue and signals again.
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On("queueUpdated", () => { drained.TrySetResult(); });
        await AdminPostAsync($"/api/runs/{queued[0].RunId}/cancel", new { });
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var drainedQueue = await (await AdminGetAsync("/api/queue")).Content.ReadFromJsonAsync<List<QueueItemDto>>();
        Assert.Empty(drainedQueue!);
    }

    [Fact]
    public async Task ProjectsGroup_ReceivesChangeEvents()
    {
        await using var connection = await ConnectAsync();
        var projects = await connection.InvokeAsync<List<ProjectDto>>("SubscribeProjects");
        Assert.Contains(projects, p => p.Name == "Default");

        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On("projectsChanged", () => changed.TrySetResult());

        var create = await AdminPostAsync("/api/projects", new { name = "rt-project", description = "temp" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SubscribeUsers_RequiresSuperAdmin()
    {
        await using var connection = await ConnectAsync();
        var users = await connection.InvokeAsync<List<UserRowDto>>("SubscribeUsers");
        Assert.Contains(users, u => u.Username == "admin" && u.Role == "SuperAdmin");
    }

    public void Dispose()
    {
        try
        {
            _factory.Dispose();
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }
}

// Client-side mirrors of hub payloads.
public sealed record WorkflowSummaryDto(string Name, string Project, bool Enabled);
public sealed record QueueItemDto(long JobRunId, long RunId, string WorkflowName, string Project, string JobKey, string RunsOn, string? RequiredLabel, DateTimeOffset CreatedAt);
public sealed record ProjectDto(long Id, string Name, string? Description);
public sealed record UserRowDto(long Id, string Username, string Role);
